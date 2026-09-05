using System.Threading.Channels;
using Periphery.Camera.Testing;

namespace Periphery.Camera.Tests;

/// <summary>
/// Verifies ADR-0082: <see cref="BufferExhaustionPolicy"/> controls the delivery
/// channel, the two values behave differently, an evicted frame's pooled buffer
/// comes back, and the producer stall is counted. Also ADR-0035 Decision 9 —
/// exhaustion affects future frames, never active leases.
/// </summary>
/// <remarks>
/// <para>
/// Every expectation here is hand-derived from the fake's contract, never from
/// the session. <see cref="InMemoryCameraBackend"/> produces frame <c>n</c> with
/// every byte set to <c>n &amp; 0xFF</c> (<see cref="CameraFramePatterns.FrameIndexConstant"/>),
/// numbers frames from 1, and parks forever on read <c>MaxFrames + 1</c> after
/// signalling <see cref="InMemoryCameraBackend.ReadHangReached"/>. So a session
/// left to run against <c>MaxFrames: n</c> reaches a settled state — exactly
/// <c>n</c> frames produced, producer parked — that a test can assert against
/// without racing it.
/// </para>
/// </remarks>
[Collection("Camera")]
public sealed class BufferExhaustionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    // ── The policy reaches the channel ─────────────────────────────────

    [Theory]
    [InlineData(BufferExhaustionPolicy.LatestWins, BoundedChannelFullMode.DropOldest)]
    [InlineData(BufferExhaustionPolicy.StallProducer, BoundedChannelFullMode.Wait)]
    public void FullModeFor_MapsEachPolicyToItsChannelBehaviour(
        BufferExhaustionPolicy policy, BoundedChannelFullMode expected) =>
        Assert.Equal(expected, CameraSession.FullModeFor(policy));

    /// <summary>
    /// The consumer's allowance, the queue's reservation, and one spare for the
    /// producer to copy into. The spare is the term that keeps latest-wins honest
    /// when the consumer is holding everything it was promised.
    /// </summary>
    [Theory]
    [InlineData(3, 1, 5)]
    [InlineData(1, 1, 3)]
    [InlineData(2, 4, 7)]
    public void PoolSizeFor_IsBufferCountPlusQueueDepthPlusOneSpare(
        int bufferCount, int queueDepth, int expected) =>
        Assert.Equal(expected, CameraSession.PoolSizeFor(
            new CameraSessionOptions(BufferCount: bufferCount, QueueDepth: queueDepth)));

    // ── The two values differ, observably, on frame content ────────────

    /// <summary>
    /// Five frames, a queue of one, and a consumer that reads nothing until the
    /// producer has run out. Latest-wins keeps the newest, so the one frame left
    /// is frame 5 and frames 1-4 were evicted and counted.
    /// </summary>
    [Fact]
    public async Task LatestWins_HandsASlowConsumerTheNewestFrame()
    {
        await using var backend = new InMemoryCameraBackend { MaxFrames = 5 };
        await using var session = await CameraTestHarness.OpenSessionAsync(
            backend,
            options: new CameraSessionOptions(
                BufferCount: 3, QueueDepth: 1,
                ExhaustionPolicy: BufferExhaustionPolicy.LatestWins));

        await session.StartCaptureAsync();
        await backend.ReadHangReached.WaitAsync(Patience);

        using var frame = await session.ReadFrameAsync();

        AssertEveryByteIs(5, frame);
        Assert.Equal(4, session.Metrics.FramesDropped);

        // The producer only parks under StallProducer. Under latest-wins the
        // write always completes, so the stall instrument must stay still —
        // this is the negative half of "the instrument moves when it should".
        Assert.Equal(0, session.Metrics.ProducerStalls);
        Assert.Equal(TimeSpan.Zero, session.Metrics.ProducerStallTime);
    }

    /// <summary>
    /// The same five frames and the same queue of one under
    /// <see cref="BufferExhaustionPolicy.StallProducer"/>. The producer parks on
    /// frame 2 rather than evicting frame 1, so the consumer's first frame is
    /// frame 1 and nothing is dropped. That difference in delivered pixels is
    /// the behaviour the enum never had.
    /// </summary>
    [Fact]
    public async Task StallProducer_HandsASlowConsumerTheOldestFrame_AndParksInstead()
    {
        await using var backend = new InMemoryCameraBackend { MaxFrames = 5 };
        await using var session = await CameraTestHarness.OpenSessionAsync(
            backend,
            options: new CameraSessionOptions(
                BufferCount: 3, QueueDepth: 1,
                ExhaustionPolicy: BufferExhaustionPolicy.StallProducer));

        await session.StartCaptureAsync();

        // Frame 1 goes into the queue of one; frame 2 finds it full. The count
        // is recorded on entry to the stall, so it is observable while parked.
        await WaitUntil(() => session.Metrics.ProducerStalls >= 1,
            "the producer to park on a full queue");

        using var frame = await session.ReadFrameAsync();

        AssertEveryByteIs(1, frame);
        Assert.Equal(0, session.Metrics.FramesDropped);

        // Reading frame 1 frees the slot, so the stall ends and its duration
        // lands. The producer stalled on real wall-clock, so this is > zero.
        await WaitUntil(() => session.Metrics.ProducerStallTime > TimeSpan.Zero,
            "the parked stall to end and report its duration");

        await session.StopCaptureAsync();
    }

    // ── The regression that would kill a session ───────────────────────

    /// <summary>
    /// An evicted frame's pooled buffer must come back. The channel hands the
    /// evicted frame to <c>itemDropped</c> and then forgets it; nothing else
    /// would ever dispose it, so a session that skips that disposal loses one
    /// buffer per drop and stops delivering after <c>BufferCount</c> of them.
    /// </summary>
    /// <remarks>
    /// A four-buffer pool and nineteen evictions, so a leak of even one buffer
    /// per drop exhausts it long before the end. The sharp
    /// assertion is the lease count: with the buffers coming back, exactly one
    /// frame is outstanding (frame 20, sitting in the queue). With them
    /// stranded, the pool bleeds out and the count sticks at four.
    /// </remarks>
    [Fact]
    public async Task LatestWins_ReturnsAnEvictedFramesBufferToThePool()
    {
        await using var backend = new InMemoryCameraBackend { MaxFrames = 20 };
        await using var session = await CameraTestHarness.OpenSessionAsync(
            backend,
            options: new CameraSessionOptions(
                BufferCount: 2, QueueDepth: 1,
                ExhaustionPolicy: BufferExhaustionPolicy.LatestWins));

        await session.StartCaptureAsync();
        await backend.ReadHangReached.WaitAsync(Patience);

        Assert.Equal(20, backend.FrameCounter);
        Assert.Equal(19, session.Metrics.FramesDropped);
        Assert.Equal(1, session.Metrics.OutstandingLeases);

        // Nineteen drops later the pool still delivers, and what it delivers is
        // the twentieth frame rather than a recycled older one.
        var frame = await session.ReadFrameAsync();
        AssertEveryByteIs(20, frame);

        frame.Dispose();
        Assert.Equal(0, session.Metrics.OutstandingLeases);

        await session.StopCaptureAsync();
    }

    /// <summary>
    /// The degenerate configuration: one buffer for the consumer, a queue of
    /// one. The queue's frame comes out of its own allowance rather than the
    /// consumer's, so the producer always has a buffer to copy the newest frame
    /// into and always has the queued frame to evict in exchange.
    /// </summary>
    /// <remarks>
    /// Seeding the pool with <c>BufferCount</c> alone puts the single buffer
    /// inside the queued frame, which refuses every later frame for want of one
    /// and leaves frame 1 to be delivered — a policy named latest-wins handing
    /// over the stalest frame there is, which is the defect ADR-0082 D2 deleted
    /// <c>DropIncoming</c> for.
    /// </remarks>
    [Fact]
    public async Task LatestWins_KeepsTheNewestFrame_WithASingleConsumerBuffer()
    {
        await using var backend = new InMemoryCameraBackend { MaxFrames = 8 };
        await using var session = await CameraTestHarness.OpenSessionAsync(
            backend,
            options: new CameraSessionOptions(
                BufferCount: 1, QueueDepth: 1,
                ExhaustionPolicy: BufferExhaustionPolicy.LatestWins));

        await session.StartCaptureAsync();
        await backend.ReadHangReached.WaitAsync(Patience);

        using var frame = await session.ReadFrameAsync();

        AssertEveryByteIs(8, frame);
        Assert.Equal(7, session.Metrics.FramesDropped);

        await session.StopCaptureAsync();
    }

    /// <summary>
    /// The state the whole policy turns on: the consumer is holding its entire
    /// allowance of <c>BufferCount</c> frames <em>and</em> the queue is full. The
    /// producer's spare buffer means it can still copy the newest frame and trade
    /// the queued one for it, so what the consumer reads next is the newest frame
    /// the camera produced — not the stale one that was queued when it took its
    /// last lease.
    /// </summary>
    /// <remarks>
    /// Without the spare (a pool of <c>BufferCount + QueueDepth</c>) this exact
    /// state empties the pool: the copy is refused before the write that would
    /// have evicted anything, every later frame is dropped, and the queued frame
    /// goes stale in place. That is <c>DropIncoming</c> behaviour wearing the
    /// latest-wins name, so this asserts the delivered pixels rather than a drop
    /// count — a count cannot tell the two apart.
    /// </remarks>
    [Fact]
    public async Task LatestWins_KeepsTheNewestFrame_WhileTheConsumerHoldsItsWholeAllowance()
    {
        // Paced so the consumer takes its two leases well before the producer
        // runs out: 30 frames at 5 ms is ~150 ms, and the two reads take
        // microseconds. The assertions do not depend on the rate.
        await using var backend = new InMemoryCameraBackend
        {
            MaxFrames = 30,
            FrameDelay = TimeSpan.FromMilliseconds(5),
        };
        await using var session = await CameraTestHarness.OpenSessionAsync(
            backend,
            options: new CameraSessionOptions(
                BufferCount: 2, QueueDepth: 1,
                ExhaustionPolicy: BufferExhaustionPolicy.LatestWins));

        await session.StartCaptureAsync();

        // Take and keep the consumer's whole allowance of two.
        using var firstHeld = await session.ReadFrameAsync();
        using var secondHeld = await session.ReadFrameAsync();

        // Let the producer run to the end against that. It parks after frame 30,
        // so there is a settled state to read rather than a race.
        await backend.ReadHangReached.WaitAsync(Patience);
        Assert.Equal(30, backend.FrameCounter);

        // Two in the consumer's hands, one in the queue. The fourth seeded buffer
        // is the spare, free again because the last eviction returned it.
        Assert.Equal(3, session.Metrics.OutstandingLeases);

        using var newest = await session.ReadFrameAsync();
        AssertEveryByteIs(30, newest);

        await session.StopCaptureAsync();
    }

    /// <summary>
    /// The one drop latest-wins cannot trade its way out of: the consumer holds
    /// <em>more</em> than its allowance, so there is nothing left to copy into and
    /// this session does not revoke an active lease (ADR-0035 D9). The newest
    /// frame is refused, and counted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bounded at thirty frames so the producer parks and the session settles,
    /// rather than left unbounded and polled. An unbounded producer has no
    /// settled state, so every assertion has to synchronise on a live counter —
    /// and the obvious counter to pick, <c>FramesDropped</c>, is not a proxy for
    /// the state this test is about. Latest-wins counts two different drops into
    /// it: eviction on a write, which needs only a full queue and starts at frame
    /// 2, and the <c>TryDeliver</c> refusal that is the subject here. Waiting for
    /// the first drop can therefore return while the queue is momentarily empty
    /// and only the two held leases are outstanding, which is a legal transient
    /// and is what CI caught (expected 3, actual 2).
    /// </para>
    /// <para>
    /// Both counts below are hand-derived from the fake's contract and hold
    /// whichever path produced the drops, so nothing here depends on the
    /// consumer winning a race against the producer at start-up.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task LatestWins_CountsTheDrop_WhenTheConsumerHoldsMoreThanItsAllowance()
    {
        // A small pacing delay so the producer runs at a few hundred frames a
        // second rather than as fast as a synthetic backend can allocate. The
        // assertions below do not depend on the rate.
        await using var backend = new InMemoryCameraBackend
        {
            MaxFrames = 30,
            FrameDelay = TimeSpan.FromMilliseconds(5),
        };
        await using var session = await CameraTestHarness.OpenSessionAsync(
            backend,
            options: new CameraSessionOptions(
                BufferCount: 1, QueueDepth: 1,
                ExhaustionPolicy: BufferExhaustionPolicy.LatestWins));

        await session.StartCaptureAsync();

        // Two leases against an allowance of one, neither disposed.
        using var held = await session.ReadFrameAsync();
        using var alsoHeld = await session.ReadFrameAsync();

        // The producer parks after frame 30, so what follows is a settled state.
        await backend.ReadHangReached.WaitAsync(Patience);
        Assert.Equal(30, backend.FrameCounter);

        // Three is the only value the pool can settle at. The consumer holds two
        // and disposes neither, the third seeded buffer goes into the queue, and
        // an empty pool is absorbing — nothing returns a buffer from here.
        Assert.Equal(3, session.Metrics.OutstandingLeases);

        // Thirty produced; three of them occupy the three buffers above; every
        // other one was dropped. A leaked buffer or a double count moves this.
        Assert.Equal(27, session.Metrics.FramesDropped);

        // The counts alone cannot say which drop path ran, and the refusal is the
        // subject. A dry pool has no buffer to copy frame 30 into, so the frame
        // left in the queue is a stale one. Its sibling test above, which has the
        // spare buffer, reads frame 30 out of this same state.
        using var queued = await session.ReadFrameAsync();
        AssertNotFrame(30, queued);

        await session.StopCaptureAsync();
    }

    // ── ADR-0035 Decision 9 ────────────────────────────────────────────

    [Fact]
    public async Task ActiveLeases_NeverRevoked()
    {
        await using var backend = new InMemoryCameraBackend();
        await using var session = await CameraTestHarness.OpenSessionAsync(
            backend, options: new CameraSessionOptions(BufferCount: 2));

        await session.StartCaptureAsync();

        var frame = await session.ReadFrameAsync();
        int firstLength = frame.ContiguousBuffer.Length;
        byte firstByte = frame.ContiguousBuffer.Span[0];

        var f2 = await session.ReadFrameAsync();
        f2.Dispose();

        Assert.Equal(firstLength, frame.ContiguousBuffer.Length);
        Assert.Equal(firstByte, frame.ContiguousBuffer.Span[0]);
        Assert.Equal(640, frame.Width);

        frame.Dispose();
    }

    // ── Defaults ───────────────────────────────────────────────────────

    [Fact]
    public void DefaultPolicy_IsLatestWins() =>
        Assert.Equal(BufferExhaustionPolicy.LatestWins, new CameraSessionOptions().ExhaustionPolicy);

    [Fact]
    public void DefaultBufferCount_IsThree() =>
        Assert.Equal(3, new CameraSessionOptions().BufferCount);

    [Fact]
    public void DefaultQueueDepth_IsOne() =>
        Assert.Equal(1, new CameraSessionOptions().QueueDepth);

    // ── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// The fake fills frame <paramref name="frameIndex"/> with that index's low
    /// byte, so the whole buffer identifies which frame arrived. Asserting every
    /// byte, not the first, also catches a frame stitched from two buffers.
    /// </summary>
    private static void AssertEveryByteIs(int frameIndex, LeasedCameraFrame frame)
    {
        byte expected = (byte)(frameIndex & 0xFF);
        var span = frame.ContiguousBuffer.Span;
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] != expected)
                Assert.Fail(
                    $"Expected frame {frameIndex} (every byte 0x{expected:X2}), but byte {i} of "
                        + $"{span.Length} is 0x{span[i]:X2}.");
        }
    }

    /// <summary>
    /// The complement of <see cref="AssertEveryByteIs"/>, for where the point is
    /// that a frame could not be delivered. The fake fills a frame uniformly, so
    /// one byte names it.
    /// </summary>
    private static void AssertNotFrame(int frameIndex, LeasedCameraFrame frame)
    {
        byte unwanted = (byte)(frameIndex & 0xFF);
        byte actual = frame.ContiguousBuffer.Span[0];
        if (actual == unwanted)
            Assert.Fail(
                $"Expected a frame older than {frameIndex}, but byte 0 is 0x{actual:X2} — "
                    + $"frame {frameIndex} reached the queue, so the pool was not exhausted.");
    }

    /// <summary>
    /// Polls a producer-thread observation until it holds. The producer runs
    /// concurrently with the test, so there is no synchronous moment to read
    /// these at; a fixed delay would either be slow or flaky.
    /// </summary>
    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Timed out after {Patience.TotalSeconds:F0}s waiting for {what}.");
            await Task.Delay(10);
        }
    }
}
