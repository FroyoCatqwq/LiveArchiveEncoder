using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.FFmpeg;
using LiveArchiveEncoder.PostProcess;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.5 #1: the second half of "declared capability is not real capability".
///
/// v7.9.4 fixed the worst case (mpeg1video declares 5/10/12/15 fps and refuses
/// every one of them). Sweeping the whole legacy set with
/// <c>tools/capability_sweep.py</c> turned up two more classes:
///
///   * encoders whose container stores the frame rate as an <b>integer</b> --
///     rv10/rv20 silently retime 23.976 to 23.000, 29.97 to 29.000, 59.94 to
///     59.000, and roqvideo refuses all three outright. Neither declares a rate
///     list, so nothing snapped them before;
///   * containers that accept a stream but do not keep it -- AVI stores an RV20
///     stream and ffprobe reads it back as "rawvideo", and Matroska/ASF drop the
///     palette of a pal8 codec (msrle: YAVG 123 in AVI, 16 in both).
///
/// These tests pin the conclusions, not the sweep.
/// </summary>
public sealed class EncoderRateAndContainerTests
{
    private static EncoderCaps Caps(string name, params double[] rates) => new()
    {
        Name = name,
        SupportedRates = [.. rates],
    };

    private static EncoderInfo Encoder(string name, string ext, string format) => new()
    {
        Name = name,
        Extension = ext,
        FormatName = format,
    };

    // ---- whole-number frame rates -----------------------------------------

    [Test]
    public async Task RealMediaAndRoqOnlyTakeWholeFrameRates()
    {
        await Assert.That(EncoderMath.UsesWholeFps("rv10")).IsTrue();
        await Assert.That(EncoderMath.UsesWholeFps("rv20")).IsTrue();
        await Assert.That(EncoderMath.UsesWholeFps("roqvideo")).IsTrue();
        // mpeg1video also has an integer table, but it is a *declared* one and
        // is handled by the eight-real-rate list instead.
        await Assert.That(EncoderMath.UsesWholeFps("mpeg1video")).IsFalse();
        await Assert.That(EncoderMath.UsesWholeFps("mpeg4")).IsFalse();
    }

    [Test]
    public async Task FractionalRequestsAreRoundedForThoseEncoders()
    {
        foreach (string name in (string[])["rv10", "rv20", "roqvideo"])
        {
            (double a, bool aExact) = EncoderMath.ChooseBaseFps(23.976, Caps(name));
            await Assert.That(a).IsEqualTo(24.0).Because(name);
            await Assert.That(aExact).IsFalse().Because(name);

            (double b, _) = EncoderMath.ChooseBaseFps(29.97, Caps(name));
            await Assert.That(b).IsEqualTo(30.0).Because(name);

            (double c, _) = EncoderMath.ChooseBaseFps(59.94, Caps(name));
            await Assert.That(c).IsEqualTo(60.0).Because(name);

            // A whole number is left alone, and an exact request is exact.
            (double d, bool dExact) = EncoderMath.ChooseBaseFps(25.0, Caps(name));
            await Assert.That(d).IsEqualTo(25.0).Because(name);
            await Assert.That(dExact).IsTrue().Because(name);
        }
    }

    [Test]
    public async Task ADeclaredFractionalRateIsDroppedFromTheUsableList()
    {
        // Should a build ever declare 23.976 for rv20, it is a rate the file
        // cannot express, so the usable list keeps only the whole numbers.
        List<double> rates = EncoderMath.UsableRates(Caps("rv20", 23.976, 24.0, 30.0));

        await Assert.That(rates).IsEquivalentTo(new List<double> { 24.0, 30.0 });
    }

    [Test]
    public async Task TheRateRulesOfTheOtherEncodersAreUnchanged()
    {
        (double mpeg1, bool mpeg1Exact) = EncoderMath.ChooseBaseFps(15.0, Caps("mpeg1video", 5, 10, 12, 15));
        await Assert.That(mpeg1).IsEqualTo(24000.0 / 1001.0);
        await Assert.That(mpeg1Exact).IsFalse();

        (double mpeg2, bool mpeg2Exact) = EncoderMath.ChooseBaseFps(15.0, Caps("mpeg2video", 5, 10, 12, 15));
        await Assert.That(mpeg2).IsEqualTo(15.0);
        await Assert.That(mpeg2Exact).IsTrue();
    }

    // ---- the container table ----------------------------------------------

