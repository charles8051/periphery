---
title: "ADR-0007 — Niche-Platform Backend Feasibility Analysis"
status: "Informational"
status_note: "RFC"
date: "2025-07-15"
authors: ""
tags: ["architecture", "decision"]
supersedes: ""
superseded_by: ""
---

# ADR-0007 — Niche-Platform Backend Feasibility Analysis

---

## Context

Periphery currently targets `net8.0` / `net10.0` with a Windows provider (WMI / cfgmgr32) shipping and Linux / macOS providers planned. This document evaluates the feasibility of implementing `IDeviceProvider` / `IDeviceMonitorProvider` backends for **Android**, **tvOS**, **Browser (WASM)**, and a curated set of additional niche platforms.

The analysis is grounded in:

- The existing provider contract (`IDeviceProvider`, `IDeviceMonitorProvider`)
- The `DeviceInfo` record model and `DeviceCategory` enum
- The architectural constraints (discovery only, zero third-party runtime deps, async-first, immutable snapshots)
- Available .NET TFMs (`net8.0-android`, `net8.0-tvos`, `net8.0-browser`, `net8.0-tizen`, `net8.0-ios`)

---

## 1. Android (`net8.0-android`)

### 1.1 Feasibility: ✅ **High — strong candidate**

### 1.2 Platform APIs

| Category | Android API | .NET Binding | Discovery? | Monitoring? |
|----------|-------------|-------------|-----------|------------|
| **USB** | `android.hardware.usb.UsbManager` | `Android.Hardware.Usb.UsbManager` | ✅ `DeviceList` property returns all attached USB devices | ✅ `ACTION_USB_DEVICE_ATTACHED` / `DETACHED` broadcast intents |
| **Bluetooth** | `android.bluetooth.BluetoothManager` / `BluetoothAdapter` | `Android.Bluetooth.BluetoothManager` | ✅ `GetBondedDevices()` for paired; `StartDiscovery()` for scanning | ✅ `ACTION_FOUND`, `ACTION_ACL_CONNECTED` / `DISCONNECTED` broadcasts |
| **Network** | `android.net.ConnectivityManager`, `NetworkInterface` | `Android.Net.ConnectivityManager` + `System.Net.NetworkInformation` | ✅ Both Java and BCL APIs enumerate interfaces | ✅ `ConnectivityManager.NetworkCallback` |
| **Display** | `android.view.Display`, `DisplayManager` | `Android.Views.Display` | ✅ `DisplayManager.GetDisplays()` returns resolution, density, name | ✅ `DisplayListener` callbacks |
| **Audio** | `android.media.AudioManager`, `AudioDeviceInfo` | `Android.Media.AudioManager` | ✅ `GetDevices(GetDevicesTargets)` (API 23+) | ✅ `AudioDeviceCallback` (API 23+) |
| **Storage** | `android.os.storage.StorageManager` | `Android.OS.Storage.StorageManager` | 🟡 Scoped storage limits visibility; can enumerate mount points | 🟡 `ACTION_MEDIA_MOUNTED` / `REMOVED` |
| **HID** | `android.hardware.input.InputManager` | `Android.Hardware.Input.InputManager` | ✅ `GetInputDeviceIds()` | ✅ `InputDeviceListener` |
| **Sensors** | `android.hardware.SensorManager` | `Android.Hardware.SensorManager` | ✅ `GetSensorList(SensorType.All)` | ❌ No connect/disconnect events for built-in sensors |

### 1.3 `DeviceInfo` Mapping

| DeviceInfo Property | Android Source |
|---------------------|----------------|
| `Id` | USB: `UsbDevice.DeviceId`; BT: `BluetoothDevice.Address`; Input: `InputDevice.Id` |
| `Name` | `UsbDevice.DeviceName`, `BluetoothDevice.Name`, `InputDevice.Name` |
| `VendorId` / `ProductId` | `UsbDevice.VendorId` / `ProductId` (API 21+); `InputDevice.VendorId` / `ProductId` |
| `SerialNumber` | `UsbDevice.SerialNumber` (requires permission) |
| `IsConnected` | USB: device present in `DeviceList`; BT: `BluetoothProfile` connection state |
| `MacAddress` | `BluetoothDevice.Address` → `PhysicalAddress` |
| `BatteryChargePercent` | `BluetoothDevice` + `BatteryManager` extras (limited) |
| `DisplayResolution` | `Display.GetRealSize()` → `Size` |
| `BusType` | Infer from API used (USB, Bluetooth, etc.) |

