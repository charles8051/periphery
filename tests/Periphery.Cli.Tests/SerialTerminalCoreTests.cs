using System.IO.Ports;
using System.Text;
using Periphery.Cli.Serial.Core;

namespace Periphery.Cli.Tests;

/// <summary>The pure rules behind <c>periphery serial</c>.</summary>
public sealed class KeyEncoderTests
{
    private static ConsoleKeyInfo Key(char c, ConsoleKey key = default, bool control = false) =>
        new(c, key, shift: false, alt: false, control: control);

    private static byte[] Sent(KeyAction action) => Assert.IsType<KeyAction.Send>(action).Bytes;

    [Fact]
    public void Enter_SendsTheConfiguredLineEnding()
    {
        var enter = Key('\r', ConsoleKey.Enter);

        Assert.Equal(new byte[] { 0x0D }, Sent(new KeyEncoder(LineEnding.Cr).Encode(enter).Action));
        Assert.Equal(new byte[] { 0x0A }, Sent(new KeyEncoder(LineEnding.Lf).Encode(enter).Action));
        Assert.Equal(new byte[] { 0x0D, 0x0A }, Sent(new KeyEncoder(LineEnding.CrLf).Encode(enter).Action));
    }

    [Fact]
    public void CtrlRightBracket_Quits()
    {
        var (_, action) = new KeyEncoder(LineEnding.Cr).Encode(Key('\x1D', ConsoleKey.Oem6, control: true));

        Assert.IsType<KeyAction.Quit>(action);
    }

    [Fact]
    public void CtrlC_IsSentAsEtx()
    {
        var (_, action) = new KeyEncoder(LineEnding.Cr).Encode(Key('\x03', ConsoleKey.C, control: true));

        Assert.Equal(new byte[] { 0x03 }, Sent(action));
    }

    [Fact]
    public void ArrowKeys_SendVt100Sequences()
    {
        var encoder = new KeyEncoder(LineEnding.Cr);

        Assert.Equal("\e[A"u8.ToArray(), Sent(encoder.Encode(Key('\0', ConsoleKey.UpArrow)).Action));
        Assert.Equal("\e[D"u8.ToArray(), Sent(encoder.Encode(Key('\0', ConsoleKey.LeftArrow)).Action));
        Assert.Equal("\e[3~"u8.ToArray(), Sent(encoder.Encode(Key('\0', ConsoleKey.Delete)).Action));
    }

    [Fact]
    public void KeyWithNoCharacterOrSequence_IsIgnored()
    {
        var (_, action) = new KeyEncoder(LineEnding.Cr).Encode(Key('\0', ConsoleKey.F5));

        Assert.IsType<KeyAction.Ignore>(action);
    }

    [Fact]
    public void NonAsciiCharacter_IsSentAsUtf8()
    {
        var (_, action) = new KeyEncoder(LineEnding.Cr).Encode(Key('é'));

        Assert.Equal(new byte[] { 0xC3, 0xA9 }, Sent(action));
    }

    [Fact]
    public void SurrogatePair_IsHeldUntilComplete_ThenSentAsOneCharacter()
    {
        string emoji = "😀";
        var encoder = new KeyEncoder(LineEnding.Cr);

        (encoder, var first) = encoder.Encode(Key(emoji[0]));
        (encoder, var second) = encoder.Encode(Key(emoji[1]));

        Assert.IsType<KeyAction.Ignore>(first);
        Assert.Equal(Encoding.UTF8.GetBytes(emoji), Sent(second));
        Assert.Null(encoder.PendingHighSurrogate);
    }

    [Fact]
    public void LoneLowSurrogate_IsDropped()
    {
        var (_, action) = new KeyEncoder(LineEnding.Cr).Encode(Key("😀"[1]));

        Assert.IsType<KeyAction.Ignore>(action);
    }
}

public sealed class SerialLineSettingsTests
{
    [Fact]
    public void Default_Is115200_8N1()
    {
        Assert.Equal("115200 8N1", SerialLineSettings.Default.Describe());
    }

