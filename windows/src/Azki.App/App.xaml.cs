using Azki.App.ViewModels;
using Microsoft.UI.Dispatching;
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

    /// <summary>The workspace (one simulator session); outlives page navigation.</summary>
    public static WorkspaceViewModel Workspace { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Before the window: its first page binds to the workspace.
        Workspace = new WorkspaceViewModel(DispatcherQueue.GetForCurrentThread());
        _window = new MainWindow();
        MainWindow = _window;
        _window.Activate();
    }
}
