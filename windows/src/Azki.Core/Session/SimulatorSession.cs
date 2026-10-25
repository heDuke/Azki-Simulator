using System.Text.Json.Nodes;
using Azki.Core.Protocol;
using Azki.Core.Runtime;

namespace Azki.Core.Session;

/// <summary>The bridge's <c>status</c> reply (fields shared by every backend).</summary>
public sealed record SessionStatus(string State, string? AdapterState, string? Error, string? FailureDetail,
    int? LastExit, int? Pid, string? StopReason, JsonNode? Raw)
{
    public bool IsRunning => State is "running" or "starting";

    public static SessionStatus From(JsonNode? node) => new(
        JsonText.String(node, "state") ?? "unknown",
        JsonText.String(node, "adapterState"),
        JsonText.String(node, "error"),
        JsonText.String(node, "failureDetail"),
        JsonText.Int(node, "lastExit"),
        JsonText.Int(node, "pid"),
        JsonText.String(node, "stopReason"),
        node);
}

/// <summary>
/// One served session: the supervised <c>serve</c> process, its control connection and frame reader.
/// UI-independent counterpart of <c>SimulatorSessionModel.swift</c> + the serve half of
/// <c>StandaloneRuntimeModel.swift</c>.
/// </summary>
/// <remarks>
/// There are no boot timeouts here: a guest commonly needs a minute or more to reach its UI
/// (first frame ~6 s, watch face 20–60 s). Only individual requests have deadlines.
/// </remarks>
public sealed class SimulatorSession : IAsyncDisposable
{
    private readonly SupervisedProcess _serve;
    private bool _disposed;

    private SimulatorSession(PreparedSession prepared, SupervisedProcess serve, SessionConnection connection)
    {
        Prepared = prepared;
        _serve = serve;
        Connection = connection;
        Frames = new FrameReader(prepared.SessionDirectory, connection.Manifest.Geometry);
    }

    public PreparedSession Prepared { get; }
    public SessionConnection Connection { get; }
    public FrameReader Frames { get; }
    public SessionManifest Manifest => Connection.Manifest;
    public string ServeLogPath => Path.Combine(Prepared.Root, "serve.log");
    public bool ServeRunning => !_serve.HasExited;
    public int ServeProcessId => _serve.Id;
    public int? ActiveProcessCount => _serve.ActiveProcessCount;

    /// <summary>Starts <c>serve</c> and waits (up to <paramref name="endpointTimeout"/>, default 60 s) for its control endpoint.</summary>
    public static async Task<SimulatorSession> LaunchAsync(RuntimeHost host, PreparedSession prepared,
        TimeSpan? endpointTimeout = null, CancellationToken cancellation = default)
    {
        var serve = host.Serve(prepared);
        try
        {
            var control = Path.Combine(prepared.SessionDirectory, "control.sock");
            var deadline = DateTime.UtcNow + (endpointTimeout ?? TimeSpan.FromSeconds(60));
            while (!File.Exists(control))  // also true for an AF_UNIX socket
            {
                if (serve.HasExited)
                {
                    throw new SimulatorException(SimulatorErrorKind.Engine,
                        $"serve 提前退出（退出码 {serve.ExitCode}），见 {Path.Combine(prepared.Root, "serve.log")}");
                }
                if (DateTime.UtcNow > deadline)
                {
                    throw new SimulatorException(SimulatorErrorKind.Engine, "serve 未发布控制端点 control.sock");
                }
                await Task.Delay(100, cancellation).ConfigureAwait(false);
            }
            var connection = SessionConnection.Open(prepared.SessionDirectory);
            return new SimulatorSession(prepared, serve, connection);
        }
        catch
        {
            serve.Dispose();
            throw;
        }
    }

    public async Task<SessionStatus> StatusAsync(CancellationToken cancellation = default) =>
        SessionStatus.From(await Connection.CallAsync("status", cancellation: cancellation).ConfigureAwait(false));

    /// <summary>Starts the guest. The reply comes once the core is launched, long before the guest UI.</summary>
    public async Task<SessionStatus> StartAsync(CancellationToken cancellation = default)
    {
        Frames.Reset();
        return SessionStatus.From(await Connection.CallAsync("start", timeout: TimeSpan.FromSeconds(60),
            cancellation: cancellation).ConfigureAwait(false));
    }

    public async Task InputAsync(int x, int y, bool down, CancellationToken cancellation = default) =>
        await Connection.CallAsync("input", new JsonObject { ["x"] = x, ["y"] = y, ["down"] = down },
            cancellation: cancellation).ConfigureAwait(false);

    /// <summary>Stops the guest through the bridge (the runner reaps QEMU and its process group).</summary>
    public async Task<SessionStatus> StopAsync(CancellationToken cancellation = default) =>
        SessionStatus.From(await Connection.CallAsync("stop", timeout: TimeSpan.FromSeconds(45),
            cancellation: cancellation).ConfigureAwait(false));

    /// <summary>
    /// Stops the guest if it is still running, then ends <c>serve</c> and everything in its job.
    /// <c>serve</c> has no console the app could send CTRL_BREAK to, so after the bridge-level stop
    /// (which already reaped the core) the job is terminated.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            if (!_serve.HasExited)
            {
                var status = await StatusAsync().ConfigureAwait(false);
                if (status.IsRunning)
                {
                    await StopAsync().ConfigureAwait(false);
                }
            }
        }
        catch (SimulatorException)
        {
            // The job termination below still ends every process.
        }
        _serve.Dispose();
        Connection.Dispose();
    }
}
