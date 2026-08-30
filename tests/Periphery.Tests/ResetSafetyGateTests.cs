using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Periphery.Tests;

/// <summary>
/// <see cref="ResetSafetyGate.All"/> — the combinator that exists so composing configuration can
/// never silently discard a caller's veto.
/// </summary>
public class ResetSafetyGateTests
{
    private static readonly DeviceInfo Device = new() { Id = "d" };

    private sealed class Gate(bool answer) : IResetSafetyGate
    {
        public int Calls { get; private set; }
        public ValueTask<bool> CanResetAsync(DeviceInfo device, CancellationToken ct)
        {
            Calls++;
            return new ValueTask<bool>(answer);
        }
    }

    [Fact]
    public async Task Permits_only_when_every_gate_permits()
    {
        Assert.True(await ResetSafetyGate.All(new Gate(true), new Gate(true))!
            .CanResetAsync(Device, CancellationToken.None));

        Assert.False(await ResetSafetyGate.All(new Gate(true), new Gate(false))!
            .CanResetAsync(Device, CancellationToken.None));

        // Order must not matter: a refusal anywhere is a refusal.
        Assert.False(await ResetSafetyGate.All(new Gate(false), new Gate(true))!
            .CanResetAsync(Device, CancellationToken.None));
    }

    [Fact]
    public async Task Short_circuits_on_the_first_refusal()
    {
        var refusing = new Gate(false);
        var later = new Gate(true);

        Assert.False(await ResetSafetyGate.All(refusing, later)!.CanResetAsync(Device, CancellationToken.None));

        Assert.Equal(1, refusing.Calls);
        Assert.Equal(0, later.Calls);        // never consulted once the answer is already no
    }

    [Fact]
    public void Skips_nulls_and_does_not_box_a_lone_gate()
    {
        var only = new Gate(true);
        Assert.Same(only, ResetSafetyGate.All(null, only, null));
        Assert.Null(ResetSafetyGate.All(null, null));
        Assert.Null(ResetSafetyGate.All());
        Assert.Null(ResetSafetyGate.All(null));
    }
}
