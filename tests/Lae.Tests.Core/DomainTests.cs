using Lae.Core;
using Lae.Core.Settings;

namespace Lae.Tests.Core;

/// <summary>
/// The domain code moved out of v7 on the way to M1.
///
/// <para>These are not exhaustive tests of things that already worked in v7 --
/// they are the check that the <em>copy</em> is faithful. A move that only
/// compiles proves nothing; the constants, the quality calibration and the
/// catalogues are the values every encode depends on, so a slipped digit shows
/// up here rather than in a bad recording three weeks later.</para>
/// </summary>
public sealed class DomainTests
{
    /// <summary>
    /// Which knob a given encoder actually has. Getting this wrong is what made
    /// v7.9.8.2 ask OpenH264 for a CRF it does not have and overshoot the
    /// bitrate by three times.
    /// </summary>
    [Test]
    public async Task EncodersAreClassifiedByTheKnobTheyHave()
    {
        await Assert.That(EncoderMath.ScaleOf("libx264")).IsEqualTo(EncoderMath.QualityScale.Crf);
        await Assert.That(EncoderMath.ScaleOf("libx265")).IsEqualTo(EncoderMath.QualityScale.Crf);
        await Assert.That(EncoderMath.ScaleOf("mpeg4")).IsEqualTo(EncoderMath.QualityScale.Qv);
        await Assert.That(EncoderMath.ScaleOf("flv")).IsEqualTo(EncoderMath.QualityScale.Qv);
        // Bitrate only: no CRF, no usable -q:v.
        await Assert.That(EncoderMath.ScaleOf("libopenh264")).IsEqualTo(EncoderMath.QualityScale.Bitrate);
        // Unknown names fall back to the mpeg family, which is the safer guess:
        // a stray -q:v is ignored by more encoders than a stray -crf is.
        await Assert.That(EncoderMath.ScaleOf("something-unknown")).IsEqualTo(EncoderMath.QualityScale.Qv);
        await Assert.That(EncoderMath.ScaleOf(null)).IsEqualTo(EncoderMath.QualityScale.Qv);
    }

    /// <summary>
    /// The calibration is <c>crf = 7.5 + 1.17 * qv</c>, measured rather than
    /// derived. If this drifts, an era preset silently changes its picture.
    /// </summary>
    [Test]
    public async Task QualityConversionUsesTheMeasuredCalibration()
    {
        // 7.5 + 1.17 * 2 = 9.84 -> 10
        await Assert.That(EncoderMath.ConvertQuality("mpeg4", "libx264", 2)).IsEqualTo(10);
        // (10 - 7.5) / 1.17 = 2.14 -> 2
        await Assert.That(EncoderMath.ConvertQuality("libx264", "mpeg4", 10)).IsEqualTo(2);
        // Clamped into each scale's legal range rather than wrapped.
        await Assert.That(EncoderMath.ConvertQuality("mpeg4", "libx264", 31)).IsLessThanOrEqualTo(51);
        await Assert.That(EncoderMath.ConvertQuality("libx264", "mpeg4", 51)).IsLessThanOrEqualTo(31);
        await Assert.That(EncoderMath.ConvertQuality("libx264", "mpeg4", 0)).IsGreaterThanOrEqualTo(2);
    }

    /// <summary>Every quality the mpeg family can express survives a round trip.</summary>
    [Test]
    public async Task QualitySurvivesARoundTrip()
    {
        for (int qv = 2; qv <= 31; qv++)
        {
            int crf = EncoderMath.ConvertQuality("mpeg4", "libx264", qv);
            int back = EncoderMath.ConvertQuality("libx264", "mpeg4", crf);
            // A rounding step, not a drift: the mapping is a straight line and
            // two roundings can move by one.
            await Assert.That(Math.Abs(back - qv)).IsLessThanOrEqualTo(1);
        }
    }

    /// <summary>A bitrate-only encoder has no number to translate.</summary>
    [Test]
    public async Task BitrateEncodersKeepTheNumberTheyWereGiven()
    {
        await Assert.That(EncoderMath.ConvertQuality("mpeg4", "libopenh264", 8)).IsEqualTo(8);
        await Assert.That(EncoderMath.ConvertQuality("libopenh264", "libx264", 700)).IsEqualTo(700);
    }

