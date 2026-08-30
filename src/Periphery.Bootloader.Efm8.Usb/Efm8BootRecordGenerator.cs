// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Linq;
using Periphery.Firmware;

namespace Periphery.Bootloader.Efm8.Usb;

/// <summary>
/// Converts an Intel HEX firmware image into an EFM8 factory-bootloader (AN945)
/// boot-record stream — the host-side equivalent of SiLabs' <c>hex2boot</c>, in
/// pure C#. The output is the same <c>$</c>-framed byte stream that
/// <see cref="Efm8Protocol.ParseRecords"/> consumes and
/// <see cref="Efm8BootloaderUploader"/> replays, so the build -> flash pipeline
/// needs no external tool (ADR: supersedes the reflash ADR's "input must come from
/// hex2boot" decision).
/// </summary>
/// <remarks>
/// <para>A faithful port of <c>hex2boot.py</c> (the recovered, byte-verified SiLabs
/// source), pure and total per ADR-0052: same image + options -> same bytes, no IO,
/// no clock. Validated byte-for-byte against real <c>hex2boot</c> output (see the
/// golden-file test).</para>
/// <para><b>Brick-safety (preserved from hex2boot, do not change):</b></para>
/// <list type="bullet">
/// <item><b>Reset-vector written last.</b> When targeting bank 0 from address 0
/// (the app), the byte at address 0 is blanked to 0xFF for the main write+verify
/// pass and the real reset-vector byte is emitted as the <em>final</em> Write
/// before RunApp. An interrupted flash therefore leaves address 0 = 0xFF, so the
/// bootloader keeps control instead of jumping into a half-written app.</item>
/// <item><b>No Lock record by default.</b> <see cref="Efm8BootOptions.Lock"/>
/// defaults to <see langword="null"/>; a Lock (0x35) can permanently disable
/// bootloader writes / debug access.</item>
/// <item><b>Region map honored.</b> Writes stay within the part's app region; the
/// reserved bootloader region at the top of flash is never targeted.</item>
/// </list>
/// </remarks>
public static class Efm8BootRecordGenerator
{
    /// <summary>
    /// Parses <paramref name="hexText"/> and converts it to a boot-record stream
    /// (see <see cref="FromImage"/>).
    /// </summary>
    /// <exception cref="IntelHexFormatException">The HEX is malformed.</exception>
    public static byte[] FromIntelHex(string hexText, Efm8BootOptions options)
        => FromImage(IntelHexImage.Parse(hexText), options);

    /// <summary>The Verify (<c>0x34</c>) record's command byte — public so a caller can tell a
    /// verify-record rejection apart from any other failure in an <see cref="Efm8UploadResult"/>
    /// (e.g. <see cref="VerifyOnly"/>'s stream has exactly one, and a caller building a
    /// verify-outcome type of its own keys off <c>FailedCommand == VerifyCommand</c> for "content
    /// mismatch" versus "something else went wrong").</summary>
    public const byte VerifyCommand = 0x34;

    /// <summary>The Setup (<c>0x31</c>) record's command byte — public for the same disambiguation
    /// reason as <see cref="VerifyCommand"/>.</summary>
    public const byte SetupCommand = 0x31;

    /// <summary>
    /// Converts an already-parsed <paramref name="image"/> to a boot-record stream:
    /// optional Identify records, a Setup record, the per-region erase/write/verify
    /// records, the failsafe reset-vector write, an optional Lock, and (unless
    /// <see cref="Efm8BootOptions.Wait"/>) a final RunApp.
    /// </summary>
    public static byte[] FromImage(IntelHexImage image, Efm8BootOptions options)
    {
        ArgumentNullException.ThrowIfNull(image);

        var output = new List<byte>();
        bool failsafe = options.Bank == 0 && options.Start == 0;

        foreach (ushort id in options.IdsOrEmpty)
            WriteIdentify(output, id);

        WriteSetup(output, options.Bank);

        // Hold back the reset-vector byte: blank address 0 to 0xFF for the main pass
        // so a partial flash can never present a valid-looking reset vector.
        byte resetVector = image.Get(0);
        bool holdReset = failsafe && resetVector != 0xFF;
        IntelHexImage working = holdReset ? image.With(0, 0xFF) : image;

        foreach (var (start, stop, page) in GetRegions(options.Start, options.Top, options.Map))
            EmitRegion(output, working.Slice(start, stop), page, options.Erase);

        // ...and write the real reset vector last, after every other byte is verified.
        if (holdReset)
            WriteWrite(output, 0, stackalloc byte[] { resetVector });

        if (options.Lock is ushort lockByte)
            WriteLock(output, lockByte);

        if (!options.Wait)
            WriteRunApp(output);

        return output.ToArray();
    }

