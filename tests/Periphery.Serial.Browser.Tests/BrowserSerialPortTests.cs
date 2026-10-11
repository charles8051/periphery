using System.Buffers;
using System.IO.Pipelines;
using Periphery.Serial.Browser.Tests.Fakes;

namespace Periphery.Serial.Browser.Tests;

/// <summary>
/// <see cref="BrowserSerialPort"/>'s pumps over a fake Web Serial port whose far end the test plays.
/// No browser and no clock: bytes move when the test writes them.
/// </summary>
public class BrowserSerialPortTests
{
    // Bounds a failure only.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static readonly SerialPortOpenOptions Line = new() { BaudRate = 115200, Parity = SerialPortParity.Even };

    private static (FakeLine Line, PipeWebSerialPort Fake, BrowserSerialPort Port) Create()
    {
        var line = new FakeLine();
        var fake = new PipeWebSerialPort(line);
        return (line, fake, new BrowserSerialPort(fake, 0x10C4, 0xEA60));
    }

    private static WebSerialRejection Rejection(string name) => new(name, "browser message");

    private static async Task<byte[]> ReadExactlyAsync(PipeReader reader, int count)
    {
        while (true)
        {
            var result = await reader.ReadAsync().AsTask().WaitAsync(Bound);
            if (result.Buffer.Length >= count)
            {
                var bytes = result.Buffer.Slice(0, count).ToArray();
                reader.AdvanceTo(result.Buffer.GetPosition(count));
                return bytes;
            }
            if (result.IsCompleted)
                throw new EndOfStreamException($"Only {result.Buffer.Length} of {count} bytes arrived.");
            reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
        }
    }

    [Fact]
    public void A_port_reports_its_adapters_ids_and_has_no_pipe_until_opened()
    {
        var (_, _, port) = Create();

        Assert.Equal((ushort?)0x10C4, port.UsbVendorId);
        Assert.Equal((ushort?)0xEA60, port.UsbProductId);
        Assert.False(port.IsOpen);
        Assert.Throws<InvalidOperationException>(() => port.Pipe);
    }

    [Fact]
    public async Task Open_passes_the_line_settings()
    {
        var (_, fake, port) = Create();

        await port.OpenAsync(Line);

        Assert.True(port.IsOpen);
        Assert.Equal(
            """open {"baudRate":115200,"dataBits":8,"stopBits":1,"parity":"even","bufferSize":255,"flowControl":"none"}""",
            Assert.Single(fake.Log));
        await port.DisposeAsync();
    }

    [Fact]
    public async Task Opening_twice_is_refused()
    {
        var (_, _, port) = Create();
        await port.OpenAsync(Line);

        await Assert.ThrowsAsync<InvalidOperationException>(() => port.OpenAsync(Line));
        await port.DisposeAsync();
    }

    [Theory]
    [InlineData("InvalidStateError", typeof(InvalidOperationException))]
    [InlineData("NetworkError", typeof(IOException))]
    public async Task A_refused_open_maps_to_a_dotnet_exception(string name, Type expected)
    {
        var (_, fake, port) = Create();
        fake.RejectOpen = Rejection(name);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => port.OpenAsync(Line));

        Assert.IsType(expected, ex);
        Assert.False(port.IsOpen);
    }

    [Fact]
    public async Task An_open_cancelled_while_it_ran_closes_the_port_again()
    {
        using var cts = new CancellationTokenSource();
        var (_, fake, port) = Create();
        fake.OnOpen = cts.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => port.OpenAsync(Line, cts.Token));

        Assert.Equal("close", fake.Log.Last());
        Assert.False(port.IsOpen);
    }

    [Fact]
    public async Task Bytes_the_device_sends_arrive_on_Input_in_order()
    {
        var (line, _, port) = Create();
        await port.OpenAsync(Line);

        await line.DeviceSends.WriteAsync(new byte[] { 1, 2, 3 });
        await line.DeviceSends.WriteAsync(new byte[] { 4, 5 });

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, await ReadExactlyAsync(port.Pipe.Input, 5));
        await port.DisposeAsync();
    }

    [Fact]
    public async Task Bytes_written_to_Output_reach_the_device_in_order()
    {
        var (line, _, port) = Create();
        await port.OpenAsync(Line);

        await port.Pipe.Output.WriteAsync(new byte[] { 9, 8, 7 });
        await port.Pipe.Output.WriteAsync(new byte[] { 6 });

        Assert.Equal(new byte[] { 9, 8, 7, 6 }, await ReadExactlyAsync(line.DeviceReceives, 4));
        await port.DisposeAsync();
    }

    [Fact]
    public async Task A_lost_device_faults_Input_with_an_IOException()
    {
        var (_, fake, port) = Create();
        fake.RejectReads = Rejection("NetworkError");

        await port.OpenAsync(Line);

        var ex = await Assert.ThrowsAsync<IOException>(() => port.Pipe.Input.ReadAsync().AsTask().WaitAsync(Bound));
        Assert.Contains("NetworkError", ex.Message);
        await port.DisposeAsync();
    }

    [Fact]
    public async Task A_failed_write_surfaces_on_the_next_write()
    {
        var (_, fake, port) = Create();
        fake.RejectWrites = Rejection("NetworkError");
        await port.OpenAsync(Line);

        // The first write is handed to the pump, which fails and completes the reader side of
        // Output with the error. The writer's next write throws it.
        await port.Pipe.Output.WriteAsync(new byte[] { 1 });

        var ex = await Assert.ThrowsAsync<IOException>(
            () => port.Pipe.Output.WriteAsync(new byte[] { 2 }).AsTask().WaitAsync(Bound));
        Assert.Contains("NetworkError", ex.Message);
        await port.DisposeAsync();
    }

    [Fact]
    public async Task Signals_need_an_open_port_and_pass_only_the_ones_given()
    {
        var (_, fake, port) = Create();

        await Assert.ThrowsAsync<InvalidOperationException>(() => port.SetSignalsAsync(dataTerminalReady: true));

        await port.OpenAsync(Line);
        await port.SetSignalsAsync(dataTerminalReady: true, requestToSend: false);

        Assert.Equal("""setSignals {"dataTerminalReady":true,"requestToSend":false}""", fake.Log.Last());
        await port.DisposeAsync();
    }

    [Fact]
    public async Task Disposal_closes_the_port_and_completes_Input()
    {
        var (_, fake, port) = Create();
        await port.OpenAsync(Line);
        var pending = port.Pipe.Input.ReadAsync().AsTask();

        await port.DisposeAsync();

        var result = await pending.WaitAsync(Bound);
        Assert.True(result.IsCompleted);
        Assert.Equal("close", fake.Log.Last());
        Assert.False(port.IsOpen);
    }

    [Fact]
    public async Task A_port_never_opened_is_disposed_without_closing()
    {
        var (_, fake, port) = Create();

        await port.DisposeAsync();

        Assert.Empty(fake.Log);
    }
}
