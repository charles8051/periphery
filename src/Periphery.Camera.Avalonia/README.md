# Periphery.Camera.Avalonia

A live camera preview control for Avalonia. Bind `CameraPreview` to a `DeviceInfo`, and the control runs the capture loop, reconnects after an unplug, and marshals frames to the UI thread.

```bash
dotnet add package Periphery.Camera.Avalonia --prerelease
```

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:cam="https://periphery.dev/camera-avalonia">
  <Grid RowDefinitions="Auto,*,Auto">
    <ComboBox x:Name="DevicePicker" Grid.Row="0" .../>

    <cam:CameraPreview Grid.Row="1"
                       Name="Preview"
                       Device="{Binding ElementName=DevicePicker, Path=SelectedItem}"
                       MaxResolution="1280,720"/>

    <TextBlock Grid.Row="2"
               Text="{Binding ElementName=Preview, Path=StatusDescription}"/>
  </Grid>
</Window>
```

## Properties

| Property | Default | Purpose |
|---|---|---|
| `Device` | `null` | The camera to preview. `null` disconnects. |
| `MaxResolution` | `1280×720` | Largest resolution to open |
| `IsLive` (read-only) | `false` | `true` while frames are flowing |
| `StatusDescription` (read-only) | `"Idle."` | Status text for display |
| `LastError` (read-only) | `null` | Most recent open or capture error |

## Formats

The control opens the largest resolution, then the highest frame rate, among the formats it can display: BGRA32, RGBA32, MJPEG, NV12, and YUY2. A camera that offers none of these fails to open, and `LastError` names its formats.

Like `Periphery.Camera`, the control supports Windows and Linux.
