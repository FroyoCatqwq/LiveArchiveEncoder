using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.Encoding;
using LiveArchiveEncoder.PostProcess;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.8.1: RealMedia. The container and both codecs were reachable before this
/// version -- <c>--encoder rv10</c> already wrote a .rm -- but the audio side was
/// whatever the rm muxer defaults to (ac3), which no player of the period ever
/// decoded. And the second-pass catalog did not know rv10/rv20 at all, so
/// <c>--recompress-codec rv20</c> silently fell back to libx264 in MP4: a
/// "RealVideo" delivery that was H.264.
///
/// Every case below is one of those two failures.
/// </summary>
public sealed class RealMediaTests
{
    // ---- the live pass: a .rm gets a RealAudio track --------------------

    [Test]
    public async Task RealMediaPrefersItsOwnAudioCodec()
    {
        await Assert.That(Constants.DefaultAudioByContainer["rm"][0]).IsEqualTo("real_144");
        await Assert.That(AudioCatalog.Compatible("rm", ["aac", "ac3", "real_144"])[0])
            .IsEqualTo("real_144");
    }

    [Test]
    public async Task RealAudioIsEightKilohertzMonoOnly()
    {
        // FFmpeg's real_144 encoder lists exactly one rate and one channel
        // layout; anything else is snapped rather than refused, because a value
        // the encoder cannot honour is not a reason to lose the run.
        (int? rate, int channels, int kbps) = AudioCatalog.Effective(
            "real_144", rate: 44100, channels: 2, kbps: 96, container: "rm");

        await Assert.That(rate).IsEqualTo(8000);
        await Assert.That(channels).IsEqualTo(1);
        await Assert.That(kbps).IsEqualTo(96);      // the bitrate is not a knob: FFmpeg ignores it
    }

    [Test]
    public async Task RealAudioBelongsToRealMediaOnly()
    {
        await Assert.That(AudioCatalog.SupportsContainer("real_144", "rm")).IsTrue();
        await Assert.That(AudioCatalog.SupportsContainer("real_144", "mp4")).IsFalse();
        await Assert.That(AudioTrack.ContainerFor("real_144")).IsEqualTo("rm");
    }

    [Test]
    public async Task AnUnsupportedRateIsReportedNotSilentlyAccepted()
    {
        string? problem = AudioCatalog.Validate("real_144", "rm", rate: 44100, channels: 1, kbps: 16);
        await Assert.That(problem).IsNotNull();
        await Assert.That(problem!).Contains("8000");

        await Assert.That(AudioCatalog.Validate("real_144", "rm", rate: 8000, channels: 1, kbps: 16))
            .IsNull();
    }

    // ---- the second pass: rv10/rv20 exist at all ------------------------

    [Test]
    public async Task TheSecondPassKnowsRealVideo()
    {
        // Before v7.9.8.1 FindVideo returned null for both, and
        // EraPreset.NativeVideoArgs silently substituted libx264.
        await Assert.That(RecompressCodecs.FindVideo("rv10")).IsNotNull();
        await Assert.That(RecompressCodecs.FindVideo("rv20")).IsNotNull();
        await Assert.That(RecompressCodecs.RecommendedAudio("rv20")).IsEqualTo("real_144");
    }

    [Test]
    public async Task RealVideoPairsWithRealAudioInRealMedia()
    {
        await Assert.That(RecompressCodecs.DefaultContainer("rv20", "real_144")).IsEqualTo("rm");
        await Assert.That(RecompressCodecs.ValidateContainer("rv10", "real_144", "rm")).IsNull();
    }

    [Test]
    public async Task RealVideoRefusesContainersThatCannotHoldIt()
    {
        // Measured 2026-10-27: AVI stores no fourcc for rv10 (it probes back as
        // rawvideo) and the Matroska muxer refuses it outright. Letting either
        // through would produce a file that looks encoded and does not play --
        // and it is also why --parallel stays off for these codecs.
        string? inMp4 = RecompressCodecs.ValidateContainer("rv20", "real_144", "mp4");
        await Assert.That(inMp4).IsNotNull();
        await Assert.That(inMp4!).Contains("rm");
        await Assert.That(Constants.ConcatSafeFormats.Contains("rm")).IsFalse();
    }
}
