using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Localization;
using LiveArchiveEncoder.PostProcess;
using LiveArchiveEncoder.Remote;
using LiveArchiveEncoder.UI;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.9.7 regressions: the reported flv EINVAL (a -maxrate with no
/// -bufsize), the CUI -b:v row, and the audit fixes that came with it.
/// </summary>
public sealed class V7997Tests
{
    [Test]
    public async Task AMaxrateWithoutABufferIsCompletedAutomatically()
    {
        // Exactly the fragment from the bug report: FFmpeg's MPEG-video
        // encoders answer EINVAL "Either both buffer size and max rate or
        // neither must be specified". Two seconds of maxrate is the same
        // buffer the CRF path already uses.
        (string fragment, string? note) = FragmentEditor.NormalizeVbvPair(
            "-q:v 11 -maxrate 300k -minrate 128k");
        await Assert.That(fragment.Contains("-bufsize 600k", StringComparison.Ordinal)).IsTrue();
        await Assert.That(note).IsNotNull();
        await Assert.That(FragmentEditor.ValidateVbvPair(fragment)).IsNull();
    }

    [Test]
    public async Task ABufferWithoutAMaxrateIsCompletedAutomatically()
    {
        (string fragment, string? note) = FragmentEditor.NormalizeVbvPair("-q:v 11 -bufsize 600k");
        await Assert.That(fragment.Contains("-maxrate 300k", StringComparison.Ordinal)).IsTrue();
        await Assert.That(note).IsNotNull();
    }

    [Test]
    public async Task ACompletePairIsLeftAlone()
    {
        const string fragment = "-maxrate 300k -minrate 128k -bufsize 600k";
        (string result, string? note) = FragmentEditor.NormalizeVbvPair(fragment);
        await Assert.That(result).IsEqualTo(fragment);
        await Assert.That(note).IsNull();
    }

    [Test]
    public async Task LibraryEncodersAreNotRejectedForAHalfPair()
    {
        // Measured against the bundled FFmpeg: libx264, mpeg4 and libopenh264
        // all accept -maxrate without -bufsize. Only flv1/h261-style encoders
        // refuse it, so a blanket rejection would break valid configurations.
        await Assert.That(FragmentEditor.ValidateVbvPair("-crf 26 -maxrate 300k")).IsNull();
        await Assert.That(FragmentEditor.ValidateVbv(
            "-c:v libx264 -crf 26 -b:v 400k -maxrate 300k")).IsNull();
        // An impossible bound is still a hard error for every encoder.
        await Assert.That(FragmentEditor.ValidateVbvPair("-maxrate 100k -minrate 300k")).IsNotNull();
    }

    [Test]
    public async Task SetFlagDoesNotSwallowTheNextTokenAfterASwitch()
    {
        // -an takes no value. Without the ValueLessFlags guard, removing it
        // used to eat "-c:v" as well and leave a dangling "flv".
        string result = FragmentEditor.SetFlag("-an -c:v flv -b:v 240k", "-an", null);
        await Assert.That(result).IsEqualTo("-c:v flv -b:v 240k");
        // A valued flag still consumes its value.
        await Assert.That(FragmentEditor.SetFlag("-c:v flv -b:v 240k", "-c:v", null))
            .IsEqualTo("-b:v 240k");
    }

    [Test]
    public async Task MergeDeduplicatesFlagsRepeatedInsideExtras()
    {
        // --recompress-maxrate 300 plus --recompress-extra "-maxrate 400k"
        // used to produce two -maxrate flags; FFmpeg is last-wins, so the
        // explicit number lost silently.
        string merged = FragmentEditor.Merge(
            "-c:v libx264 -b:v 250k", "-maxrate 300k -maxrate 400k -bufsize 600k");
        await Assert.That(merged.Contains("-maxrate 300k", StringComparison.Ordinal)).IsTrue();
        await Assert.That(merged.Contains("-maxrate 400k", StringComparison.Ordinal)).IsFalse();
        await Assert.That(merged.Split("-maxrate").Length - 1).IsEqualTo(1);
    }

    [Test]
    public async Task KbpsUnderstandsMegabitSuffixAndExplicitZero()
    {
        // The codec catalog itself emits "-b:v 0" (VP9/AV1 CRF) and "-b:v 1M".
        (List<string> tokens, string? error) = PostArgs.Parse("-b:v 1M -maxrate 0");
        await Assert.That(error).IsNull();
        await Assert.That(FragmentEditor.Kbps(tokens, "-b:v")).IsEqualTo(1000);
        await Assert.That(FragmentEditor.Kbps(tokens, "-maxrate")).IsEqualTo(0);
        await Assert.That(FragmentEditor.ValidateVbv("-c:v libvpx-vp9 -crf 30 -b:v 0")).IsNull();
    }

    [Test]
    public async Task UnknownLongOptionIsNotSwallowedAsAValue()
    {
        (CliOptions? missing, string? error) = CliParser.Parse(
            ["clip.mkv", "--recompress-extra", "--bogus"]);
        await Assert.That(error).IsNotNull();
        // A single-dash FFmpeg fragment must still be a legal value.
        (CliOptions? ok, error) = CliParser.Parse(
            ["clip.mkv", "--recompress-extra", "-preset slow"]);
        await Assert.That(error).IsNull();
        await Assert.That(ok!.RecompressExtra).IsEqualTo("-preset slow");
    }

    [Test]
    public async Task HandWrittenFragmentHonoursExplicitVideoBitrate()
    {
        // --recompress-video-kbps was read only inside the catalog-generated
        // branch, so a hand-written --recompress-args kept its own -b:v 240k.
        (CliOptions? opts, _) = CliParser.Parse(
            ["clip.mkv", "--recompress-args", "-c:v flv -q:v 9 -b:v 240k",
             "--recompress-video-kbps", "400"]);
        (RecompressSettings rc, string? error) = Cli.ResolveRecompress(opts!, null, true);
        await Assert.That(error).IsNull();
        await Assert.That(rc.Args.Contains("-b:v 400k", StringComparison.Ordinal)).IsTrue();
        await Assert.That(rc.Args.Contains("-q:v", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task CatalogGeneratedFragmentStillKeepsItsQuality()
    {
        // The M1 fix must not rewrite a fragment the codec catalog generated.
        (CliOptions? opts, _) = CliParser.Parse(
            ["clip.mkv", "--recompress-codec", "libx264", "--recompress-codec", "libopenh264",
             "--recompress-quality", "26", "--recompress-video-kbps", "400"]);
        (RecompressSettings rc, string? error) = Cli.ResolveRecompress(opts!, null, true);
        await Assert.That(error).IsNull();
        await Assert.That(rc.Args.Contains("-crf 26", StringComparison.Ordinal)).IsTrue();
        await Assert.That(rc.Args.Contains("-b:v", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task NewMenuAndProbeKeysExistInBothLanguages()
    {
        foreach (string key in new[]
                 {
                     "rc.menu.videobitrate", "parallel.netslow.throughput",
                     "parallel.netslow.jitter", "parallel.netslow.latency",
                 })
        {
            await Assert.That(L.KeysFor(L.English).Contains(key)).IsTrue();
            await Assert.That(L.KeysFor(L.Chinese).Contains(key)).IsTrue();
        }
    }

    [Test]
    public async Task NetworkMeasurementNamesTheVetoReason()
    {
        // The bug report showed 125.8x headroom and "not enough headroom" in
        // the same line: jitter (51%) was the real veto.
        var jitter = new NetworkMeasurement("read", 24.7, 13, 51, 4, 1570, false, "jitter");
        await Assert.That(jitter.RejectReason).IsEqualTo("jitter");
        await Assert.That(jitter.Headroom).IsGreaterThan(100);
        var adequate = new NetworkMeasurement("read", 24.7, 13, 5, 4, 1570, true);
        await Assert.That(adequate.RejectReason).IsNull();
        // 24.7 MiB/s against 1570 kbps is ~125.9x; assert the shape, not the
        // rounded digit (the report's 125.8 came from a slightly smaller MB/s).
        await Assert.That(adequate.Summary).Contains("x headroom");
    }
}
