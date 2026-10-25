using Azki.Core.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Azki.App.ViewModels;

/// <summary>
/// Shell view model. Holds the one-time Windows build warning (decision #9: warn, never block).
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private bool _isOsWarningOpen;

    public MainViewModel()
        : this(WindowsBuildRequirement.CurrentOsVersion())
    {
    }

    public MainViewModel(Version osVersion)
    {
        OsVersion = osVersion;
        OsWarningMessage = WindowsBuildRequirement.WarningMessage(osVersion) ?? string.Empty;
        _isOsWarningOpen = WindowsBuildRequirement.ShouldWarn(osVersion);
        DismissOsWarningCommand = new RelayCommand(() => IsOsWarningOpen = false);
    }

    public Version OsVersion { get; }

    public string OsVersionText => $"Windows {OsVersion}";

    public string OsWarningTitle { get; } = "系统版本低于推荐版本";

    public string OsWarningMessage { get; }

    /// <summary>Shown once per launch; closing it hides it for the rest of the session.</summary>
    public bool IsOsWarningOpen
    {
        get => _isOsWarningOpen;
        set => SetProperty(ref _isOsWarningOpen, value);
    }

    public IRelayCommand DismissOsWarningCommand { get; }

    public static string ArchitectureText =>
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();

    public static string FrameworkText =>
        System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
}
