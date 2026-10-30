using System.Text.Json.Nodes;
using Azki.Core.Protocol;
using Azki.Core.Runtime;

namespace Azki.Core.Session;

/// <summary>The bridge's <c>status</c> reply (fields shared by every backend).</summary>
public sealed record SessionStatus(string State, string? AdapterState, string? Error, string? FailureDetail,
    long? LastExit, int? Pid, string? StopReason, JsonNode? Raw)
{
    public bool IsRunning => State is "running" or "starting";

    /// <summary>
    /// The runner's exit code for display, or null while it has not exited. Windows NTSTATUS-style
    /// codes (0x80000000 and up) are shown in hex; negative values are POSIX signals.
    /// </summary>
    public static string? DescribeExit(long? code) => code switch
    {
        null => null,
        < 0 => $"信号 {-code}",
        0xC000013A => "0xC000013A（Ctrl+Break/Ctrl+C 停止）",
        0xC0000005 => "0xC0000005（访问冲突）",
        0xC0000409 => "0xC0000409（进程快速失败）",
        >= 0x80000000 and <= 0xFFFFFFFF => $"0x{code:X8}",
        _ => code.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>A short Chinese explanation of a bridge/adapter error code (null when unknown).</summary>
    public static string? Explain(string? code, string? detail = null) => code switch
    {
        null => null,
        "host_error" when detail?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true
            => "运行器与 QEMU 的控制连接（QMP，127.0.0.1 回环端口）超时",
        "host_error" => "运行器与 QEMU 的控制连接（QMP）出错",
        "core_exited" => "QEMU/运行器进程意外退出",
        "missing_result" => "运行器退出时没有写出 result.json",
        "invalid_result" => "运行器的 result.json 无法解析",
        "output_limit" => "运行输出超过限额或检查失败",
        "unsafe_output" => "运行目录出现不安全的文件",
        "invalid_control" => "运行器的 control.json 未能完整发布",
        "unsafe_control" => "运行器发布的控制端点不符合要求",
        "peer_identity" => "进程身份校验失败（运行器/QEMU 不属于本会话的进程组，或端点对端 PID 不符）",
        "identity_mismatch" => "运行器报告的身份与已批准的会话不符",
        "supervisor_timeout" => "达到会话的最长运行时间",
        "frame_copy_failed" or "frame_unavailable" or "invalid_frame" => "复制画面 frame.raw 失败",
        "isolation_unavailable" => "本机缺少运行器启动器",
        "cleanup_incomplete" or "native_cleanup_unconfirmed" => "进程组清理未确认",
        "runner_io_error" => "监视运行器时文件读写出错",
        "runner_internal_error" => "监视运行器时出现内部错误",
        "input_timeout" => "触摸输入在时限内没有得到确认",
        "guest_fault" => "固件触发了 CPU 异常",
        _ => null,
    };

    /// <summary>A .NET process exit code as Python reports it (Windows: the unsigned DWORD).</summary>
    public static long? ProcessExit(int? code) => code is not { } value ? null
        : OperatingSystem.IsWindows() ? (uint)value : value;

    public static SessionStatus From(JsonNode? node) => new(
        JsonText.String(node, "state") ?? "unknown",
        JsonText.String(node, "adapterState"),
        JsonText.String(node, "error"),
        JsonText.String(node, "failureDetail"),
        JsonText.Long(node, "lastExit"),
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
    public long? ServeExitCode => SessionStatus.ProcessExit(_serve.ExitCode);
    public int? ActiveProcessCount => _serve.ActiveProcessCount;

    /// <summary>How long serve may take to publish control.sock (cold start: Python, imports, Defender scans).</summary>
    public static readonly TimeSpan DefaultEndpointTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Reply deadline of <c>start</c>: launching the runner, not waiting for the guest UI.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Reply deadline of <c>stop</c>: the runner's bounded stop plus process-group cleanup.</summary>
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Starts <c>serve</c> and waits (up to <paramref name="endpointTimeout"/>, default 120 s) for its control endpoint.</summary>
    public static async Task<SimulatorSession> LaunchAsync(RuntimeHost host, PreparedSession prepared,
        TimeSpan? endpointTimeout = null, CancellationToken cancellation = default)
    {
        var serve = host.Serve(prepared);
        try
        {
            var control = Path.Combine(prepared.SessionDirectory, "control.sock");
            var log = Path.Combine(prepared.Root, "serve.log");
            var limit = endpointTimeout ?? DefaultEndpointTimeout;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!File.Exists(control))  // also true for an AF_UNIX socket
            {
                if (serve.HasExited)
                {
                    throw new SimulatorException(SimulatorErrorKind.Engine,
                        $"引擎 serve 启动后 {clock.Elapsed.TotalSeconds:0.0} 秒就退出了（退出码 {SessionStatus.DescribeExit(SessionStatus.ProcessExit(serve.ExitCode))}），"
                        + $"未发布控制端点。serve.log 末尾：{Brief(log)}");
                }
                if (clock.Elapsed > limit)
                {
                    throw new SimulatorException(SimulatorErrorKind.Engine,
                        $"等待引擎 serve 发布控制端点 control.sock 超时（已等待 {clock.Elapsed.TotalSeconds:0} 秒）。"
                        + $"serve.log 末尾：{Brief(log)}");
                }
                await Task.Delay(100, cancellation).ConfigureAwait(false);
            }
            SessionConnection connection;
            try
            {
                connection = SessionConnection.Open(prepared.SessionDirectory);
            }
            catch (SimulatorException error)
            {
                throw new SimulatorException(error.Kind, "读取会话描述 session.json 失败：" + error.Message, error.Code, error);
            }
            return new SimulatorSession(prepared, serve, connection);
        }
        catch
        {
            serve.Dispose();
            throw;
        }
    }

    private static string Brief(string log)
    {
        var tail = Diagnostics.LogTail.Brief(log, 4);
        return tail.Length == 0 ? "（空）" : tail;
    }

    /// <summary>
    /// Reply deadline of <c>status</c>. The bridge answers serially and the adapter holds its lock while it
    /// stops a failed runner (up to ~10 s), so this is longer than bridge.request's 10 s.
    /// </summary>
    public static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(20);

    public async Task<SessionStatus> StatusAsync(CancellationToken cancellation = default) =>
        SessionStatus.From(await Connection.CallAsync("status", timeout: StatusTimeout, cancellation: cancellation).ConfigureAwait(false));

    /// <summary>Starts the guest. The reply comes once the core is launched, long before the guest UI.</summary>
    public async Task<SessionStatus> StartAsync(CancellationToken cancellation = default)
    {
        Frames.Reset();
        return SessionStatus.From(await Connection.CallAsync("start", timeout: StartTimeout,
            cancellation: cancellation).ConfigureAwait(false));
    }

    public async Task InputAsync(int x, int y, bool down, CancellationToken cancellation = default) =>
        await Connection.CallAsync("input", new JsonObject { ["x"] = x, ["y"] = y, ["down"] = down },
            cancellation: cancellation).ConfigureAwait(false);

    /// <summary>Stops the guest through the bridge (the runner reaps QEMU and its process group).</summary>
    public async Task<SessionStatus> StopAsync(CancellationToken cancellation = default) =>
        SessionStatus.From(await Connection.CallAsync("stop", timeout: StopTimeout,
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