    /// <summary>
    /// Builds a <b>read-only</b> boot-record stream: a Setup record, then one Verify record per
    /// region computed over <paramref name="image"/> using the same region/page/CRC-chunking walk
    /// <see cref="FromImage"/> uses for a real flash's own Verify records (see the failsafe caveat
    /// below for the one case they intentionally diverge). Emits no Identify, Erase, Write, Lock,
    /// <b>or RunApp</b> record — nothing in the stream can modify flash, regardless of the
    /// erase-and-reflash confirmation the generic uploader still requires to replay it (that gate is
    /// a property of the uploader, not of this stream's content).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The point is independent confirmation of what is <em>already on the device</em>, without a
    /// reflash: replay this against a board's bootloader and each Verify record's Acknowledge/
    /// non-Acknowledge reply says whether that region's current flash content matches
    /// <paramref name="image"/>, byte for byte, as authoritatively as the bootloader's own CRC check
    /// can say it. That is the same check a real flash's own embedded Verify record already relies
    /// on (<c>Efm8HidProgrammer.FlashAsync</c> marks its result <c>verified: false</c> precisely
    /// because that in-session check is not treated as independent proof) — a <em>separate</em>,
    /// later bootloader session built from this method is the independent check that flash's own
    /// result explicitly declines to claim.
    /// </para>
    /// <para>
    /// <b>Checks the true final image, not FromImage's transiently-blanked failsafe pass.</b> When
    /// <paramref name="image"/>'s address 0 is non-<c>0xFF</c> and the failsafe applies (bank 0,
    /// start 0 — see <see cref="FromImage"/>), a real flash's <em>embedded</em> Verify record
    /// intentionally covers address 0 blanked to <c>0xFF</c>, because the real reset vector is
    /// withheld until a separate Write record after that Verify succeeds. A board a completed flash
    /// actually leaves behind holds the <em>real</em> byte at address 0, not the blanked one — this
    /// method's whole job is confirming that completed, final state, so it verifies
    /// <paramref name="image"/> exactly as given, unmodified. Its CRC therefore differs from
    /// <see cref="FromImage"/>'s embedded Verify record whenever the failsafe would have applied to
    /// a flash of the same image; the two are legitimately different checks, not a bug in either.
    /// </para>
    /// <para>
    /// <b>No RunApp on purpose.</b> <see cref="Efm8BootloaderUploader"/> stops replaying at the
    /// first non-Acknowledge reply, so a stream that ended <c>[..., Verify, RunApp]</c> would never
    /// reach RunApp on a genuine mismatch (a non-Acknowledge Verify reply) — stranding the board in
    /// the bootloader exactly when the check finds something wrong. Leaving the bootloader is the
    /// caller's job, unconditionally, via a separate <see cref="RunAppOnly"/> transfer sent
    /// regardless of whether the verify matched (see <c>TreehopperFirmwareUpdate.VerifyFromFileAsync</c>).
    /// </para>
    /// </remarks>
    public static byte[] VerifyOnly(IntelHexImage image, Efm8BootOptions options)
    {
        ArgumentNullException.ThrowIfNull(image);
        // An empty image yields zero Verify records - just the Setup record, which any board
        // acknowledges trivially - so the upload would report Success without having checked a
        // single byte of flash. A vacuous "match" is worse than an error here: this method's whole
        // purpose is an honest answer about device content.
        if (image.IsEmpty)
            throw new ArgumentException(
                "Cannot verify an empty image: a stream with no Verify record would report a trivial "
                + "match without checking any flash content.", nameof(image));

        var output = new List<byte>();
        WriteSetup(output, options.Bank);

        foreach (var (start, stop, page) in GetRegions(options.Start, options.Top, options.Map))
            EmitVerifyOnlyRegion(output, image.Slice(start, stop), page);

        return output.ToArray();
    }

