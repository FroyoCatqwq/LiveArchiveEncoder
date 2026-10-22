using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.Frames;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// Guard rails for the two pieces of shared state that a parallel encode runs
/// into (v7.9.3.1).
///
/// Both of these are counters, and both used to be plain fields: the timer was a
/// single boolean, so the first shard to finish handed the coarse Windows timer
/// back to every shard still encoding, and the frame pool's free list would
/// happily accept the same buffer twice and then hand it to two consumers.
/// </summary>
public sealed class ParallelHybridTests
{
    // ------------------------------------------------- HighResolutionTimer

    /// <summary>
    /// Balanced Enable/Disable pairs put the counter back to zero, and the
    /// period is only given back when the last holder lets go.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task TimerCountsItsHolders()
    {
        int before = HighResolutionTimer.Depth;

        HighResolutionTimer.Enable();
        await Assert.That(HighResolutionTimer.Depth).IsEqualTo(before + 1);

        HighResolutionTimer.Enable();
        await Assert.That(HighResolutionTimer.Depth).IsEqualTo(before + 2);

        // One shard finishing must not close the timer for the others.
        HighResolutionTimer.Disable();
        await Assert.That(HighResolutionTimer.Depth).IsEqualTo(before + 1);

        HighResolutionTimer.Disable();
        await Assert.That(HighResolutionTimer.Depth).IsEqualTo(before);
    }

    /// <summary>
    /// An unbalanced Disable cannot drive the counter negative -- a negative
    /// depth would make the *next* Enable look like a second holder and never
    /// raise the period at all.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task TimerIgnoresAStrayDisable()
    {
        int before = HighResolutionTimer.Depth;
        while (HighResolutionTimer.Depth > 0) HighResolutionTimer.Disable();

        HighResolutionTimer.Disable();
        await Assert.That(HighResolutionTimer.Depth).IsEqualTo(0);

        HighResolutionTimer.Enable();
        await Assert.That(HighResolutionTimer.Depth).IsEqualTo(1);
        HighResolutionTimer.Disable();
        await Assert.That(HighResolutionTimer.Depth).IsEqualTo(before);
    }

    // ------------------------------------------------------ FrameBufferPool

    /// <summary>
    /// Rent, publish, take-latest: the sequence the slot loop actually performs,
    /// and the buffers come back to the free list afterwards.
    /// </summary>
    [Test]
    public async Task PoolRecyclesBuffersThroughAPublishTakeCycle()
    {
        var pool = new FrameBufferPool(capacity: 2, frameSize: 16);

        await Assert.That(pool.TryRent(out int first)).IsTrue();
        pool.Publish(first, 1);
        await Assert.That(pool.ReadyCount).IsEqualTo(1);

        await Assert.That(pool.TryTakeLatest(out int latest, out long sequence)).IsTrue();
        await Assert.That(latest).IsEqualTo(first);
        await Assert.That(sequence).IsEqualTo(1);
        await Assert.That(pool.ReadyCount).IsEqualTo(0);
        pool.Release(latest);

        // Every slot must still be rentable; a leaked or double-freed slot shows
        // up here as a missing one. Rent without releasing -- returning them
        // while counting would let the pool hand the same slot out forever.
        var rented = new List<int>();
        while (pool.TryRent(out int index)) rented.Add(index);
        await Assert.That(rented.Count).IsEqualTo(4).Because("capacity + 2 slots must all come back");
    }

    /// <summary>
    /// A double release must not corrupt the free list. It used to write past
    /// the end of a fixed array (`_free[_freeCount++]`), which throws from a
    /// place that looks nothing like the mistake, or hands one buffer to two
    /// consumers and corrupts a frame.
    /// </summary>
    [Test]
    public async Task DoubleReleaseIsIgnored()
    {
        var pool = new FrameBufferPool(capacity: 1, frameSize: 8);

        await Assert.That(pool.TryRent(out int index)).IsTrue();
        pool.Release(index);
        pool.Release(index);        // the mistake
        pool.Release(index);

        var again = new List<int>();
        while (pool.TryRent(out int slot)) again.Add(slot);
        // 1 + 2 slots, and no more: the extra releases were dropped, not queued.
        await Assert.That(again.Count).IsEqualTo(3);
        await Assert.That(again.Distinct().Count()).IsEqualTo(3)
            .Because("a queued duplicate would hand one buffer out twice");
    }

    /// <summary>When full, the oldest frame is dropped rather than blocking the capture thread.</summary>
    [Test]
    public async Task OldestFrameIsEvictedWhenTheRingIsFull()
    {
        var pool = new FrameBufferPool(capacity: 2, frameSize: 8);

        for (int i = 0; i < 5; i++)
        {
            await Assert.That(pool.TryRent(out int slot)).IsTrue();
            pool.Publish(slot, i + 1);
        }
        await Assert.That(pool.ReadyCount).IsEqualTo(2);

        await Assert.That(pool.TryTakeLatest(out _, out long sequence)).IsTrue();
        await Assert.That(sequence).IsEqualTo(5).Because("the newest frame is the one that survives");
    }
}
