---
title: "ADR-0037: Source-generated COM for the Periphery.Camera Windows backend"
status: "Accepted"
date: "2026-05-08"
authors: "@charles8051"
tags: ["architecture", "decision", "camera", "windows", "interop", "com", "media-foundation"]
supersedes: ""
superseded_by: ""
---

# ADR-0037: Source-generated COM for the Periphery.Camera Windows backend

## Status

> **History note:** an earlier revision of this ADR documented a "raw vtable
> invocation workaround" for `IMFSample` / `IMFMediaBuffer` / `IMF2DBuffer`
> based on the diagnosis that those interfaces' QueryInterface tables were
> restricted on .NET 10. **That diagnosis was wrong.** The cause was a typo
> in the `[Guid("…")]` attribute on `IMFSample` (last 12 hex digits were
> `5a1a54d4f179` instead of the canonical `5a1c634f58e4`). With the
> correct IID, every cast works on every camera; no workaround is needed.
> The cautionary tale is preserved below as Hazard A in the pattern doc.

## Context

When the Periphery.Camera Windows backend was ported from `[ComImport]` to
source-generated COM (`[GeneratedComInterface]`, follow-up to ADR-0035),
two distinct bugs surfaced — only one of them was real:

1. **Real bug — missing vtable slots.** The legacy `[ComImport]`
   declarations omitted real native methods that nobody had been calling:
   `IMFAttributes::GetStringLength` (slot 11) and
   `IMFSourceReader::SetCurrentPosition` (slot 8). Built-in COM had
   tolerated these omissions because it generates IL stubs lazily;
   source-gen lays the whole vtable down up front, so the off-by-one
   shifted every later method onto the wrong native function. We
   declared every native slot in source-gen interface order to fix it.

2. **Apparent bug — `InvalidCastException` on every `(IMFSample)cast`.**
   On the AVerMedia PW513 webcam, every sample returned by
   `IMFSourceReader::ReadSample` threw `InvalidCastException` from
   `Marshal.ThrowExceptionForHR`. We added a NexiGo N60 webcam from a
   different vendor — same crash. We diagnosed this as a "restricted
   QI" issue (the COM object's QueryInterface table refusing to admit
   it implemented `IMFSample` / `IMFMediaBuffer` / `IMF2DBuffer`,
   despite the vtable layout being compatible) and implemented a
   raw-vtable workaround. The workaround captured frames successfully.