### 1.4 Architecture Concerns

| Concern | Severity | Detail |
|---------|----------|--------|
| **Android Context required** | 🟡 Significant | Most system services require an `Android.Content.Context`. The provider will need a static `Activity` / `Application` reference or an initialization API. This breaks the current zero-config pattern. |
| **Permission model** | 🟡 Significant | USB host access requires `android.permission.USB_PERMISSION` (runtime prompt). Bluetooth scanning requires `BLUETOOTH_SCAN` (Android 12+). Location permission may be needed for BLE. |
| **BroadcastReceiver lifecycle** | 🟡 Significant | Monitor provider must register/unregister `BroadcastReceiver`s, which ties into the Activity lifecycle. Improper management → leaked receivers. |
| **No unified device tree** | 🟢 Low | Same issue as Linux/macOS — fan out across subsystems. Architecture already accommodates this. |
| **`System.Management` dependency** | 🟡 Significant | The current csproj unconditionally references `System.Management` (WMI). This must become conditional on Windows TFM to avoid Android build failures. |

### 1.5 Effort Estimate

| Component | Effort |
|-----------|--------|
| `AndroidDeviceProvider : IDeviceProvider` | Medium — multiple subsystem enumerators, context/permission plumbing |
| `AndroidDeviceMonitorProvider : IDeviceMonitorProvider` | Medium — BroadcastReceiver wiring, per-category event sources |
| Build/packaging changes | Low-Medium — add `net8.0-android` TFM, conditional `System.Management` reference |
| **Total** | **~3–4 weeks for a senior contributor** |

### 1.6 Verdict

Android is the strongest niche-platform candidate. The Android SDK provides rich, well-documented device enumeration and monitoring APIs with first-class .NET bindings via `Mono.Android.dll`. Every current `DeviceCategory` in the enum can be meaningfully supported. The main engineering challenge is the context/permission initialization story and lifecycle management.

---

## 2. tvOS (`net8.0-tvos`)

### 2.1 Feasibility: 🔴 **Low — not recommended**

### 2.2 Platform APIs

| Category | tvOS API | Available? | Notes |
|----------|----------|-----------|-------|
| **USB** | — | ❌ | tvOS has no USB Host API. Apple TV has a single USB-C port for power/diagnostics only. |
| **Bluetooth** | `CoreBluetooth` (BLE only) | 🟡 Partial | BLE scanning/discovery works. Classic Bluetooth not available. MFi game controllers use `GameController.framework`. |
| **Network** | `NWPathMonitor`, `SystemConfiguration` | ✅ | Network interface enumeration and monitoring work. `System.Net.NetworkInformation` also works. |
| **Display** | `UIScreen` | 🟡 Minimal | Returns the TV display resolution. Only one screen on Apple TV. |
| **Audio** | `AVAudioSession` | 🟡 Minimal | Can detect route changes (HDMI, AirPlay), but no fine-grained audio device enumeration. |
| **Storage** | — | ❌ | tvOS apps are sandboxed with no access to external storage. No removable media. |
| **HID** | `GameController.framework` | 🟡 Limited | Game controllers only (Siri Remote, MFi controllers, DualShock, Xbox). No generic HID. |
| **Sensors** | — | ❌ | Apple TV has no accelerometer, gyroscope, GPS, etc. |

### 2.3 Architecture Concerns

| Concern | Severity | Detail |
|---------|----------|--------|
| **Extremely limited device surface** | 🔴 Critical | An Apple TV typically has: 1 network interface, 1 display, 1 Bluetooth radio, 0–2 game controllers. Most `DeviceCategory` values return empty. |
| **No USB/HID/Storage** | 🔴 Critical | Three of the most-used categories are completely unsupported. Violates the "platform parity at the abstraction layer" principle. |
| **App Store restrictions** | 🟡 Significant | Apple restricts tvOS apps from using private APIs or accessing hardware in unsanctioned ways. IOKit is not available. |
| **Tiny audience** | 🟡 Significant | .NET tvOS apps are extremely rare. The MAUI ecosystem does not target tvOS. The `.NET for tvOS` workload exists but has minimal community adoption. |
| **Build/CI complexity** | 🟡 Significant | Building for tvOS requires Xcode on macOS. Adds a CI build target for minimal value. |

