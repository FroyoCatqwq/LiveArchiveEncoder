using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.Encoding;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.6 #1: the tool can now write pictures in a chosen pixel format.
///
/// <para>Everything downstream of that choice depends on one number: how many
/// bytes one frame occupies on the rawvideo pipe. A single byte of error shifts
/// every following frame and turns the picture into noise -- which is exactly
/// what happened to msrle in v7.9.4. So the table is pinned here against the
/// numbers <c>tools/pixfmt_sweep.py</c> measured on this build (2026-10-24,
/// 64x48, 3 frames), format by format.</para>
/// </summary>
public sealed class PixelFormatCatalogTests
{
    /// <summary>
    /// Bytes per pixel as measured. Kept as a literal table on purpose: if
    /// someone edits the catalog, this test is the thing that has to be edited
    /// too, deliberately, after re-running the sweep.
    /// </summary>
    private static readonly (string Format, double Bpp)[] Measured =
    [
        ("yuv410p", 1.125), ("yuv411p", 1.5), ("yuv420p", 1.5), ("yuv422p", 2.0),
        ("yuv440p", 2.0), ("yuv444p", 3.0), ("yuvj411p", 1.5), ("yuvj420p", 1.5),
        ("yuvj422p", 2.0), ("yuvj444p", 3.0),
        ("yuv420p9le", 3.0), ("yuv420p10le", 3.0), ("yuv420p12le", 3.0),
        ("yuv420p14le", 3.0), ("yuv420p16le", 3.0),
        ("yuv422p9le", 4.0), ("yuv422p10le", 4.0), ("yuv422p12le", 4.0),
        ("yuv422p14le", 4.0), ("yuv422p16le", 4.0),
        ("yuv444p9le", 6.0), ("yuv444p10le", 6.0), ("yuv444p12le", 6.0),
        ("yuv444p14le", 6.0), ("yuv444p16le", 6.0),
        ("nv12", 1.5), ("nv21", 1.5), ("nv16", 2.0), ("nv20le", 4.0),
        ("nv24", 3.0), ("nv42", 3.0),
        ("p010le", 3.0), ("p012le", 3.0), ("p016le", 3.0), ("p210le", 4.0), ("p410le", 6.0),
        ("uyvy422", 2.0), ("yuyv422", 2.0), ("yvyu422", 2.0),
        ("yuva420p", 2.5), ("yuva422p", 3.0), ("yuva444p", 4.0), ("yuva420p10le", 5.0),
        ("gray", 1.0), ("gray9le", 2.0), ("gray10le", 2.0), ("gray12le", 2.0),
        ("gray14le", 2.0), ("gray16le", 2.0), ("grayf32le", 4.0),
        ("ya8", 2.0), ("ya16le", 4.0), ("monob", 0.125), ("monow", 0.125),
        ("rgb24", 3.0), ("bgr24", 3.0), ("rgb8", 1.0), ("bgr8", 1.0),
        ("rgb4_byte", 1.0), ("bgr4_byte", 1.0),
        ("rgb555le", 2.0), ("rgb555be", 2.0), ("bgr555le", 2.0),
        ("rgb565le", 2.0), ("rgb565be", 2.0), ("bgr565le", 2.0),
        ("x2rgb10le", 4.0), ("x2bgr10le", 4.0), ("rgb48le", 6.0), ("rgb48be", 6.0),
        ("rgba", 4.0), ("bgra", 4.0), ("argb", 4.0), ("abgr", 4.0),
        ("rgb0", 4.0), ("bgr0", 4.0), ("0rgb", 4.0), ("0bgr", 4.0),
        ("gbrp", 3.0), ("gbrp9le", 6.0), ("gbrp10le", 6.0), ("gbrp12le", 6.0),
        ("gbrp14le", 6.0), ("gbrp16le", 6.0), ("gbrpf32le", 12.0),
        ("pal8", 1.0),
    ];

    [Test]
    public async Task EveryRowMatchesTheMeasuredFrameSize()
    {
        foreach ((string format, double bpp) in Measured)
        {
            await Assert.That(PixelFormatCatalog.TryGet(format, out PixFmtInfo info)).IsTrue()
                .Because($"{format} must be in the transport table");
            await Assert.That(info.BytesPerPixel).IsEqualTo(bpp)
                .Because($"{format} measured {bpp} bytes per pixel on this build");
        }
    }