    [Fact]
    public void Describe_NamesParityStopBitsAndHandshake()
    {
        var line = new SerialLineSettings(9600, 7, Parity.Even, StopBits.Two, Handshake.RequestToSend);

        Assert.Equal("9600 7E2 rtscts", line.Describe());
    }

    [Theory]
    [InlineData("n", Parity.None)]
    [InlineData("EVEN", Parity.Even)]
    [InlineData("odd", Parity.Odd)]
    [InlineData("mark", Parity.Mark)]
    [InlineData("s", Parity.Space)]
    public void Parity_Parses(string text, Parity expected)
    {
        Assert.True(SerialLineSettings.TryParseParity(text, out var parity));
        Assert.Equal(expected, parity);
    }

    [Theory]
    [InlineData("1", StopBits.One)]
    [InlineData("1.5", StopBits.OnePointFive)]
    [InlineData("2", StopBits.Two)]
    public void StopBits_Parse(string text, StopBits expected)
    {
        Assert.True(SerialLineSettings.TryParseStopBits(text, out var stopBits));
        Assert.Equal(expected, stopBits);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("3")]
    [InlineData("one")]
    public void StopBits_RejectsOthers(string text)
    {
        Assert.False(SerialLineSettings.TryParseStopBits(text, out _));
    }

    [Fact]
    public void Parity_And_Handshake_RejectUnknownNames()
    {
        Assert.False(SerialLineSettings.TryParseParity("x", out _));
        Assert.False(SerialLineSettings.TryParseHandshake("dtrdsr", out _));
    }
}

public sealed class HexFormatterTests
{
    [Fact]
    public void Bytes_AreSpaceSeparatedUpperCaseHex()
    {
        var (column, text) = HexFormatter.Format(0, [0x48, 0x0A, 0xFF]);

        Assert.Equal("48 0A FF ", text);
        Assert.Equal(3, column);
    }

    [Fact]
    public void SixteenthByte_EndsTheLine_EvenAcrossChunks()
    {
        byte[] all = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();

        var (column, first) = HexFormatter.Format(0, all.AsSpan(0, 10));
        (column, string second) = HexFormatter.Format(column, all.AsSpan(10));

        Assert.Equal(
            "00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F\n10 11 12 13 ",
            first + second);
        Assert.Equal(4, column);
    }
}

public sealed class SerialPortSelectionTests
{
    private static readonly DeviceInfo Ftdi = new()
    {
        Id = "FTDIBUS\\VID_0403+PID_6001+A\\0000", Name = "USB Serial Port (COM4)",
        PortName = new SerialPortName("COM4"),
    };

    private static readonly DeviceInfo JLink = new()
    {
        Id = "USB\\VID_1366&PID_1015&MI_00\\1", Name = "JLink CDC UART Port (COM29)",
        PortName = new SerialPortName("COM29"),
    };

    private static readonly DeviceInfo NoPort = new() { Id = "ACPI\\PNP0501\\1", Name = "Communications Port" };

    [Fact]
    public void PortName_MatchesCaseInsensitively_AndWinsOverName()
    {
        var result = SerialPortSelection.Select([Ftdi, JLink], port: "com29", id: null, name: "USB Serial");

        Assert.Same(JLink, result.Device);
        Assert.Null(result.Unsupervised);
        Assert.Null(result.Error);
    }

    [Fact]
    public void PortName_MatchingNoDevice_IsOpenedUnsupervised()
    {
        var result = SerialPortSelection.Select([Ftdi], port: "/dev/ttyUSB0", id: null, name: null);

        Assert.Null(result.Device);
        Assert.Equal(new SerialPortName("/dev/ttyUSB0"), result.Unsupervised);
        Assert.Null(result.Error);
    }

