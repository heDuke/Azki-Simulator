using System.Diagnostics;
using System.Text.Json.Nodes;
using Azki.Core.Protocol;
using Azki.Core.Storage;

namespace Azki.Core.Runtime;

/// <summary>One firmware build an imported OTA can run as (an <c>items[]</c> entry of library-import).</summary>
public sealed record FirmwareCandidate(string ProfileId, string DisplayName, string Model, string? FirmwareVersion,
    int Width, int Height, bool Ready, string? Reason)
{
    public override string ToString() => DisplayName;

    internal static FirmwareCandidate From(JsonNode item)
    {
        var availability = item["availability"];
        return new FirmwareCandidate(
            JsonText.String(item, "id") ?? throw new SimulatorException(SimulatorErrorKind.Engine, "固件条目缺少 id"),
            JsonText.String(item, "displayName") ?? JsonText.String(item, "id")!,
            JsonText.String(item, "model") ?? "",
            JsonText.String(item, "firmwareVersion"),
            JsonText.Int(item, "width") ?? 0,
            JsonText.Int(item, "height") ?? 0,
            JsonText.Bool(availability, "ready") ?? true,
            JsonText.String(availability, "reason"));
    }
}

public sealed record ImportResult(string Sha256, IReadOnlyList<FirmwareCandidate> Candidates, bool NeedsSelection);

/// <summary>A prepared, not yet served session (<c>prepare-bundled</c>).</summary>
/// <remarks><paramref name="IdentityDigest"/> is null for sessions that need no execution approval.</remarks>
public sealed record PreparedSession(string Root, string SessionDirectory, string? ProfileId, string? IdentityDigest);

/// <summary>
/// Drives the bundled Python runtime (<c>Runtime/azki-simulator.py</c>), the same commands
/// <c>windows/engine/azki-engine.py</c> uses: library-import → prepare-bundled → serve.
/// C# port of the launch half of <c>StandaloneRuntimeModel.swift</c>.
/// </summary>
public sealed class RuntimeHost
{
    public RuntimeHost(EngineBundle bundle, DataLocations locations)
    {
        Bundle = bundle;
        Locations = locations;
    }

    public EngineBundle Bundle { get; }
    public DataLocations Locations { get; }

    /// <summary>Isolated, no site, no bytecode (bundled_runtime requires -I -S -B), UTF-8 stdio.</summary>
    public ProcessStartInfo PythonStartInfo(params string[] arguments)
    {
        var info = new ProcessStartInfo(Bundle.Python) { WorkingDirectory = Bundle.Root };
        foreach (var argument in new[] { "-I", "-S", "-B", "-X", "utf8", Bundle.Cli }.Concat(arguments))
        {
            info.ArgumentList.Add(argument);
        }
        return info;
    }

    /// <summary>Runs one CLI command and returns its JSON output (throws on <c>ok: false</c>).</summary>
    public async Task<JsonObject> RunJsonAsync(TimeSpan timeout, CancellationToken cancellation, params string[] arguments)
    {
        var errors = new System.Text.StringBuilder();
        using var process = JobObjectLauncher.Start(PythonStartInfo(arguments), line =>
        {
            lock (errors) { if (errors.Length < 16_384) { errors.Append(line).Append('\n'); } }
        });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        int exit;
        try
        {
            exit = await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new SimulatorException(SimulatorErrorKind.Engine, $"引擎命令 {arguments[0]} 超时（{timeout.TotalSeconds:0} 秒）");
        }
        // Process.WaitForExitAsync also waits for the redirected streams to drain.
        var text = process.StandardOutput.Trim();
        JsonObject? result = null;
        try
        {
            result = JsonNode.Parse(text) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
        }
        if (result is null)
        {
            string tail;
            lock (errors) { tail = errors.ToString(); }
            throw new SimulatorException(SimulatorErrorKind.Engine,
                $"引擎命令 {arguments[0]} 失败（退出码 {exit}）：{Tail(text + "\n" + tail)}");
        }
        if (JsonText.Bool(result, "ok") == false)
        {
            var error = result["error"];
            throw new SimulatorException(SimulatorErrorKind.Engine,
                JsonText.String(error, "message") ?? $"引擎命令 {arguments[0]} 失败", JsonText.String(error, "code"));
        }
        return result;
    }