The "restricted QI" diagnosis was wrong. After comparing against
[smourier/DirectNAot](https://github.com/smourier/DirectNAot) and
[TerraFX.Interop.Windows](https://github.com/terrafx/terrafx.interop.windows)
— two large open-source `[GeneratedComInterface]` libraries that bind
the full Windows SDK — both used a different IID for `IMFSample` than
ours. Cross-checking against
[microsoft/win32metadata's `mfobjects.h`](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/mfobjects.h)
confirmed the canonical IID is `c40a00f2-b93a-4d80-ae8c-5a1c634f58e4`
(`MIDL_INTERFACE("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4") IMFSample`),
not the `c40a00f2-b93a-4d80-ae8c-5a1a54d4f179` we had transcribed from
some non-canonical source early in the rewrite. With the correct IID,
QI returns `S_OK` on every sample on both cameras and the source-gen
cast works as designed.

## Decision

The Periphery.Camera Windows backend uses **`[GeneratedComInterface]`
end-to-end**, including for `IMFSample`, `IMFMediaBuffer`, and
`IMF2DBuffer`. The full set of source-generated interfaces:

- `IMFAttributes`, `IMFMediaType`, `IMFActivate`, `IMFMediaSource`,
  `IMFSourceReader`, `IMFSample`, `IMFMediaBuffer`, `IMF2DBuffer`
- `IAMCameraControl`, `IAMVideoProcAmp` (DirectShow controls
  reachable via QI from the MF source)

All IIDs are verified against the canonical Windows SDK headers in
`microsoft/win32metadata`. No raw vtable invocation is used. The cast
operator and source-gen RCWs handle wrapper lifetimes via
`ComObject.FinalRelease()` / `MfInterop.Release<T>(ref T?)`.

`MfInterop` retains a `ProbeQi(nint, in Guid, string)` diagnostic
helper that calls `Marshal.QueryInterface` directly and prints the HR
to stderr. It's intended to be dropped in temporarily when a future
contributor hits an `InvalidCastException` and wants to confirm whether
their IID is correct — same diagnostic recipe that, with hindsight,
would have caught the typo in this ADR's original investigation
within minutes.

## Rationale

1. **Source-gen is the canonical .NET 8+ approach.** Built-in COM
   marshalling is disabled by default on .NET 8/10, so any
   `[ComImport]` code path will throw `NotSupportedException` at
   runtime. Source-gen is also AOT- and trim-friendly, which the
   project requires.

2. **No real evidence that mainline MF interfaces have restricted
   QI.** The two open-source peers we found ([DirectNAot][dn],
   [TerraFX.Interop.Windows][terrafx]) both source-gen `IMFSample`
   directly with the canonical IID and don't carry any restricted-QI
   workaround. Our prior conclusion that the issue was "systemic to
   the MF source-reader path on .NET 10, not vendor-specific" was
   correct in observation but wrong in diagnosis: the issue was
   systemic because a wrong IID always produces the same wrong
   answer regardless of which COM object you ask.

3. **Raw vtable invocation has real costs.** It gives up source-gen's
   compile-time type checking on the affected calls — a wrong slot
   index or wrong delegate signature becomes a runtime crash instead
   of a build error. We had no real reason to pay that cost.

[dn]: https://github.com/smourier/DirectNAot
[terrafx]: https://github.com/terrafx/terrafx.interop.windows

## Alternatives considered

- **Keep raw vtable invocation as a defensive measure.** Rejected: no
  evidence it provides any defensive value, and it loses type-checking.
  If a future MF interface really did have a restricted QI table, we'd
  reach for raw vtable then — but we'd want positive evidence first
  (via `ProbeQi`), not a precautionary blanket workaround.
- **Roll back to `[ComImport]`.** Not an option: built-in COM is
  disabled by default in .NET 8+, the runtime throws
  `NotSupportedException` on first use.
- **Use a third-party MF binding library** (`Vortice.MediaFoundation`,
  `DirectNAot`) instead of declaring our own interfaces. Plausible
  long-term direction but out of scope here. Our subset of MF (8
  interfaces) is small enough that maintaining it directly is fine,
  and we control exactly which methods are exposed to the rest of the
  backend.

## Consequences

- **Code is simpler.** `MfCameraBackend.ExtractFrame` uses normal
  source-gen casts (`buffer is IMF2DBuffer buffer2D`,
  `sample.ConvertToContiguousBuffer(out IMFMediaBuffer? buffer)`)
  with full type checking.
- **Diagnostics are honest.** Future contributors who hit
  `InvalidCastException` will be steered toward verifying the IID
  rather than implementing a phantom workaround. The pattern doc's
  Hazard A captures this directly.
- **`ProbeQi` is the canonical first move** when a cast fails. One
  line, prints the HR with a note pointing back at the SDK-header
  verification step.

## Affected files

- `src/Periphery.Camera/Windows/MfInterop.cs` — `[GeneratedComInterface]`
  declarations for `IMFSample`, `IMFMediaBuffer`, `IMF2DBuffer` with
  canonical IIDs; `ProbeQi` diagnostic helper.
- `src/Periphery.Camera/Windows/MfCameraBackend.cs` — `ExtractFrame`
  uses source-gen casts.
- `docs/patterns/source-generated-com-interop.md` — Hazard A
  documents the wrong-IID class of bug with the diagnostic recipe.

## Lessons

1. When a "systemic" failure looks the same across unrelated devices,
   the most likely cause is something systemic *in our code*, not in
   the wider system. Consider that before reaching for a workaround.
2. IIDs are 16 bytes of opaque hex. Cross-check at least two
   independent canonical sources before trusting one. The Windows SDK
   header is the source of truth; natural-language docs and search
   results are not.
3. `Marshal.QueryInterface` directly on a raw pointer is a 5-line
   debug step that bypasses source-gen entirely. Run it *first* when
   a cast fails, not after building a workaround.
