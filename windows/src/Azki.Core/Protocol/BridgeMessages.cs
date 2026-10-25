using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Azki.Core.Protocol;

/// <summary>
/// Version 1 bridge request: one JSON line per connection, answered by one JSON line.
/// C# port of <c>SimulatorBridgeRequest</c> (SimulatorSessionProtocol.swift); the server is
/// <c>BridgeServer.serve</c>/<c>dispatch</c> in <c>bridge.py</c>.
/// </summary>
public sealed record BridgeRequest(string SessionId, string Command, JsonObject? Arguments = null)
{
    /// <summary>bridge.py <c>MAX_MESSAGE</c>; applies to the request and the response line.</summary>
    public const int MaximumBytes = 65_536;

    public byte[] Encode()
    {
        var message = new JsonObject
        {
            ["version"] = 1,
            ["id"] = SessionId,
            ["command"] = Command,
            ["arguments"] = Arguments?.DeepClone() ?? new JsonObject(),
        };
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        if (bytes.Length > MaximumBytes)
        {
            throw new SimulatorException(SimulatorErrorKind.Transport, "请求超过协议长度限制");
        }
        return bytes;
    }
}

/// <summary>Bridge reply: <c>{"ok": true, "result": ...}</c> or <c>{"ok": false, "error": {code, message}}</c>.</summary>
public sealed record BridgeResponse(bool Ok, JsonNode? Result, string? ErrorCode, string? ErrorMessage)
{
    public static BridgeResponse Decode(ReadOnlySpan<byte> line)
    {
        if (line.Length > BridgeRequest.MaximumBytes)
        {
            throw new SimulatorException(SimulatorErrorKind.Transport, "回复超过协议长度限制");
        }
        try
        {
            var node = JsonNode.Parse(line.ToArray()) as JsonObject
                ?? throw new SimulatorException(SimulatorErrorKind.Transport, "回复不是 JSON 对象");
            bool ok = node["ok"] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
            var error = node["error"] as JsonObject;
            return new BridgeResponse(ok, node["result"]?.DeepClone(),
                error?["code"]?.GetValue<string>(), error?["message"]?.GetValue<string>());
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            throw new SimulatorException(SimulatorErrorKind.Transport, "回复格式无效：" + error.Message);
        }
    }

    /// <summary>The result, or a <see cref="SimulatorException"/> carrying the bridge's code and message.</summary>
    public JsonNode? CheckedResult()
    {
        if (!Ok)
        {
            throw new SimulatorException(SimulatorErrorKind.Remote,
                ErrorMessage is null ? "本机桥接拒绝了请求" : $"{ErrorMessage} [{ErrorCode}]", ErrorCode);
        }
        return Result;
    }
}

internal static class JsonText
{
    public static string? String(JsonNode? node, string key) =>
        node is JsonObject obj && obj[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static int? Int(JsonNode? node, string key) =>
        node is JsonObject obj && obj[key] is JsonValue value && value.GetValueKind() == JsonValueKind.Number
            && value.TryGetValue<double>(out var number) ? (int)number : null;

    public static bool? Bool(JsonNode? node, string key) =>
        node is JsonObject obj && obj[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
}
