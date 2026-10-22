using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Encoding;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.2: shards must never be written in the container the user asked for.
///
/// The bug these guard: a 243 s / 196 Mbit/s rawvideo source split into five
/// FLV shards rejoined with <c>-f concat -c copy</c> produced a file whose
/// picture stopped at 194.8 s while the sound ran to 238.6 s. All five shards
/// decoded cleanly on their own (730 frames each, zero errors); only the join
/// was broken -- 730 of 3651 frames came back as "Bad picture start code". The
/// same shards staged in Matroska joined perfectly and survived a
/// <c>-c copy</c> remux to FLV.
///
/// A unit test cannot encode a file, so it pins the decision instead: every
/// shard is staged as Matroska, and the target container is only applied by the
/// remux after the join.
/// </summary>
public sealed class ShardStagingTests
{
    private static EncoderInfo Encoder(string name, string ext, string format) => new()
    {
        Name = name,
        Extension = ext,
        FormatName = format,
        Codec = name,
    };

    [Test]
    public async Task EveryConcatSafeContainerIsStagedInMatroska()
    {
        foreach (string format in Constants.ConcatSafeFormats.OrderBy(f => f, StringComparer.Ordinal))
        {
            EncoderInfo staging = ParallelEncoder.StagingEncoder(
                Encoder("mpeg4", format, format));

            await Assert.That(staging.FormatName).IsEqualTo("matroska")
                .Because($"{format} shards are joined through Matroska");
            await Assert.That(staging.Extension).IsEqualTo("mkv");
        }
    }

    [Test]
    public async Task StagingKeepsTheEncoderItself()
    {
        // Swapping the container must not swap the codec: the shard is encoded
        // with the user's encoder, only the muxer changes.
        EncoderInfo flv = Encoder("flv", "flv", "flv");
        EncoderInfo staging = ParallelEncoder.StagingEncoder(flv);

        await Assert.That(staging.Name).IsEqualTo("flv");
        await Assert.That(staging.Codec).IsEqualTo("flv");
        await Assert.That(staging.Family).IsEqualTo(flv.Family);
        await Assert.That(flv.FormatName).IsEqualTo("flv").Because("the source info is unchanged");
    }

    [Test]
    public async Task MatroskaStagingHasAnExtensionTheValidatorAccepts()
    {
        // RealtimeEncoder.RunSingle validates the shard path against the
        // staging encoder; a missing entry would throw on every shard.
        await Assert.That(Constants.FormatExtensions.TryGetValue("matroska", out var exts)).IsTrue();
        await Assert.That(exts!.Contains(".mkv")).IsTrue();
    }

    [Test]
    public async Task MatroskaStagingKnowsTheAudioEncodersTheShardsUse()
    {
        // The audio table is keyed by container, so the staging container has
        // to appear there or an explicit --audio-encoder would be refused.
        foreach (string audio in new[] { "libmp3lame", "mp2", "aac", "ac3" })
            await Assert.That(Core.AudioCatalog.SupportsContainer(audio, "matroska")).IsTrue()
                .Because($"matroska must accept {audio} for staged shards");
    }
}