    /// <summary>
    /// A one-record boot-record stream containing only RunApp: resets the device and runs whatever
    /// application is currently in flash. Used to unconditionally leave the bootloader after a
    /// <see cref="VerifyOnly"/> check — see that method's remarks for why leaving must be a separate
    /// transfer rather than trailing the verify stream itself.
    /// </summary>
    public static byte[] RunAppOnly()
    {
        var output = new List<byte>();
        WriteRunApp(output);
        return output.ToArray();
    }

    /// <summary>The Erase (<c>0x32</c>) record's command byte.</summary>
    private const byte EraseCommand = 0x32;

    /// <summary>The Write (<c>0x33</c>) record's command byte.</summary>
    private const byte WriteCommand = 0x33;

    /// <summary>
    /// Builds a read-only verify-only stream (see <see cref="VerifyOnly"/>) directly from an
    /// already-built flash blob — e.g. the exact bytes <see cref="FromImage"/> produced and a caller
    /// just replayed against a device — for a caller that has those bytes but not the original
    /// source <see cref="IntelHexImage"/> (see <see cref="VerifyOnly"/> for that path).
    /// </summary>
    /// <remarks>
    /// <b>Reconstructs the true final image; does NOT replay the blob's own embedded Verify
    /// record(s).</b> An earlier revision of this method did exactly that — extracted the Setup and
    /// Verify records already present in the blob — which is wrong whenever <see cref="FromImage"/>'s
    /// reset-vector failsafe applied (bank 0, start 0, a non-<c>0xFF</c> reset vector — true of every
    /// real firmware image, since <c>0xFF</c> is erased flash, never a valid reset vector). That
    /// embedded Verify record intentionally covers the reset vector blanked to <c>0xFF</c>, computed
    /// <em>before</em> the real byte is written in a separate, later Write record (see
    /// <see cref="FromImage"/>'s remarks); replaying it literally checks a state the device is never
    /// actually left in, so every automatic verification would report a permanent, spurious mismatch.
    /// Instead, this method replays every record in stream order into a fresh byte map — a later
    /// Write at an address already seen correctly overrides an earlier one, and a data-less Erase
    /// (<see cref="Efm8EraseMode.Separate"/>'s erase-then-write pair; <see cref="Efm8BootOptions.Ub1"/>'s
    /// default <see cref="Efm8EraseMode.WithData"/> never emits one) resets its <em>whole page</em>
    /// back to erased flash (<c>0xFF</c>), discarding whatever an earlier record in the stream wrote
    /// there — exactly as flashing the device itself applied these records in order — and hands that
    /// reconstructed, TRUE final image to <see cref="VerifyOnly"/>: the same path a caller holding the
    /// original source image takes.
    /// </remarks>
    /// <exception cref="Efm8BootFormatException"><paramref name="flashBlob"/> is not a well-formed boot-record stream.</exception>
    /// <exception cref="ArgumentException"><paramref name="flashBlob"/> contains no Write data — nothing to independently check (delegated to <see cref="VerifyOnly"/>'s own empty-image guard).</exception>
    public static byte[] VerifyOnlyFromBlob(ReadOnlyMemory<byte> flashBlob, Efm8BootOptions options)
    {
        var records = Efm8Protocol.ParseRecords(flashBlob);
        var bytes = new Dictionary<int, byte>();
        foreach (var record in records)
        {
            if (record.Command != WriteCommand && record.Command != EraseCommand)
                continue;
            var span = record.Frame.Span;
            int addr = (span[3] << 8) | span[4];
            int dataLength = span.Length - 5;

            if (record.Command == EraseCommand && dataLength == 0)
            {
                // A data-less Erase (Efm8EraseMode.Separate) resets its whole page to erased flash,
                // not just the address byte the record names - remove anything an earlier record in
                // this stream wrote there, so a write-then-erase-then-later-write sequence for the
                // same page doesn't leave the earlier write's stale bytes in the reconstruction.
                int page = PageFor(addr, options.Map);
                int pageStart = (addr / page) * page;
                for (int a = pageStart; a < pageStart + page; a++)
                    bytes.Remove(a);
                continue;
            }

            // Write, and a data-carrying Erase (Efm8EraseMode.WithData - Ub1's default, and the only
            // mode that reaches here with data) share the same
            // [$][len][cmd][addrHi][addrLo][data...] shape.
            for (int i = 0; i < dataLength; i++)
                bytes[addr + i] = span[5 + i];
        }

        return VerifyOnly(IntelHexImage.FromBytes(bytes.Select(kv => (kv.Key, kv.Value))), options);
    }

