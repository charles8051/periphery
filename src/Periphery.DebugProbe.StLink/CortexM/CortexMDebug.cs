// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.DebugProbe.CortexM;

/// <summary>
/// The Cortex-M debug shell: halt, reset-and-halt, resume, and core-register access, as memory
/// operations through <see cref="ITargetMemory"/>. The register values come from
/// <see cref="CortexMRegisters"/>; this class owns only the polling and its deadlines.
/// </summary>
/// <remarks>
/// Polls are back to back. Each is a probe round trip, which is the only cadence it needs.
/// Deadlines are read from the injected <see cref="TimeProvider"/> (ADR-0089).
/// </remarks>
internal sealed class CortexMDebug
{
    /// <summary>How long a halt, a reset, or a register transfer may take.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(500);

    private readonly ITargetMemory _memory;
    private readonly TimeProvider _time;

    public CortexMDebug(ITargetMemory memory, TimeProvider time)
    {
        _memory = memory;
        _time = time;
    }

    public async Task HaltAsync(CancellationToken ct)
    {
        await _memory.WriteWordAsync(CortexMRegisters.Dhcsr, CortexMRegisters.DhcsrHalt, ct).ConfigureAwait(false);
        await WaitForHaltAsync(DefaultTimeout, "halt", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resets the system and halts the core before its first instruction, through DEMCR's
    /// reset vector catch.
    /// </summary>
    public async Task ResetAndHaltAsync(CancellationToken ct)
    {
        await HaltAsync(ct).ConfigureAwait(false);
        uint demcr = await _memory.ReadWordAsync(CortexMRegisters.Demcr, ct).ConfigureAwait(false);
        await _memory.WriteWordAsync(CortexMRegisters.Demcr, CortexMRegisters.WithResetCatch(demcr, true), ct).ConfigureAwait(false);
        _ = await _memory.ReadWordAsync(CortexMRegisters.Dhcsr, ct).ConfigureAwait(false); // clears S_RESET_ST
        await _memory.WriteWordAsync(CortexMRegisters.Aircr, CortexMRegisters.AircrSystemReset, ct).ConfigureAwait(false);

        // The debug port can fault a read while the system is in reset. Such a read is retried
        // until the deadline, and the last fault is reported if the core never comes back halted.
        long start = _time.GetTimestamp();
        bool resetSeen = false;
        Exception? lastFault = null;
        while (true)
        {
            try
            {
                uint dhcsr = await _memory.ReadWordAsync(CortexMRegisters.Dhcsr, ct).ConfigureAwait(false);
                resetSeen |= CortexMRegisters.ResetSinceLastRead(dhcsr);
                if (resetSeen && CortexMRegisters.IsHalted(dhcsr))
                    break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastFault = ex;
            }

            if (_time.GetElapsedTime(start) > DefaultTimeout)
                throw lastFault is null
                    ? new CortexMException($"The core did not halt after reset within {DefaultTimeout.TotalMilliseconds:F0} ms.")
                    : new CortexMException($"The core did not halt after reset: {lastFault.Message}", lastFault);
        }

        await _memory.WriteWordAsync(CortexMRegisters.Demcr, CortexMRegisters.WithResetCatch(demcr, false), ct).ConfigureAwait(false);
    }

    /// <summary>Lets the core run from its current PC, with halting debug still enabled.</summary>
    public Task ResumeAsync(CancellationToken ct) =>
        _memory.WriteWordAsync(CortexMRegisters.Dhcsr, CortexMRegisters.DhcsrRun, ct);

    /// <summary>
    /// Turns halting debug off and resets the system, so the application starts as it would
    /// from power-on.
    /// </summary>
    public async Task ResetAndRunAsync(CancellationToken ct)
    {
        uint demcr = await _memory.ReadWordAsync(CortexMRegisters.Demcr, ct).ConfigureAwait(false);
        await _memory.WriteWordAsync(CortexMRegisters.Demcr, CortexMRegisters.WithResetCatch(demcr, false), ct).ConfigureAwait(false);
        await _memory.WriteWordAsync(CortexMRegisters.Dhcsr, CortexMRegisters.DhcsrDetach, ct).ConfigureAwait(false);
        await _memory.WriteWordAsync(CortexMRegisters.Aircr, CortexMRegisters.AircrSystemReset, ct).ConfigureAwait(false);
    }

    /// <summary>Waits for the core to halt. Throws on lockup or past the deadline.</summary>
    public async Task WaitForHaltAsync(TimeSpan timeout, string what, CancellationToken ct)
    {
        long start = _time.GetTimestamp();
        while (true)
        {
            uint dhcsr = await _memory.ReadWordAsync(CortexMRegisters.Dhcsr, ct).ConfigureAwait(false);
            if (CortexMRegisters.IsHalted(dhcsr))
                return;
            if (CortexMRegisters.IsLockedUp(dhcsr))
                throw new CortexMException($"The core locked up during {what}.");
            ThrowIfPast(start, timeout, what);
        }
    }

    public async Task WriteRegisterAsync(CoreRegister register, uint value, CancellationToken ct)
    {
        await _memory.WriteWordAsync(CortexMRegisters.Dcrdr, value, ct).ConfigureAwait(false);
        await _memory.WriteWordAsync(CortexMRegisters.Dcrsr, CortexMRegisters.DcrsrWrite(register), ct).ConfigureAwait(false);
        await WaitForRegisterAsync(register, ct).ConfigureAwait(false);
    }

    public async Task<uint> ReadRegisterAsync(CoreRegister register, CancellationToken ct)
    {
        await _memory.WriteWordAsync(CortexMRegisters.Dcrsr, CortexMRegisters.DcrsrRead(register), ct).ConfigureAwait(false);
        await WaitForRegisterAsync(register, ct).ConfigureAwait(false);
        return await _memory.ReadWordAsync(CortexMRegisters.Dcrdr, ct).ConfigureAwait(false);
    }

    private async Task WaitForRegisterAsync(CoreRegister register, CancellationToken ct)
    {
        long start = _time.GetTimestamp();
        while (!CortexMRegisters.IsRegisterReady(
                   await _memory.ReadWordAsync(CortexMRegisters.Dhcsr, ct).ConfigureAwait(false)))
            ThrowIfPast(start, DefaultTimeout, $"transfer of {register}");
    }

    private void ThrowIfPast(long start, TimeSpan timeout, string what)
    {
        if (_time.GetElapsedTime(start) > timeout)
            throw new CortexMException($"The core did not finish {what} within {timeout.TotalMilliseconds:F0} ms.");
    }
}

/// <summary>The core did not halt, reset or transfer a register as asked.</summary>
public sealed class CortexMException : Exception
{
    public CortexMException(string message) : base(message) { }

    public CortexMException(string message, Exception innerException) : base(message, innerException) { }
}
