using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Hid.Tests.Codecs;

/// <summary>
/// Tests for the claim-and-bind detection policy in <see cref="MegatecQxCodec"/>.
/// The policy is exercised through <see cref="MegatecQxCodec.DetectAsync"/>,
/// which takes a probe delegate — so the dialect-negotiation logic is unit-
/// testable with fakes, no HID device and no real timeouts. (The bound-verb
/// cache, self-heal-on-miss, and real wire transport in
/// <c>ReadSnapshotAsync</c> are validated live against a real UPS.)
/// </summary>
public class MegatecQxCodecTests
{
    // A well-formed status line — what a probe returns when its verb "answers".
    private const string ValidLine = "(120.0 120.0 120.0 010 60.0 13.0 25.0 10000000";

    /// <summary>
    /// Builds a probe that returns a mapped response per verb (null = silent)
    /// and records, in order, the verbs it was asked to probe.
    /// </summary>
    private static (Func<MegatecDialect, CancellationToken, ValueTask<string?>> Probe, List<string> Probed)
        FakeProbe(IReadOnlyDictionary<string, string?> byVerb)
    {
        var probed = new List<string>();
        Func<MegatecDialect, CancellationToken, ValueTask<string?>> probe = (dialect, _) =>
        {
            probed.Add(dialect.Verb);
            byVerb.TryGetValue(dialect.Verb, out var response);
            return ValueTask.FromResult(response);
        };
        return (probe, probed);
    }

    [Fact]
    public async Task DetectAsync_FirstCandidateAnswers_BindsItAndStops()
    {
        // Q1 answers; QS would too, but must never be probed once Q1 wins.
        var (probe, probed) = FakeProbe(new Dictionary<string, string?>
        {
            ["Q1"] = ValidLine,
            ["QS"] = ValidLine,
        });

        var result = await MegatecQxCodec.DetectAsync(probe, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(MegatecDialect.Q1, result.Value.Dialect);
        Assert.Equal(ValidLine, result.Value.Response);
        Assert.Equal(new[] { "Q1" }, probed);  // stopped after the first match
    }

    [Fact]
    public async Task DetectAsync_Q1Silent_QsAnswers_BindsQs()
    {
        // The observed scenario: this 0665:5161 firmware ignores Q1 and
        // answers QS. Detection must fall through Q1's silence and bind QS.
        var (probe, probed) = FakeProbe(new Dictionary<string, string?>
        {
            ["Q1"] = null,            // silent — device doesn't implement Q1
            ["QS"] = ValidLine,
        });

        var result = await MegatecQxCodec.DetectAsync(probe, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(MegatecDialect.QS, result.Value.Dialect);
        Assert.Equal(ValidLine, result.Value.Response);
        Assert.Equal(new[] { "Q1", "QS" }, probed);  // probed in order, Q1 first
    }

    [Fact]
    public async Task DetectAsync_NoneAnswer_ReturnsNull()
    {
        var (probe, probed) = FakeProbe(new Dictionary<string, string?>
        {
            ["Q1"] = null,
            ["QS"] = null,
        });

        var result = await MegatecQxCodec.DetectAsync(probe, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(new[] { "Q1", "QS" }, probed);  // all candidates tried
    }

    [Fact]
    public async Task DetectAsync_SkipsMalformedResponse()
    {
        // A '('-prefixed but malformed line (e.g. another consumer's truncated
        // reply, or noise) is NOT an answer — IsWellFormed gates it out, so
        // detection moves on to the next candidate.
        var (probe, _) = FakeProbe(new Dictionary<string, string?>
        {
            ["Q1"] = "(garbage",      // starts with '(' but too few fields
            ["QS"] = ValidLine,
        });

        var result = await MegatecQxCodec.DetectAsync(probe, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(MegatecDialect.QS, result.Value.Dialect);
    }

    [Fact]
    public async Task DetectAsync_NullProbe_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await MegatecQxCodec.DetectAsync(null!, CancellationToken.None));
    }

    // ── MegatecDialect data: candidates are peers sharing one response shape. ──

    [Fact]
    public void Candidates_IncludeQ1AndQs()
    {
        Assert.Contains(MegatecDialect.Q1, MegatecDialect.Candidates);
        Assert.Contains(MegatecDialect.QS, MegatecDialect.Candidates);
    }

    [Fact]
    public void Candidates_ArePeers_AllShareTheStatusLinePrefix()
    {
        // Every candidate elicits the same '('-prefixed status line — that's
        // why a single parser and a single codec cover the whole family.
        Assert.All(MegatecDialect.Candidates, d => Assert.Equal('(', d.ResponsePrefix));
    }

    [Fact]
    public void Dialect_Verbs_AreTheWireCommands()
    {
        Assert.Equal("Q1", MegatecDialect.Q1.Verb);
        Assert.Equal("QS", MegatecDialect.QS.Verb);
    }
}