### 2.4 Effort Estimate

| Component | Effort |
|-----------|--------|
| `TvOsDeviceProvider` | Low (because so little is discoverable) |
| `TvOsDeviceMonitorProvider` | Low-Medium |
| Build/packaging changes | Medium — tvOS workload, Xcode dependency, CI |
| **Total** | **~1–2 weeks** (but marginal value) |

### 2.5 Verdict

**Not recommended.** tvOS is an appliance platform with a locked-down device model. Periphery's core value proposition — discovering and tracking diverse hardware peripherals — doesn't translate. A tvOS backend would support Bluetooth (BLE only), network, and display in a severely limited form. The effort-to-value ratio is poor. If game controller discovery is needed, a dedicated `GameController.framework` integration would be more appropriate as a separate library.

---

## 3. Browser / WASM (`net8.0-browser`)

### 3.1 Feasibility: 🟡 **Medium — possible with severe caveats**

### 3.2 Platform APIs (Web APIs via JS Interop)

| Category | Web API | Status | Discovery? | Monitoring? |
|----------|---------|--------|-----------|------------|
| **USB** | [WebUSB API](https://developer.mozilla.org/en-US/docs/Web/API/WebUSB_API) | 🟡 Chrome/Edge only | ✅ `navigator.usb.getDevices()` (after prior `requestDevice()` grant) | ✅ `connect` / `disconnect` events |
| **Bluetooth** | [Web Bluetooth API](https://developer.mozilla.org/en-US/docs/Web/API/Web_Bluetooth_API) | 🟡 Chrome/Edge only | 🟡 Only previously-granted devices via `getDevices()` | 🟡 `gattserverdisconnected` event |
| **HID** | [WebHID API](https://developer.mozilla.org/en-US/docs/Web/API/WebHID_API) | 🟡 Chrome/Edge only | ✅ `navigator.hid.getDevices()` (after prior `requestDevice()` grant) | ✅ `connect` / `disconnect` events |
| **Serial** | [Web Serial API](https://developer.mozilla.org/en-US/docs/Web/API/Web_Serial_API) | 🟡 Chrome/Edge only | ✅ `navigator.serial.getPorts()` (after prior `requestPort()` grant) | ✅ `connect` / `disconnect` events |
| **Gamepad** | [Gamepad API](https://developer.mozilla.org/en-US/docs/Web/API/Gamepad_API) | ✅ Wide support | ✅ `navigator.getGamepads()` | ✅ `gamepadconnected` / `disconnected` events |
| **Network** | [Network Information API](https://developer.mozilla.org/en-US/docs/Web/API/NetworkInformation) | 🟡 Limited | 🟡 Connection type only (wifi/cellular/etc), no interface list | 🟡 `change` event |
| **Display** | `screen` object | ✅ Wide support | ✅ `screen.width`, `screen.height` | 🟡 `resize` event (limited) |
| **Audio** | [Web Audio API](https://developer.mozilla.org/en-US/docs/Web/API/Web_Audio_API) / `enumerateDevices()` | ✅ Wide support | ✅ `navigator.mediaDevices.enumerateDevices()` returns audio input/output | ✅ `devicechange` event |
| **Storage** | [Storage API](https://developer.mozilla.org/en-US/docs/Web/API/Storage_API) | ✅ Wide support | ❌ No enumeration of physical drives | ❌ |

### 3.3 Architecture Concerns

| Concern | Severity | Detail |
|---------|----------|--------|
| **User-gesture permission model** | 🔴 Critical | WebUSB, WebBluetooth, WebHID, and Web Serial all require a **user gesture** (click) to call `requestDevice()` / `requestPort()`. Periphery's `Devices.FindAsync()` pattern assumes programmatic enumeration. The browser only returns devices the user has *previously granted* — there is no "enumerate all" capability. This is a fundamental semantic mismatch. |
| **Browser support fragmentation** | 🔴 Critical | WebUSB, WebBluetooth, WebHID, and Web Serial are **Chromium-only** (Chrome, Edge, Opera). Firefox and Safari do not implement them and have stated opposition. A browser provider would be Chromium-specific. |
| **JS interop overhead** | 🟡 Significant | Every hardware API call must go through `[JSImport]` / `IJSRuntime` interop. Adds marshalling cost and complexity. |
| **WebAssembly sandbox** | 🔴 Critical | The WASM sandbox forbids direct hardware access. All access is mediated by browser APIs, which are permission-gated and grant-scoped. The library cannot discover devices the user hasn't already permitted. |
| **No P/Invoke** | 🟡 Significant | The existing Windows provider pattern (P/Invoke into cfgmgr32.dll) has no equivalent. Everything must go through JS interop. |
| **Async model mismatch** | 🟡 Significant | Web APIs return JS `Promise`s. The `[JSImport]` interop can marshal these to `Task`, but the wiring is verbose and error-prone. |
| **`DeviceInfo.Id` instability** | 🟡 Significant | Web APIs provide opaque device references, not stable IDs. `USBDevice` objects don't persist across page loads unless re-granted. |

### 3.4 Effort Estimate

| Component | Effort |
|-----------|--------|
| JS interop bridge (`periphery-browser.js`) | High — wrap every Web API, handle permissions, marshal to .NET |
| `BrowserDeviceProvider : IDeviceProvider` | Medium-High — per-API wiring, permission state management |
| `BrowserDeviceMonitorProvider` | Medium — event listener registration/cleanup via JS interop |
| Build/packaging changes | Medium — `net8.0-browser` TFM, JS module bundling |
| **Total** | **~4–6 weeks** |

### 3.5 Verdict

**Technically possible, but philosophically misaligned.** The browser's permission model makes "enumerate all devices" impossible — you can only see devices the user has explicitly granted. This inverts Periphery's discovery model. The best approach would be a separate `Periphery.Browser` package that exposes a modified API (`RequestAndEnumerateAsync()` requiring a user gesture), rather than trying to force the `IDeviceProvider` contract. Gamepad and media devices (audio input/output via `enumerateDevices()`) are the only categories that work without user-gesture permission gates and could fit the current API shape.

---

## 4. Additional Niche Platforms

### 4.1 iOS (`net8.0-ios`) — ✅ **High feasibility** (natural companion to macOS)

| Category | iOS API | Available? |
|----------|---------|-----------|
| USB | `ExternalAccessory.framework` (MFi only) | 🟡 Limited — MFi accessories only, no generic USB host |
| Bluetooth | `CoreBluetooth` (BLE) | ✅ Rich BLE scanning, discovery, monitoring |
| Network | `NWPathMonitor`, `SystemConfiguration`, `NetworkExtension` | ✅ Full interface enumeration and monitoring |
| Display | `UIScreen` | ✅ Internal + external displays |
| Audio | `AVAudioSession`, `AVAudioRoutingArbiter` | ✅ Route detection, device enumeration |
| HID | `GameController.framework` | ✅ Game controllers (MFi, Xbox, DualShock) |
| Storage | — | ❌ Sandboxed, no removable media enumeration |

**Verdict:** iOS is a strong candidate, especially since the macOS provider (planned) shares the same `CoreBluetooth`, `IOKit`, and `AVFoundation` layers. A single Apple provider with `#if IOS` / `#if MACCATALYST` conditionals could cover both platforms efficiently. The main limitation is USB — iOS has no USB host API for arbitrary devices, only MFi accessories.

### 4.2 Tizen (`net8.0-tizen`) — 🟡 **Medium feasibility** (Samsung-maintained)

| Category | Tizen API | Available? |
|----------|-----------|-----------|
| USB | `Tizen.System.Usb` | ✅ USB host enumeration |
| Bluetooth | `Tizen.Network.Bluetooth` | ✅ Full Bluetooth stack |
| Network | `Tizen.Network.Connection` | ✅ Interface enumeration |
| Display | `Tizen.System.Display` | ✅ Display info |
| Audio | `Tizen.Multimedia.AudioDevice` | ✅ Audio device management |
| Storage | `Tizen.System.Storage` | ✅ Internal/external storage |

**Verdict:** Tizen has comprehensive device APIs with official .NET bindings (Samsung maintains the `Tizen.NET` workload). All major `DeviceCategory` values are supportable. The audience is narrow (Samsung TVs, Galaxy Watch, appliances) but the APIs are well-documented. If Samsung TV/watch support is a product goal, this is viable. The `Tizen.*` namespace packages would be the first third-party runtime dependency, which conflicts with the "zero third-party runtime deps" constraint — though they'd be conditional on the TFM.

### 4.3 Raspberry Pi / IoT (`net8.0` + `System.Device.Gpio`) — ✅ **High feasibility**

This isn't a separate TFM — it's the Linux provider running on ARM with additional IoT-specific enrichment.

| Category | API | Available? |
|----------|-----|-----------|
| USB | sysfs (same as Linux provider) | ✅ Same as desktop Linux |
| GPIO | `System.Device.Gpio` | ✅ Pin enumeration, state monitoring |
| I²C | `System.Device.I2c` | ✅ Bus scanning for connected devices |
| SPI | `System.Device.Spi` | ✅ Bus enumeration |
| 1-Wire | sysfs (`/sys/bus/w1/devices/`) | ✅ Temperature sensors, etc. |
| Serial | `/dev/ttyS*`, `/dev/ttyAMA*` | ✅ Already modeled by `SerialPortName` |

**Verdict:** The Linux provider already covers Raspberry Pi for standard device categories (USB, Bluetooth, Network, etc.). The IoT-specific addition would be a `DeviceCategory.Gpio` / `DeviceCategory.I2c` etc. extension, potentially in a separate `Periphery.IoT` package that references `System.Device.Gpio`. This is the **highest value-add** niche platform because it directly serves the embedded/maker audience where device discovery matters most.

### 4.4 Windows IoT Core (legacy) — ❌ **Not recommended**

Windows IoT Core is effectively end-of-life. Microsoft has moved IoT scenarios to full Windows 11 IoT Enterprise, which the existing Windows provider already covers. No separate backend needed.

### 4.5 FreeBSD / illumos — 🟡 **Low priority, medium feasibility**

FreeBSD has `devd` (device state change daemon) and `/dev` enumeration. .NET runs on FreeBSD via community effort. The audience is very small. Not recommended unless a contributor volunteers.

---

## 5. Platform Comparison Matrix

| Criterion | Android | tvOS | Browser/WASM | iOS | Tizen | RPi/IoT |
|-----------|---------|------|-------------|-----|-------|---------|
| **DeviceCategory coverage** | ✅ 8/9 | 🔴 3/9 | 🟡 4/9* | ✅ 6/9 | ✅ 7/9 | ✅ 8/9+ |
| **Monitoring support** | ✅ Rich | 🟡 Limited | 🟡 Permission-gated | ✅ Rich | ✅ Rich | ✅ Rich |
| **.NET TFM exists** | ✅ `net8.0-android` | ✅ `net8.0-tvos` | ✅ `net8.0-browser` | ✅ `net8.0-ios` | ✅ `net8.0-tizen` | ✅ `net8.0` (Linux ARM) |
| **First-class .NET bindings** | ✅ Mono.Android | ✅ Xamarin.TVOS | ❌ JS interop only | ✅ Xamarin.iOS | ✅ Tizen.NET | ✅ System.Device.Gpio |
| **Audience size** | ✅ Large | 🔴 Tiny | 🟡 Medium | ✅ Large | 🟡 Small | ✅ Medium |
| **Alignment with Periphery's model** | ✅ Strong | 🔴 Weak | 🔴 Weak | ✅ Strong | ✅ Strong | ✅ Strong |
| **Zero-dependency achievable** | ✅ Yes | ✅ Yes | ✅ Yes | ✅ Yes | 🟡 Needs Tizen pkgs | 🟡 Needs GPIO pkg |
| **Recommended?** | ✅ **Yes** | ❌ No | 🟡 Partial | ✅ **Yes** | 🟡 If demanded | ✅ **Yes** |

\* Browser: only Gamepad + MediaDevices work without permission gates; WebUSB/WebHID/WebBluetooth are permission-gated.

---

## 6. Recommended Implementation Order

```
Phase 5: Android Provider
  ├─ AndroidCategoryMap (system service → DeviceCategory)
  ├─ Context/permission initialization API
  ├─ Per-subsystem enumerators (USB, BT, Network, Display, Audio, HID)
  ├─ AndroidDeviceProvider : IDeviceProvider
  ├─ AndroidDeviceMonitorProvider : IDeviceMonitorProvider (BroadcastReceiver)
  └─ Conditional TFM in csproj (`net8.0-android`)

Phase 6: iOS Provider (shares code with macOS Phase 3-4)
  ├─ CoreBluetooth BLE scanner
  ├─ NWPathMonitor network enumeration
  ├─ AVAudioSession route detection
  ├─ GameController.framework HID
  ├─ AppleDeviceProvider : IDeviceProvider (shared base with macOS)
  └─ Conditional TFM in csproj (`net8.0-ios`)

Phase 7: IoT Extensions (optional separate package)
  ├─ Periphery.IoT project (references System.Device.Gpio)
  ├─ GPIO pin discovery and state monitoring
  ├─ I²C bus scanning
  ├─ New DeviceCategory values (Gpio, I2c, Spi, OneWire)
  └─ Linux ARM integration tests

Phase 8: Browser Provider (partial, optional separate package)
  ├─ Periphery.Browser project
  ├─ periphery-browser.js interop module
  ├─ GamepadProvider (no permission gate)
  ├─ MediaDeviceProvider (audio input/output, no gesture required after initial)
  ├─ WebUSB/WebHID/WebSerial wrappers (requires modified API with user-gesture hook)
  └─ net8.0-browser TFM
```

---

## 7. Build & Packaging Impact

### Current State

```xml
<TargetFrameworks>net8.0;net10.0</TargetFrameworks>
<PackageReference Include="System.Management" Version="9.0.5" />  <!-- Windows-only -->
```

### Multi-platform Target

```xml
<TargetFrameworks>net8.0;net10.0;net8.0-android;net8.0-ios</TargetFrameworks>

<!-- Conditional dependencies -->
<ItemGroup Condition="$([MSBuild]::IsOSPlatform('Windows'))">
  <PackageReference Include="System.Management" Version="9.0.5" />
</ItemGroup>
```

Alternatively (and preferably), platform-specific code and dependencies stay in separate projects:

```
Periphery/                  → net8.0, net10.0 (core + Windows)
Periphery.Android/          → net8.0-android
Periphery.iOS/              → net8.0-ios
Periphery.IoT/              → net8.0 (references System.Device.Gpio)
Periphery.Browser/          → net8.0-browser
```

Each platform package would reference the core `Periphery` package and register its provider via the `DeviceProviderFactory` extension point.

### Open Question

The architecture currently uses `DeviceProviderFactory` with runtime `OperatingSystem.IsXxx()` dispatch and `internal` provider interfaces. Adding external platform packages requires either:

1. **Make `IDeviceProvider` public** and add a registration API (`DeviceProviderFactory.Register(IDeviceProvider)`)
2. **Keep everything internal** with `InternalsVisibleTo` per platform assembly
3. **Ship a single mega-package** with all TFMs and conditional compilation

Option (1) is cleanest for extensibility. Option (2) preserves encapsulation. Option (3) is simplest but bloats the package.

---

## 8. Decision

This ADR is informational. The recommended prioritization is:

1. **Android** — Highest value, strong API coverage, large audience
2. **iOS** — Natural companion to macOS, shared Apple framework code
3. **RPi/IoT** — High value for embedded audience, extends the Linux provider
4. **Browser/WASM** — Only if a partial/modified API is acceptable
5. **tvOS** — Not recommended
6. **Tizen** — Only if Samsung ecosystem support is a product requirement

Implementation should not begin until the Linux and macOS providers (Phases 1–4) are complete, as those establish patterns that Android and iOS providers will reuse.

---

## References

- [.NET Target Framework Monikers](https://learn.microsoft.com/dotnet/standard/frameworks#supported-target-frameworks)
- [Android.Hardware.Usb.UsbManager](https://learn.microsoft.com/dotnet/api/android.hardware.usb.usbmanager?view=net-android-35.0)
- [WebUSB API (MDN)](https://developer.mozilla.org/en-US/docs/Web/API/WebUSB_API)
- [Web Bluetooth API (MDN)](https://developer.mozilla.org/en-US/docs/Web/API/Web_Bluetooth_API)
- [System.Device.Gpio](https://learn.microsoft.com/dotnet/iot/tutorials/gpio-pinout)
- [.NET MAUI Supported Platforms](https://learn.microsoft.com/dotnet/maui/supported-platforms)
- [Native AOT on iOS-like platforms](https://learn.microsoft.com/dotnet/core/deploying/native-aot/ios-like-platforms/)
- `docs/ARCHITECTURE.md` §2.3, §7, §10.6
