using System.Runtime.InteropServices;

namespace Azki.Core.Runtime;

/// <summary>
/// An <c>AzkiEngine-&lt;rid&gt;</c> bundle (see <c>windows/scripts/assemble-engine.py</c>):
/// <c>Engine/</c> (manifest, embeddable Python, QEMU), <c>Runtime/azki-simulator.py</c> and
/// <c>Firmware/catalog.json</c>. The runtime itself verifies every Engine file against its manifest.
/// </summary>
public sealed record EngineBundle(string Root)
{
    /// <summary>Environment override for the bundle directory (developer use and tests).</summary>
    public const string EnvironmentVariable = "AZKI_ENGINE_BUNDLE";

    public string EngineRoot => Path.Combine(Root, "Engine");
    public string RuntimeRoot => Path.Combine(Root, "Runtime");
    public string Cli => Path.Combine(RuntimeRoot, "azki-simulator.py");

    /// <summary><c>Engine/python/python.exe</c> on Windows, <c>Engine/python/bin/python3.12</c> elsewhere.</summary>
    public string Python => OperatingSystem.IsWindows()
        ? Path.Combine(EngineRoot, "python", "python.exe")
        : Path.Combine(EngineRoot, "python", "bin", "python3.12");

    public bool IsComplete =>
        File.Exists(Path.Combine(EngineRoot, "manifest.json")) && File.Exists(Python) && File.Exists(Cli);

    /// <summary>Directory names searched next to the app, in order.</summary>
    public static IReadOnlyList<string> CandidateNames { get; } =
        ["engine", "AzkiEngine-" + RuntimeIdentifier, "AzkiEngine"];

    public static string RuntimeIdentifier =>
        (OperatingSystem.IsWindows() ? "win-" : OperatingSystem.IsMacOS() ? "osx-" : "linux-")
        + (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64");

    /// <summary>
    /// Finds the bundle: an explicit path, then <see cref="EnvironmentVariable"/>, then
    /// <c>engine/</c> (or <c>AzkiEngine-&lt;rid&gt;</c>) beside the app or one directory above it.
    /// </summary>
    public static EngineBundle? Locate(string? configured = null, string? appDirectory = null)
    {
        foreach (var candidate in Candidates(configured, appDirectory ?? AppContext.BaseDirectory))
        {
            var bundle = new EngineBundle(Path.GetFullPath(candidate));
            if (bundle.IsComplete)
            {
                return bundle;
            }
        }
        return null;
    }

    public static IEnumerable<string> Candidates(string? configured, string appDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured;
        }
        var environment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environment))
        {
            yield return environment;
        }
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(appDirectory));
        foreach (var directory in parent is null ? [appDirectory] : new[] { appDirectory, parent })
        {
            foreach (var name in CandidateNames)
            {
                yield return Path.Combine(directory, name);
            }
        }
    }

    /// <summary>
    /// QEMU receives paths through the ANSI code page and the machine option syntax uses commas,
    /// so every path handed to the engine must be ASCII without commas (azki-engine.py check_ascii).
    /// </summary>
    public static bool IsEngineSafePath(string path) =>
        path.All(character => character is >= ' ' and < (char)0x7f && character != ',');
}