    [Test]
    public async Task AudioCatalogueKnowsTheUsualCodecs()
    {
        await Assert.That(AudioCatalog.Find("libmp3lame")).IsNotNull();
        await Assert.That(AudioCatalog.Find("aac")).IsNotNull();
        await Assert.That(AudioCatalog.Find("nonexistent-codec")).IsNull();

        // MP3 inside AVI is the archive default; this pairing is what the
        // container rules exist to police.
        await Assert.That(AudioCatalog.SupportsContainer("libmp3lame", "avi")).IsTrue();

        // Channels are clamped into what the codec can carry rather than
        // rejected, so a 5.1 source still encodes.
        await Assert.That(AudioCatalog.ClampChannels("libmp3lame", 6)).IsGreaterThan(0);
    }

    [Test]
    public async Task PixelFormatCatalogueKnowsTheCommonFormats()
    {
        await Assert.That(PixelFormatCatalog.TryGet("yuv420p", out PixFmtInfo info)).IsTrue();
        await Assert.That(info.Name).IsEqualTo("yuv420p");

        await Assert.That(PixelFormatCatalog.TryGet("pal8", out _)).IsTrue();
        await Assert.That(PixelFormatCatalog.TryGet("not-a-format", out _)).IsFalse();

        // "auto", "source" and "-" all mean "let the encoder decide", and all
        // three normalise to the empty string so callers test one thing.
        await Assert.That(PixelFormatCatalog.Normalise(" AUTO ")).IsEqualTo("");
        await Assert.That(PixelFormatCatalog.Normalise("source")).IsEqualTo("");
        await Assert.That(PixelFormatCatalog.Normalise(" yuv420p ")).IsEqualTo("yuv420p");
    }

    /// <summary>
    /// The archive defaults. They were the same numbers in every version since
    /// v6.3.3, and they are what a run with no settings file produces.
    /// </summary>
    [Test]
    public async Task TheArchiveDefaultsAreUnchanged()
    {
        await Assert.That(Constants.DefaultWidth).IsEqualTo(256);
        await Assert.That(Constants.DefaultHeight).IsEqualTo(192);
        await Assert.That(Constants.DefaultFps).IsEqualTo(13.0);
        await Assert.That(Constants.DefaultFpsLevels.Length).IsEqualTo(4);
        await Assert.That(Constants.DefaultFpsLevels[0]).IsEqualTo(13.0);

        // The settings document must not keep a second copy of these numbers.
        EncodeDefaults defaults = new SettingsDocument().Defaults;
        await Assert.That(defaults.Width).IsEqualTo(Constants.DefaultWidth);
        await Assert.That(defaults.Height).IsEqualTo(Constants.DefaultHeight);
        await Assert.That(defaults.Fps).IsEqualTo(Constants.DefaultFps);
        await Assert.That(defaults.FpsLevels.Count).IsEqualTo(Constants.DefaultFpsLevels.Length);
    }

    /// <summary>
    /// The second-pass catalogue: which codecs exist, what container each can be
    /// written into, and which pairings the muxer refuses. A preset is only as
    /// good as this table.
    /// </summary>
    [Test]
    public async Task TheRecompressCatalogueRejectsImpossiblePairings()
    {
        await Assert.That(RecompressCodecs.FindVideo("libx264")).IsNotNull();
        await Assert.That(RecompressCodecs.FindVideo("nope")).IsNull();
        await Assert.That(RecompressCodecs.IsCopyAudio("copy")).IsTrue();
        await Assert.That(RecompressCodecs.IsCopyAudio("aac")).IsFalse();

        // H.264 + AAC is the sane mp4 pairing, so a container is offered.
        string? container = RecompressCodecs.DefaultContainer("libx264", "aac");
        await Assert.That(container).IsNotNull();
        await Assert.That(RecompressCodecs.ValidateContainer("libx264", "aac", container!)).IsNull();

        // A container that cannot hold the pair must be reported, not silently
        // accepted and then fail inside FFmpeg minutes later.
        await Assert.That(RecompressCodecs.ValidateContainer("libx264", "aac", "not-a-container")).IsNotNull();
    }
}