    /// <summary>
    /// v7.9.6, found by the sweep: the old table said 1.5, which would have cut
    /// every 4:4:0 frame in half. Nothing in the legacy set produces 4:4:0,
    /// which is why it was never noticed.
    /// </summary>
    [Test]
    public async Task FourFourZeroIsTwoBytesPerPixelNotOneAndAHalf()
    {
        await Assert.That(PixelFormatCatalog.TryGet("yuv440p", out PixFmtInfo info)).IsTrue();
        await Assert.That(info.BytesPerPixel).IsEqualTo(2.0);
        await Assert.That(info.Family).IsEqualTo("yuv440");
        await Assert.That(PixelFormatCatalog.TryGet("yuv440p", out PixFmtInfo again) ? again.BitDepth : 0)
            .IsEqualTo(8);
        await Assert.That(PixelFormatUtil.RawFrameSize(64, 48, "yuv440p")).IsEqualTo(64 * 48 * 2);
    }

    [Test]
    public async Task FourFourZeroOnlyNeedsAnEvenHeight()
    {
        // 4:4:0 keeps chroma at full width -- the old name heuristic read the
        // "440" as a 4:2:0 family and demanded an even width too.
        (int w, int h) = PixelFormatUtil.RequiredDivisibility("yuv440p");
        await Assert.That(w).IsEqualTo(1);
        await Assert.That(h).IsEqualTo(2);

        (int nw, int nh, bool exact) = PixelFormatUtil.NormalizeTransportDimensions(65, 49, "yuv440p");
        await Assert.That(exact).IsFalse();
        await Assert.That(nw).IsEqualTo(65);
        await Assert.That(nh).IsEqualTo(48);
    }

    [Test]
    public async Task ChromaGridsComeFromTheTable()
    {
        await Assert.That(PixelFormatUtil.RequiredDivisibility("yuv420p")).IsEqualTo((2, 2));
        await Assert.That(PixelFormatUtil.RequiredDivisibility("yuv422p")).IsEqualTo((2, 1));
        await Assert.That(PixelFormatUtil.RequiredDivisibility("yuv411p")).IsEqualTo((4, 1));
        await Assert.That(PixelFormatUtil.RequiredDivisibility("yuv410p")).IsEqualTo((4, 4));
        await Assert.That(PixelFormatUtil.RequiredDivisibility("nv12")).IsEqualTo((2, 2));
        await Assert.That(PixelFormatUtil.RequiredDivisibility("gbrp")).IsEqualTo((1, 1));
    }

    /// <summary>
    /// Bit-packed monochrome pads every row to a whole byte, so a 65-pixel-wide
    /// frame is 9 bytes per row and the width has to reach a whole byte too.
    /// </summary>
    [Test]
    public async Task BitPackedMonochromeUsesByteAlignedRows()
    {
        await Assert.That(PixelFormatUtil.RawFrameSize(64, 48, "monob")).IsEqualTo(384);
        await Assert.That(PixelFormatUtil.RawFrameSize(65, 48, "monob")).IsEqualTo(9 * 48);
        await Assert.That(PixelFormatUtil.RequiredDivisibility("monob")).IsEqualTo((8, 1));
    }

    [Test]
    public async Task PaletteOnlyAppliesToPal8()
    {
        await Assert.That(PixelFormatCatalog.TryGet("pal8", out PixFmtInfo pal)).IsTrue();
        await Assert.That(pal.HasPalette).IsTrue();
        await Assert.That(pal.FrameBytes(64, 48)).IsEqualTo(64 * 48 + PixelFormatCatalog.PaletteBytes);

        // rgb8/bgr8 are packed 3:3:2 colour, not indexed -- no palette.
        await Assert.That(PixelFormatCatalog.TryGet("rgb8", out PixFmtInfo rgb)).IsTrue();
        await Assert.That(rgb.HasPalette).IsFalse();
        await Assert.That(rgb.FrameBytes(64, 48)).IsEqualTo(64 * 48);
    }

