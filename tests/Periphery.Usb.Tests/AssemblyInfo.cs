using Xunit;

// The Periphery.Usb Meter is process-wide, and PipeSerializationTests reads it through a
// MeterListener to assert queue depth and in-flight occupancy. xUnit puts each test class
// in its own collection and runs collections in parallel by default, so UsbDeviceTests,
// UsbTransferWatchdogTests and LinuxUsbIntegrationTests — all of which run transfers, and
// the watchdog one deliberately wedges them — would post measurements into the same
// instruments while those assertions are sampling. That is invisible locally on a machine
// where the scheduling happens to separate them, and shows up as an unreproducible CI
// failure on a box with a different core count.
//
// Serialising the assembly removes the whole class of contamination. It costs nothing
// here: 36 tests, well under a second end to end.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
