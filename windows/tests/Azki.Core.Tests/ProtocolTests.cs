using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Azki.Core.Protocol;
using Azki.Core.Session;

namespace Azki.Core.Tests;

public sealed class ProtocolTests
{
    private const string Id = "8f2cd3ed-62f8-4b7c-859f-78939782f420";

    internal static string ManifestJson(string id = Id, int width = 336, int height = 480, string socket = "control.sock") =>
        $$"""{"version": 1, "id": "{{id}}", "directory": "/x", "socket": "{{socket}}", "framebuffer": "frame.raw", "width": {{width}}, "height": {{height}}, "capabilities": ["status", "start", "stop", "input"], "profileID": "miwear.watch.p67cn@3.101.043@a4ce85647786"}""";

    [Fact]
    public void DecodesTheBridgeSessionDescriptor()
    {
        var manifest = SessionManifest.Decode(Encoding.UTF8.GetBytes(ManifestJson()));
        Assert.Equal(Id, manifest.Id);
        Assert.Equal((336, 480), (manifest.Geometry.Width, manifest.Geometry.Height));
        Assert.Equal("miwear.watch.p67cn@3.101.043@a4ce85647786", manifest.ProfileId);
        Assert.Contains("input", manifest.Capabilities);
    }

    [Theory]
    [InlineData(100, 100, "control.sock")]
    [InlineData(336, 480, "other.sock")]
    public void RejectsUnknownGeometryOrFileNames(int width, int height, string socket)
    {
        Assert.Throws<SimulatorException>(() =>
            SessionManifest.Decode(Encoding.UTF8.GetBytes(ManifestJson(width: width, height: height, socket: socket))));
    }

    [Fact]
    public void RequestIsOneVersionOneJsonLine()
    {
        var bytes = new BridgeRequest(Id, "input", new JsonObject { ["x"] = 1, ["y"] = 2, ["down"] = true }).Encode();
        Assert.Equal((byte)'\n', bytes[^1]);
        var node = JsonNode.Parse(bytes)!;
        Assert.Equal(1, (int)node["version"]!);
        Assert.Equal(Id, (string)node["id"]!);
        Assert.Equal("input", (string)node["command"]!);
        Assert.True((bool)node["arguments"]!["down"]!);
        Assert.Equal("{}", new JsonObject().ToJsonString());
        Assert.Equal("{}", JsonNode.Parse(new BridgeRequest(Id, "status").Encode())!["arguments"]!.ToJsonString());
    }

    [Fact]
    public void ResponseErrorsCarryTheBridgeCode()
    {
        var failure = BridgeResponse.Decode("""{"ok": false, "error": {"code": "unsafe_session", "message": "Run path is too long"}}"""u8);
        var error = Assert.Throws<SimulatorException>(() => failure.CheckedResult());
        Assert.Equal("unsafe_session", error.Code);
        Assert.Contains("Run path is too long", error.Message);
        var success = BridgeResponse.Decode("""{"ok": true, "result": {"state": "running"}}"""u8);
        Assert.Equal("running", SessionStatus.From(success.CheckedResult()).State);
    }

    [Fact]
    public void EndpointRecordMatchesWin32Compat()
    {
        var token = new string('a', 64);
        var endpoint = ControlEndpoint.Decode(Encoding.UTF8.GetBytes(
            $$"""{"schema": "azki.endpoint.v1", "transport": "tcp", "host": "127.0.0.1", "port": 50123, "token": "{{token}}", "pid": 4}"""));
        Assert.Equal(new ControlEndpoint(50123, token), endpoint);
        Assert.Null(ControlEndpoint.Decode("""{"schema": "azki.endpoint.v1", "transport": "tcp", "host": "127.0.0.1", "port": 9, "token": null}"""u8).Token);
        Assert.Throws<SimulatorException>(() => ControlEndpoint.Decode(
            """{"schema": "azki.endpoint.v1", "transport": "tcp", "host": "0.0.0.0", "port": 9, "token": null}"""u8));
        Assert.Throws<SimulatorException>(() => ControlEndpoint.Decode(
            """{"schema": "azki.endpoint.v1", "transport": "tcp", "host": "127.0.0.1", "port": 9, "token": "short"}"""u8));
    }

    [Fact]
    public async Task TalksToALoopbackBridgeWithItsToken()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "session.json"), ManifestJson());
        var token = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        File.WriteAllText(Path.Combine(directory.Path, "control.sock"),
            $$"""{"schema": "azki.endpoint.v1", "transport": "tcp", "host": "127.0.0.1", "port": {{port}}, "token": "{{token}}", "pid": 1}""" + "\n");

        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
            Assert.Equal(token, await reader.ReadLineAsync(TestContext.Current.CancellationToken));
            var request = JsonNode.Parse((await reader.ReadLineAsync(TestContext.Current.CancellationToken))!)!;
            Assert.Equal(Id, (string)request["id"]!);
            Assert.Equal("status", (string)request["command"]!);
            var reply = Encoding.UTF8.GetBytes("""{"ok": true, "result": {"state": "stopped", "adapterState": "ready", "lastExit": null}}""" + "\n");
            await client.GetStream().WriteAsync(reply, TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        using var connection = SessionConnection.Open(directory.Path);
        var status = SessionStatus.From(await connection.CallAsync("status", cancellation: TestContext.Current.CancellationToken));
        await server;
        Assert.Equal("stopped", status.State);
        Assert.Equal("ready", status.AdapterState);
        Assert.Null(status.LastExit);
    }

    [Theory]
    [InlineData("3221225786", 3221225786L, "0xC000013A（Ctrl+Break/Ctrl+C 停止）")]
    [InlineData("4294967295", 4294967295L, "0xFFFFFFFF")]
    [InlineData("2", 2L, "2")]
    [InlineData("-9", -9L, "信号 9")]
    public void WindowsExitCodesAreNotSaturated(string json, long expected, string text)
    {
        // Python reports a Windows exit code as the unsigned DWORD; reading it as int showed 2147483647.
        var status = SessionStatus.From(JsonNode.Parse("""{"state": "crashed", "lastExit": """ + json + "}"));
        Assert.Equal(expected, status.LastExit);
        Assert.Equal(text, SessionStatus.DescribeExit(status.LastExit));
    }

    [Fact]
    public void NoExitCodeWhileTheRunnerHasNotExited()
    {
        Assert.Null(SessionStatus.DescribeExit(SessionStatus.From(JsonNode.Parse("""{"state": "running", "lastExit": null}""")).LastExit));
        Assert.Null(SessionStatus.From(JsonNode.Parse("""{"state": "running"}""")).LastExit);
    }

    [Fact]
    public async Task ClosedWithoutReplyIsDisconnected()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "session.json"), ManifestJson());
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        File.WriteAllText(Path.Combine(directory.Path, "control.sock"),
            $$"""{"schema": "azki.endpoint.v1", "transport": "tcp", "host": "127.0.0.1", "port": {{port}}, "token": null, "pid": 1}""");
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            var buffer = new byte[1024];
            _ = await client.GetStream().ReadAsync(buffer, TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);
        using var connection = SessionConnection.Open(directory.Path);
        var error = await Assert.ThrowsAsync<SimulatorException>(() => connection.SendAsync("status", cancellation: TestContext.Current.CancellationToken));
        Assert.Equal(SimulatorErrorKind.Disconnected, error.Kind);
        await server;
    }
}