    // Which page size covers `address` in `map` - the boot-record wire format carries no length for
    // a data-less Erase, so VerifyOnlyFromBlob looks it up the same way EmitRegion's own erase/write
    // split does, from the region table both already share.
    private static int PageFor(int address, Efm8FlashMap map)
    {
        foreach (var (start, stop, page) in RegionsFor(map))
            if (address >= start && address <= stop)
                return page;
        throw new ArgumentOutOfRangeException(
            nameof(address), address, $"Address 0x{address:X4} is outside every region of the {map} flash map.");
    }

    // mem2boot(): emit erase/write records for one region's sub-image, then a Verify
    // over [minaddr, maxaddr] with the CRC accumulated across the written bytes.
    private static void EmitRegion(List<byte> output, IntelHexImage image, int page, Efm8EraseMode erase)
    {
        if (image.IsEmpty) return;

        foreach (var (addr, data, crc, isLast) in WalkRegionChunks(image, page))
        {
            bool atPageBoundary = addr % page == 0;
            if (erase == Efm8EraseMode.None || !atPageBoundary)
            {
                WriteWrite(output, addr, data);
            }
            else if (erase == Efm8EraseMode.Separate)
            {
                WriteErase(output, addr, ReadOnlySpan<byte>.Empty);
                WriteWrite(output, addr, data);
            }
            else // WithData: erase the page and write its data in one record
            {
                WriteErase(output, addr, data);
            }

            if (isLast)
                WriteVerify(output, (image.MinAddress / page) * page, image.MaxAddress, crc);
        }
    }

    // Emits only the Verify record a real flash of this region would end with — no Erase/Write.
    // Walks the identical chunk sequence EmitRegion's write pass does (same shared enumerator), so a
    // verify's CRC is never computed a different way than a real flash's own Verify record would
    // compute it: this checks "does the device match a flash of this image," not "does the device
    // match whatever a second, independently-written CRC pass happens to produce."
    private static void EmitVerifyOnlyRegion(List<byte> output, IntelHexImage image, int page)
    {
        if (image.IsEmpty) return;

        (int Addr, byte[] Data, ushort Crc, bool IsLast) last = default;
        foreach (var chunk in WalkRegionChunks(image, page))
            last = chunk;

        WriteVerify(output, (image.MinAddress / page) * page, image.MaxAddress, last.Crc);
    }

    // The one CRC/chunking walk both EmitRegion and EmitVerifyOnlyRegion fold over: round the first
    // byte down to a page boundary, then step through in Math.Min(128, page)-sized chunks to
    // maxAddr, accumulating CRC-16/XMODEM across every chunk in order. Yields each chunk's address,
    // bytes, and running CRC so far, flagging the last chunk (where the running CRC is the region's
    // final Verify CRC).
    private static IEnumerable<(int Addr, byte[] Data, ushort Crc, bool IsLast)> WalkRegionChunks(
        IntelHexImage image, int page)
    {
        ushort crc = 0;
        int minAddr = (image.MinAddress / page) * page;
        int maxAddr = image.MaxAddress;
        int recSize = Math.Min(128, page);

        for (int addr = minAddr; addr <= maxAddr; addr += recSize)
        {
            int size = Math.Min(recSize, maxAddr - addr + 1);
            byte[] data = image.ToBinary(addr, size);
            crc = Crc16Xmodem(data, crc);
            yield return (addr, data, crc, addr + recSize > maxAddr);
        }
    }

