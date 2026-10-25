using System.Runtime.InteropServices.WindowsRuntime;
using Azki.App.ViewModels;
using Azki.Core.Frame;
using Azki.Core.Runtime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;

namespace Azki.App.Views;

/// <summary>
/// Workspace: firmware picker, device model, start/stop, status and the live screen.
/// Counterpart of <c>SimulatorWorkspaceView.swift</c> + <c>SimulatorNativeCanvas.swift</c>.
/// </summary>
public sealed partial class WorkspacePage : Page
{
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private WriteableBitmap? _bitmap;
    private bool _pointerDown;

    public WorkspacePage()
    {
        InitializeComponent();
        ViewModel.FrameReady += Render;
        ViewModel.PropertyChanged += (_, _) => SyncModelSelection();
        _clock.Tick += (_, _) => { if (ViewModel.IsRunning) { ViewModel.Tick(); } };
        _clock.Start();
    }

    public WorkspaceViewModel ViewModel => App.Workspace;

    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add(".zip");
        picker.FileTypeFilter.Add(".bin");
        picker.FileTypeFilter.Add("*");
        // Unpackaged WinUI: pickers need the owner window handle.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await ViewModel.ImportAsync(file.Path);
        }
    }

    private async void OnFirmwareKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && !string.IsNullOrWhiteSpace(FirmwareBox.Text))
        {
            e.Handled = true;
            await ViewModel.ImportAsync(FirmwareBox.Text.Trim().Trim('"'));
        }
    }

    private void OnModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelBox.SelectedItem is FirmwareCandidate candidate && candidate != ViewModel.SelectedCandidate)
        {
            ViewModel.Select(candidate);
        }
    }

    private void SyncModelSelection()
    {
        if (!Equals(ModelBox.SelectedItem, ViewModel.SelectedCandidate))
        {
            ModelBox.SelectedItem = ViewModel.SelectedCandidate;
        }
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        _bitmap = null;
        Screen.Source = null;
        await ViewModel.StartAsync();
    }

    private async void OnStop(object sender, RoutedEventArgs e) => await ViewModel.StopAsync();

    private void Render(SimulatorFrame frame)
    {
        if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
        {
            _bitmap = new WriteableBitmap(frame.Width, frame.Height);
            Screen.Source = _bitmap;
            Screen.MaxWidth = frame.Width * 2;
            Screen.MaxHeight = frame.Height * 2;
        }
        using (var stream = _bitmap.PixelBuffer.AsStream())
        {
            stream.Write(frame.Bgra, 0, frame.Bgra.Length);
        }
        _bitmap.Invalidate();
    }

    // Preview of M3: pointer → guest touch (one finger).
    private void OnScreenPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Send(e, down: true))
        {
            _pointerDown = true;
            Screen.CapturePointer(e.Pointer);
        }
    }

    private void OnScreenPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerDown)
        {
            Send(e, down: true, move: true);
        }
    }

    private void OnScreenPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerDown)
        {
            _pointerDown = false;
            Send(e, down: false);
            Screen.ReleasePointerCapture(e.Pointer);
        }
    }

    private bool Send(PointerRoutedEventArgs e, bool down, bool move = false)
    {
        if (_bitmap is null || !ViewModel.IsRunning || Screen.ActualWidth <= 0 || Screen.ActualHeight <= 0)
        {
            return false;
        }
        var point = e.GetCurrentPoint(Screen).Position;
        int x = Math.Clamp((int)(point.X / Screen.ActualWidth * _bitmap.PixelWidth), 0, _bitmap.PixelWidth - 1);
        int y = Math.Clamp((int)(point.Y / Screen.ActualHeight * _bitmap.PixelHeight), 0, _bitmap.PixelHeight - 1);
        _ = ViewModel.InputAsync(x, y, down, move);
        e.Handled = true;
        return true;
    }
}
