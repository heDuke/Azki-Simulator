namespace Azki.Core.Platform;

/// <summary>
/// Windows version policy for Azki Simulator.
/// The supported baseline is Windows 11 26H2 (OS build 26300). Older Windows 11 builds are
/// <b>not</b> blocked: the app shows a single warning at startup and keeps running
/// (owner decision 2026-10-25). The manifest/TargetPlatformMinVersion stays at 10.0.26100.0
/// because there is no 26300-series Windows SDK.
/// </summary>
public static class WindowsBuildRequirement
{
    /// <summary>First Windows 11 26H2 build.</summary>
    public const int RecommendedMinimumBuild = 26300;

    /// <summary>Windows 11 builds start at 22000; anything below is Windows 10 or older.</summary>
    public const int Windows11FirstBuild = 22000;

    /// <summary>Returns the status for a Windows version (major.minor.build).</summary>
    public static WindowsBuildStatus Evaluate(Version osVersion)
    {
        ArgumentNullException.ThrowIfNull(osVersion);

        if (osVersion.Major < 10)
        {
            return WindowsBuildStatus.BelowWindows11;
        }

        if (osVersion.Major == 10 && osVersion.Build < Windows11FirstBuild)
        {
            return WindowsBuildStatus.BelowWindows11;
        }

        if (osVersion.Major == 10 && osVersion.Build < RecommendedMinimumBuild)
        {
            return WindowsBuildStatus.OlderThanRecommended;
        }

        return WindowsBuildStatus.Supported;
    }

    /// <summary>True when the startup warning should be shown (never blocks startup).</summary>
    public static bool ShouldWarn(Version osVersion) => Evaluate(osVersion) != WindowsBuildStatus.Supported;

    /// <summary>
    /// Current OS version. On .NET 5+ <see cref="Environment.OSVersion"/> reports the real
    /// Windows build without needing a compatibility manifest entry.
    /// </summary>
    public static Version CurrentOsVersion() => Environment.OSVersion.Version;

    /// <summary>Chinese user-facing warning text, or <c>null</c> when no warning is needed.</summary>
    public static string? WarningMessage(Version osVersion) => Evaluate(osVersion) switch
    {
        WindowsBuildStatus.Supported => null,
        WindowsBuildStatus.BelowWindows11 =>
            $"当前系统版本为 {osVersion}，Azki Simulator 仅面向 Windows 11 26H2（build {RecommendedMinimumBuild}）及以上开发和测试。程序会继续运行，但部分功能可能异常。",
        _ =>
            $"当前系统 build {osVersion.Build} 低于 Windows 11 26H2（build {RecommendedMinimumBuild}）。程序会继续运行，但未在此版本上测试，部分功能可能异常。",
    };
}

public enum WindowsBuildStatus
{
    Supported,
    OlderThanRecommended,
    BelowWindows11,
}
