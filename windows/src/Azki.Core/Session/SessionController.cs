using Azki.Core.Frame;
using Azki.Core.Runtime;

namespace Azki.Core.Session;

public enum SessionPhase
{
    /// <summary>No firmware imported yet (or the engine is missing).</summary>
    Idle,
    Importing,
    /// <summary>Firmware imported; a candidate can be started.</summary>
    Ready,
    Starting,
    Running,
    Stopping,
}

/// <summary>Immutable view of <see cref="SessionController"/> for the UI.</summary>
public sealed record SessionSnapshot(
    SessionPhase Phase,
    string? FirmwarePath,
    IReadOnlyList<FirmwareCandidate> Candidates,
    FirmwareCandidate? Selected,
    SessionStatus? Status,
    DisplayGeometry? Geometry,
    long FrameCount,
    uint LastSequence,
    DateTime? StartedAt,
    string? Error,
    string? SessionDirectory);

/// <summary>
/// Single-session workflow behind the Workspace page: import an OTA, pick the device when it fits
/// several, then prepare → serve → start, poll status and frames, stop. UI-independent (events are
/// raised on worker threads) so the same code runs in the WinUI app, tests and console harnesses.
/// Counterpart of <c>SimulatorSessionModel.swift</c> + <c>StandaloneRuntimeModel.swift</c>.
/// </summary>
/// <remarks>
/// Frames are polled at ~60 Hz with at most one read in flight; missed deadlines are skipped, never
/// queued (SimulatorFrameCadence). Status is polled once a second. No boot timeout: the guest often
/// shows its first frame after ~6 s and its UI after a minute or more.
/// </remarks>
public sealed class SessionController : IAsyncDisposable
{
    public static readonly TimeSpan FrameInterval = TimeSpan.FromTicks(166_667);
    public static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(1);

    private readonly RuntimeHost _host;
    private readonly object _lock = new();
    private SessionPhase _phase = SessionPhase.Idle;
    private string? _firmwarePath;
    private IReadOnlyList<FirmwareCandidate> _candidates = [];
    private FirmwareCandidate? _selected;
    private SessionStatus? _status;
    private string? _error;
    private long _frames;
    private DateTime? _startedAt;
    private SimulatorSession? _session;
    private CancellationTokenSource? _loops;
    private Task _loopTask = Task.CompletedTask;

    public SessionController(RuntimeHost host) => _host = host;

    /// <summary>Raised (on a worker thread) whenever <see cref="Snapshot"/> changes.</summary>
    public event Action<SessionSnapshot>? Changed;

    /// <summary>Raised (on the frame worker) for each new frame; handlers must return quickly.</summary>
    public event Action<SimulatorFrame>? FrameArrived;

    public RuntimeHost Host => _host;

    public SessionSnapshot Snapshot
    {
        get
        {
            lock (_lock)
            {
                return new SessionSnapshot(_phase, _firmwarePath, _candidates, _selected, _status,
                    _session?.Manifest.Geometry, _frames, _session?.Frames.LastSequence ?? 0, _startedAt, _error,
                    _session?.Prepared.SessionDirectory);
            }
        }
    }

    /// <summary>Imports an OTA package (idempotent) and lists the devices it can run as.</summary>
    public async Task ImportAsync(string firmwarePath, CancellationToken cancellation = default)
    {
        Transition(SessionPhase.Importing, allowed: [SessionPhase.Idle, SessionPhase.Ready], update: () =>
        {
            _firmwarePath = firmwarePath;
            _candidates = [];
            _selected = null;
            _error = null;
        });
        try
        {
            var result = await _host.ImportAsync(firmwarePath, cancellation: cancellation).ConfigureAwait(false);
            if (result.Candidates.Count == 0)
            {
                throw new SimulatorException(SimulatorErrorKind.Engine, "固件已导入，但没有可运行的机型");
            }
            Update(() =>
            {
                _candidates = result.Candidates;
                _selected = result.Candidates.Count == 1 ? result.Candidates[0] : null;
                _phase = SessionPhase.Ready;
            });
        }
        catch (Exception error)
        {
            Update(() =>
            {
                _phase = SessionPhase.Idle;
                _error = error.Message;
            });
        }
    }

    public void Select(FirmwareCandidate? candidate) => Update(() =>
    {
        if (candidate is null || _candidates.Contains(candidate))
        {
            _selected = candidate;
        }
    });

