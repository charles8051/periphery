// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Avalonia.Metadata;

// Single XAML namespace URI so consumers can write
// xmlns:cam="https://periphery.dev/camera-avalonia"
// instead of clr-namespace declarations for every internal namespace.
[assembly: XmlnsDefinition("https://periphery.dev/camera-avalonia", "Periphery.Camera.Avalonia")]
[assembly: XmlnsDefinition("https://periphery.dev/camera-avalonia", "Periphery.Camera.Avalonia.Controls")]
[assembly: XmlnsPrefix("https://periphery.dev/camera-avalonia", "cam")]