    [Fact]
    public void NoArguments_SelectsTheOnlyPort()
    {
        var result = SerialPortSelection.Select([Ftdi], port: null, id: null, name: null);

        Assert.Same(Ftdi, result.Device);
    }

    [Fact]
    public void NoArguments_WithSeveralPorts_NamesTheListCommand()
    {
        var result = SerialPortSelection.Select([Ftdi, JLink], port: null, id: null, name: null);

        Assert.Null(result.Device);
        Assert.Equal(
            "2 serial ports connected — pick one with --name or --id (see `periphery devices list --category Ports`).",
            result.Error);
    }

    [Fact]
    public void Name_SelectsBySubstring()
    {
        var result = SerialPortSelection.Select([Ftdi, JLink], port: null, id: null, name: "jlink");

        Assert.Same(JLink, result.Device);
    }

    [Fact]
    public void SelectedDeviceWithoutAPortName_IsAnError()
    {
        var result = SerialPortSelection.Select([NoPort], port: null, id: null, name: null);

        Assert.Null(result.Device);
        Assert.Equal("'Communications Port' has no OS port name to open.", result.Error);
    }
}

public sealed class StatusNarratorTests
{
    private sealed class Session;

    private static readonly DeviceInfo Ftdi = new()
    {
        Id = "FTDIBUS\\VID_0403+PID_6001+A\\0000", Name = "USB Serial Port (COM4)",
        PortName = new SerialPortName("COM4"),
    };

    private static StatusNarrator Narrator() => new("COM4", SerialLineSettings.Default);

    [Fact]
    public void FirstSession_IsConnected_AndNamesTheQuitKey()
    {
        var (_, line) = Narrator().Next<Session>(new SessionActive<Session>(new Session(), Ftdi));

        Assert.Equal("Connected to USB Serial Port (COM4) at 115200 8N1. Ctrl+] to quit.", line);
    }

    [Fact]
    public void UnplugThenReplug_ReportsDisconnectedThenReconnected()
    {
        var narrator = Narrator();
        (narrator, _) = narrator.Next<Session>(new SessionActive<Session>(new Session(), Ftdi));
        (narrator, string? gone) = narrator.Next<Session>(new DeviceAbsent<Session>());
        (narrator, string? starting) = narrator.Next<Session>(new SessionStarting<Session>(Ftdi));
        (_, string? back) = narrator.Next<Session>(new SessionActive<Session>(new Session(), Ftdi));

        Assert.Equal("Disconnected. Waiting for COM4 to return.", gone);
        Assert.Null(starting);
        Assert.Equal("Reconnected to USB Serial Port (COM4) at 115200 8N1.", back);
    }

    [Fact]
    public void AbsentAtStart_IsWaiting()
    {
        var (_, line) = Narrator().Next<Session>(new DeviceAbsent<Session>());

        Assert.Equal("Waiting for COM4.", line);
    }

    [Fact]
    public void RepeatedOpenFailures_ReportOnce()
    {
        var error = new UnauthorizedAccessException("Access to the path 'COM4' is denied.");
        var narrator = Narrator();

        (narrator, string? first) = narrator.Next<Session>(new SessionUnavailable<Session>(Ftdi, error, 1));
        (_, string? second) = narrator.Next<Session>(new SessionUnavailable<Session>(Ftdi, error, 2));

        Assert.Equal("Cannot open COM4: Access to the path 'COM4' is denied. Retrying.", first);
        Assert.Null(second);
    }

    [Fact]
    public void DeviceNameWithoutThePort_IsShownAlongsideIt()
    {
        var device = Ftdi with { Name = "FT232R USB UART", PortName = new SerialPortName("/dev/ttyUSB0") };

        var (_, line) = new StatusNarrator("/dev/ttyUSB0", SerialLineSettings.Default)
            .Next<Session>(new SessionActive<Session>(new Session(), device));

        Assert.Equal("Connected to /dev/ttyUSB0 (FT232R USB UART) at 115200 8N1. Ctrl+] to quit.", line);
    }
}
