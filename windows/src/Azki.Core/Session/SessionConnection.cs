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
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(timeout ?? DefaultTimeout(command));
            try
            {
                using var socket = await ConnectAsync(deadline.Token).ConfigureAwait(false);
                await socket.SendAsync(payload, SocketFlags.None, deadline.Token).ConfigureAwait(false);
                var line = await ReceiveLineAsync(socket, deadline.Token).ConfigureAwait(false);
                return BridgeResponse.Decode(line);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                throw new SimulatorException(SimulatorErrorKind.Transport, $"本机连接失败：{command} 超时");
            }
            catch (SocketException error)
            {
                throw new SimulatorException(SimulatorErrorKind.Disconnected, "模拟器会话已断开：" + error.Message, inner: error);
            }
            catch (IOException error)
            {
                throw new SimulatorException(SimulatorErrorKind.Disconnected, "模拟器会话已断开：" + error.Message, inner: error);
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
        (await SendAsync(command, arguments, timeout, cancellation).ConfigureAwait(false)).CheckedResult();

    private async Task<Socket> ConnectAsync(CancellationToken cancellation)
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
                throw new SimulatorException(SimulatorErrorKind.Disconnected, "模拟器会话已断开：控制端点不存在");
            }
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, endpoint.Port), cancellation).ConfigureAwait(false);
                if (endpoint.Token is not null)
                {
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
        var unix = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await unix.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellation).ConfigureAwait(false);
            return unix;
        }
        catch
        {
            unix.Dispose();
            throw;
        }
    }

    private static async Task<byte[]> ReceiveLineAsync(Socket socket, CancellationToken cancellation)
    {
        var buffer = new byte[BridgeRequest.MaximumBytes + 1];
        int length = 0;
        while (true)
        {
            int read = await socket.ReceiveAsync(buffer.AsMemory(length), SocketFlags.None, cancellation).ConfigureAwait(false);
            if (read == 0)
            {
                throw new SimulatorException(SimulatorErrorKind.Disconnected, "模拟器会话已断开：桥接未回复即关闭连接");
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
                throw new SimulatorException(SimulatorErrorKind.Transport, "本机连接失败：回复超过协议长度限制");
            }
        }
    }

    public void Dispose() => _gate.Dispose();
}
