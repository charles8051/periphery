// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;
using System.Globalization;

namespace Periphery.Usb.Browser.Core;

/// <summary>Which of the two <see cref="DeviceId"/> forms a grant carries (ADR-0092 D5).</summary>
internal enum UsbGrantIdForm
{
    /// <summary>
    /// <c>webusb:VVVV:PPPP:&lt;serial&gt;</c>. Built from the three facts Chromium keys a persisted
    /// grant on, so it is stable across replug, reload and restart while the grant lasts.
    /// </summary>
    Durable,

    /// <summary>
    /// <c>webusb-session:VVVV:PPPP:&lt;n&gt;</c>. Stable for the current attachment only.
    /// </summary>
    Session,
}

/// <summary>One attached, granted device and the id the registry gave it.</summary>
internal sealed record UsbGrant(GrantHandle Handle, UsbGrantFacts Facts, DeviceId Id, UsbGrantIdForm Form);

/// <summary>
/// The document's attached WebUSB grants and the ids minted for them. An immutable value: every
/// change goes through <see cref="UsbGrantIds"/> and returns a new registry.
/// </summary>
internal sealed record UsbGrantRegistry
{
    /// <summary>A registry with no grants that has minted no session ids.</summary>
    public static UsbGrantRegistry Empty { get; } = new();

    /// <summary>The attached grants, keyed by the shell's handle.</summary>
    public ImmutableDictionary<GrantHandle, UsbGrant> Grants { get; init; } =
        ImmutableDictionary<GrantHandle, UsbGrant>.Empty;

    /// <summary>
    /// How many session ids this registry has minted. The next one is this plus one. It only
    /// grows, so a session id is never reused for another attachment within the document.
    /// </summary>
    public int SessionIdsMinted { get; init; }
}

/// <summary>
/// Mints and resolves WebUSB device ids (ADR-0092 D5). Total functions over
/// <see cref="UsbGrantRegistry"/>: no JS, no clock, no IO.
/// </summary>
/// <remarks>
/// <para>A device gets the durable form when it reports a serial number and no other attached
/// grant shares its vendor ID, product ID and serial. Otherwise it gets a session id.</para>
/// <para>When a device arrives that shares the key of an attached device holding a durable id,
/// that device is re-minted with a session id too. Two units with one serial cannot be told apart
/// after a reload, so neither may keep the id that claims to survive one. The result is the same
/// whichever twin is admitted first.</para>
/// <para>An attached device is never promoted back. Once its twin leaves, it keeps its session id
/// until it is unplugged; its next attachment gets the durable form again.</para>
/// </remarks>
internal static class UsbGrantIds
{
    internal const string DurablePrefix = "webusb:";
    internal const string SessionPrefix = "webusb-session:";

    /// <summary>
    /// Admits the device behind <paramref name="handle"/>. Admitting a handle the registry already
    /// holds returns its current id and leaves the registry unchanged, because
    /// <c>getDevices()</c> returns the same <c>USBDevice</c> object on every call while the
    /// device stays attached.
    /// </summary>
    public static (UsbGrantRegistry Next, DeviceId Id) Admit(
        UsbGrantRegistry state, UsbGrantFacts facts, GrantHandle handle)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(facts);

        if (state.Grants.TryGetValue(handle, out var existing))
            return (state, existing.Id);

        var grants = state.Grants;
        int minted = state.SessionIdsMinted;

        // Ordered by handle so the session numbers handed out do not depend on the dictionary's
        // enumeration order.
        var durableTwins = state.Grants.Values
            .Where(g => g.Form == UsbGrantIdForm.Durable && g.Facts.SharesGrantKeyWith(facts))
            .OrderBy(g => g.Handle.Value);

        foreach (var twin in durableTwins)
        {
            minted++;
            grants = grants.SetItem(twin.Handle, twin with
            {
                Id = SessionId(twin.Facts, minted),
                Form = UsbGrantIdForm.Session,
            });
        }

        bool shared = state.Grants.Values.Any(g => g.Facts.SharesGrantKeyWith(facts));

        UsbGrant admitted;
        if (facts.HasSerialNumber && !shared)
        {
            admitted = new UsbGrant(handle, facts, DurableId(facts), UsbGrantIdForm.Durable);
        }
        else
        {
            minted++;
            admitted = new UsbGrant(handle, facts, SessionId(facts, minted), UsbGrantIdForm.Session);
        }

        var next = state with
        {
            Grants = grants.Add(handle, admitted),
            SessionIdsMinted = minted,
        };
        return (next, admitted.Id);
    }

    /// <summary>
    /// Drops the grant behind <paramref name="handle"/>, on <c>disconnect</c> or after
    /// <c>forget()</c>. Releasing a handle the registry does not hold returns it unchanged.
    /// </summary>
    public static UsbGrantRegistry Release(UsbGrantRegistry state, GrantHandle handle)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Grants.ContainsKey(handle)
            ? state with { Grants = state.Grants.Remove(handle) }
            : state;
    }

    /// <summary>
    /// The attached grant <paramref name="id"/> names, or <see langword="null"/> when no attached
    /// grant has that id. The comparison is <see cref="DeviceId"/>'s, which ignores case.
    /// </summary>
    public static UsbGrant? Resolve(UsbGrantRegistry state, DeviceId id)
    {
        ArgumentNullException.ThrowIfNull(state);
        foreach (var grant in state.Grants.Values)
        {
            if (grant.Id == id)
                return grant;
        }
        return null;
    }

    internal static DeviceId DurableId(UsbGrantFacts facts)
        => new(string.Create(CultureInfo.InvariantCulture,
            $"{DurablePrefix}{facts.VendorId:X4}:{facts.ProductId:X4}:{facts.SerialNumber}"));

    internal static DeviceId SessionId(UsbGrantFacts facts, int n)
        => new(string.Create(CultureInfo.InvariantCulture,
            $"{SessionPrefix}{facts.VendorId:X4}:{facts.ProductId:X4}:{n}"));
}
