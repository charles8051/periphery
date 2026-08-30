// Periphery.Treehopper demo — pure-core rebuild (ADR-0052)
//
//   dotnet run --project examples/Periphery.Examples.Treehopper [blink|gpio|analog|pwm|i2c]
//     blink  (default)  toggle the on-board LED
//     gpio              toggle pin 0 as a push-pull output
//     analog            stream analog readings from pin 0 via board.Reports
//     pwm               ramp hardware PWM duty on pin 7 (Pwm1)
//     i2c               scan the I2C bus (0x08–0x77)

var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "blink";

TreehopperBoard board;
try
{
    board = await TreehopperBoard.OpenFirstAsync();
}
catch (TreehopperException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

await using (board)
{
    Console.WriteLine($"Connected to '{board.DeviceInfo.Name}'.");

    switch (mode)
    {
        case "gpio":   await GpioAsync(board);    break;
        case "analog": await AnalogAsync(board);  break;
        case "pwm":    await PwmAsync(board);     break;
        case "i2c":    await I2cScanAsync(board); break;
        default:       await BlinkAsync(board);   break;
    }
}

return 0;

static async Task BlinkAsync(TreehopperBoard board)
{
    Console.WriteLine("Blinking on-board LED…");
    for (int i = 0; i < 6; i++)
    {
        bool on = (i % 2) == 0;
        await board.SetLedAsync(on);
        Console.WriteLine($"  LED {(on ? "on " : "off")}");
        await Task.Delay(300);
    }
    await board.SetLedAsync(false);
}

static async Task GpioAsync(TreehopperBoard board)
{
    Console.WriteLine("Toggling pin 0 (push-pull output) 6x...");
    await using var pin = await board.Pins[0].ConfigureAsync(PinMode.PushPullOutput);
    for (int i = 0; i < 6; i++)
    {
        bool high = (i % 2) == 0;
        await pin.WriteAsync(high);
        Console.WriteLine($"  pin 0 {(high ? "high" : "low ")}");
        await Task.Delay(300);
    }
}

static async Task AnalogAsync(TreehopperBoard board)
{
    await using var pin = await board.Pins[0].ConfigureAsync(PinMode.AnalogInput);

    // One-shot: the current value, no stream plumbing.
    Console.WriteLine($"pin 0 now = {await pin.ReadVoltageAsync():F3} V");

    // Stream: per-pin watch, de-duplicated, first element is the current value.
    Console.WriteLine("Watching pin 0 (up to 10 samples)...");
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    int n = 0;
    try
    {
        await foreach (var snap in pin.WatchAsync(cts.Token))
        {
            Console.WriteLine($"  pin 0 = {snap.AnalogVoltage():F3} V  (raw {snap.Adc})");
            if (++n >= 10) break;
        }
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("  (no change in 5 s - pin may be at a steady voltage)");
    }
}

static async Task PwmAsync(TreehopperBoard board)
{
    Console.WriteLine("Ramping hardware PWM on pin 7 (Pwm1) at 732 Hz...");
    await using var pwm = await board.UsePwmAsync(PwmFrequency.Freq732Hz);
    for (int duty = 0; duty <= 100; duty += 20)
    {
        await pwm.SetDutyCycleAsync(PwmChannel.Pwm1, duty / 100.0);
        Console.WriteLine($"  pin 7 duty {duty}%");
        await Task.Delay(300);
    }
}

static async Task I2cScanAsync(TreehopperBoard board)
{
    Console.WriteLine("Scanning I2C bus (0x08-0x77) at 100 kHz...");
    await using var i2c = await board.UseI2cAsync(speedKhz: 100);

    int found = 0;
    for (byte address = 0x08; address <= 0x77; address++)
    {
        if (await i2c.PingAsync(address))
        {
            Console.WriteLine($"  device acknowledged at 0x{address:X2}");
            found++;
        }
    }
    Console.WriteLine($"{found} device(s) found.");
}
