using Azki.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Azki.App.Views;

/// <summary>M0 placeholder for <c>Sources/AzkiSimulator/UI/SimulatorSettingsView.swift</c>.</summary>
public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();

        var vm = new MainViewModel();
        EnvironmentText.Text =
            $"系统：{vm.OsVersionText}\n进程架构：{MainViewModel.ArchitectureText}\n运行时：{MainViewModel.FrameworkText}";
    }
}
