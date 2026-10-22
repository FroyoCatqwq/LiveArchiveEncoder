using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.4 #1: msrle produced a black picture. Its transport format is pal8, and
/// a rawvideo pal8 frame is width*height <b>plus</b> the 1024-byte palette -- the
/// pipe reader cut every frame 1024 bytes short, so the frame data shifted by a
/// kilobyte per frame and the palette (the only place an indexed picture keeps
/// its colours) ended up in the wrong place.
///
/// Measured against this build (2026-10-22): 64x48 pal8 rawvideo is 4096 bytes
/// per frame, not 3072.
/// </summary>
public sealed class PaletteTransportTests
{
    [Test]
    public async Task Pal8FramesCarryThePaletteOnTopOfTheIndices()
    {
        await Assert.That(PixelFormatUtil.RawFrameSize(64, 48, "pal8")).IsEqualTo(64 * 48 + 1024);
        await Assert.That(PixelFormatUtil.RawFrameSize(384, 216, "pal8")).IsEqualTo(384 * 216 + 1024);
    }

    [Test]
    public async Task OtherFormatsAreUnchanged()
    {
        await Assert.That(PixelFormatUtil.RawFrameSize(320, 240, "yuv420p")).IsEqualTo(320 * 240 * 3 / 2);
        await Assert.That(PixelFormatUtil.RawFrameSize(320, 240, "yuv422p")).IsEqualTo(320 * 240 * 2);
        await Assert.That(PixelFormatUtil.RawFrameSize(320, 240, "rgb24")).IsEqualTo(320 * 240 * 3);
        // rgb8/bgr8 are packed 8-bit colour, not indexed: no palette.
        await Assert.That(PixelFormatUtil.RawFrameSize(320, 240, "rgb8")).IsEqualTo(320 * 240);
        await Assert.That(PixelFormatUtil.RawFrameSize(320, 240, "bgr8")).IsEqualTo(320 * 240);
    }

    [Test]
    public async Task OnlyPal8CountsAsIndexed()
    {
        await Assert.That(PixelFormatUtil.CarriesPalette("pal8")).IsTrue();
        await Assert.That(PixelFormatUtil.CarriesPalette("PAL8")).IsTrue();
        await Assert.That(PixelFormatUtil.CarriesPalette("rgb8")).IsFalse();
        await Assert.That(PixelFormatUtil.CarriesPalette("yuv420p")).IsFalse();
    }

    [Test]
    public async Task AnUnknownFormatIsStillRejected()
    {
        await Assert.That(() => PixelFormatUtil.RawFrameSize(320, 240, "bogus8"))
            .Throws<ArgumentException>();
    }
}

/// <summary>
/// v7.9.4 #3: mpeg1video accepted "--fps 15" and then died in preflight with
/// "MPEG-1/2 does not support 15/1 fps". The rate is in the encoder's own
/// "Supported framerates" line, so the check that trusted that list could never
/// catch it -- the eight frame_rate_code values are the honest answer.
/// </summary>
public sealed class EncoderRateTests
{
    private static EncoderCaps Caps(string name, params double[] declared) => new()
    {
        Name = name,
        SupportedRates = [.. declared],
    };

    /// <summary>The declared list exactly as this build prints it (2026-10-22).</summary>
    private static EncoderCaps Mpeg1Caps() => Caps("mpeg1video",
        24000.0 / 1001.0, 24, 25, 30000.0 / 1001.0, 30, 50, 60000.0 / 1001.0, 60,
        15, 5, 10, 12);

    [Test]
    public async Task Mpeg1OnlyOffersTheRealFrameRateCodes()
    {
        List<double> rates = EncoderMath.UsableRates(Mpeg1Caps());

        await Assert.That(rates).Count().IsEqualTo(8);
        foreach (double unofficial in new[] { 5.0, 10.0, 12.0, 15.0 })
        {
            await Assert.That(rates).DoesNotContain(unofficial)
                .Because($"{unofficial} fps is one of libmpeg3's unofficial rates");
        }
        await Assert.That(rates).Contains(24.0);
        await Assert.That(rates).Contains(25.0);
    }

    [Test]
    public async Task Mpeg1SnapsFifteenToTheNearestRealRate()
    {
        (double fps, bool exact) = EncoderMath.ChooseBaseFps(15.0, Mpeg1Caps());

        await Assert.That(exact).IsFalse();
        await Assert.That(fps).IsEqualTo(24000.0 / 1001.0).Within(1e-6);
    }

    [Test]
    public async Task Mpeg1LeavesARealRateAlone()
    {
        (double fps, bool exact) = EncoderMath.ChooseBaseFps(25.0, Mpeg1Caps());

        await Assert.That(exact).IsTrue();
        await Assert.That(fps).IsEqualTo(25.0);
    }

    [Test]
    public async Task Mpeg2KeepsEveryDeclaredRate()
    {
        // MPEG-2 carries a frame rate extension, and this build really does
        // encode 5/10/12/15/75 fps (measured 2026-10-22), so its declared list
        // is the truth and must not be filtered like MPEG-1's.
        EncoderCaps caps = Caps("mpeg2video", 5, 10, 12, 15, 24, 25, 30, 75);

        await Assert.That(EncoderMath.UsableRates(caps)).Count().IsEqualTo(8);
        (double fps, bool exact) = EncoderMath.ChooseBaseFps(15.0, caps);
        await Assert.That(exact).IsTrue();
        await Assert.That(fps).IsEqualTo(15.0);
    }

