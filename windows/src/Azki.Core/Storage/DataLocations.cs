using Azki.Core.Runtime;

namespace Azki.Core.Storage;

/// <summary>
/// Data directories. C# port of <c>SimulatorDataLocations.swift</c>:
/// <c>%LOCALAPPDATA%\AzkiSimulator\{Library,Sessions}</c> on Windows.
/// </summary>
public sealed record DataLocations(string Root)
{
    public const string EnvironmentVariable = "AZKI_DATA_ROOT";

    public string Library => Path.Combine(Root, "Library");
    public string Sessions => Path.Combine(Root, "Sessions");

    /// <summary>
    /// The default root. QEMU needs ASCII paths: when the profile path is not (for example a
    /// Chinese user name) the Windows fallback is <c>%SystemDrive%\AzkiSimulatorData</c>.
    /// </summary>
    public static DataLocations Default()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return new DataLocations(Path.GetFullPath(configured));
        }
        string root;
        if (OperatingSystem.IsWindows())
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AzkiSimulator");
            if (!EngineBundle.IsEngineSafePath(root))
            {
                var drive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
                root = Path.Combine(drive + Path.DirectorySeparatorChar, "AzkiSimulatorData");
            }
        }
        else
        {
            var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            root = Path.Combine(string.IsNullOrEmpty(data)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share") : data,
                "AzkiSimulator");
        }
        return new DataLocations(root);
    }

    /// <summary>
    /// Creates the root and the sessions directory (owner-only on Unix; the runtime rejects
    /// group/world access). The library directory is created by the runtime itself.
    /// </summary>
    public void Ensure()
    {
        foreach (var directory in new[] { Root, Sessions })
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
            }
            else
            {
                Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }
}
