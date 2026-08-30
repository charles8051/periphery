using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Periphery;
using Periphery.Camera;

namespace Periphery.Examples.CameraAvalonia;

/// <summary>
/// Stage 2 of the Avalonia preview plan: the heavy lifting (session
/// lifecycle, capture loop, dispose, status binding) lives in
/// <c>Periphery.Camera.Avalonia.CameraPreview</c>. This window is now
/// a device picker bound to a control — that's the entire app.
/// </summary>
public partial class MainWindow : Window
{
    public ObservableCollection<DeviceInfo> Cameras { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DevicePicker.ItemsSource = Cameras;
        Loaded += async (_, _) => await RefreshDevicesAsync();
    }

    private async Task RefreshDevicesAsync()
    {
        try
        {
            var devices = await CameraDevice.EnumerateAsync().ConfigureAwait(true);
            Cameras.Clear();
            foreach (var d in devices) Cameras.Add(d);
        }
        catch
        {
            // Enumeration failures are surfaced via the picker being empty;
            // a real app would log or display the error.
        }
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e) =>
        await RefreshDevicesAsync();

    private void OnDisconnectClick(object? sender, RoutedEventArgs e)
    {
        // Setting the picker's selection to null cascades through the
        // ComboBox→Device binding and disconnects the preview.
        DevicePicker.SelectedItem = null;
    }
}