    [Test]
    public async Task EveryOfferedContainerIsOneTheProjectCanName()
    {
        foreach ((string encoder, (string Ext, string Format)[] choices) in
                 Constants.ContainerChoicesByEncoder)
        {
            await Assert.That(choices.Length).IsGreaterThan(0).Because(encoder);
            foreach ((string ext, string format) in choices)
            {
                await Assert.That(Constants.FormatExtensions.ContainsKey(format)).IsTrue()
                    .Because($"{encoder} -> {format} has no extension table");
                await Assert.That(Constants.FormatExtensions[format].Contains("." + ext)).IsTrue()
                    .Because($"{encoder} -> {format} is offered as .{ext}, "
                        + $"but {format} maps to {string.Join("/", Constants.FormatExtensions[format])}");
            }
        }
    }

    [Test]
    public async Task NoPal8EncoderIsOfferedAPaletteLossyContainer()
    {
        // msrle's only pixel format is pal8. Measured (2026-10-23): the same clip
        // is YAVG 123 in AVI/MOV and YAVG 16 -- pure black -- in Matroska and
        // ASF, because those store a VfW codec without a palette.
        foreach ((string encoder, (string Ext, string Format)[] choices) in
                 Constants.ContainerChoicesByEncoder)
        {
            if (encoder != "msrle") continue;
            foreach ((_, string format) in choices)
            {
                await Assert.That(Constants.PaletteLossyFormats.Contains(format)).IsFalse()
                    .Because($"msrle in {format} decodes to a black picture");
            }
        }

        // ... and the two that do carry a palette are the ones on offer.
        EncoderInfo msrle = Encoder("msrle", "avi", "avi");
        await Assert.That(EncoderMath.AllowedContainers(msrle).Select(c => c.Format))
            .IsEquivalentTo(new[] { "avi", "mov" });
    }

    [Test]
    public async Task TheVerifiedContainersAreReachableByRequest()
    {
        // Each pair here was measured: the muxer takes it, ffprobe reads the
        // codec back under its own name, and a decoded frame still has its
        // colour. Before v7.9.5 all of them were refused.
        (string Encoder, string Ext, string Format, string Want)[] cases =
        [
            ("mpeg4", "avi", "avi", "mkv"),
            ("mpeg1video", "mpg", "mpeg", "mp4"),
            ("mpeg2video", "mpg", "mpeg", "mov"),
            ("libx264", "mp4", "mp4", "flv"),
            ("libtheora", "ogv", "ogg", "mkv"),
            ("h261", "h261", "h261", "avi"),
        ];

        foreach ((string name, string ext, string format, string want) in cases)
        {
            EncoderInfo? moved = EncoderMath.ResolveContainer(Encoder(name, ext, format), want);
            await Assert.That(moved).IsNotNull().Because($"{name} should take {want}");
            await Assert.That(moved!.FormatName).IsEqualTo(want == "mkv" ? "matroska" : want);
        }
    }

    [Test]
    public async Task PairsThatOnlyMuxAreStillRefused()
    {
        // AVI happily stores an RV20 stream and ffprobe then reads "rawvideo";
        // the rm muxer writes the RealVideo tag over whatever it was given.
        // Both produce files no player decodes, so they stay refused.
        await Assert.That(EncoderMath.ResolveContainer(Encoder("rv20", "rm", "rm"), "avi")).IsNull();
        await Assert.That(EncoderMath.ResolveContainer(Encoder("rv20", "rm", "rm"), "mkv")).IsNull();
        await Assert.That(EncoderMath.ResolveContainer(Encoder("mpeg4", "avi", "avi"), "rm")).IsNull();
        await Assert.That(EncoderMath.ResolveContainer(Encoder("qtrle", "mov", "mov"), "avi")).IsNull();
    }

    [Test]
    public async Task TheTypedExtensionPicksTheNewContainersToo()
    {
        // --encoder mpeg4 out.mkv, with no --container: the name is the request.
        EncoderInfo mpeg4 = Encoder("mpeg4", "avi", "avi");
        await Assert.That(EncoderMath.ResolveByExtension(mpeg4, "clip.mkv").FormatName)
            .IsEqualTo("matroska");
        await Assert.That(EncoderMath.ResolveByExtension(mpeg4, "clip.mov").FormatName)
            .IsEqualTo("mov");

        // An extension this encoder genuinely cannot write is left alone, so the
        // caller still reports the mismatch instead of guessing.
        await Assert.That(EncoderMath.ResolveByExtension(mpeg4, "clip.rm").FormatName)
            .IsEqualTo("avi");
    }

