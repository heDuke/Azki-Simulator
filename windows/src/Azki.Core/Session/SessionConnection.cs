using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Azki.Core.Protocol;

namespace Azki.Core.Session;

/// <summary>
/// Control connection to one <c>azki-simulator.py serve</c> bridge.
/// C# port of the request half of <c>SimulatorSessionConnection.swift</c>.
/// </summary>
/// <remarks>
/// Each request uses a fresh connection (the bridge answers one line per connection, serially).
/// Transport: when <c>control.sock</c> is an endpoint record (always on Windows) the bridge is
/// reached over loopback TCP with the per-serve token; otherwise (macOS/Linux) it is an AF_UNIX
/// socket, whose peer check (same uid) happens on the Python side. Requests are serialised.
/// </remarks>
public sealed class SessionConnection : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SessionConnection(string sessionDirectory, SessionManifest manifest)
    {
        SessionDirectory = sessionDirectory;
        Manifest = manifest;
    }

    public string SessionDirectory { get; }
    public SessionManifest Manifest { get; }
    public string ControlPath => Path.Combine(SessionDirectory, Manifest.Socket);

    /// <summary>Default reply deadlines of <c>bridge.request</c>: 20 s for start/stop, 10 s otherwise.</summary>
    public static TimeSpan DefaultTimeout(string command) =>
        command is "start" or "stop" ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(10);

    public static SessionConnection Open(string sessionDirectory) =>
        new(sessionDirectory, SessionManifest.Load(sessionDirectory));

    public async Task<BridgeResponse> SendAsync(string command, JsonObject? arguments = null,
        TimeSpan? timeout = null, CancellationToken cancellation = default)
    {
        var payload = new BridgeRequest(Manifest.Id, command, arguments).Encode();
        var limit = timeout ?? DefaultTimeout(command);
        var queued = Stopwatch.StartNew();
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        var exchange = new Exchange(command, limit);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(limit);
            try
            {
                using var socket = await ConnectAsync(exchange, deadline.Token).ConfigureAwait(false);
                exchange.Step = "发送请求";
                await socket.SendAsync(payload, SocketFlags.None, deadline.Token).ConfigureAwait(false);
                exchange.Step = "等待回复";
                var line = await ReceiveLineAsync(socket, exchange, deadline.Token).ConfigureAwait(false);
                return BridgeResponse.Decode(line);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                throw new SimulatorException(SimulatorErrorKind.Transport, exchange.Describe(
                    exchange.Step == "等待回复"
                        ? "引擎在时限内没有回复（serve 正忙或卡住）"
                        : "超时") + (queued.Elapsed - exchange.Elapsed > TimeSpan.FromSeconds(1)
                            ? $"；此前排队等待其他请求 {(queued.Elapsed - exchange.Elapsed).TotalSeconds:0.0} 秒" : ""));
            }
            catch (SocketException error)
            {
                throw new SimulatorException(SimulatorErrorKind.Disconnected,
                    exchange.Describe(SocketReason(error)), inner: error);
            }
            catch (IOException error)
            {
                throw new SimulatorException(SimulatorErrorKind.Disconnected,
                    exchange.Describe("连接中断：" + error.Message), inner: error);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Sends a command and returns its checked result (throws on <c>ok: false</c>).</summary>
    public async Task<JsonNode?> CallAsync(string command, JsonObject? arguments = null,
        TimeSpan? timeout = null, CancellationToken cancellation = default) =>
        (await SendAsync(command, arguments, timeout, cancellation).ConfigureAwait(false)).CheckedResult(command);

    private static string SocketReason(SocketException error) => error.SocketErrorCode switch
    {
        SocketError.ConnectionRefused => "连接被拒绝（serve 已退出或未在监听）",
        SocketError.ConnectionReset or SocketError.ConnectionAborted => "连接被引擎重置",
        SocketError.TimedOut => "TCP 连接超时",
        SocketError.AccessDenied => "连接被系统拒绝（防火墙或安全软件？）",
        _ => $"套接字错误 {error.SocketErrorCode}：{error.Message}",
    };

    /// <summary>What one request was doing, for errors that name the step and the time spent.</summary>
    private sealed class Exchange(string command, TimeSpan limit)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public string Step { get; set; } = "读取控制端点文件 control.sock";
        public string? Target { get; set; }
        public TimeSpan Elapsed => _clock.Elapsed;

        public string Describe(string reason) =>
            $"本机连接失败：命令 {command}，步骤「{Step}」{(Target is null ? "" : "（" + Target + "）")}：{reason}"
            + $"（已用 {Elapsed.TotalSeconds:0.0} 秒，时限 {limit.TotalSeconds:0} 秒）";
    }

    private async Task<Socket> ConnectAsync(Exchange exchange, CancellationToken cancellation)
    {
        var path = ControlPath;
        if (OperatingSystem.IsWindows() || ControlEndpoint.IsEndpointFile(path))
        {
            ControlEndpoint endpoint;
            try
            {
                endpoint = ControlEndpoint.Load(path);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new SimulatorException(SimulatorErrorKind.Disconnected,
                    exchange.Describe("控制端点文件不存在（serve 已退出？）：" + path));
            }
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                exchange.Step = "TCP 连接";
                exchange.Target = $"127.0.0.1:{endpoint.Port}";
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, endpoint.Port), cancellation).ConfigureAwait(false);
                if (endpoint.Token is not null)
                {
                    exchange.Step = "发送令牌";
                    await socket.SendAsync(System.Text.Encoding.ASCII.GetBytes(endpoint.Token + "\n"), SocketFlags.None,
                        cancellation).ConfigureAwait(false);
                }
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        if (!File.Exists(path))
        {
            throw new SimulatorException(SimulatorErrorKind.Disconnected,
                exchange.Describe("控制端点文件不存在（serve 已退出？）：" + path));
        }
        var unix = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            exchange.Step = "连接 Unix 套接字";
            exchange.Target = path;
            await unix.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellation).ConfigureAwait(false);
            return unix;
        }
        catch
        {
            unix.Dispose();
            throw;
        }
    }

    private static async Task<byte[]> ReceiveLineAsync(Socket socket, Exchange exchange, CancellationToken cancellation)
    {
        var buffer = new byte[BridgeRequest.MaximumBytes + 1];
        int length = 0;
        while (true)
        {
            int read = await socket.ReceiveAsync(buffer.AsMemory(length), SocketFlags.None, cancellation).ConfigureAwait(false);
            if (read == 0)
            {
                throw new SimulatorException(SimulatorErrorKind.Disconnected, exchange.Describe(
                    "引擎未回复就关闭了连接（令牌不符，或 serve 正在退出）"));
            }
            int newline = Array.IndexOf(buffer, (byte)'\n', length, read);
            length += read;
            if (newline >= 0)
            {
                Debug.Assert(newline < length);
                return buffer[..(newline + 1)];
            }
            if (length >= buffer.Length)
            {
                throw new SimulatorException(SimulatorErrorKind.Transport, exchange.Describe("回复超过协议长度限制"));
            }
        }
    }

    public void Dispose() => _gate.Dispose();
}
