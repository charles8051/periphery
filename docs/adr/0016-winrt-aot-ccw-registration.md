---
title: "ADR-0016: AOT-Safe WinRT Marshalling via CsWinRT GeneratedWinRTExposedExternalType"
status: "Superseded"
date: "2026-07-14"
authors: "@charles8051 (review)"
tags: ["architecture", "decision", "windows", "winrt", "aot", "trimming", "marshalling"]
supersedes: ""
superseded_by: "0018-winrt-enrichment-tfm-coupling.md"
---

# ADR-0016: AOT-Safe WinRT Marshalling via CsWinRT GeneratedWinRTExposedExternalType

## Context

### Discovery

During testing of `device-dump.cs` (a .NET 10 file-based app), WinRT enrichment silently failed
with the following exception, which was swallowed by the `catch` in
`WindowsDeviceProvider.EnumerateAsync` and routed to `NullLoggerFactory`:

```
System.InvalidCastException: Failed to create a CCW for object of type 'System.String[]'
for interface with IID 'E2FCC7C1-3BFC-5A0B-B2B0-72E769D1CB7E': the specified cast is not valid.
  at WinRT.ComWrappersSupport.CreateCCWForObjectForABI(Object obj, Guid iid)
  at ABI.Windows.Devices.Enumeration.IDeviceInformationStaticsMethods
       .FindAllAsync(IObjectReference, String, IEnumerable`1)
  at Periphery.Windows.WindowsWinRTEnricher.BuildDisplayMonitorMapAsync
```

IID `E2FCC7C1-3BFC-5A0B-B2B0-72E769D1CB7E` is `IIterable<String>` (the WinRT projection of
`IEnumerable<string>`). CsWinRT needs a CCW (COM Callable Wrapper) factory registered for
whatever concrete .NET type is passed as `additionalProperties` to `FindAllAsync`. The CCW
factory is generated code that marshals the .NET type to WinRT's ABI. Under a JIT runtime it
is always present; under Native AOT or aggressive IL trimming, the linker removes it unless
something in the static call graph roots it.

### Root cause

File-based apps (`.cs` files run via `dotnet run file.cs`) default to `PublishAot=true` — this
is documented in the .NET 10 SDK file-based app spec and differs from traditional `.csproj`
projects where AOT is opt-in. The immediate workaround applied was `#:property PublishAot=false`
in `device-dump.cs`, which restores JIT behaviour and makes the CCW available at runtime.

`Periphery.Examples` was unaffected because it is a standard `.csproj` project with no AOT
and no trimming enabled in its Debug build configuration.

### Why the CCW is trimmed

CsWinRT 2.2.0 (shipped in `Microsoft.Windows.SDK.NET.ref 10.0.17763.57` and later) generates
CCW factories for types that it knows will be passed to WinRT APIs at build time. For types
defined *in the same project* (e.g. a custom `IEnumerable<T>` implementation) the source
generator can see them and root their factories automatically. For externally-defined types
passed as interface arguments (e.g. `string[]`, `List<string>`, or any `IEnumerable<string>`
passed to `DeviceInformation.FindAllAsync`), the source generator cannot infer the concrete
runtime type from a static call graph — the parameter is typed as `IEnumerable<string>` at
the call site.

The trimmer therefore removes the CCW factory for `string[]`, `List<string>`, and the
compiler-generated `ReadOnlySingleElementList<string>` (produced by collection literals on
`IReadOnlyList<string>`) because no static reference roots any of them as WinRT-marshalable.

Changing `s_instanceIdProp` from `string[]` to `List<string>` or `IReadOnlyList<string>` during
investigation did not fix the problem — all three are subject to the same trimming for the same
reason. The collection type does not matter; the missing rooting does.

### The fix mechanism

CsWinRT 2.1+ introduced `[GeneratedWinRTExposedExternalType(typeof(T))]` — an assembly-level
attribute that instructs the CsWinRT Roslyn source generator to emit a module initializer
that calls `ComWrappersSupport.RegisterHelperType(typeof(T), typeof(ABI.T))` for the named
type before any WinRT call occurs. The trimmer sees this static initializer and keeps the CCW
factory alive.

