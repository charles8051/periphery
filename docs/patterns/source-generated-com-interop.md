# Source-generated COM interop: gotchas and triage

> **Read this before** porting a Windows backend to source-generated COM
> (`[GeneratedComInterface]` / `[LibraryImport]`), or when debugging an
> existing one. Two issues in here will eat your day if you don't know
> about them up front.

The Periphery.Camera Windows backend (`src/Periphery.Camera/Windows/`) is
the canonical implementation of all of these. If you're adding a new
Windows-only extension that talks to a COM API (Media Foundation, DirectShow,
WASAPI, DXGI, etc.), this is required reading.

Authoritative reference: [.NET source-generated COM (Microsoft Learn)][docs].

[docs]: https://learn.microsoft.com/dotnet/standard/native-interop/comwrappers-source-generation

---

## Triage: I hit a problem, where do I look?

Match your symptom to the row, jump to the linked section.

| Symptom                                                            | Most likely cause                                                  | Where                            |
|---|---|---|
| `InvalidCastException` from `Marshal.ThrowExceptionForHR`          | Wrong IID in your `[Guid(...)]` attribute (most common — verify against the SDK header) | [Hazard A](#hazard-a-wrong-iid)                |
| `AccessViolationException` (`0xC0000005`) inside a generated stub  | Missing slot in your `[GeneratedComInterface]` — calls land on the wrong native method | [Hazard B](#hazard-b-missing-vtable-slot)      |
| HR-success returned but the *next* native call complains about the value | Same as above (silent variant — wrong native method accepted bogus args, your call returned `S_OK`) | [Hazard B](#hazard-b-missing-vtable-slot) |
| `SYSLIB1099` warning on `Marshal.GetObjectForIUnknown` etc.        | You're mixing legacy COM marshalling APIs with source-gen          | [Section 3](#3-marshalcomobject-apis-are-out)  |
| `SYSLIB1090` "StringMarshalling must match the base"               | Derived `[GeneratedComInterface]` disagrees with its base on `StringMarshalling` | [Section 5](#5-stringmarshallingutf16-propagation) |
| Generator emits a `<Method>Length_<N>` you never declared          | Stale incremental build — old declaration's output is cached       | [Section 6](#6-the-generator-output-is-the-truth) |
| Cast works in tests, throws against a real device                  | `FakeBackend` doesn't have the real IID round-trip; **see Hazard A** | [Hazard A](#hazard-a-wrong-iid)                |

---

## Background: what `QueryInterface` actually does

This is the foundation for both top hazards, so it's worth being precise.

Every COM object inherits from `IUnknown`, which has exactly three slots:

```c
HRESULT QueryInterface(REFIID riid, void** ppv);   // slot 0
ULONG   AddRef();                                  // slot 1
ULONG   Release();                                 // slot 2
```

`QueryInterface` (QI) is the universal "do you implement interface X?" call.
You hand it an IID, it returns `S_OK` and writes a pointer to its vtable for
that interface (with `AddRef`), or it returns `E_NOINTERFACE` (`0x80004002`)
and writes null. It's a virtual call into native code; the COM object can
answer however it wants.

In source-generated COM, **`(IFoo)comObject` is not free**. It routes through
`IDynamicInterfaceCastable.IsInterfaceImplemented(typeof(IFoo).TypeHandle,
throwIfNotImplemented: true)`, which calls `QueryInterface(IID_IFoo)` under
the hood. If QI returns `E_NOINTERFACE`, the cast throws
`InvalidCastException` — **regardless** of whether the underlying vtable
matches `IFoo`'s layout.

That last clause is the trap. Internalize it.

```
Source-gen cast (IFoo)x   ≡   x->QueryInterface(IID_IFoo) + cast
Built-in COM cast (IFoo)x ≡   x->QueryInterface(IID_IFoo) + cast
Direct vtable invocation  ≡   ((Vtbl*)x)->slot_N(args)        ← bypasses QI entirely
```

Built-in COM was disabled by default starting in .NET 8, so any `[ComImport]`
code paths in modern .NET don't actually run. The "it worked in .NET
Framework" intuition is misleading here — both built-in and source-gen do QI
on cast, but only source-gen actually runs.

---

## Hazard A — Wrong IID

**Symptom:** `InvalidCastException` (`Specified cast is not valid`) thrown
from `Marshal.ThrowExceptionForHR`, with a stack trace that goes through
`ComObject.IDynamicInterfaceCastable.IsInterfaceImplemented` →
`ComInterfaceMarshaller<T>.ConvertToManaged`. Often appears on the **first
real device** while passing all unit tests against a `Fake*Backend`.

### What goes wrong

The IID you put in the `[Guid("...")]` attribute on your
`[GeneratedComInterface]` doesn't match the canonical IID for that
interface. Source-gen casts route through `QueryInterface(IID_T)`. The
COM object correctly answers `E_NOINTERFACE` because the IID you handed
it doesn't identify any interface it implements, and the cast throws.

This **looks identical** to a "restricted QI" or "broken driver" bug —
the COM object is responding correctly, but the request itself is
malformed. The fix is in your code, not in handling broken COM.

### How we hit it

In the Periphery.Camera rewrite, `IMFSample`'s `[Guid]` was set to
`c40a00f2-b93a-4d80-ae8c-5a1a54d4f179`. The canonical IID per
`mfobjects.h` is `c40a00f2-b93a-4d80-ae8c-5a1c634f58e4`. Two fields
matched, the last 12 hex digits didn't. Every cast against every camera
threw `InvalidCastException`. We initially mis-attributed it to a
"restricted QI shim layer" because the QI failure was systemic across
two unrelated cameras. The actual cause was simpler and entirely in our
code.

The IID came from a non-canonical source (a search result or natural-
language doc). It looked plausible — same prefix as the real IID — and
went uncorrected. **Lesson: always cross-check IIDs against the
canonical SDK header in [microsoft/win32metadata][win32meta], not
against natural-language documentation.**

[win32meta]: https://github.com/microsoft/win32metadata/tree/main/generation/WinSDK/RecompiledIdlHeaders/um

### Diagnostic recipe (run this when you hit `InvalidCastException`)

Step 1. **Verify your IID against the canonical SDK header.** Find the
header that defines the interface (`mfobjects.h`, `mfreadwrite.h`,
`mfidl.h`, `dshow.h`, etc.) and grep for `MIDL_INTERFACE`:

```bash
curl -sL "https://raw.githubusercontent.com/microsoft/win32metadata/main/generation/WinSDK/RecompiledIdlHeaders/um/mfobjects.h" \
    | grep -B 1 'IMFSample :'
# → MIDL_INTERFACE("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")
#       IMFSample : public IMFAttributes
```

Compare digit-by-digit against your `[Guid("...")]` attribute. The vast
majority of `InvalidCastException` cases under source-gen come down to
this.

Step 2. **If the IID is right, prove it on the actual pointer.** Drop in
[`MfInterop.ProbeQi`](../../src/Periphery.Camera/Windows/MfInterop.cs):

```csharp
Guid iid = new("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4");
MfInterop.ProbeQi(rawPtr, in iid, "IMFSample");
// → "QI(IMFSample, {…}) ptr=0x… hr=0x00000000 (S_OK)"  ← good, cast should work
// → "QI(IMFSample, {…}) ptr=0x… hr=0x80004002 (E_NOINTERFACE)"  ← back to step 1
```

`Marshal.QueryInterface` goes directly to the COM object's QI vtable
slot — no source-gen wrapping. `S_OK` means the object truly implements
the interface and the cast should succeed; `E_NOINTERFACE` means
either the IID is wrong (revisit step 1) or you genuinely have a COM
pointer to a different interface (uncommon).

Step 3. **If you're sure the IID is right and QI returns S_OK but the
cast still fails**, check for the other usual suspects:
- Stale incremental build — see [Section 6](#6-the-generator-output-is-the-truth).
- Wrong managed-side type — e.g. casting a `ComObject` wrapped via a
  *different* `StrategyBasedComWrappers` instance; see
  [Section 8](#8-strategybasedcomwrappers-instance-scoping).

### Don't reach for raw vtable invocation as the fix

It's tempting to "work around" `E_NOINTERFACE` by bypassing the cast and
calling vtable slots directly through `unsafe` function pointers. Don't.
Fix the IID instead. Raw vtable invocation gives up source-gen's
type-checking on the affected calls — wrong slot or wrong delegate
signature becomes a runtime crash — and we have no real-world evidence
that any well-known MF interface lies in its QI table when queried with
the canonical IID. (We thought we did. We didn't. We had a typo.)

---

## Hazard B — Missing vtable slot

**Symptom:** `AccessViolationException`, *or* a call returns `S_OK` but a
later native call fails because the previous one quietly wrote to the
wrong place. The crash usually points inside an autogenerated stub
(`<Interface>F…__InterfaceImplementation.<Interface>.<Method>`).

### What goes wrong

Built-in COM (`[ComImport]`) generates an IL stub *per call*, lazily, only
for methods you actually invoke. If your interface declaration omits a
native method that nobody calls, nobody notices.

Source-gen lays down the **whole vtable up front** from your declaration,
in declaration order. A missing slot shifts every later method by one. The
first call into a misaligned slot dispatches to the wrong native function —
with the wrong register/stack layout, because every method's signature
differs.

We hit this twice in Periphery.Camera, both inherited from a published
`[ComImport]` declaration that omitted a real native slot:

| Interface           | Missing native slot          | Effect when called             |
|---|---|---|
| `IMFAttributes`     | `GetStringLength` (slot 11)  | `SetGUID` lands on `SetDouble`; passes `Guid*` where `double` is expected in `XMM2`. Returns `S_OK` (!), but the attribute store ends up with the wrong type, and the next `MFEnumDeviceSources` returns `MF_E_INVALIDMEDIATYPE`. |
| `IMFSourceReader`   | `SetCurrentPosition` (slot 8)| `ReadSample` lands on `SetCurrentPosition`; the native code dereferences `dwStreamIndex=0xFFFFFFFC` as a `GUID*`, immediate `0xC0000005`. |

### Diagnostic recipe

Build with the generator output preserved, then read the actual vtable
that source-gen produced:

```bash
dotnet build path/to/your.csproj -p:EmitCompilerGeneratedFiles=true
grep -E "Vtable\.[A-Za-z]+_[0-9]+" \
    path/to/obj/Debug/<tfm>/generated/Microsoft.Interop.ComInterfaceGenerator/\
Microsoft.Interop.ComInterfaceGenerator/<YourNamespace>.<Interface>.cs
```

Each line is `Vtable.<Name>_<slot> = &ABI_<Name>;`. The `<slot>` numbers
**must** match the SDK header for the interface, slot for slot. If your
`SetGUID_24` is at 24 but the header says slot 23, you've omitted a slot
between IUnknown and `SetGUID`.

Then cross-check against the SDK header. For `IMFAttributes` that's
`mfobjects.h`; for `IMFSourceReader` that's `mfreadwrite.h`. Count slots
starting at 3 (after QI/AddRef/Release).

### Don't try to patch with `_VtblGap*`

Built-in COM honored `void _VtblGap1_5();` to skip slots. Source-gen
treats it as a real method and crashes at runtime — see
[dotnet/runtime#102421][gh-102421]. Just declare every slot, with the
right signature.

[gh-102421]: https://github.com/dotnet/runtime/issues/102421

---

## 3. `Marshal.*ComObject` APIs are out

These warn `SYSLIB1099` under source-gen and don't actually work:

| Legacy                               | Source-gen replacement                                              |
|---|---|
| `Marshal.GetObjectForIUnknown(ptr)`  | `wrappers.GetOrCreateObjectForComInstance(ptr, CreateObjectFlags.UniqueInstance)` |
| `Marshal.GetIUnknownForObject(obj)`  | Pass the typed wrapper directly to a `[LibraryImport]` parameter; the marshaller calls `GetOrCreateComInterfaceForObject` for you. |
| `Marshal.ReleaseComObject(obj)`      | `((ComObject)obj).FinalRelease()`                                   |
| `(IFoo)source` for QI                | Still `(IFoo)source` — but routes through `QueryInterface` (see Hazard A). Use `as` for null on failure, `(T)` to throw. |
| `typeof(IFoo).GUID`                  | A `static readonly Guid IID_IFoo = new("…")` constant. Reflection breaks under NativeAOT. |

We centralize the disposal pattern in `MfInterop.Release<T>(ref T?)`:

```csharp
internal static void Release<T>(ref T? wrapper) where T : class
{
    if (wrapper is ComObject co) co.FinalRelease();
    wrapper = null;
}
```

Don't mix the two styles in one file.

---

## 4. Walking COM-allocated arrays

Functions like `MFEnumDeviceSources` return a `CoTaskMem`-allocated array of
`IUnknown*`. Walk it with ComWrappers, free the buffer afterwards:

```csharp
int hr = MfInterop.MFEnumDeviceSources(attrs, out nint arrayPtr, out uint count);
ThrowForHr(hr, "MFEnumDeviceSources failed");

var activates = new IMFActivate[count];
try
{
    for (int i = 0; i < (int)count; i++)
    {
        nint ptr = Marshal.ReadIntPtr(arrayPtr, i * nint.Size);
        activates[i] = (IMFActivate)MfInterop.Wrappers.GetOrCreateObjectForComInstance(
            ptr, CreateObjectFlags.UniqueInstance);
    }
}
finally
{
    Marshal.FreeCoTaskMem(arrayPtr);  // free the buffer; each wrapper owns its element
}
```

`UniqueInstance` matters — you want a fresh `ComObject` per entry so
`FinalRelease()` on one doesn't disturb the others.

---

## 5. `StringMarshalling.Utf16` propagation

Source-gen requires that any `[GeneratedComInterface]` derived from another
`[GeneratedComInterface]` declare the **same** `StringMarshalling` value
(`SYSLIB1090`). Setting it on `IMFAttributes` forces every derived interface
to also set it.

For wide vtables where only one or two methods involve strings, it's often
cleaner to keep the interface marshalling-free and convert manually:

```csharp
[PreserveSig] int GetAllocatedString(in Guid guidKey, out nint ppwszValue, out uint pcchLength);

// caller:
internal static int GetAllocatedString(IMFAttributes attrs, in Guid key, out string value)
{
    int hr = attrs.GetAllocatedString(key, out nint ptr, out _);
    if (hr < 0 || ptr == 0) { value = string.Empty; return hr; }
    try   { value = Marshal.PtrToStringUni(ptr) ?? string.Empty; return hr; }
    finally { Marshal.FreeCoTaskMem(ptr); }
}
```

---

## 6. The generator output IS the truth

When a port "compiles fine but crashes at runtime," the actually-generated
vtable is the source of truth. Build with `-p:EmitCompilerGeneratedFiles=true`
and inspect:

- `obj/<config>/<tfm>/generated/Microsoft.Interop.ComInterfaceGenerator/.../<Interface>.cs`

Things to check there:

- The `Vtable.<Name>_<N>` slot numbers match the native header.
- Each method's `delegate* unmanaged[MemberFunction]<...>` parameter list
  matches the C signature (especially: pointers vs. by-value structs,
  blittable vs. marshalled types, `[PreserveSig]` returning `int` for
  HRESULT methods).
- `IIUnknownInterfaceType.Iid` decodes to the right GUID.

**Stale incremental builds will lie to you.** If a method appears in the
generator output that isn't in your source, blow away `obj/` and rebuild
before debugging further — that "phantom" is leftover from a previous
declaration. Spent an hour on this one.

---

## 7. Inheritance is exact, not "best effort"

Source-gen lays out derived-interface vtables as
`<base methods, in order> <derived methods, in order>`. There's no shadowing,
no `new` keyword, no auto-deduplication.

```csharp
[GeneratedComInterface]
[Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555")]
internal partial interface IMFMediaType : IMFAttributes
{
    // IMFAttributes contributes slots 3–32. New methods start at 33.
    [PreserveSig] int GetMajorType(out Guid pguidMajorType);
    // …
}
```

If `IMFAttributes` is missing a slot, **every** derived interface inherits
the misalignment. Fix the base first.

There's also a known bug for 3+ level chains in
[dotnet/runtime#86662][gh-86662] — for very deep hierarchies, prefer flat
declarations until that's resolved.

[gh-86662]: https://github.com/dotnet/runtime/issues/86662

---

## 8. `StrategyBasedComWrappers` instance scoping

`ComInterfaceMarshaller<T>` uses an internal
`StrategyBasedComWrappers.DefaultMarshallingInstance`. If you also create
a custom one for explicit pointer conversions you'll have two instances in
play. **Pick one** for explicit conversions and use it consistently:

```csharp
internal static readonly StrategyBasedComWrappers Wrappers = new();
```

Don't `new` one per call site. Wrapper caching prevents re-QI churn.

---

## Search keywords

If you arrived here from a stack-trace search, these are the strings most
likely to land on this page:

- `InvalidCastException` `IDynamicInterfaceCastable.IsInterfaceImplemented`
  `ComInterfaceMarshaller` `ConvertToManaged` `Marshal.ThrowExceptionForHR`
  (almost always: wrong IID — see Hazard A)
- `0x80004002` `E_NOINTERFACE` (verify the IID against the SDK header)
- `AccessViolationException` `0xC0000005` `<Interface>F…__InterfaceImplementation`
- `MF_E_INVALIDMEDIATYPE` after `SetGUID` (vtable misalignment, see Hazard B)
- `SYSLIB1090` `SYSLIB1099` `_VtblGap`

---

## References

- [.NET source-generated COM (Microsoft Learn)](https://learn.microsoft.com/dotnet/standard/native-interop/comwrappers-source-generation)
- [SYSLIB1090–1099 diagnostics](https://learn.microsoft.com/dotnet/fundamentals/syslib-diagnostics/syslib-cominterfacegenerator)
- [dotnet/runtime#102421 — `_VtblGap*` not honored](https://github.com/dotnet/runtime/issues/102421)
- [dotnet/runtime#86662 — derivation chains for 3+ interfaces](https://github.com/dotnet/runtime/issues/86662)
- [dotnet/runtime#114468 — ILC-friendly vtables for RVA folding](https://github.com/dotnet/runtime/issues/114468)
- [DerivedComInterfaces design doc](https://github.com/dotnet/runtime/blob/main/docs/design/libraries/ComInterfaceGenerator/DerivedComInterfaces.md)
- [microsoft/win32metadata — canonical SDK headers](https://github.com/microsoft/win32metadata/tree/main/generation/WinSDK/RecompiledIdlHeaders/um) (the source of truth for IIDs)
- [smourier/DirectNAot](https://github.com/smourier/DirectNAot) and
  [TerraFX.Interop.Windows](https://github.com/terrafx/terrafx.interop.windows) —
  worked examples of large `[GeneratedComInterface]` libraries (cross-check
  IID values when in doubt)
- ADR-0037 — "Source-generated COM for Periphery.Camera Windows backend"
  ([`docs/adr/0037-mf-sample-raw-vtable.md`](../adr/0037-mf-sample-raw-vtable.md))
