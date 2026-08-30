# Where a third-party integration package lives

> **Read this before** starting any package that puts a Periphery type and a
> third-party library's type in the same method signature. `ICameraFrame` to an
> Avalonia `Bitmap`, to an OpenCV `Mat`, to a graph runtime's frame item. The
> answer is either an opt-in package in this repo or a package in the consuming
> repo. The two are not interchangeable.

Periphery has four integrations today and they live in three places.

| Integration | Lives in | Library it binds to |
|---|---|---|
| `Periphery.Camera.Avalonia` | this repo, opt-in package | `Avalonia` |
| `Periphery.Camera.OpenCvSharp` | this repo, opt-in package | `OpenCvSharp4` |
| `Periphery.Camera.Testing` | this repo, opt-in package | none; it binds `Periphery.Camera`'s internals |
| `FrameFlow.Camera` | the `frame-flow` repo | `FrameFlow.Graph`, the consumer's own substrate |

Each placement was decided on its own, in [ADR-0045](../adr/0045-substrate-independence-from-crossbar.md)
and [ADR-0065](../adr/0065-camera-testing-seam.md). Neither ADR states the rule
that separates the cases. This note states it.

---

## The routing rule

Answer in order. The first yes decides.

### Q1 — Periphery internals

Does the integration have to reach an `internal` type: a backend contract, a
factory hook, an internal constructor?

If yes, it lives here. The `InternalsVisibleTo` grant is a line in the *host*
package's csproj, so only this repo can add it. ADR-0065 rejected granting one
per consumer assembly, because that does not scale and it exposes the whole
internal surface instead of a curated one.

An internals-bound package is version-locked to its host. Pack an exact
`[x.y.z]` dependency range rather than NuGet's default minimum. A mismatched
pair then fails at restore instead of at run time with `TypeLoadException`.
`Periphery.Camera.Testing.csproj` does this in its `PinExactCameraDependency`
target.

`Periphery.Camera.Testing` is the worked example. It binds `ICameraBackend`,
`RawCameraFrame`, and the internal `CameraDevice` / `CameraSession`
constructors, so it could not be built anywhere else.

### Q2 — a foreign substrate

Does the foreign library define an item, ownership, or scheduling protocol that
a Periphery type would have to implement in order to participate?

If yes, the adapter lives in the consumer's repo. `FrameFlow.Graph` is such a
library: an item flowing through the graph must be an `IFrame` and an
`IRefCounted`, on the runtime's ownership discipline.

The test is a sentence you can check. **Would the integration be tidier if a
Periphery type implemented a foreign interface?** If the answer is yes, the
foreign library is a substrate, and the adapter does not belong here. Hosting
one here does not stay a wrapper. The pressure to put the foreign base on
`ICameraFrame` and delete the wrapper is constant. [ADR-0042](../adr/0042-periphery-crossbar-substrate-integration.md)
gave in to that pressure once, and ADR-0045 undid it.

`FrameFlow.Camera`'s `CameraFrameAdapter` is the shape this produces. It is a
`sealed class CameraFrameAdapter : IFrame, IRefCounted` in the consumer repo,
holding a reference to the inner `ICameraFrame`.

Direction alone does not answer Q2. Take the integrations that bind a foreign
library, which is the only set Q2 has to sort: `Periphery.Camera.Avalonia`,
`FrameFlow.Camera`, and `Periphery.Camera.OpenCvSharp`. All three point the
same way. Periphery produces a value and the foreign library
consumes it. Direction puts them in one bucket and they do not all belong in
one repo. Participation is what separates them.

`Periphery.Camera.Testing` is not in that set. It binds no foreign library, and
Q1 has already routed it.

### Q3 — a leaf converter

Otherwise the integration turns a Periphery value into a foreign value and the
relationship ends there. An Avalonia `Bitmap` and an OpenCV `Mat` are
destinations for pixels. Neither one needs `ICameraFrame` to be a type of its
own.

A leaf converter lives here as an opt-in package named
`Periphery.{Domain}.{Library}`, after the binding rather than the technology.
`OpenCvSharp4` and `Emgu.CV` are incompatible bindings of the same library, so
`Periphery.Camera.OpenCv` would claim a name that a second package cannot
share.

Three constraints apply.

- **Foreign types stay inside the package.** No type in `Periphery` or in a
  `Periphery.{Domain}` extension gains a foreign base, member, or attribute.
  The integration package's own types may derive from foreign ones.
  `CameraPreview` is an Avalonia `Control` that implements Periphery's
  `ICameraFrameSink`, and that sink interface lives in
  `src/Periphery.Camera/ICameraFrameSink.cs`.
