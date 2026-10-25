using System.Text.Json;
using Azki.Core.Frame;

namespace Azki.Core.Protocol;

/// <summary>
/// The session descriptor (<c>session.json</c>) written by <c>bridge.py</c>.
/// C# port of <c>SimulatorSessionManifest</c> in
/// <c>Sources/AzkiSimulator/Core/SimulatorSessionProtocol.swift</c>.
/// </summary>
/// <remarks>
/// The descriptor is identical on every platform: <c>socket</c> stays <c>control.sock</c>.
/// On Windows that file is a loopback endpoint record (port + token, see
/// <see cref="Session.ControlEndpoint"/>) instead of an AF_UNIX socket.
/// </remarks>
public sealed record SessionManifest(
    int Version,
    string Id,
    string Socket,
    string Framebuffer,
    DisplayGeometry Geometry,
    IReadOnlyList<string> Capabilities,
    string? ProfileId)
{
    public const string FileName = "session.json";
    public const int MaximumBytes = 16_384;

    public static SessionManifest Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaximumBytes)
        {
            throw new SimulatorException(SimulatorErrorKind.InvalidSession, "清单超过 16 KiB");
        }
        try
        {
            using var document = JsonDocument.Parse(data.ToArray());
            var root = document.RootElement;
            int version = root.GetProperty("version").GetInt32();
            string id = root.GetProperty("id").GetString() ?? "";
            string socket = root.GetProperty("socket").GetString() ?? "";
            string framebuffer = root.GetProperty("framebuffer").GetString() ?? "";
            int width = root.GetProperty("width").GetInt32();
            int height = root.GetProperty("height").GetInt32();
            var capabilities = root.GetProperty("capabilities").EnumerateArray().Select(item => item.GetString() ?? "").ToArray();
            string? profile = root.TryGetProperty("profileID", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            var geometry = DisplayGeometry.Find(width, height);
            if (version != 1 || !Guid.TryParse(id, out _) || socket != "control.sock" || framebuffer != "frame.raw"
                || geometry is null || capabilities.Length > 32 || capabilities.Any(item => item.Length is 0 or > 64))
            {
                throw new SimulatorException(SimulatorErrorKind.InvalidSession, "版本、小米手环屏幕尺寸或受限文件名不匹配");
            }
            return new SessionManifest(version, id, socket, framebuffer, geometry.Value, capabilities, profile);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new SimulatorException(SimulatorErrorKind.InvalidSession, "清单格式无效：" + error.Message);
        }
    }

    public static SessionManifest Load(string sessionDirectory)
    {
        var path = Path.Combine(sessionDirectory, FileName);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaximumBytes || info.LinkTarget is not null)
        {
            throw new SimulatorException(SimulatorErrorKind.InvalidSession, "缺少会话清单或清单不是普通文件");
        }
        return Decode(File.ReadAllBytes(path));
    }
}
