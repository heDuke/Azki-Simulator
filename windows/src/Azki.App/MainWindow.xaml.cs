using Azki.App.ViewModels;
using Azki.App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Azki.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));

        NavView.SelectedItem = NavView.MenuItems[0];
        AppWindow.Closing += OnClosing;
    }

    private bool _shutdownDone;

    /// <summary>
    /// Stops a running simulator before the window goes away. Even without this (crash, Task Manager)
    /// nothing is left behind: the engine runs in kill-on-close Job Objects owned by this process.
    /// </summary>
    private async void OnClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_shutdownDone || !App.Workspace.HasSession)
        {
            return;
        }
        args.Cancel = true;
        Title = "Azki Simulator — 正在停止模拟器…";
        var stop = App.Workspace.ShutdownAsync();
        await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(20)));
        _shutdownDone = true;
        Close();
    }

    public MainViewModel ViewModel { get; } = new();

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var pageType = args.IsSettingsSelected
            ? typeof(SettingsPage)
            : (args.SelectedItem as NavigationViewItem)?.Tag switch
            {
                "instances" => typeof(InstanceManagerPage),
                _ => typeof(WorkspacePage),
            };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }
}
