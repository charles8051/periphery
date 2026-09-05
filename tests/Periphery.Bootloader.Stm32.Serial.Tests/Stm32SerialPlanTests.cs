namespace Periphery.Bootloader.Stm32.Serial.Tests;

public class Stm32SerialPlanTests
{
    private const uint FlashBase = 0x08000000;

    private static FirmwareImage Image(uint address, int length) =>
        FirmwareImage.FromBytes(address, new byte[length]);

    [Fact]
    public void PageCountToCover_rounds_up_to_a_whole_page()
    {
        Assert.Equal(1, Stm32SerialPlan.PageCountToCover(Image(FlashBase, 1), 2048));
        Assert.Equal(1, Stm32SerialPlan.PageCountToCover(Image(FlashBase, 2048), 2048));
        Assert.Equal(2, Stm32SerialPlan.PageCountToCover(Image(FlashBase, 2049), 2048));
    }

    [Fact]
    public void PageCountToCover_counts_from_page_zero_not_from_the_image_base()
    {
        // Extended Erase always erases 0..N, so an image high in flash still erases everything
        // beneath it. Documented on Stm32SerialStep.ErasePages.
        Assert.Equal(17, Stm32SerialPlan.PageCountToCover(Image(FlashBase + 0x8000, 1), 2048));
    }

    [Fact]
    public void PageCountToCover_is_zero_for_an_empty_image()
    {
        Assert.Equal(0, Stm32SerialPlan.PageCountToCover(FirmwareImage.Empty, 2048));
        Assert.Equal(0, Stm32SerialPlan.PageCountToCover(Image(FlashBase, 0), 2048));
    }

    [Fact]
    public void Plan_chunks_writes_at_the_configured_size_with_absolute_addresses()
    {
        var image = FirmwareImage.FromBytes(FlashBase, new byte[600]);
        var serial = Stm32SerialOptions.Default with { WriteChunkSize = 256 };
        var steps = Stm32SerialPlan.Plan(image, serial, FlashOptions.Default);

        var writes = steps.OfType<Stm32SerialStep.Write>().ToArray();
        Assert.Equal(3, writes.Length);
        Assert.Equal((FlashBase, 256), (writes[0].Address, writes[0].Data.Length));
        Assert.Equal((FlashBase + 256, 256), (writes[1].Address, writes[1].Data.Length));
        Assert.Equal((FlashBase + 512, 88), (writes[2].Address, writes[2].Data.Length));
    }

    [Fact]
    public void Plan_orders_erase_then_writes_then_verify_then_go()
    {
        var image = FirmwareImage.FromBytes(FlashBase, new byte[8]);
        var steps = Stm32SerialPlan.Plan(image, Stm32SerialOptions.Default, FlashOptions.Default);

        Assert.Collection(steps,
            s => Assert.IsType<Stm32SerialStep.ErasePages>(s),
            s => Assert.IsType<Stm32SerialStep.Write>(s),
            s => Assert.IsType<Stm32SerialStep.Verify>(s),
            s => Assert.IsType<Stm32SerialStep.Go>(s));
    }

    [Fact]
    public void Plan_emits_a_mass_erase_only_when_mass_is_asked_for()
    {
        var image = FirmwareImage.FromBytes(FlashBase, new byte[8]);
        var options = FlashOptions.Default with { Erase = EraseMode.Mass };

        var steps = Stm32SerialPlan.Plan(image, Stm32SerialOptions.Default, options);

        // No page count is computed and none is needed: the whole flash goes, including the parts
        // the image does not reach.
        Assert.IsType<Stm32SerialStep.EraseAll>(steps[0]);
        Assert.DoesNotContain(steps, s => s is Stm32SerialStep.ErasePages);
    }

    [Fact]
    public void Plan_omits_erase_verify_and_go_when_the_options_turn_them_off()
    {
        var image = FirmwareImage.FromBytes(FlashBase, new byte[8]);
        var options = new FlashOptions { Erase = EraseMode.None, Verify = false, LeaveAfterFlash = false };

        var steps = Stm32SerialPlan.Plan(image, Stm32SerialOptions.Default, options);

        Assert.Single(steps);
        Assert.IsType<Stm32SerialStep.Write>(steps[0]);
    }

    [Fact]
    public void Plan_jumps_to_the_lowest_segment_address()
    {
        var image = FirmwareImage.FromSegments(new[]
        {
            new FirmwareSegment(FlashBase + 0x4000, new byte[4]),
            new FirmwareSegment(FlashBase + 0x1000, new byte[4]),
        });

        var steps = Stm32SerialPlan.Plan(image, Stm32SerialOptions.Default, FlashOptions.Default);

        var go = Assert.IsType<Stm32SerialStep.Go>(steps[^1]);
        Assert.Equal(FlashBase + 0x1000, go.JumpAddress);
    }

    [Fact]
    public void Plan_skips_empty_segments()
    {
        var image = FirmwareImage.FromSegments(new[]
        {
            new FirmwareSegment(FlashBase, new byte[4]),
            new FirmwareSegment(FlashBase + 0x100, ReadOnlyMemory<byte>.Empty),
        });

        var steps = Stm32SerialPlan.Plan(image, Stm32SerialOptions.Default, FlashOptions.Default);

        Assert.Single(steps.OfType<Stm32SerialStep.Write>());
        Assert.Single(steps.OfType<Stm32SerialStep.Verify>());
    }
}
