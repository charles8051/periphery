namespace Periphery.Usb.Browser.Tests;

/// <summary>
/// The WebUSB id rules of ADR-0092 D5, run against the pure registry with no browser.
/// </summary>
public class UsbGrantIdsTests
{
    private static UsbGrantFacts Treehopper(string? serial) => new()
    {
        VendorId = 0x10C4,
        ProductId = 0x8A7E,
        SerialNumber = serial,
        ProductName = "Treehopper",
        ManufacturerName = "Treehopper",
    };

    private static (UsbGrantRegistry, DeviceId) Admit(UsbGrantRegistry state, UsbGrantFacts facts, int handle)
        => UsbGrantIds.Admit(state, facts, new GrantHandle(handle));

    [Fact]
    public void A_device_with_a_unique_serial_gets_the_durable_form()
    {
        var (_, id) = Admit(UsbGrantRegistry.Empty, Treehopper("AB12CD"), 1);

        Assert.Equal("webusb:10C4:8A7E:AB12CD", id.Value);
    }

    [Fact]
    public void Vendor_and_product_ids_are_four_digit_uppercase_hex()
    {
        var facts = new UsbGrantFacts { VendorId = 0x0483, ProductId = 0xdf11, SerialNumber = "x" };

        var (_, id) = Admit(UsbGrantRegistry.Empty, facts, 1);

        Assert.Equal("webusb:0483:DF11:x", id.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_device_without_a_serial_gets_a_session_id(string? serial)
    {
        var (_, id) = Admit(UsbGrantRegistry.Empty, Treehopper(serial), 1);

        Assert.Equal("webusb-session:10C4:8A7E:1", id.Value);
    }

    [Fact]
    public void Session_ids_count_up_across_the_document()
    {
        var (s1, a) = Admit(UsbGrantRegistry.Empty, Treehopper(null), 1);
        var (_, b) = Admit(s1, Treehopper(null), 2);

        Assert.Equal("webusb-session:10C4:8A7E:1", a.Value);
        Assert.Equal("webusb-session:10C4:8A7E:2", b.Value);
    }

    [Fact]
    public void A_released_session_number_is_never_handed_out_again()
    {
        var (s1, first) = Admit(UsbGrantRegistry.Empty, Treehopper(null), 1);
        var s2 = UsbGrantIds.Release(s1, new GrantHandle(1));
        var (_, second) = Admit(s2, Treehopper(null), 2);

        Assert.NotEqual(first, second);
        Assert.Equal("webusb-session:10C4:8A7E:2", second.Value);
    }

    [Fact]
    public void Admitting_a_held_handle_again_returns_its_id_and_the_same_registry()
    {
        var (s1, first) = Admit(UsbGrantRegistry.Empty, Treehopper(null), 7);

        var (s2, again) = Admit(s1, Treehopper(null), 7);

        Assert.Equal(first, again);
        Assert.Same(s1, s2);
    }

    [Fact]
    public void A_replugged_device_gets_the_same_durable_id_under_a_new_handle()
    {
        var (s1, before) = Admit(UsbGrantRegistry.Empty, Treehopper("AB12CD"), 1);
        var s2 = UsbGrantIds.Release(s1, new GrantHandle(1));

        var (_, after) = Admit(s2, Treehopper("AB12CD"), 2);

        Assert.Equal(before, after);
    }

    [Fact]
    public void A_twin_arriving_demotes_the_attached_device_and_both_get_session_ids()
    {
        var (s1, _) = Admit(UsbGrantRegistry.Empty, Treehopper("AB12CD"), 1);

        var (s2, second) = Admit(s1, Treehopper("AB12CD"), 2);

        var first = s2.Grants[new GrantHandle(1)];
        Assert.Equal(UsbGrantIdForm.Session, first.Form);
        Assert.Equal("webusb-session:10C4:8A7E:1", first.Id.Value);
        Assert.Equal("webusb-session:10C4:8A7E:2", second.Value);
        Assert.Null(UsbGrantIds.Resolve(s2, "webusb:10C4:8A7E:AB12CD"));
    }

    [Fact]
    public void Twins_end_with_session_ids_whichever_is_admitted_first()
    {
        var (a1, _) = Admit(UsbGrantRegistry.Empty, Treehopper("AB12CD"), 1);
        var (a2, _) = Admit(a1, Treehopper("AB12CD"), 2);

        var (b1, _) = Admit(UsbGrantRegistry.Empty, Treehopper("AB12CD"), 2);
        var (b2, _) = Admit(b1, Treehopper("AB12CD"), 1);

        Assert.All(a2.Grants.Values, g => Assert.Equal(UsbGrantIdForm.Session, g.Form));
        Assert.All(b2.Grants.Values, g => Assert.Equal(UsbGrantIdForm.Session, g.Form));
    }

    [Fact]
    public void Serials_that_differ_only_in_case_are_twins()
    {
        var (s1, _) = Admit(UsbGrantRegistry.Empty, Treehopper("ab12cd"), 1);

        var (s2, _) = Admit(s1, Treehopper("AB12CD"), 2);

        Assert.All(s2.Grants.Values, g => Assert.Equal(UsbGrantIdForm.Session, g.Form));
    }

    [Fact]
    public void One_serial_under_two_product_ids_is_not_a_twin()
    {
        var other = Treehopper("AB12CD") with { ProductId = 0xEAC9 };
        var (s1, _) = Admit(UsbGrantRegistry.Empty, Treehopper("AB12CD"), 1);

        var (s2, id) = Admit(s1, other, 2);

        Assert.Equal("webusb:10C4:EAC9:AB12CD", id.Value);
        Assert.All(s2.Grants.Values, g => Assert.Equal(UsbGrantIdForm.Durable, g.Form));
    }

    [Fact]
    public void A_third_twin_leaves_the_demoted_ids_alone()
    {
        var (s1, _) = Admit(UsbGrantRegistry.Empty, Treehopper("AB12CD"), 1);
        var (s2, _) = Admit(s1, Treehopper("AB12CD"), 2);

        var (s3, third) = Admit(s2, Treehopper("AB12CD"), 3);

        Assert.Equal(s2.Grants[new GrantHandle(1)], s3.Grants[new GrantHandle(1)]);
        Assert.Equal(s2.Grants[new GrantHandle(2)], s3.Grants[new GrantHandle(2)]);
        Assert.Equal("webusb-session:10C4:8A7E:3", third.Value);
    }

    [Fact]
    public void A_device_whose_twin_leaves_keeps_its_session_id()
    {
        var (s1, _) = Admit(UsbGrantRegistry.Empty, Treehopper("AB12CD"), 1);
        var (s2, _) = Admit(s1, Treehopper("AB12CD"), 2);
        var demoted = s2.Grants[new GrantHandle(1)].Id;

        var s3 = UsbGrantIds.Release(s2, new GrantHandle(2));
        var (_, again) = Admit(s3, Treehopper("AB12CD"), 1);

        Assert.Equal(demoted, again);
    }

    [Fact]
    public void A_twin_arriving_beside_a_demoted_device_gets_a_session_id()
    {
        var (s1, _) = Admit(UsbGrantRegistry.Empty, Treehopper("AB12CD"), 1);
        var (s2, _) = Admit(s1, Treehopper("AB12CD"), 2);
        var s3 = UsbGrantIds.Release(s2, new GrantHandle(2));

        var (_, replugged) = Admit(s3, Treehopper("AB12CD"), 3);

        Assert.StartsWith(UsbGrantIds.SessionPrefix, replugged.Value);
    }

    [Fact]
    public void The_durable_form_returns_once_no_twin_is_attached()
    {
        var (s1, _) = Admit(UsbGrantRegistry.Empty, Treehopper("AB12CD"), 1);
        var (s2, _) = Admit(s1, Treehopper("AB12CD"), 2);
        var s3 = UsbGrantIds.Release(UsbGrantIds.Release(s2, new GrantHandle(1)), new GrantHandle(2));

        var (_, id) = Admit(s3, Treehopper("AB12CD"), 3);

        Assert.Equal("webusb:10C4:8A7E:AB12CD", id.Value);
    }

    [Fact]
    public void Resolve_finds_a_grant_by_id_ignoring_case()
    {
        var (s1, _) = Admit(UsbGrantRegistry.Empty, Treehopper("ab12cd"), 4);

        var grant = UsbGrantIds.Resolve(s1, "WEBUSB:10c4:8a7e:AB12CD");

        Assert.NotNull(grant);
        Assert.Equal(new GrantHandle(4), grant.Handle);
    }

    [Fact]
    public void Resolve_returns_null_for_a_released_grant()
    {
        var (s1, id) = Admit(UsbGrantRegistry.Empty, Treehopper("AB12CD"), 1);

        var s2 = UsbGrantIds.Release(s1, new GrantHandle(1));

        Assert.Null(UsbGrantIds.Resolve(s2, id));
    }

    [Fact]
    public void Releasing_an_unknown_handle_returns_the_same_registry()
    {
        var (s1, _) = Admit(UsbGrantRegistry.Empty, Treehopper(null), 1);

        Assert.Same(s1, UsbGrantIds.Release(s1, new GrantHandle(99)));
    }
}
