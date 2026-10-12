// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Periphery.Usb.Windows;

namespace Periphery.Usb.Tests;

public class InterfaceGuidListTests
{
    private static readonly Guid StLink = new("DBCE1CD9-A320-4B51-A365-A0C3F3C5FB29");
    private static readonly Guid Other = new("DEE824EF-729B-4A0E-9C14-B7117D33A817");

    [Fact]
    public void A_multi_string_yields_each_guid_in_order()
    {
        var parsed = InterfaceGuidList.Parse("{DBCE1CD9-A320-4b51-A365-A0C3F3C5FB29}\0{dee824ef-729b-4a0e-9c14-b7117d33a817}\0\0");

        Assert.Equal([StLink, Other], parsed);
    }

    [Fact]
    public void A_single_string_without_braces_yields_one_guid()
    {
        Assert.Equal([StLink], InterfaceGuidList.Parse("dbce1cd9-a320-4b51-a365-a0c3f3c5fb29"));
    }

    [Fact]
    public void Entries_that_are_not_guids_are_skipped_and_duplicates_dropped()
    {
        var parsed = InterfaceGuidList.Parse("not-a-guid\0{DBCE1CD9-A320-4B51-A365-A0C3F3C5FB29}\0 {dbce1cd9-a320-4b51-a365-a0c3f3c5fb29} \0");

        Assert.Equal([StLink], parsed);
    }

    [Fact]
    public void An_empty_value_yields_nothing()
    {
        Assert.Empty(InterfaceGuidList.Parse("\0\0"));
    }
}
