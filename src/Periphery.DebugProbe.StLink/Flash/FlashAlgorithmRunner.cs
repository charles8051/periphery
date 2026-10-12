// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Periphery.Bootloader;
using Periphery.DebugProbe.CortexM;

namespace Periphery.DebugProbe.Flash;

/// <summary>
/// The algorithm runner shell (ADR-0094 D5): loads a flash algorithm into its RAM window and
/// runs the plan's steps on a halted core. The plan is pure; this class owns the probe I/O and
/// the waits.
/// </summary>
internal sealed class FlashAlgorithmRunner
{
    private readonly ITargetMemory _memory;
    private readonly CortexMDebug _core;

    public FlashAlgorithmRunner(ITargetMemory memory, CortexMDebug core)
    {
        _memory = memory;
        _core = core;
    }

    /// <summary>
    /// Writes the breakpoint header and the algorithm's code and data at the layout's start, then
    /// reads them back. A window that is not RAM on this part fails here, before anything runs.
    /// </summary>
    public async Task LoadAsync(FlashAlgorithm algorithm, RamLayout layout, CancellationToken ct)
    {
        int length = RamLayout.BreakpointHeader.Length + algorithm.Code.Length;
        var image = new byte[(length + 3) / 4 * 4];
        RamLayout.BreakpointHeader.CopyTo(image);
        algorithm.Code.Span.CopyTo(image.AsSpan(RamLayout.BreakpointHeader.Length));
        await _memory.WriteAsync(layout.HeaderAddress, image, ct).ConfigureAwait(false);

        var readBack = new byte[image.Length];
        await _memory.ReadAsync(layout.HeaderAddress, readBack, ct).ConfigureAwait(false);
        int mismatch = readBack.AsSpan().CommonPrefixLength(image);
        if (mismatch < image.Length)
            throw new FlashAlgorithmException(
                $"{algorithm.Name} did not load: RAM at 0x{layout.HeaderAddress + (uint)mismatch:X8} read back " +
                $"0x{readBack[mismatch]:X2}, expected 0x{image[mismatch]:X2}. Is 0x{algorithm.RamStart:X8} RAM on this part?");
    }

    /// <summary>
    /// Runs one call: sets its registers, resumes the core, and waits for the function to return
    /// into the breakpoint header. Throws when the core halts anywhere else or the function
    /// returns non-zero.
    /// </summary>
    public async Task RunAsync(AlgorithmCall call, RamLayout layout, CancellationToken ct)
    {
        foreach (var (register, value) in call.RegisterWrites(layout))
            await _core.WriteRegisterAsync(register, value, ct).ConfigureAwait(false);

        await _core.ResumeAsync(ct).ConfigureAwait(false);
        await _core.WaitForHaltAsync(call.Timeout, call.Name, ct).ConfigureAwait(false);

        uint pc = await _core.ReadRegisterAsync(CoreRegister.DebugReturnAddress, ct).ConfigureAwait(false);
        if (pc != layout.HeaderAddress)
            throw new FlashAlgorithmException(
                $"{call.Name} stopped at 0x{pc:X8} instead of returning to 0x{layout.HeaderAddress:X8}.");

        uint result = await _core.ReadRegisterAsync(CoreRegister.R0, ct).ConfigureAwait(false);
        if (result != 0)
            throw new FlashAlgorithmException($"{call.Name}(0x{call.R0:X8}) returned {result}.");
    }

    /// <summary>Runs a plan's steps in order, reporting erase and write progress in bytes.</summary>
    public async Task ExecuteAsync(
        IReadOnlyList<FlashStep> steps, FlashAlgorithm algorithm, RamLayout layout,
        IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        long eraseTotal = steps.OfType<FlashStep.Call>()
            .Where(s => s.Algorithm.Name == "EraseSector")
            .Sum(s => (long)algorithm.SectorContaining(s.Algorithm.R0).Size);
        long writeTotal = steps.OfType<FlashStep.WriteBuffer>().Sum(s => (long)s.Data.Length);
        long erased = 0, written = 0;

        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            switch (step)
            {
                case FlashStep.WriteBuffer buffer:
                    await _memory.WriteAsync(buffer.Address, buffer.Data, ct).ConfigureAwait(false);
                    break;

                case FlashStep.Call { Algorithm: var call }:
                    await RunAsync(call, layout, ct).ConfigureAwait(false);
                    if (call.Name == "EraseSector")
                    {
                        erased += algorithm.SectorContaining(call.R0).Size;
                        progress?.Report(new FlashProgress(FlashPhase.Erasing, erased, eraseTotal, $"0x{call.R0:X8}"));
                    }
                    else if (call.Name == "ProgramPage")
                    {
                        written += call.R1 ?? 0;
                        progress?.Report(new FlashProgress(FlashPhase.Writing, written, writeTotal));
                    }
                    break;
            }
        }
    }
}

/// <summary>A flash algorithm call failed on the target.</summary>
public sealed class FlashAlgorithmException : Exception
{
    public FlashAlgorithmException(string message) : base(message) { }
}
