using System.Text.Json;

namespace Azki.Core.Session;

/// <summary>
/// Loopback endpoint record that stands in for an AF_UNIX socket on Windows
/// (<c>win32_compat.write_endpoint</c>): <c>{"schema": "azki.endpoint.v1", "transport": "tcp",
/// "host": "127.0.0.1", "port": N, "token": "&lt;64 hex&gt;", "pid": N}</c>.
/// A client sends <c>token + "\n"</c> first; a wrong or missing token is closed without a reply.
/// </summary>
public sealed record ControlEndpoint(int Port, string? Token)
{
    public const string Schema = "azki.endpoint.v1";
    public const int TokenHexLength = 64;
    public const int MaximumBytes = 4096;

    public static ControlEndpoint Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaximumBytes)
        {
            throw Invalid("端点记录过长");
        }
        try
        {
            using var document = JsonDocument.Parse(data.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schema", out var schema) || schema.GetString() != Schema
                || !root.TryGetProperty("transport", out var transport) || transport.GetString() != "tcp"
                || !root.TryGetProperty("host", out var host) || host.GetString() != "127.0.0.1"
                || !root.TryGetProperty("port", out var port) || !port.TryGetInt32(out int number) || number is <= 0 or >= 65536)
            {
                throw Invalid("端点记录字段不匹配");
            }
            string? token = null;
            if (root.TryGetProperty("token", out var tokenValue) && tokenValue.ValueKind != JsonValueKind.Null)
            {
                token = tokenValue.GetString();
                if (token is null || token.Length != TokenHexLength || !token.All(Uri.IsHexDigit))
                {
                    throw Invalid("端点 token 格式无效");
                }
            }
            return new ControlEndpoint(number, token);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            throw Invalid("端点记录不是有效 JSON");
        }
    }

    /// <summary>True when <paramref name="path"/> is a regular file holding an endpoint record.</summary>
    public static bool IsEndpointFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var expected = "{\"schema\": \"azki.endpoint.v1\""u8;
            Span<byte> prefix = stackalloc byte[expected.Length];
            int read = stream.ReadAtLeast(prefix, prefix.Length, throwOnEndOfStream: false);
            return read == expected.Length && prefix.SequenceEqual(expected);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static ControlEndpoint Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[MaximumBytes + 1];
        int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return Decode(buffer.AsSpan(0, read));
    }

    private static SimulatorException Invalid(string detail) =>
        new(SimulatorErrorKind.Transport, "本机连接失败：" + detail);
}