    [Test]
    public async Task RateLessFormatsAreTheRawBitstreams()
    {
        await Assert.That(Constants.RateLessFormats.Contains("h261")).IsTrue();
        await Assert.That(Constants.RateLessFormats.Contains("h263")).IsTrue();
        await Assert.That(Constants.RateLessFormats.Contains("a64")).IsTrue();
        // AVI is the answer the note recommends, so it must not be in the set.
        await Assert.That(Constants.RateLessFormats.Contains("avi")).IsFalse();
    }
}

/// <summary>
/// v7.9.5 #2: the site presets. 2005-2009 China was not one transcode ladder but
/// six, and a site's preset is a bitrate profile -- it must not claim a live
/// encoder, because "what Youku did to my upload" says nothing about how the
/// upload was captured.
/// </summary>
public sealed class SitePresetTests
{
    [Test]
    public async Task EverySitePresetHasOnlyARecompressLayer()
    {
        foreach (EraPreset site in EraPresets.Sites)
        {
            await Assert.That(site.Kind).IsEqualTo(EraKind.Site).Because(site.Id);
            await Assert.That(site.HasLive).IsFalse().Because(site.Id);
            await Assert.That(site.Encoder).IsNull().Because(site.Id);
            await Assert.That(site.LimitKbps).IsNull().Because(site.Id);
        }

        await Assert.That(EraPresets.Sites.Select(s => s.Id))
            .IsEquivalentTo(new[] { "sina06", "sohu06", "qq06", "tudou06", "youku07", "youtube07" });
    }

    [Test]
    public async Task ASitePresetLeavesTheLiveConfigurationAlone()
    {
        var config = new Config
        {
            Encoder = new EncoderInfo { Name = "mpeg4", Extension = "avi", FormatName = "avi" },
            Width = 640,
            Height = 480,
            Fps = 25,
            Limit = 900,
        };

        bool applied = EraPresets.Find("youku07")!.ApplyLive(config);

        await Assert.That(applied).IsFalse();
        await Assert.That(config.Width).IsEqualTo(640);
        await Assert.That(config.Height).IsEqualTo(480);
        await Assert.That(config.Fps).IsEqualTo(25.0);
        await Assert.That(config.Limit).IsEqualTo(900.0);
        await Assert.That(config.Encoder!.Name).IsEqualTo("mpeg4");
    }

    [Test]
    public async Task EveryPresetIdIsUniqueAndHasADescription()
    {
        var ids = new List<string>();
        foreach (EraPreset preset in EraPresets.All)
        {
            await Assert.That(ids.Contains(preset.Id)).IsFalse().Because($"{preset.Id} is listed twice");
            ids.Add(preset.Id);
            await Assert.That(preset.NoteKey).StartsWith("era.").Because(preset.Id);
        }
    }

    [Test]
    public async Task TheSiteFragmentsAreValidAndCarryTheirBitrate()
    {
        foreach (EraPreset site in EraPresets.Sites)
        {
            (List<string> tokens, string? error) = PostArgs.Parse(site.RecompressArgs);
            await Assert.That(error).IsNull().Because($"{site.Id}: {error}");
            await Assert.That(tokens).Contains("-b:v").Because(site.Id);
            // A site targeted a bitrate, not a quality level: -crf would make the
            // number in the preset a suggestion instead of the ladder it models.
            await Assert.That(tokens.Contains("-crf")).IsFalse().Because(site.Id);
        }
    }

    [Test]
    public async Task TheEraPresetsStillHaveTheirLiveLayer()
    {
        foreach (EraPreset era in EraPresets.WithLiveLayer)
        {
            await Assert.That(era.Kind).IsEqualTo(EraKind.Era).Because(era.Id);
            await Assert.That(era.Encoder).IsNotNull().Because(era.Id);
            await Assert.That(era.Width).IsNotNull().Because(era.Id);
            await Assert.That(era.Fps).IsNotNull().Because(era.Id);
            // Applied to a fresh config, a real era still writes the live layer.
            await Assert.That(era.ApplyLive(new Config())).IsTrue().Because(era.Id);
        }
    }

    [Test]
    public async Task AnUnknownIdFindsNothing()
    {
        await Assert.That(EraPresets.Find(null)).IsNull();
        await Assert.That(EraPresets.Find("")).IsNull();
        await Assert.That(EraPresets.Find("youku99")).IsNull();
        await Assert.That(EraPresets.Find("YOUKU07")).IsNotNull();
    }
}
