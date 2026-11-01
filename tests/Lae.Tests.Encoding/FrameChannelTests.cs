using Lae.Encoding;

namespace Lae.Tests.Encoding;

/// <summary>
/// The frame handoff. Every rule here exists because the alternative breaks a
/// running encode in a way that is hard to see: a tear shows as one corrupted
/// frame, an unbounded queue shows as output that falls further behind the clock.
/// </summary>
public sealed class FrameChannelTests
{
    [Test]
    public async Task HandsFramesOverInOrder()
    {
        var channel = new FrameChannel(2, capacity: 4);
        channel.Publish([1, 1]);
        channel.Publish([2, 2]);

        byte[] destination = new byte[2];

        await Assert.That(channel.TryTake(destination)).IsTrue();
        await Assert.That(destination).IsEquivalentTo(new byte[] { 1, 1 });

        await Assert.That(channel.TryTake(destination)).IsTrue();
        await Assert.That(destination).IsEquivalentTo(new byte[] { 2, 2 });
    }

    [Test]
    public async Task ReturnsFalseWhenTheQueueIsEmpty()
    {
        var channel = new FrameChannel(2);
        await Assert.That(channel.TryTake(new byte[2])).IsFalse();
    }

    /// <summary>
    /// A producer ahead of the consumer must not grow the queue without limit: the
    /// frame that goes is the oldest, because the newest is the one still worth
    /// showing.
    /// </summary>
    [Test]
    public async Task DropsTheOldestFrameWhenFull()
    {
        var channel = new FrameChannel(2, capacity: 2);
        channel.Publish([1, 1]);
        channel.Publish([2, 2]);
        channel.Publish([3, 3]);

        byte[] destination = new byte[2];

        await Assert.That(channel.Dropped).IsEqualTo(1L);
        await Assert.That(channel.Count).IsEqualTo(2);

        channel.TryTake(destination);
        await Assert.That(destination).IsEquivalentTo(new byte[] { 2, 2 });

        channel.TryTake(destination);
        await Assert.That(destination).IsEquivalentTo(new byte[] { 3, 3 });
    }

    /// <summary>
    /// A frame of the wrong length means the pipeline's arithmetic disagreed with
    /// the wire. Failing loudly here is what stops a wrong frame size from
    /// becoming a file that decodes into noise.
    /// </summary>
    [Test]
    public async Task RefusesAFrameOfTheWrongLength()
    {
        var channel = new FrameChannel(4);

        await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            channel.Publish([1, 2, 3]);
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task ReportsWhenTheProducerIsDone()
    {
        var channel = new FrameChannel(4);
        await Assert.That(channel.Completed).IsFalse();

        channel.Complete();

        await Assert.That(channel.Completed).IsTrue();
    }

    /// <summary>
    /// The queue bridges two threads, so the property worth proving is that
    /// nothing is invented: every frame either reaches the consumer or is counted
    /// as dropped, and the queue drains to empty.
    /// </summary>
    [Test]
    public async Task SurvivesConcurrentUse()
    {
        const int frameSize = 4;
        const int total = 500;
        var channel = new FrameChannel(frameSize, capacity: 16);
        var seen = new List<int>(total);

        Task producer = Task.Run(() =>
        {
            for (int i = 0; i < total; i++)
            {
                byte[] frame = new byte[frameSize];
                frame.AsSpan().Fill((byte)(i % 251));
                channel.Publish(frame);
            }

            channel.Complete();
        });

        byte[] destination = new byte[frameSize];

        // "Empty" and "done" are two locked reads, and the producer may publish
        // between them: it can fill the whole queue after TryTake failed and before
        // Completed is observed. Those frames are neither taken nor counted as
        // dropped, so the sums stop adding up -- which is what this test did under
        // a loaded machine, once, and cost an evening.
        //
        // The contract is "complete means no more will be published", so the safe
        // stop is: a take that fails *after* completion was already known. Two
        // observations of the same fact cannot be split by a publish.
        bool completed = false;
        while (true)
        {
            if (channel.TryTake(destination))
            {
                seen.Add(destination[0]);
                completed = false;
                continue;
            }

            if (completed) break;

            completed = channel.Completed;
            if (!completed) await Task.Delay(1);
        }

        await producer;
        await Assert.That(channel.Count).IsEqualTo(0);
        await Assert.That(seen.Count + (int)channel.Dropped).IsEqualTo(total);
    }
}