The attribute is defined in `WinRT.Runtime.dll` (already a transitive dependency of the
Windows TFMs via `Microsoft.Windows.SDK.NET.ref`) and processed by the Roslyn source generator
in `Microsoft.Windows.CsWinRT`. The CsWinRT package is **build-time only** — it ships
`cswinrt.exe` (WinMD code generator), Roslyn analyzers, and MSBuild targets. Nothing from it
lands in consumers' output unless `CsWinRTGenerateProjection=true` is active.

---

## Decision

Add `Microsoft.Windows.CsWinRT` as a **build-time-only** (`PrivateAssets="all"`) package
reference scoped to the Windows TFMs. Disable WinMD projection generation
(`CsWinRTGenerateProjection=false`) since Periphery is a *consumer* of the Windows SDK
projection, not an author. Add a single new file
`Periphery/Windows/WinRTMarshalRegistrations.cs` (compiled only on Windows TFMs) that
declares the `[GeneratedWinRTExposedExternalType]` attributes for every .NET type passed
as `IEnumerable<string>` to WinRT APIs in the enricher.

### Scope of registrations required

The only WinRT API in Periphery that accepts `IEnumerable<string>` is:

```
Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
    string aqsFilter,
    IEnumerable<string> additionalProperties)
```

Called from `WindowsWinRTEnricher` with `s_instanceIdProp` (a `string[]`). Registration of
`string[]` is therefore the minimum required. `List<string>` is registered as a precaution
for any future call sites that use a list literal.

### Impact on package consumers

- **Runtime:** Zero. `WinRT.Runtime.dll` is already present in every Windows TFM build via
  `Microsoft.Windows.SDK.NET.ref`. The generated module initializer adds ~10 lines of IL.
- **Build time:** The CsWinRT Roslyn analyzer pass adds roughly 1–2 seconds to an incremental
  Windows TFM build. The `cswinrt.exe` WinMD generation phase is skipped entirely
  (`CsWinRTGenerateProjection=false`).
- **NuGet graph:** `PrivateAssets="all"` prevents `Microsoft.Windows.CsWinRT` from appearing
  in consumers' dependency graphs. It is invisible to downstream packages and applications.
- **Non-Windows TFMs:** The `PackageReference` and `PropertyGroup` are both conditioned on
  `$(TargetFramework.Contains('-windows'))`. Non-Windows builds are completely unaffected.

---

## Consequences

### Positive

- **POS-001**: WinRT enrichment works correctly under Native AOT and IL trimming without any
  per-consumer configuration. `PublishAot=true` is now safe for file-based apps and published
  executables referencing Periphery on Windows-TFM targets.
- **POS-002**: The fix lives entirely in the library. Consumers do not need to add their own
  `[GeneratedWinRTExposedExternalType]` declarations or set `PublishAot=false`.
- **POS-003**: `CsWinRTGenerateProjection=false` ensures the expensive `cswinrt.exe` WinMD
  code generation phase never runs, keeping build times comparable to before.
- **POS-004**: `PrivateAssets="all"` ensures the package is fully invisible to consumers.

### Negative

