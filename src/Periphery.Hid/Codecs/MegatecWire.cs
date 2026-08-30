// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Hid.Codecs;

/// <summary>
/// Low-level wire helper for ASCII command/response protocols carried over HID
/// input/output reports — the Megatec Qx family (Megatec <c>Q1</c>, Voltronic
/// <c>QS</c>, MegaTec II, Phoenixtec-quirks, etc.). Stateless; shared by every
/// codec and dialect in the family.
/// </summary>
/// <remarks>
/// <para>
/// The wire pattern, established by the ADR-0048 spike against the Cypress 0665
/// family:
/// </para>
/// <list type="number">
/// <item>Send <c>command + '\r'</c> as one or more output reports, each
///       zero-padded up to <see cref="HidDevice.MaxOutputReportLength"/>.
///       Most Megatec commands fit in a single 8-byte report.</item>
/// <item>Read input reports, accumulating bytes until the response prefix
///       character is seen, then continue accumulating until the next
///       <c>'\r'</c>. The bytes before the prefix are treated as noise
///       (zero padding, command echoes from other shared-handle consumers
///       like vendor monitoring software).</item>
/// <item>Return the response as an ASCII string, prefix-inclusive,
///       terminator-exclusive.</item>
/// </list>
/// <para>
/// Why prefix-based reassembly instead of "next report after our write": the HID
/// input endpoint is multicast across every open handle on the device. Other
/// consumers (ViewPower, NUT, etc.) writing commands cause those bytes to
/// surface on our input stream. The spike confirmed this — we see <c>QID\r</c>
/// and <c>GM\r</c> echoes from ViewPower interleaved with the status responses.
/// Looking for our own response's prefix character is the only reliable
/// disambiguator. (It is still prefix-based, not sender-based: a <em>different</em>
/// consumer's reply that happens to share our prefix is indistinguishable here —
/// the codec's claim-and-bind handshake therefore wants a quiet bus, see
/// <see cref="MegatecQxCodec"/>.)
/// </para>
/// </remarks>
internal static class MegatecWire
{
    /// <summary>
    /// Sends <paramref name="command"/> + <c>'\r'</c> to <paramref name="device"/>
    /// and reads back the response that begins with <paramref name="responsePrefix"/>
    /// and ends at the next <c>'\r'</c>. Returns <c>null</c> if the response
    /// doesn't arrive within <paramref name="timeout"/>.
    /// </summary>
    /// <param name="device">Opened HID device handle.</param>
    /// <param name="command">
    /// ASCII command (e.g. <c>"Q1"</c>, <c>"QS"</c>). The <c>'\r'</c> terminator
    /// is appended automatically; callers should not include it.
    /// </param>
    /// <param name="responsePrefix">
    /// First character of the expected response — <c>'('</c> for a status line,
    /// <c>'#'</c> for an <c>F</c> rating line, etc. The wire helper skips every
    /// byte before this character so noise on the multicast input stream (zero
    /// padding, other consumers' command echoes) doesn't corrupt the parse.
    /// </param>
    /// <param name="timeout">
    /// Wall-clock cap on the round-trip. Returns <c>null</c> if exceeded.
    /// </param>
    /// <param name="ct">Caller cancellation token.</param>
    public static async ValueTask<string?> RequestAsync(
        HidDevice device,
        string command,
        char responsePrefix,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(command);

        // 1. Send the command across one or more output reports.
        await SendCommandAsync(device, command, ct).ConfigureAwait(false);

        // 2. Read input reports until we find the response.
        return await ReadResponseAsync(device, responsePrefix, timeout, ct).ConfigureAwait(false);
    }

    private static async Task SendCommandAsync(
        HidDevice device, string command, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(command + '\r');

        // Pad each report to MaxOutputReportLength. Some Megatec firmware
        // ignores short writes that don't fill a complete report buffer.
        int reportLen = device.MaxOutputReportLength > 0
            ? device.MaxOutputReportLength
            : bytes.Length;

        for (int offset = 0; offset < bytes.Length; offset += reportLen)
        {
            int chunkLen = Math.Min(reportLen, bytes.Length - offset);
            var chunk = new byte[reportLen];
            Array.Copy(bytes, offset, chunk, 0, chunkLen);
            // Remaining bytes stay zero — explicit pad.
            await device.WriteReportAsync(new HidReport(0, chunk), ct).ConfigureAwait(false);
        }
    }

    private static async Task<string?> ReadResponseAsync(
        HidDevice device, char responsePrefix, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        var sb = new StringBuilder(capacity: 64);
        bool seenPrefix = false;

        try
        {
            while (!deadline.IsCancellationRequested)
            {
                var report = await device.ReadReportAsync(deadline.Token).ConfigureAwait(false);
                // Materialize via ToArray rather than iterating Span<byte>
                // directly — a ref struct local can't live across an await,
                // and an array allocation per report is negligible at HID
                // polling rates.
                var data = report.Data.ToArray();
                for (int i = 0; i < data.Length; i++)
                {
                    byte b = data[i];

                    // Zero bytes are output-report padding — Megatec
                    // responses are pure ASCII, no embedded nulls.
                    if (b == 0)
                        continue;

                    char c = (char)b;
                    if (!seenPrefix)
                    {
                        if (c == responsePrefix)
                        {
                            seenPrefix = true;
                            sb.Append(c);
                        }
                        // else: skip noise (zero padding, command echoes
                        // from other consumers, leftover bytes from a
                        // prior request whose response was discarded).
                    }
                    else
                    {
                        if (c == '\r')
                            return sb.ToString();
                        sb.Append(c);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (
            deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Wall-clock timeout — return null below.
        }

        return null;
    }
}
