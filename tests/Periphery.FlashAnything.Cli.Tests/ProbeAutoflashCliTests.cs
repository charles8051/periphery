namespace Periphery.FlashAnything.Cli.Tests;

/// <summary>
/// The <c>--port</c> and <c>--repeat</c> surface (adr.md Decisions 8 and 10).
/// </summary>
public class ProbeAutoflashCliTests
{
    private static Parsed Parse(params string[] args)
    {
        var result = Cli.Parse(args);
        Assert.Null(result.Error);
        return result.Value!;
    }

    private static string ErrorFrom(params string[] args)
    {
        var result = Cli.Parse(args);
        Assert.NotNull(result.Error);
        return result.Error!;
    }

    [Fact]
    public void No_port_means_no_binding()
    {
        var p = Parse("autoflash", "--file", "app.hex");

        Assert.True(p.Ports.IsEmpty);
        Assert.Equal(RepeatMode.None, p.Repeat);
    }

    [Fact]
    public void A_port_is_bound()
    {
        var p = Parse("autoflash", "--file", "app.hex", "--port", "COM7");

        Assert.Equal(new SerialPortName("COM7"), Assert.Single(p.Ports));
    }

    [Fact]
    public void Ports_accumulate_for_a_multi_fixture_bench()
    {
        var p = Parse("autoflash", "--file", "app.hex", "--port", "COM7", "--port", "COM9");

        Assert.Equal(
            new[] { new SerialPortName("COM7"), new SerialPortName("COM9") },
            p.Ports);
    }

    [Fact]
    public void Repeat_defaults_off()
    {
        // The evidence a board left is weaker than the evidence one arrived, so a succession of
        // boards is something the operator asks for.
        var p = Parse("autoflash", "--file", "app.hex", "--port", "COM7");

        Assert.Equal(RepeatMode.None, p.Repeat);
    }

    [Fact]
    public void Bare_repeat_selects_the_inference_mode()
    {
        var p = Parse("autoflash", "--file", "app.hex", "--port", "COM7", "--repeat");

        Assert.Equal(RepeatMode.Silence, p.Repeat);
    }

    [Fact]
    public void Repeat_silence_is_spelled_out_too()
    {
        var p = Parse("autoflash", "--file", "app.hex", "--port", "COM7", "--repeat", "silence");

        Assert.Equal(RepeatMode.Silence, p.Repeat);
    }

    [Fact]
    public void Repeat_cts_is_refused_rather_than_treated_as_inference()
    {
        // adr.md describes --repeat=cts, where a present-detect line observes the departure. It is
        // not implemented, and accepting the spelling while inferring instead would hide exactly
        // the difference between observing and guessing.
        string error = ErrorFrom("autoflash", "--file", "app.hex", "--port", "COM7", "--repeat", "cts");

        Assert.Contains("not implemented", error);
    }

    [Fact]
    public void An_unknown_repeat_mode_is_refused()
    {
        string error = ErrorFrom("autoflash", "--file", "app.hex", "--port", "COM7", "--repeat", "always");

        Assert.Contains("Unknown --repeat mode", error);
    }

    [Fact]
    public void A_port_without_a_name_is_refused()
    {
        Assert.Contains("--port requires a port name", ErrorFrom("autoflash", "--file", "app.hex", "--port"));
    }

    [Fact]
    public void Port_does_not_swallow_the_option_after_it()
    {
        // "--port --yes" would otherwise bind a fixture called "--yes" and silently stay in dry
        // run: a typo that quietly disables the safety flag it ate.
        string error = ErrorFrom("autoflash", "--file", "app.hex", "--port", "--yes");

        Assert.Contains("--port requires a port name", error);
    }

    [Fact]
    public void Repeat_equals_cts_gets_the_explanation_not_unknown_option()
    {
        // The spelling adr.md uses. Being told "cts is not implemented, here is what silence does"
        // is the whole point of refusing it.
        string error = ErrorFrom("autoflash", "--file", "app.hex", "--port", "COM7", "--repeat=cts");

        Assert.Contains("not implemented", error);
        Assert.DoesNotContain("Unknown option", error);
    }

    [Fact]
    public void Repeat_equals_silence_is_accepted()
    {
        var p = Parse("autoflash", "--file", "app.hex", "--port", "COM7", "--repeat=silence");

        Assert.Equal(RepeatMode.Silence, p.Repeat);
    }

    [Fact]
    public void An_unknown_repeat_mode_in_the_equals_form_is_refused_too()
    {
        Assert.Contains("Unknown --repeat mode",
            ErrorFrom("autoflash", "--file", "app.hex", "--repeat=always"));
    }

    [Fact]
    public void Repeat_before_another_option_does_not_swallow_it()
    {
        // --repeat takes an optional value, so it must not eat the next flag.
        var p = Parse("autoflash", "--file", "app.hex", "--repeat", "--port", "COM7", "--yes");

        Assert.Equal(RepeatMode.Silence, p.Repeat);
        Assert.Equal(new SerialPortName("COM7"), Assert.Single(p.Ports));
        Assert.True(p.Yes);
    }
}
