namespace Periphery.Serial.Browser.Tests;

/// <summary>The JSON the serial package exchanges with its JS module.</summary>
public class WebSerialJsonTests
{
    [Fact]
    public void Filters_write_only_the_fields_they_set_under_Web_Serials_names()
    {
        var json = WebSerialJson.WriteFilters(
        [
            new SerialPortRequestFilter { UsbVendorId = 0x10C4, UsbProductId = 0xEA60 },
            new SerialPortRequestFilter { BluetoothServiceClassId = new Guid("00001101-0000-1000-8000-00805f9b34fb") },
        ]);

        Assert.Equal(
            """[{"usbVendorId":4292,"usbProductId":60000},{"bluetoothServiceClassId":"00001101-0000-1000-8000-00805f9b34fb"}]""",
            json);
    }

    [Fact]
    public void A_product_id_without_a_vendor_id_is_refused()
    {
        Assert.Throws<ArgumentException>(() => WebSerialJson.WriteFilters([new SerialPortRequestFilter { UsbProductId = 1 }]));
    }

    [Fact]
    public void Options_write_every_field_with_the_defaults_filled_in()
    {
        Assert.Equal(
            """{"baudRate":9600,"dataBits":8,"stopBits":1,"parity":"none","bufferSize":255,"flowControl":"none"}""",
            WebSerialJson.WriteOptions(new SerialPortOpenOptions { BaudRate = 9600 }));
    }

    [Fact]
    public void Options_carry_parity_stop_bits_and_flow_control()
    {
        var json = WebSerialJson.WriteOptions(new SerialPortOpenOptions
        {
            BaudRate = 57600, DataBits = 7, StopBits = 2, Parity = SerialPortParity.Odd,
            HardwareFlowControl = true, BufferSize = 4096,
        });

        Assert.Equal(
            """{"baudRate":57600,"dataBits":7,"stopBits":2,"parity":"odd","bufferSize":4096,"flowControl":"hardware"}""",
            json);
    }

    [Theory]
    [InlineData(0, 8, 1, 255)]
    [InlineData(9600, 6, 1, 255)]
    [InlineData(9600, 8, 3, 255)]
    [InlineData(9600, 8, 1, 0)]
    [InlineData(9600, 8, 1, 16 * 1024 * 1024 + 1)]
    public void Options_Web_Serial_cannot_take_are_refused(int baud, int dataBits, int stopBits, int bufferSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WebSerialJson.WriteOptions(new SerialPortOpenOptions
        {
            BaudRate = baud, DataBits = dataBits, StopBits = stopBits, BufferSize = bufferSize,
        }));
    }

    [Fact]
    public void Signals_write_only_the_ones_given()
    {
        Assert.Equal("""{"requestToSend":true}""", WebSerialJson.WriteSignals(null, true, null));
        Assert.Equal("""{"dataTerminalReady":false,"break":true}""", WebSerialJson.WriteSignals(false, null, true));
    }

    [Fact]
    public void Setting_no_signal_is_refused()
    {
        Assert.Throws<ArgumentException>(() => WebSerialJson.WriteSignals(null, null, null));
    }

    [Fact]
    public void Reads_a_ports_handle_and_ids()
    {
        Assert.Equal(((int, ushort?, ushort?))(4, 4292, 60000), WebSerialJson.ReadPort("""{"handle":4,"usbVendorId":4292,"usbProductId":60000}"""));
        Assert.Equal(((int, ushort?, ushort?))(5, null, null), WebSerialJson.ReadPort("""{"handle":5,"usbVendorId":null,"usbProductId":null}"""));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    public void A_port_in_another_shape_is_a_format_error(string json)
    {
        Assert.Throws<FormatException>(() => WebSerialJson.ReadPort(json));
    }

    [Fact]
    public void Reads_a_rejection()
    {
        Assert.Equal(new WebSerialRejection("NotFoundError", "No port selected by the user."),
            WebSerialJson.ReadRejection("""{"name":"NotFoundError","message":"No port selected by the user."}"""));
    }
}
