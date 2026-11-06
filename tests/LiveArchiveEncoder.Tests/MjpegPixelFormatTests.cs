using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Encoding;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.8.11: FFmpeg's MJPEG encoder rejects a limited-range picture at the
/// default strictness -- <c>libavcodec/mjpegenc_common.c:569</c>,
/// <c>ff_mjpeg_encode_check_pix_fmt</c>. It accepts one only with
/// <c>-strict unofficial</c>, which this tool never passes. So the live pass
/// has to write full-range YUV and a JPEG colour range itself, the way the
/// second pass already does.
/// </summary>
public sealed class MjpegPixelFormatTests
{
    [Test]
    [Arguments("yuv420p", "yuvj420p")]
    [Arguments("yuv422p", "yuvj422p")]
    [Arguments("yuv444p", "yuvj444p")]
    [Arguments("yuvj420p", "yuvj420p")]
    [Arguments("yuvj422p", "yuvj422p")]
    [Arguments("yuvj444p", "yuvj444p")]
    [Arguments("gray", "yuvj420p")]
    public async Task MjpegWritesFullRangeWhetherOrNotItWasRequested(
        string transport, string expected)
    {
        await Assert.That(string.Join(' ', ArgsBuilder.EncoderPixFmtArgs("mjpeg", transport, false)))
            .IsEqualTo($"-pix_fmt {expected}");
        await Assert.That(string.Join(' ', ArgsBuilder.EncoderPixFmtArgs("mjpeg", transport, true)))
            .IsEqualTo($"-pix_fmt {expected}");
    }

    [Test]
    public async Task OtherEncodersKeepTheirPreviousBehaviour()
    {
        await Assert.That(ArgsBuilder.EncoderPixFmtArgs("mpeg4", "yuv420p", false).Count)
            .IsEqualTo(0);
        await Assert.That(string.Join(' ', ArgsBuilder.EncoderPixFmtArgs("mpeg4", "yuv420p", true)))
            .IsEqualTo("-pix_fmt yuv420p");
        await Assert.That(ArgsBuilder.NeedsJpegRange("libx264")).IsFalse();
        await Assert.That(ArgsBuilder.NeedsJpegRange("mjpeg")).IsTrue();
    }

    [Test]
    public async Task MjpegPinsJpegRangeEvenWhenTheSourceSaysTv()
    {
        var stream = new StreamInfo { ColorRange = "tv", ColorSpace = "smpte170m" };
        const string expected = "-color_range pc -colorspace smpte170m";

        await Assert.That(string.Join(' ', ArgsBuilder.ColorArgs(stream, null, "mjpeg")))
            .IsEqualTo(expected);
        // An explicit --color-range tv cannot put MJPEG back into limited range.
        await Assert.That(string.Join(' ', ArgsBuilder.ColorArgs(
                stream, new ColorOverride("tv", null, null, null), "mjpeg")))
            .IsEqualTo(expected);
        // Every other encoder still inherits the source's tags unchanged.
        await Assert.That(string.Join(' ', ArgsBuilder.ColorArgs(stream, null, "mpeg4")))
            .IsEqualTo("-color_range tv -colorspace smpte170m");
    }
}
