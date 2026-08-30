using Xunit;

// CameraDevice.BackendFactory is a process-global static, and the "Camera" collection's
// fixture installs the InMemoryCameraBackend into it for that collection's whole span.
// xUnit runs collections in parallel by default, so any device-backed class outside that
// collection could resolve CameraDevice.OpenAsync to the fake while it was installed — and
// pass. The overlap was exact rather than incidental: CameraTestFormats.Vga advertises
// 640x480 YUY2 and InMemoryCameraBackend emits non-zero frames, which is precisely what the
// loopback tests assert, so a leaked fake produced a green run rather than a puzzling red one
// (#276).
//
// Serialising the assembly removes the class of contamination instead of relying on every
// device-backed class remembering to opt into a non-parallel collection. RigCollectionConventionTests
// enforces the opt-in as well; this is the belt under that brace, and the one that still holds
// if a future class is added without either.
//
// That belt is load-bearing, not decorative, and the suite demonstrates it. Flip this attribute
// to false and RigGuard_FailsWhenTheInMemoryBackendIsInstalled fails on its very first line --
// Assert.Null(CameraDevice.BackendFactory) -- because the "Camera" fixture has the fake installed
// while a class outside that collection is running. Six for six on a 16-core box; it is not a
// narrow race. Note which class caught it: RigCollectionConventionTests carries no Integration
// trait and is not in the rig collection, so neither the [Collection] attribute nor the convention
// check would have protected it. That is the residual hole this attribute closes (#279 turn 3).
//
// A second, latent reason to keep it: CameraDiagnostics.Meter is a static on a process-global
// meter, and CameraDiagnosticsTests:37 asserts an EXACT measurement count through a MeterListener
// while other classes in this assembly produce frames -- the same shape as the Usb rationale.
// That one did not reproduce in the experiment above, so it is a hazard rather than a live bug.
//
// It costs almost nothing here: 283 tests / 668 ms parallel before, 286 tests / 823 ms after.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
