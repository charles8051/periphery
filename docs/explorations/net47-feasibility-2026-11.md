# Building Periphery for .NET Framework 4.7: feasibility

**Date:** 2026-11-04
**Status:** Exploration. No decision has been taken and nothing in `src/` has changed.
**Method:** A throwaway copy of `src/` was retargeted to `net47` and built, patched until it
compiled where that was tractable, and run on the CLR.
**Scope:** The multi-targeted libraries under `src/`. Tests, examples and apps were not retargeted.

## Conclusion

A `net47` build is feasible for the core packages, on Windows only. `Periphery` compiled and
enumerated the same devices as `net10.0`. `Periphery.Camera` and
`Periphery.Bootloader.Stm32.Serial` cost more than the rest, for separate reasons.

The largest risk is one that compiles cleanly. A polyfilled `[UnmanagedCallersOnly]` builds and
then crashes the process under GC load. See [Native callbacks](#native-callbacks).

## Evidence and its limits

What was measured:

- `Periphery`, `Periphery.Firmware` and `Periphery.Serial` compile for `net47`.
- `Devices.Enumerate()` on `net47` returned 359 devices on a Windows 11 x64 machine. The output
  was identical to a `net10.0` build of the unmodified source, on the same machine, across 17
  `DeviceInfo` fields.
- `Devices.Watch()` started and ran for 8 seconds on `net47`. No device event fired during that
  run, so event delivery is unproven.

What was not measured:

- **No real 4.7 runtime.** The binaries targeted `net47` and ran on the machine's installed
  .NET Framework. A later version replaces 4.7 in place, so testing on 4.7 itself needs an older
  Windows install.
- **No test suite ran on `net47`.**
- **No hardware I/O ran on `net47`.** USB, HID, serial and camera transfers were not exercised.
- **The per-package error counts are first-pass.** Only `Periphery` was patched to completion.
  The compiler reports binding errors first and stops, and a package whose dependency fails is
  blocked. More errors may follow in every other row of the table below.

## Results by package

| Package | Result on `net47` | Work needed |
|---|---|---|
| `Periphery.Firmware` | Compiles | Polyfills only |
| `Periphery.Serial` | Compiles | Polyfills only |
| `Periphery` | Compiles and runs | The changes under [Required changes](#required-changes) |
| `Periphery.Monitor` | 1 error (CS8889) | Add a calling convention to the `delegate* unmanaged` in `MonitorInterop` |
| `Periphery.Bootloader` | 2 errors (CS8701) | Replace the default interface methods in `IBootloaderEntry` |
| `Periphery.Usb` | 2 errors | `ThreadPoolBoundHandle` is missing, so rewrite the overlapped I/O in `WinUsbBackend` |
| `Periphery.Treehopper` | 2 errors | Same errors as `Periphery.Usb`, which it references |
| `Periphery.DebugProbe.StLink` | 4 errors | Same errors as `Periphery.Usb` and `Periphery.Bootloader` |
| `Periphery.Hid` | 195 errors | Almost all are polyfill ambiguity, see [Polyfills](#polyfills) |
| `Periphery.Serial.Rjcp` | 182 errors | Almost all are polyfill ambiguity, see [Polyfills](#polyfills) |
| `Periphery.Camera` | 23 errors | Rewrite the Media Foundation interop, see [Camera](#camera) |
| `Periphery.Bootloader.Stm32.Serial` | Restore fails (NU1202) | `CallAndResponse` must multi-target first |

Not attempted: `Periphery.Ble.InTheHand`, `Periphery.Camera.Avalonia`,
`Periphery.Camera.OpenCvSharp`, `Periphery.Camera.Testing`, the remaining bootloader, firmware and
Treehopper packages, `Periphery.FlashAnything`, and every CLI and GUI project.

## Dependencies

| Dependency | Assets checked | Usable on `net47` |
|---|---|---|
| `Microsoft.Extensions.Logging.Abstractions` | `net462` (10.0.14) | Yes, 9.0.0 restored and built |
| `System.IO.Ports`, `System.IO.Pipelines` | `net462` (10.0.x) | Yes, 9.0.0 restored and built |
| `RJCP.SerialPortStream` 3.0.5 | `net40`, `net462` | Yes by assets |
| `InTheHand.BluetoothLE` 4.0.45 | `net462`, `netstandard2.0` | Yes by assets |
| `OpenCvSharp4` 4.13 | `netstandard2.0` | Yes by assets, not built |
| `Avalonia` 11.2.0 | `netstandard2.0` | Yes by assets, not built |
| `Spectre.Console`, `Spectre.Console.Cli` | `netstandard2.0` | Yes by assets, not built |
| `CommunityToolkit.Mvvm` | `netstandard2.0` | Yes by assets, not built |
| `CallAndResponse` 2.0.0-alpha.7 | `net8.0` only | No |

The scratch build added these for `net47` only: `Polyfill`, `System.Memory`,
`System.Threading.Tasks.Extensions`, `Microsoft.Bcl.AsyncInterfaces`, `Microsoft.Bcl.TimeProvider`,
`Microsoft.Bcl.HashCode`, `System.Collections.Immutable`, `System.Text.Json` and
`System.Threading.Channels`. The core package therefore gains a larger dependency graph on this
target, and an executable consumer needs binding redirects. The smoke test set
`AutoGenerateBindingRedirects`.

C# language features were not the obstacle. `LangVersion=latest` compiles records, `init`,
`required` and collection expressions once the polyfilled attributes are present. The `[LoggerMessage]`
and `System.Text.Json` source generators ran without changes.

## Required changes

### P/Invoke declarations

`LibraryImportAttribute` is not public on `net47`, and the generator does not run for that target.
All 199 `[LibraryImport]` declarations need to become `[DllImport]` for this target. A regex
rewrite handled every one in the scratch copy, and Windows enumeration then matched `net10.0`.

The rewrite mapped `StringMarshalling.Utf8` to `CharSet.Ansi` to get a compile. That is wrong
wherever a UTF-8 string crosses the boundary on a non-UTF-8 code page. A real port should use
`[MarshalAs(UnmanagedType.LPUTF8Str)]`, which arrived in 4.7.

Open choice: keep `LibraryImport` on `net8.0` and `net10.0` behind conditional compilation, or
write a small generator that emits both forms. Moving every target to `DllImport` was not evaluated
against the AOT analysis the repository enables on some packages.

### Native callbacks

`[UnmanagedCallersOnly]` has no effect on the .NET Framework CLR. A polyfilled attribute satisfies
the compiler, and `&Method` then yields a pointer to a managed method with no transition from
native code.

A test called `EnumDisplayMonitors` in a loop for 30 seconds while four threads allocated and
forced gen 0 collections. The callback allocated and checked its `data` argument.

| Callback form | x64 | x86 |
|---|---|---|
| Polyfilled `[UnmanagedCallersOnly]` | Crashed, exit 139, no result printed | Exited 127, no result printed |
| Rooted delegate via `Marshal.GetFunctionPointerForDelegate` | 238,887 calls, 0 bad arguments | 170,318 calls, 0 bad arguments |

An earlier single-threaded version of the test, with no argument check and only forced collections
inside the callback, passed 200 calls on both architectures. The 8-second watcher run also passed.
Neither result shows the form is safe.

Four Windows sites need rooted delegates:

- `Periphery.Monitor/Windows/MonitorPathResolver.cs`
- `Periphery/Windows/WindowsBluetoothLinkWatch.cs`
- `Periphery/Windows/WindowsDeviceMonitorProvider.cs`
- `Periphery/Windows/WindowsDisplayChangeSink.cs`

The delegate must outlive the native registration. A port should also keep the attribute undefined
on `net47`, so that a new callback fails the build.

### Public API differences

These types or members cannot match the `net8.0` and `net10.0` surface. `PublicAPI/<tfm>/` already
supports recording per-framework differences.

| Symbol | Problem on `net47` | Scratch stand-in |
|---|---|---|
| `IReadOnlySet<T>`, for example `ITagEmittingEnricher.EmitsTags` | Missing, and `HashSet<T>` does not implement it | `IReadOnlyCollection<T>` |
| `System.Net.IPNetwork`, on `DeviceInfo.Network` and `IPNetworkJsonConverter` | Missing | A local struct whose `Contains` throws |
| `ITagEmittingEnricher.Scope` | The runtime has no default interface methods | Abstract property |
| `IBootloaderEntry` default members | The runtime has no default interface methods | Not patched |

The default interface members are a source break for anyone implementing those interfaces on
`net47`.

### Missing BCL members

`Periphery` needed stand-ins for these. Each is mechanical, and most are small.

- `ArgumentNullException.ThrowIfNull` and the sibling `ArgumentException`,
  `ArgumentOutOfRangeException` and `ObjectDisposedException` guards
- `Marshal.GetLastPInvokeError`
- `OperatingSystem.IsWindows` and its siblings
- `Task.Delay(TimeSpan, TimeProvider, CancellationToken)`
- `Environment.TickCount64` and `Environment.ProcessId`
- `Task.IsCompletedSuccessfully`
- `TimeSpan` multiplication
- `GeneratedRegex`, at 2 sites in `WindowsDeviceProvider`
- Span overloads of `int.Parse`, `float.Parse` and `new Guid`
- `ThreadPool.QueueUserWorkItem` with `preferLocal`

`Periphery` also uses `Volatile.Write(ref nint, ...)`, which has no overload on this target. The
scratch copy replaced it with a plain assignment to get a compile, which drops the memory barrier.
A real port needs a correct replacement.

### Polyfills

The `Polyfill` package compiles its types as `internal` into each project. Where a project also sees
another project's internals through `InternalsVisibleTo`, the two copies collide. That produced
most of the errors in `Periphery.Hid` (183 CS0121) and `Periphery.Serial.Rjcp` (176 CS0121).

`src/` has 21 projects that grant `InternalsVisibleTo` to another `Periphery.*` project. A port
should settle this first, most likely with a single shared compatibility assembly. The same collision
would hit any hand-written extension members.

### Camera

`Periphery.Camera` uses `GeneratedComInterface` for 10 interfaces and `StrategyBasedComWrappers` for
object lifetime. Neither exists on `net47`. The replacement is classic `[ComImport]` interfaces with
runtime-callable wrappers, which changes reference-count and threading behaviour in code that manages
Media Foundation samples. This is a rewrite of the Windows backend's interop rather than a mechanical
change, and it needs camera hardware to verify.

### USB

`ThreadPoolBoundHandle` is missing from the `net47` reference assemblies. `WinUsbBackend` and
`WinUsbInterop` depend on it for overlapped I/O. `ThreadPool.BindHandle` with `Overlapped.Pack` is
the likely replacement. This path has not been built, and it needs a device to verify. Everything
built on `Periphery.Usb` waits on it.

### Non-Windows code

.NET Framework runs only on Windows. The scratch build excluded `Linux/**` and `MacOS/**`, and
removed the Linux and macOS branches from `DeviceProviderFactory`. `UsbDevice`, `HidDevice`,
`MonitorDevice` and `CameraDevice` dispatch on `OperatingSystem.IsLinux` as well, and `Periphery.Hid`
hit this on the first pass.

Excluding those trees also removed some of the missing-API work. `PeriodicTimer`,
`UnixDomainSocketEndPoint`, `NativeLibrary` and `new CancellationTokenSource(TimeSpan, TimeProvider)`
stopped being errors once they were gone.

## CI and support

A published `net47` asset needs a Windows test run on the .NET Framework. Without one, the callback
crash above would ship, because it does not appear at compile time or in a short run. ADR-0069
accepts `net8.0` as compiled but untested because the pure logic is target-independent. That
argument does not carry over to `net47`, where the runtime itself differs: no default interface
methods, no `UnmanagedCallersOnly` and different interop paths.

The test projects target `net10.0` only and would need to multi-target as well. Their compatibility
with `net47` was not checked.

## Open questions

- Is there a consumer for this target? None has been named. ADR-0069 used the same test for
  `net8.0`, where a real dependant is what justifies a test leg.
- Does the target need to be 4.7, or would 4.7.2 or 4.8 serve? Only 4.7 was built. The results above
  do not separate them, and no later version adds `LibraryImport`, default interface methods or
  `UnmanagedCallersOnly`.
- Which packages does the target need? A subset of `Periphery`, `Periphery.Firmware`,
  `Periphery.Serial` and `Periphery.Monitor` avoids USB, Camera and the `CallAndResponse` dependency.
- Should `CallAndResponse` multi-target? That is a change in another repository.

## Reproducing the spike

1. Copy `src/` out of the repository and replace `net8.0;net10.0` with `net47` in each project file.
2. Set `LangVersion=latest`, `MinVerSkip=true` and `PeripheryLocalFeedDisable=true` in the root
   `Directory.Build.props`.
3. Add the packages listed under [Dependencies](#dependencies), conditioned on `net47`, in
   `src/Directory.Build.targets`. A `Directory.Build.targets` at a higher level is shadowed by that
   file.
4. Exclude `Linux/**` and `MacOS/**`, and rewrite `[LibraryImport]` to `[DllImport]`.
5. Build `src/Periphery/Periphery.csproj` with `-p:RunAnalyzers=false -p:IsAotCompatible=false` and
   fix what remains. Some project files set `IsAotCompatible` themselves, so it has to be passed on
   the command line.
6. Reference the result from a `net47` console project with `AutoGenerateBindingRedirects` set, and
   compare the output of `Devices.Enumerate()` against a `net10.0` build.