    /// <summary>Prepares a fresh session for the selected device, serves it and starts the guest.</summary>
    public async Task StartAsync(CancellationToken cancellation = default)
    {
        FirmwareCandidate candidate;
        lock (_lock)
        {
            if (_phase != SessionPhase.Ready || _selected is null)
            {
                _error = _phase == SessionPhase.Ready ? "请先选择设备型号" : _error;
                candidate = null!;
            }
            else
            {
                candidate = _selected;
            }
        }
        if (candidate is null)
        {
            Raise();
            return;
        }
        Transition(SessionPhase.Starting, allowed: [SessionPhase.Ready], update: () =>
        {
            _error = null;
            _status = null;
            _frames = 0;
            _startedAt = null;
        });
        SimulatorSession? session = null;
        try
        {
            var prepared = await _host.PrepareAsync(candidate.ProfileId, cancellation).ConfigureAwait(false);
            session = await SimulatorSession.LaunchAsync(_host, prepared, cancellation: cancellation).ConfigureAwait(false);
            Update(() => _session = session);
            var status = await session.StartAsync(cancellation).ConfigureAwait(false);
            if (!status.IsRunning)
            {
                throw new SimulatorException(SimulatorErrorKind.Remote, Describe(status) ?? "模拟器未能启动");
            }
            var loops = new CancellationTokenSource();
            Update(() =>
            {
                _status = status;
                _startedAt = DateTime.Now;
                _phase = SessionPhase.Running;
                _loops = loops;
            });
            _loopTask = Task.WhenAll(Task.Run(() => FrameLoopAsync(session, loops.Token), CancellationToken.None),
                Task.Run(() => StatusLoopAsync(session, loops.Token), CancellationToken.None));
        }
        catch (Exception error)
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            Update(() =>
            {
                _session = null;
                _phase = SessionPhase.Ready;
                _error = error.Message;
            });
        }
    }

    public async Task InputAsync(int x, int y, bool down)
    {
        var session = _session;
        if (session is null || _phase != SessionPhase.Running)
        {
            return;
        }
        try
        {
            await session.InputAsync(x, y, down).ConfigureAwait(false);
        }
        catch (SimulatorException error)
        {
            Update(() => _error = error.Message);
        }
    }

    /// <summary>Stops the guest and ends serve (and every process it started).</summary>
    public async Task StopAsync()
    {
        SimulatorSession? session;
        CancellationTokenSource? loops;
        lock (_lock)
        {
            if (_session is null || _phase == SessionPhase.Stopping)
            {
                return;
            }
            _phase = SessionPhase.Stopping;
            session = _session;
            loops = _loops;
        }
        Raise();
        await loops!.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        SessionStatus? final = null;
        string? error = null;
        try
        {
            final = await session.StopAsync().ConfigureAwait(false);
        }
        catch (SimulatorException failure)
        {
            error = failure.Message;
        }
        await session.DisposeAsync().ConfigureAwait(false);
        Finish(final, error);
    }

    private void Finish(SessionStatus? final, string? error)
    {
        Update(() =>
        {
            _loops?.Dispose();
            _loops = null;
            _session = null;
            _status = final ?? _status;
            _error = error ?? _error;
            _phase = SessionPhase.Ready;
        });
    }

    private async Task FrameLoopAsync(SimulatorSession session, CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(FrameInterval);
        while (await timer.WaitForNextTickAsync(cancellation).ConfigureAwait(false))
        {
            FrameReadResult result;
            try
            {
                result = session.Frames.Read();
            }
            catch (SimulatorException error)
            {
                Update(() => _error = error.Message);
                continue;
            }
            if (result.Status == FrameReadStatus.New)
            {
                Interlocked.Increment(ref _frames);
                FrameArrived?.Invoke(result.Frame!);
            }
        }
    }

    private async Task StatusLoopAsync(SimulatorSession session, CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(StatusInterval);
        int failures = 0;
        while (await timer.WaitForNextTickAsync(cancellation).ConfigureAwait(false))
        {
            SessionStatus status;
            try
            {
                status = await session.StatusAsync(cancellation).ConfigureAwait(false);
                failures = 0;
            }
            catch (SimulatorException error)
            {
                // A busy bridge can miss one deadline; a dead serve cannot answer at all.
                if (++failures < 5 && session.ServeRunning)
                {
                    continue;
                }
                await EndAfterExitAsync(session, null, error.Message).ConfigureAwait(false);
                return;
            }
            Update(() => _status = status);
            if (!status.IsRunning)
            {
                await EndAfterExitAsync(session, status, Describe(status) ?? "模拟器已停止").ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>The guest ended on its own (crash or exit): release serve and report why.</summary>
    private async Task EndAfterExitAsync(SimulatorSession session, SessionStatus? status, string reason)
    {
        lock (_lock)
        {
            if (_session != session || _phase != SessionPhase.Running)
            {
                return;
            }
            _phase = SessionPhase.Stopping;
        }
        Raise();
        _loops?.Cancel();
        await session.DisposeAsync().ConfigureAwait(false);
        Finish(status, reason);
    }

    private static string? Describe(SessionStatus status)
    {
        var parts = new[] { status.Error, status.FailureDetail, SessionStatus.DescribeExit(status.LastExit) is { } code ? $"退出码 {code}" : null }
            .Where(part => !string.IsNullOrEmpty(part)).ToArray();
        return parts.Length == 0 ? null : $"状态 {status.State}：" + string.Join("；", parts);
    }

    private void Transition(SessionPhase phase, SessionPhase[] allowed, Action update)
    {
        lock (_lock)
        {
            if (!allowed.Contains(_phase))
            {
                throw new InvalidOperationException($"Cannot go from {_phase} to {phase}");
            }
            update();
            _phase = phase;
        }
        Raise();
    }

    private void Update(Action update)
    {
        lock (_lock)
        {
            update();
        }
        Raise();
    }

    private void Raise() => Changed?.Invoke(Snapshot);

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