    // get_regions(): the part map clipped to [org, top], yielding (start, stop, page)
    // for each region that overlaps. Stop is inclusive here (it bounds the slice).
    private static IEnumerable<(int Start, int Stop, int Page)> GetRegions(int org, int top, Efm8FlashMap map)
    {
        foreach (var (start, stop, page) in RegionsFor(map))
        {
            int lo = Math.Max(org, start);
            int hi = Math.Min(top, stop);
            if (lo < hi)
                yield return (lo, hi, page);
        }
    }

    private static (int Start, int Stop, int Page)[] RegionsFor(Efm8FlashMap map) => map switch
    {
        // ub1 and bb2 share the same map in hex2boot.
        Efm8FlashMap.Ub1 or Efm8FlashMap.Bb2 =>
            [(0x0000, 0x3DFF, 512), (0xF800, 0xFBBF, 64)],
        Efm8FlashMap.Sb2 =>
            [(0x0000, 0xF7FF, 1024)],
        _ =>
            [(0x0000, 0xFBFF, 512)],
    };

    // ── Record encoders (frame: '$', length, command, big-endian payload) ──
    // length = count of bytes after it = command(1) + payload.

    private static void WriteIdentify(List<byte> o, ushort blid)
    {
        o.Add((byte)'$'); o.Add(3); o.Add(0x30);
        AddU16(o, blid);
    }

    private static void WriteSetup(List<byte> o, int bank)
    {
        o.Add((byte)'$'); o.Add(4); o.Add(SetupCommand);
        AddU16(o, 0xA5F1);          // flash keys
        o.Add((byte)bank);
    }

    private static void WriteErase(List<byte> o, int addr, ReadOnlySpan<byte> data)
    {
        o.Add((byte)'$'); o.Add((byte)(3 + data.Length)); o.Add(0x32);
        AddU16(o, (ushort)addr);
        foreach (byte b in data) o.Add(b);
    }

    private static void WriteWrite(List<byte> o, int addr, ReadOnlySpan<byte> data)
    {
        o.Add((byte)'$'); o.Add((byte)(3 + data.Length)); o.Add(0x33);
        AddU16(o, (ushort)addr);
        foreach (byte b in data) o.Add(b);
    }

    private static void WriteVerify(List<byte> o, int org, int end, ushort crc)
    {
        o.Add((byte)'$'); o.Add(7); o.Add(VerifyCommand);
        AddU16(o, (ushort)org);
        AddU16(o, (ushort)end);
        AddU16(o, crc);
    }

    private static void WriteLock(List<byte> o, ushort lockByte)
    {
        o.Add((byte)'$'); o.Add(3); o.Add(0x35);
        AddU16(o, lockByte);
    }

    private static void WriteRunApp(List<byte> o)
    {
        o.Add((byte)'$'); o.Add(3); o.Add(0x36);
        AddU16(o, 0x0000);          // option 0 = run the application
    }

    private static void AddU16(List<byte> o, ushort value)
    {
        o.Add((byte)(value >> 8));   // big-endian
        o.Add((byte)(value & 0xFF));
    }

    /// <summary>
    /// CRC-16/XMODEM (poly 0x1021, init 0x0000, no reflection, no final xor) — the
    /// algorithm the bootloader's Verify (0x34) record carries, matching
    /// <c>hex2boot</c>'s <c>crc16</c>. <paramref name="seed"/> chains the running CRC
    /// across a region's write chunks.
    /// </summary>
    public static ushort Crc16Xmodem(ReadOnlySpan<byte> data, ushort seed = 0)
    {
        ushort crc = seed;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }
}