- **NEG-001**: Periphery now has a build-time dependency on `Microsoft.Windows.CsWinRT`, which
  is a Microsoft-owned package with its own release cadence. If CsWinRT makes a breaking change
  to `[GeneratedWinRTExposedExternalType]` (unlikely — it's a stable AOT API), the Windows TFM
  build would break until the version is updated.
- **NEG-002**: The `[GeneratedWinRTExposedExternalType]` mechanism is not documented with the
  same prominence as other CsWinRT features. If a future developer removes
  `WinRTMarshalRegistrations.cs` without understanding why it exists, enrichment will silently
  regress to failing under AOT. The file includes a comment explaining the consequence of removal.
- **NEG-003**: If future enrichment passes (ADR-0015) pass additional `IEnumerable<string>`
  values to new WinRT APIs, their concrete types must also be registered in
  `WinRTMarshalRegistrations.cs`. This is a non-obvious ongoing maintenance obligation.

---

## Alternatives Considered

### A — `PublishAot=false` per consumer (current workaround in `device-dump.cs`)

- **Description**: Set `#:property PublishAot=false` in each file-based app or
  `<PublishAot>false</PublishAot>` in each consuming project that uses WinRT enrichment.
- **Rejection reason**: Pushes the burden onto every consumer and is invisible — a new
  file-based app or AOT-published executable will fail silently without an obvious reason.
  Acceptable as a temporary workaround; not acceptable as a long-term library design.
- **Rollback path**: If this ADR is reverted, reinstate `#:property PublishAot=false` in
  `device-dump.cs` and document the requirement prominently in README and XML docs for
  `WindowsWinRTEnricher`.

### B — Replace `FindAllAsync(selector, additionalProperties)` with two-call pattern

- **Description**: Call `FindAllAsync(selector)` with no additional properties, then per-device
  call `DeviceInformation.CreateFromIdAsync(id, additionalProperties)` to fetch the instance ID
  property. Eliminates the `IEnumerable<string>` marshal entirely.
- **Rejection reason**: Turns a single parallel batch into N sequential round-trips (one
  per device). For a machine with 4 monitors this is acceptable; for a future batch that runs
  against all 200+ USB devices it becomes a measurable latency problem. Also defers the problem
  rather than solving it — any future API that requires `IEnumerable<string>` would hit the
  same AOT failure.
- **Rollback path**: Replace the `FindAllAsync(selector, s_instanceIdProp)` calls in
  `WindowsWinRTEnricher` with `FindAllAsync(selector)` followed by per-device
  `CreateFromIdAsync` calls. Remove `WinRTMarshalRegistrations.cs` and the
  `Microsoft.Windows.CsWinRT` package reference.

### C — Manual `ComWrappersSupport.RegisterHelperType` call in enricher initializer

- **Description**: In `WindowsWinRTEnricher.BuildAsync`, call
  `WinRT.ComWrappersSupport.RegisterHelperType(typeof(string[]), typeof(ABI.string[]))` before
  the first `FindAllAsync` invocation to register the CCW factory at runtime.
- **Rejection reason**: `ABI.string[]` is an internal CsWinRT type name; referencing it
  directly creates a tight coupling to CsWinRT internals that are not part of the public API.
  The `[GeneratedWinRTExposedExternalType]` attribute exists precisely to avoid this pattern.
  Additionally, under true Native AOT the ABI helper type itself may be trimmed, so the
  `RegisterHelperType` call would reference a type that doesn't exist in the binary.
- **Rollback path**: Same as Alternative B — replace the `FindAllAsync` overload or retain
  `PublishAot=false`.

---

## Implementation Notes

- **IMP-001**: `Periphery.csproj` — add inside a `Condition="$(TargetFramework.Contains('-windows'))"` `ItemGroup`:
  ```xml
  <PackageReference Include="Microsoft.Windows.CsWinRT" Version="2.2.0" PrivateAssets="all" />
  ```
  Add inside a matching `PropertyGroup`:
  ```xml
  <CsWinRTGenerateProjection>false</CsWinRTGenerateProjection>
  ```

- **IMP-002**: `Periphery/Windows/WinRTMarshalRegistrations.cs` — new file, compiled only
  under `#if WINDOWS10_0_17763_0_OR_GREATER`. Contains:
  ```csharp
  // Roots CCW factories for .NET types passed to WinRT APIs under Native AOT / IL trimming.
  // See ADR-0016. Removing this file causes WinRT enrichment to fail silently under AOT.
  [assembly: WinRT.GeneratedWinRTExposedExternalType(typeof(string[]))]
  [assembly: WinRT.GeneratedWinRTExposedExternalType(typeof(System.Collections.Generic.List<string>))]
  ```

- **IMP-003**: `example-scripts/device-dump.cs` — remove `#:property PublishAot=false` and
  `#:property PackAsTool=false`. File-based apps default to both; removing them restores the
  correct defaults and validates the library-level fix.

- **IMP-004**: Verify by running `dotnet run --file example-scripts/device-dump.cs -- Monitor`
  (without `--verbose`) and confirming `displayResolution`, `displayName`, and related
  properties are present in the JSON output.