    [Test]
    public async Task AmvKeepsItsDivisionTable()
    {
        EncoderCaps caps = Caps("amv");
        List<double> rates = EncoderMath.UsableRates(caps);

        await Assert.That(rates).Count().IsEqualTo(Constants.AmvFps.Length);
        foreach (double rate in Constants.AmvFps)
        {
            await Assert.That(rates).Contains(rate);
        }
        (double fps, bool exact) = EncoderMath.ChooseBaseFps(13.0, caps);
        await Assert.That(exact).IsFalse();
        await Assert.That(fps).IsEqualTo(14.0);
    }

    [Test]
    public async Task AnEncoderWithNoDeclaredListKeepsWhatItWasGiven()
    {
        (double fps, bool exact) = EncoderMath.ChooseBaseFps(13.0, Caps("msrle"));

        await Assert.That(exact).IsTrue();
        await Assert.That(fps).IsEqualTo(13.0);
    }
}

/// <summary>
/// v7.9.4 #2: "out.avi --encoder mpeg1video" was rejected even though AVI has
/// been a legal target for it since v7.8 -- the extension check only knew the
/// encoder's default container. The typed extension is the request.
/// </summary>
public sealed class ContainerFromExtensionTests
{
    private static EncoderInfo Encoder(string name, string ext, string format) => new()
    {
        Name = name, Extension = ext, FormatName = format,
    };

    [Test]
    public async Task AnAviNameSwitchesMpeg1ToAvi()
    {
        EncoderInfo moved = EncoderMath.ResolveByExtension(Encoder("mpeg1video", "mpg", "mpeg"), "clip.avi");

        await Assert.That(moved.Name).IsEqualTo("mpeg1video");
        await Assert.That(moved.FormatName).IsEqualTo("avi");
        await Assert.That(moved.Extension).IsEqualTo("avi");
    }

    [Test]
    public async Task TheEncodersOwnExtensionChangesNothing()
    {
        EncoderInfo encoder = Encoder("mpeg1video", "mpg", "mpeg");

        await Assert.That(EncoderMath.ResolveByExtension(encoder, "clip.mpg")).IsSameReferenceAs(encoder);
        await Assert.That(EncoderMath.ResolveByExtension(encoder, "clip.mpeg")).IsSameReferenceAs(encoder);
        await Assert.That(EncoderMath.ResolveByExtension(encoder, "clip.vob")).IsSameReferenceAs(encoder);
    }

    [Test]
    public async Task EveryOtherContainerOfTheEncoderIsReachable()
    {
        await Assert.That(EncoderMath.ResolveByExtension(
            Encoder("mpeg4", "avi", "avi"), "clip.mp4").FormatName).IsEqualTo("mp4");
        await Assert.That(EncoderMath.ResolveByExtension(
            Encoder("libx264", "mp4", "mp4"), "clip.mkv").FormatName).IsEqualTo("matroska");
        await Assert.That(EncoderMath.ResolveByExtension(
            Encoder("libx264", "mp4", "mp4"), "clip.avi").FormatName).IsEqualTo("avi");
    }

    [Test]
    public async Task AnImpossibleExtensionIsLeftForTheErrorToReport()
    {
        // rv20 only writes RealMedia: the caller must still see the mismatch
        // rather than be handed a silently renamed container.
        EncoderInfo rv20 = Encoder("rv20", "rm", "rm");
        await Assert.That(EncoderMath.ResolveByExtension(rv20, "clip.mkv")).IsSameReferenceAs(rv20);
        await Assert.That(EncoderMath.ResolveByExtension(rv20, "clip.avi")).IsSameReferenceAs(rv20);

        await Assert.That(() => Validation.ValidateOutputExtension(
            new FileInfo(Path.Combine(Path.GetTempPath(), "clip.mkv")), rv20))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task AMissingExtensionOrPathIsANoOp()
    {
        EncoderInfo encoder = Encoder("mpeg1video", "mpg", "mpeg");

        await Assert.That(EncoderMath.ResolveByExtension(encoder, "clip")).IsSameReferenceAs(encoder);
        await Assert.That(EncoderMath.ResolveByExtension(encoder, null)).IsSameReferenceAs(encoder);
        await Assert.That(EncoderMath.ResolveByExtension(encoder, "")).IsSameReferenceAs(encoder);
    }

    [Test]
    public async Task TheResolvedContainerPassesTheExtensionCheck()
    {
        EncoderInfo moved = EncoderMath.ResolveByExtension(Encoder("mpeg1video", "mpg", "mpeg"), "clip.avi");
        string avi = Path.Combine(Path.GetTempPath(), "clip.avi");

        // No throw is the assertion: the retargeted encoder writes the .avi name.
        Validation.ValidateOutputExtension(new FileInfo(avi), moved);
        await Assert.That(moved.FormatName).IsEqualTo("avi");

        // ... and the impossible combination is still rejected the same way.
        await Assert.That(() => Validation.ValidateOutputExtension(
            new FileInfo(Path.Combine(Path.GetTempPath(), "clip.mkv")),
            Encoder("mpeg1video", "mpg", "mpeg"))).Throws<ArgumentException>();
    }
}