    public async Task<JsonObject> CatalogAsync(CancellationToken cancellation = default) =>
        await RunJsonAsync(TimeSpan.FromMinutes(2), cancellation, "library-catalog",
            "--engine-root", Bundle.EngineRoot, "--library-root", Locations.Library).ConfigureAwait(false);

    /// <summary>Imports an OTA zip (or firmware file) into the library; idempotent for the same file.</summary>
    public async Task<ImportResult> ImportAsync(string firmwarePath, string? model = null, CancellationToken cancellation = default)
    {
        var arguments = new List<string> { "library-import", "--engine-root", Bundle.EngineRoot,
            "--library-root", Locations.Library, "--input", Path.GetFullPath(firmwarePath) };
        if (!string.IsNullOrEmpty(model))
        {
            arguments.AddRange(["--model", model]);
        }
        Locations.Ensure();
        var result = await RunJsonAsync(TimeSpan.FromMinutes(10), cancellation, [.. arguments]).ConfigureAwait(false);
        var candidates = (result["items"] as JsonArray ?? []).OfType<JsonNode>().Select(FirmwareCandidate.From).ToList();
        return new ImportResult(JsonText.String(result, "sha256") ?? "", candidates,
            JsonText.Bool(result, "needsSelection") ?? candidates.Count > 1);
    }

    /// <summary>Creates a fresh session directory for <paramref name="profileId"/> under the sessions root.</summary>
    public async Task<PreparedSession> PrepareAsync(string profileId, CancellationToken cancellation = default)
    {
        Locations.Ensure();
        var root = Path.Combine(Locations.Sessions,
            $"s-{DateTime.Now:yyyyMMdd-HHmmss}-{Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3))}");
        if (!EngineBundle.IsEngineSafePath(root))
        {
            throw new SimulatorException(SimulatorErrorKind.Engine, "会话路径必须是不含逗号的 ASCII 路径：" + root);
        }
        var result = await RunJsonAsync(TimeSpan.FromMinutes(5), cancellation, "prepare-bundled",
            "--engine-root", Bundle.EngineRoot, "--library-root", Locations.Library,
            "--profile-id", profileId, "--session-root", root).ConfigureAwait(false);
        var digest = JsonText.String(result, "identityDigest")
            ?? throw new SimulatorException(SimulatorErrorKind.Engine, "prepare-bundled 未返回 identityDigest");
        return new PreparedSession(root, JsonText.String(result, "directory") ?? Path.Combine(root, "session"), profileId, digest);
    }

    /// <summary>
    /// Starts <c>serve</c> for a prepared session inside a kill-on-close job; its output goes to
    /// <c>&lt;root&gt;/serve.log</c>.
    /// </summary>
    public SupervisedProcess Serve(PreparedSession prepared)
    {
        var log = new StreamWriter(new FileStream(Path.Combine(prepared.Root, "serve.log"), FileMode.Append,
            FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true };
        try
        {
            string[] arguments = prepared.IdentityDigest is null
                ? ["serve", prepared.SessionDirectory, "--no-discovery"]
                : ["serve", prepared.SessionDirectory, "--no-discovery", "--approved-identity-sha256", prepared.IdentityDigest];
            return JobObjectLauncher.Start(PythonStartInfo(arguments), line =>
                {
                    lock (log)
                    {
                        try { log.WriteLine(line); } catch (ObjectDisposedException) { }
                    }
                }, new LockedDisposable(log));
        }
        catch
        {
            log.Dispose();
            throw;
        }
    }

    private static string Tail(string text)
    {
        text = text.Trim();
        return text.Length <= 1200 ? text : "…" + text[^1200..];
    }

    /// <summary>Disposes the serve log under the same lock its writer callback takes.</summary>
    private sealed class LockedDisposable(StreamWriter writer) : IDisposable
    {
        public void Dispose()
        {
            lock (writer)
            {
                writer.Dispose();
            }
        }
    }
}