    [Test]
    public async Task NamesAreCaseInsensitiveAndAutoMeansNoRequest()
    {
        await Assert.That(PixelFormatCatalog.TryGet("PAL8", out _)).IsTrue();
        await Assert.That(PixelFormatCatalog.TryGet("  Yuv420P10LE ", out PixFmtInfo info)).IsTrue();
        await Assert.That(info.Name).IsEqualTo("yuv420p10le");
        await Assert.That(PixelFormatCatalog.Normalise("auto")).IsEqualTo("");
        await Assert.That(PixelFormatCatalog.Normalise(null)).IsEqualTo("");
        await Assert.That(PixelFormatCatalog.Normalise(" PAL8 ")).IsEqualTo("pal8");
    }

    [Test]
    public async Task AnUnknownFormatIsRefusedWithAUsefulMessage()
    {
        EncoderCaps caps = Caps("mpeg4", "yuv420p");
        string? problem = PixelFormatCatalog.Validate("yuv420p10be", caps);

        await Assert.That(problem).IsNotNull();
        await Assert.That(problem!).Contains("yuv420p10be");
        await Assert.That(problem!).Contains("yuv420p");
    }

    [Test]
    public async Task TheEncoderMustDeclareTheFormat()
    {
        EncoderCaps caps = Caps("mpeg4", "yuv420p");

        await Assert.That(PixelFormatCatalog.Validate("yuv420p", caps)).IsNull();
        string? problem = PixelFormatCatalog.Validate("yuv420p10le", caps);
        await Assert.That(problem).IsNotNull();
        await Assert.That(problem!).Contains("cannot take yuv420p10le");
    }

    [Test]
    public async Task AnEncoderThatDeclaresNothingIsTrusted()
    {
        // No declared list means the build cannot say no; the preflight and the
        // post-run probe are what decide, so validation must not block it.
        await Assert.That(PixelFormatCatalog.Validate("pal8", Caps("msrle"))).IsNull();
    }

    [Test]
    public async Task PaletteFormatsAreRefusedByPaletteLossyContainers()
    {
        // Measured 2026-10-23: the same msrle clip is YAVG 123 in AVI and
        // YAVG 16 (black) after one hop through Matroska.
        await Assert.That(PixelFormatUtil.PaletteContainerConflict("pal8", "matroska")).IsNotNull();
        await Assert.That(PixelFormatUtil.PaletteContainerConflict("pal8", "asf")).IsNotNull();
        await Assert.That(PixelFormatUtil.PaletteContainerConflict("pal8", "avi")).IsNull();
        await Assert.That(PixelFormatUtil.PaletteContainerConflict("pal8", "mov")).IsNull();
        // Only palette formats are affected.
        await Assert.That(PixelFormatUtil.PaletteContainerConflict("yuv420p", "matroska")).IsNull();
    }

    /// <summary>
    /// v7.9.6 #1: an explicit request is honoured exactly, never "improved" into
    /// something else, and the automatic path is unchanged from v7.9.5.
    /// </summary>
    [Test]
    public async Task AnExplicitRequestWinsOverTheSourceFormat()
    {
        EncoderCaps caps = Caps("cinepak", "rgb24", "gray");

        await Assert.That(PixelFormatUtil.ChooseTransportPixFmt("yuv420p", caps, "gray")).IsEqualTo("gray");
        await Assert.That(PixelFormatUtil.ChooseTransportPixFmt("yuv420p", caps, "RGB24")).IsEqualTo("rgb24");
        // Null and "auto" mean "no request": the source format is kept when the
        // encoder takes it (v7.9.5 behaviour).
        await Assert.That(PixelFormatUtil.ChooseTransportPixFmt("rgb24", caps, null)).IsEqualTo("rgb24");
        await Assert.That(PixelFormatUtil.ChooseTransportPixFmt("rgb24", caps, "auto")).IsEqualTo("rgb24");
    }