- **The package is a leaf.** Nothing else under `src/` references it.
  `Periphery.Camera.Avalonia` is referenced only by
  `examples/Periphery.Examples.CameraAvalonia`;
  `Periphery.Camera.OpenCvSharp` only by its two test projects.
- **Managed binding only.** Never reference a `runtime.*` native payload
  package. The consumer picks the payload for their platform.
  `Periphery.Camera.OpenCvSharp` takes `OpenCvSharp4` and no
  `OpenCvSharp4.runtime.*`, so a consumer on Linux does not carry Windows
  natives and a consumer on macOS — where no current first-party payload
  exists — is free to supply a third-party one.

---

## Which packages the dependency rule binds

| Category | Third-party runtime dependencies |
|---|---|
| `Periphery` core | `Microsoft.Extensions.Logging.Abstractions` only |
| `Periphery.{Domain}` I/O extension | `Microsoft.Extensions.Logging.Abstractions` only |
| `Periphery.{Domain}.{Platform}` backend | `Microsoft.Extensions.Logging.Abstractions` only |
| `Periphery.{Domain}.{Library}` integration | the one library it is named for, plus the above |
| Test-support package | none beyond its host package and the BCL |
| App, and an app's `.Core` library half | unconstrained |
| `tests/`, `benchmarks/`, `examples/` | unconstrained |

`Microsoft.Extensions.Logging.Abstractions` is the single baseline exception,
on the terms in [`logging-and-diagnostics.md`](logging-and-diagnostics.md): the
abstractions package, never a provider. Four shipping libraries reference it
directly: `Periphery`, `Periphery.Camera`, `Periphery.Usb`, and
`Periphery.Treehopper`. The others pick it up transitively through the core.

**Build-only references are not dependencies for this purpose.**
`Microsoft.SourceLink.GitHub` and `MinVer` both carry `PrivateAssets="All"`, so
they do not appear in the produced `.nuspec` and never reach a consumer's
dependency graph. A package whose only third-party references are those two has
no third-party runtime dependency.

**Apps are outside the rule.** `Periphery.Cli` takes `Spectre.Console`.
`Periphery.Treehopper.Control.Gui` and `Periphery.FlashAnything.Gui.Core` take
Avalonia and `CommunityToolkit.Mvvm`. Nothing consumes an app's dependency
closure, so none of this applies to one. Packability is not the test:
`Periphery.Cli` ships as a `PackAsTool` package and is still an app.
`OutputType` is the test, and a GUI's `.Core` library half counts as part of
the app it exists to build.

**A test-support package takes no test-framework dependency.**
`Periphery.Camera.Testing` references no xunit and must not. A consumer on
NUnit or MSTest has to be able to use it.

---

## What this note does not cover

**Whether Periphery should own a transport at all.** ADR-0038 declined
`LibUsbDotNet` and wrote WinUSB and libusb P/Invoke by hand. That was not a
placement question. The wrapping would have sat inside `Periphery.Usb`'s I/O
path rather than at a hand-off boundary, and the dependency table above already
forbids it. ADR-0038 refused the library on two further grounds the routing
questions do not model. `LibUsbDotNet` is LGPL-3.0 and does not carry the
native library's static-linking exception, so the terms reach the whole binary
under NativeAOT. Hand-written interop is also AOT-clean by construction. Read
[ADR-0038](../adr/0038-periphery-usb.md) for that reasoning.

**The star topology.** [ADR-0024](../adr/0024-extension-package-pattern.md)
forbids spoke-to-spoke dependencies between I/O extensions. An integration
package sits below a spoke and may reference both the core and its one
extension. `Periphery.Camera.Avalonia` references `Periphery` and
`Periphery.Camera`. It is not a spoke and it does not reopen the star rule.

**Version-range policy for the integrated library.** An integration package
inherits its library's support matrix, which can pull against Periphery's own
target-framework decisions. `Periphery.Camera.Avalonia` targets
`net8.0;net10.0` and pins `Avalonia` at `11.2.0`. One example is not enough to
write a rule from.

---

## References

- [ADR-0024](../adr/0024-extension-package-pattern.md) — extension package pattern, star topology, package naming and dependency rules.
- [ADR-0038](../adr/0038-periphery-usb.md) — declining a third-party transport inside an I/O extension.
- [ADR-0042](../adr/0042-periphery-crossbar-substrate-integration.md) — the substrate inheritance this repo tried and reversed.
- [ADR-0045](../adr/0045-substrate-independence-from-crossbar.md) — substrate independence; §3 moves the bridge to `FrameFlow.Camera`, §4 keeps `Periphery.Camera.Avalonia` here.
- [ADR-0065](../adr/0065-camera-testing-seam.md) — internals-bound test-support package and the exact-version pin.
- [`logging-and-diagnostics.md`](logging-and-diagnostics.md) — the logging abstractions dependency.
