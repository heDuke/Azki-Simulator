using System.Collections.ObjectModel;
using Azki.Core.Frame;
using Azki.Core.Runtime;
using Azki.Core.Session;
using Azki.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;

namespace Azki.App.ViewModels;

/// <summary>
/// Workspace state for the UI thread. Wraps <see cref="SessionController"/> (Azki.Core) and marshals
/// its worker-thread events through the <see cref="DispatcherQueue"/>. Lives for the whole app run so a
/// session keeps going while other pages are shown.
/// </summary>
public sealed class WorkspaceViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcher;
    private SessionSnapshot? _snapshot;
    private SimulatorFrame? _pendingFrame;
    private int _framePosted;
    private int _moveInFlight;
    private string? _firmwarePath;

    public WorkspaceViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        Locations = DataLocations.Default();
        Bundle = EngineBundle.Locate();
        if (Bundle is not null)
        {
            Controller = new SessionController(new RuntimeHost(Bundle, Locations));
            Controller.Changed += snapshot => _dispatcher.TryEnqueue(() => Apply(snapshot));
            Controller.FrameArrived += OnFrame;
            _snapshot = Controller.Snapshot;
        }
    }

    public EngineBundle? Bundle { get; }
    public DataLocations Locations { get; }
    public SessionController? Controller { get; }

    /// <summary>Raised on the UI thread with the newest frame (older undisplayed frames are dropped).</summary>
    public event Action<SimulatorFrame>? FrameReady;

    public ObservableCollection<FirmwareCandidate> Candidates { get; } = [];

    public bool EngineMissing => Bundle is null;

    public string EngineMissingMessage =>
        "未找到模拟器引擎。请使用 CI 产物 AzkiSimulator-full-<架构>（其中 engine\\ 与 AzkiSimulator.exe 同级），"
        + $"或把 AzkiEngine-{EngineBundle.RuntimeIdentifier} 解压到程序目录下的 engine\\，"
        + $"也可以用环境变量 {EngineBundle.EnvironmentVariable} 指向引擎目录。已查找："
        + string.Join("；", EngineBundle.Candidates(null, AppContext.BaseDirectory).Take(4));

    public string EngineText => Bundle is null ? "引擎：未找到" : $"引擎：{Bundle.Root}　数据：{Locations.Root}";

    public string? FirmwarePath
    {
        get => _firmwarePath;
        set => SetProperty(ref _firmwarePath, value);
    }

    public SessionPhase Phase => _snapshot?.Phase ?? SessionPhase.Idle;

    public FirmwareCandidate? SelectedCandidate => _snapshot?.Selected;

    public bool CanImport => Controller is not null && Phase is SessionPhase.Idle or SessionPhase.Ready;
    public bool CanSelectModel => Phase == SessionPhase.Ready && Candidates.Count > 1;
    public bool CanStart => Phase == SessionPhase.Ready && SelectedCandidate is not null;
    public bool CanStop => Phase == SessionPhase.Running;
    public bool IsBusy => Phase is SessionPhase.Importing or SessionPhase.Starting or SessionPhase.Stopping;
    public bool IsRunning => Phase == SessionPhase.Running;

    public string? ErrorText => _snapshot?.Error;
    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public DisplayGeometry? Geometry => _snapshot?.Geometry ?? (SelectedCandidate is { Width: > 0 } c
        ? DisplayGeometry.Find(c.Width, c.Height) : null);

    public string StatusText
    {
        get
        {
            var snapshot = _snapshot;
            if (snapshot is null)
            {
                return "引擎不可用";
            }
            return snapshot.Phase switch
            {
                SessionPhase.Idle => "请选择固件包（OTA zip）",
                SessionPhase.Importing => "正在导入固件…",
                SessionPhase.Ready when snapshot.Selected is null => $"固件可用于 {snapshot.Candidates.Count} 个机型，请选择设备型号",
                SessionPhase.Ready => $"就绪：{snapshot.Selected!.DisplayName}"
                    + (snapshot.Status is { } last ? $"（上次运行：{last.State}）" : ""),
                SessionPhase.Starting => "正在准备会话并启动引擎…",
                SessionPhase.Stopping => "正在停止…",
                SessionPhase.Running => RunningText(snapshot),
                _ => snapshot.Phase.ToString(),
            };
        }
    }

    private static string RunningText(SessionSnapshot snapshot)
    {
        var elapsed = snapshot.StartedAt is { } started ? DateTime.Now - started : TimeSpan.Zero;
        var status = snapshot.Status;
        var frames = snapshot.FrameCount == 0
            ? "等待第一帧（固件开机可能需要 1 分钟以上）"
            : $"已收到 {snapshot.FrameCount} 帧（序号 {snapshot.LastSequence}）";
        return $"运行中 {elapsed:mm\\:ss} · 状态 {status?.State}/{status?.AdapterState} · {snapshot.Geometry} · {frames}";
    }

    public async Task ImportAsync(string path)
    {
        if (Controller is null)
        {
            return;
        }
        FirmwarePath = path;
        await Controller.ImportAsync(path);
    }

    public void Select(FirmwareCandidate? candidate) => Controller?.Select(candidate);

    public Task StartAsync() => Controller?.StartAsync() ?? Task.CompletedTask;

    public Task StopAsync() => Controller?.StopAsync() ?? Task.CompletedTask;

    /// <summary>
    /// Touch input. Presses and releases are always sent (in order); while one move is in flight
    /// further moves are dropped, so dragging never queues a backlog of requests.
    /// </summary>
    public async Task InputAsync(int x, int y, bool down, bool move = false)
    {
        if (Controller is null || (move && Interlocked.Exchange(ref _moveInFlight, 1) == 1))
        {
            return;
        }
        try
        {
            await Controller.InputAsync(x, y, down);
        }
        finally
        {
            if (move)
            {
                Interlocked.Exchange(ref _moveInFlight, 0);
            }
        }
    }

    /// <summary>Stops a running session (used when the window closes).</summary>
    public async Task ShutdownAsync()
    {
        if (Controller is not null)
        {
            await Controller.DisposeAsync();
        }
    }

    public bool HasSession => Phase is SessionPhase.Starting or SessionPhase.Running or SessionPhase.Stopping;

    /// <summary>Refreshes the time-based status text.</summary>
    public void Tick() => OnPropertyChanged(nameof(StatusText));

    private void OnFrame(SimulatorFrame frame)
    {
        Volatile.Write(ref _pendingFrame, frame);
        // One UI update in flight; a newer frame simply replaces the pending one.
        if (Interlocked.Exchange(ref _framePosted, 1) == 0)
        {
            _dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, () =>
            {
                Interlocked.Exchange(ref _framePosted, 0);
                var latest = Interlocked.Exchange(ref _pendingFrame, null);
                if (latest is not null)
                {
                    FrameReady?.Invoke(latest);
                }
            });
        }
    }

    private void Apply(SessionSnapshot snapshot)
    {
        _snapshot = snapshot;
        if (!Candidates.SequenceEqual(snapshot.Candidates))
        {
            Candidates.Clear();
            foreach (var candidate in snapshot.Candidates)
            {
                Candidates.Add(candidate);
            }
        }
        OnPropertyChanged(string.Empty);
    }
}