    [Test]
    public async Task AnImpossibleRequestIsAnArgumentError()
    {
        EncoderCaps caps = Caps("mpeg4", "yuv420p");

        await Assert.That(() => PixelFormatUtil.ChooseTransportPixFmt("yuv420p", caps, "yuv444p"))
            .Throws<ArgumentException>();
        await Assert.That(() => PixelFormatUtil.ChooseTransportPixFmt("yuv420p", caps, "nonsense"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task TheAutomaticPathStillFallsBackWithinTheFamily()
    {
        // A 10-bit source into an 8-bit-only encoder keeps the chroma family and
        // drops the depth -- unchanged since v7.8.
        EncoderCaps caps = Caps("mpeg4", "yuv420p");
        await Assert.That(PixelFormatUtil.ChooseTransportPixFmt("yuv420p10le", caps)).IsEqualTo("yuv420p");
        // Case-insensitive on the declared side too: a profile written by hand
        // may spell the format differently, and a mismatch would silently
        // downgrade a 10-bit source.
        EncoderCaps upper = Caps("libx264", "YUV420P10LE", "yuv420p");
        await Assert.That(PixelFormatUtil.ChooseTransportPixFmt("yuv420p10le", upper)).IsEqualTo("yuv420p10le");
    }

    [Test]
    public async Task DitherIsOnlyMeaningfulForLowColourTargets()
    {
        await Assert.That(PixelFormatUtil.WantsDither("pal8")).IsTrue();
        await Assert.That(PixelFormatUtil.WantsDither("rgb555le")).IsTrue();
        await Assert.That(PixelFormatUtil.WantsDither("rgb8")).IsTrue();
        await Assert.That(PixelFormatUtil.WantsDither("monob")).IsTrue();
        await Assert.That(PixelFormatUtil.WantsDither("gray")).IsTrue();
        // Chroma subsampling is not a bit-depth reduction: swscale's dither
        // would do nothing there, so the flag must not be emitted.
        await Assert.That(PixelFormatUtil.WantsDither("yuv420p")).IsFalse();
        await Assert.That(PixelFormatUtil.WantsDither("yuv420p10le")).IsFalse();
        await Assert.That(PixelFormatUtil.WantsDither("rgb24")).IsFalse();
    }

    [Test]
    public async Task TheDitherFlagIsOnlyEmittedWhenItCanDoSomething()
    {
        await Assert.That(ArgsBuilder.DitherArgs("bayer", "pal8")).IsEquivalentTo(new[] { "-sws_dither", "bayer" });
        await Assert.That(ArgsBuilder.DitherArgs("auto", "pal8")).IsEmpty();
        await Assert.That(ArgsBuilder.DitherArgs("", "pal8")).IsEmpty();
        // A mode the tool does not offer is dropped rather than passed through.
        await Assert.That(ArgsBuilder.DitherArgs("bogus", "pal8")).IsEmpty();
        // Right mode, wrong target: no flag, because nothing is being reduced.
        await Assert.That(ArgsBuilder.DitherArgs("bayer", "yuv420p")).IsEmpty();
    }

    [Test]
    public async Task ColourTagsFallBackToTheSourceOneFieldAtATime()
    {
        var source = new StreamInfo
        {
            ColorRange = "tv",
            ColorSpace = "bt470bg",
            ColorPrimaries = "bt470bg",
            ColorTransfer = "bt709",
        };

        // Nothing pinned: the source's four tags are reproduced.
        await Assert.That(ArgsBuilder.ColorArgs(source)).IsEquivalentTo(new[]
        {
            "-color_range", "tv", "-colorspace", "bt470bg",
            "-color_primaries", "bt470bg", "-color_trc", "bt709",
        });

        // One pinned field only replaces that field.
        List<string> args = ArgsBuilder.ColorArgs(source, new ColorOverride(null, null, "bt2020", null));
        await Assert.That(args).Contains("bt2020");
        await Assert.That(args).Contains("bt470bg");
        await Assert.That(args.Count(x => x == "bt470bg")).IsEqualTo(1);
    }

    /// <summary>
    /// v7.9.6: libx264 ignores <c>-color_primaries</c>/<c>-color_trc</c> in this
    /// build (measured 2026-10-24: the output reports "unknown"), so the tags
    /// are also expressed as x264 parameters -- and the four names FFmpeg and
    /// x264 spell differently are translated.
    /// </summary>
    [Test]
    public async Task X264GetsTheColourTagsItActuallyWrites()
    {
        var pinned = new ColorOverride(null, "bt2020nc", "bt2020", "smpte2084");
        List<string> args = ArgsBuilder.X264ColorArgs(pinned);

        await Assert.That(args.Count).IsEqualTo(2);
        await Assert.That(args[0]).IsEqualTo("-x264-params");
        await Assert.That(args[1]).IsEqualTo("colorprim=bt2020:transfer=smpte2084:colmatrix=bt2020nc");

        // FFmpeg name -> x264 name for the four that differ.
        List<string> gamma = ArgsBuilder.X264ColorArgs(new ColorOverride(null, null, null, "gamma22"));
        await Assert.That(gamma[1]).IsEqualTo("transfer=bt470m");

        // Nothing pinned, nothing emitted.
        await Assert.That(ArgsBuilder.X264ColorArgs(ColorOverride.None)).IsEmpty();
        await Assert.That(ArgsBuilder.X264ColorArgs(null)).IsEmpty();
    }

    private static EncoderCaps Caps(string name, params string[] pixFmts) => new()
    {
        Name = name,
        SupportedPixFmts = [.. pixFmts],
    };
}

/// <summary>
/// v7.9.6 #2: the picture-format settings have to survive the settings file, the
/// command line and the menu, and a bad value has to be refused where it is
/// typed rather than halfway through an encode.
/// </summary>
public sealed class PictureFormatConfigTests
{
    [Test]
    public async Task ValidationNormalisesWhatItAccepts()
    {
        var config = new Config { PixFmt = "  RGB555LE ", Dither = "", ColorRange = "AUTO" };

        Validation.ValidateCommonConfig(config);

        await Assert.That(config.PixFmt).IsEqualTo("rgb555le");
        await Assert.That(config.Dither).IsEqualTo("auto");
        await Assert.That(config.ColorRange).IsNull();
    }

    [Test]
    public async Task AnUnknownFormatIsRejectedByValidation()
    {
        var config = new Config { PixFmt = "yuv420p10be" };

        await Assert.That(() => Validation.ValidateCommonConfig(config))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task ADitherModeOutsideTheListIsRejected()
    {
        var config = new Config { Dither = "floyd" };

        await Assert.That(() => Validation.ValidateCommonConfig(config))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task ABadColourTagIsRejectedByValidation()
    {
        var config = new Config { ColorSpace = "bt601" };

        await Assert.That(() => Validation.ValidateCommonConfig(config))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task ThePersistedShapeCarriesEveryPictureSetting()
    {
        var config = new Config
        {
            PixFmt = "pal8",
            ColorRange = "pc",
            ColorSpace = "smpte170m",
            ColorPrimaries = "smpte170m",
            ColorTransfer = "smpte170m",
            Dither = "error_diffusion",
        };

        UserConfig stored = ConfigStore.Capture(config);
        await Assert.That(stored.PixFmt).IsEqualTo("pal8");
        await Assert.That(stored.Dither).IsEqualTo("error_diffusion");

        var restored = new Config();
        ConfigStore.Apply(stored, restored, []);

        await Assert.That(restored.PixFmt).IsEqualTo("pal8");
        await Assert.That(restored.ColorRange).IsEqualTo("pc");
        await Assert.That(restored.ColorSpace).IsEqualTo("smpte170m");
        await Assert.That(restored.ColorPrimaries).IsEqualTo("smpte170m");
        await Assert.That(restored.ColorTransfer).IsEqualTo("smpte170m");
        await Assert.That(restored.Dither).IsEqualTo("error_diffusion");
    }

    [Test]
    public async Task AFormatThisBuildDoesNotKnowIsDroppedOnLoad()
    {
        // A settings file written by a newer version must not hand the encoder a
        // format it cannot size a frame for.
        var stored = new UserConfig { PixFmt = "yuv420p10be", Dither = "floyd", ColorSpace = "bt601" };

        var config = new Config();
        ConfigStore.Apply(stored, config, []);

        await Assert.That(config.PixFmt).IsNull();
        await Assert.That(config.Dither).IsEqualTo("auto");
        await Assert.That(config.ColorSpace).IsNull();
    }
}
