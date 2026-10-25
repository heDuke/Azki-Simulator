using Microsoft.UI.Xaml;

namespace Azki.App;

/// <summary>
/// Application entry point. Counterpart of <c>Sources/AzkiSimulator/AzkiSimulatorApp.swift</c>.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    /// <summary>The main window, once launched.</summary>
    public static Window? MainWindow { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        MainWindow = _window;
        _window.Activate();
    }
}
