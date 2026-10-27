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
/// a dozen, and a site's preset is a bitrate profile plus the codec that site
/// served (v7.9.5.1).
///
/// v7.9.6.2: a site preset now owns the live layer too. Choosing
/// <c>--era youku07</c> used to leave the capture side (frame size, rate,
/// ceiling, ladder, audio) exactly as the user had it, so the live product and
/// the second pass described two different machines -- and the menu's parameter
/// page showed neither. The capture side of a 2007 portal is as
/// period-specific as its delivery side, so both come from the same row now.
/// </summary>
public sealed class SitePresetTests
{
    [Test]
    public async Task EverySitePresetCarriesBothLayers()
    {
        foreach (EraPreset site in EraPresets.Sites)
        {
            await Assert.That(site.Kind).IsEqualTo(EraKind.Site).Because(site.Id);
            // v7.9.6.2: the live layer is part of the site's period.
            await Assert.That(site.HasLive).IsTrue().Because(site.Id);
            await Assert.That(site.Width).IsEqualTo(site.RcWidth).Because(site.Id);
            await Assert.That(site.Height).IsEqualTo(site.RcHeight).Because(site.Id);
            await Assert.That(site.Fps).IsEqualTo(site.RcFps).Because(site.Id);
            await Assert.That(site.LimitKbps).IsEqualTo(site.RcVideoKbps).Because(site.Id);
            await Assert.That(site.FpsLevels).IsNotNull().Because(site.Id);
            await Assert.That(site.FpsLevels![0]).IsEqualTo(site.Fps!.Value).Because(site.Id);
            // v7.9.5.1: the preset names the site's codec (that is the encoder
            // the live pass adopts when nothing else was asked for).
            await Assert.That(site.Encoder).IsNotNull().Because(site.Id);
            await Assert.That(site.VideoCodec).IsNotEmpty().Because(site.Id);
            await Assert.That(site.RecompressContainer).IsNotEmpty().Because(site.Id);
        }

        await Assert.That(EraPresets.Sites.Select(s => s.Id))
            .IsEquivalentTo(new[]
            {
                // v7.9.7: site -> year. Every row is one site in one year, and a
                // site that changed shape has one row per shape.
                "cctv/2003", "cctv/2006", "cctv/2011",
                "sina/2003", "sina/2006", "sina/2010",
                "sohu/2003", "sohu/2007", "sohu/2012",
                "netease/2003", "netease/2008",
                "qq/2006", "qq/2010",
                "tudou/2006", "tudou/2009",
                "youku/2007", "youku/2011",
                "ku6/2007", "56/2007", "acfun/2009",
                "bilibili/2010", "bilibili/2012", "bilibili/2014",
                "iqiyi/2011",
                "youtube/2007", "youtube/2011", "youtube/2015",
            });
    }

    [Test]
    public async Task TheWindowsMediaYearsIngestAndServeWmv()
    {
        // v7.9.6.3: a 2003 portal did not take an H.264 upload. The live layer,
        // the delivery codec, the container and the audio codec all have to be
        // the period's own -- otherwise "--era cctv03" claims to be 2003 while
        // capturing something 2003 never saw.
        foreach (string id in new[] { "cctv03", "sina03", "sohu03", "netease03" })
        {
            EraPreset site = EraPresets.Find(id)!;
            await Assert.That(site.Encoder).IsEqualTo("wmv2").Because(id);
            await Assert.That(site.VideoCodec).IsEqualTo("wmv2").Because(id);
            await Assert.That(site.AudioCodec).IsEqualTo("wmav2").Because(id);
            await Assert.That(site.AudioEncoder).IsEqualTo("wmav2").Because(id);
            await Assert.That(site.RecompressContainer).IsEqualTo("asf").Because(id);
            await Assert.That(site.RcFps).IsLessThanOrEqualTo(15.0).Because(id);
            // 4:3 or QCIF, and never wider than a 2003 portal window.
            await Assert.That(site.RcWidth).IsLessThanOrEqualTo(320).Because(id);
        }
    }

    [Test]
    public async Task TheFlvEraUsesTheCodecThatEraActuallyServed()
    {
        // A site's ladder is not just numbers: 320 kbps of H.264 does not look
        // like 320 kbps of Sorenson Spark, so the codec is part of the preset.
        foreach (string id in new[] { "sina06", "tudou06", "youku07", "bilibili09", "cctv06" })
        {
            EraPreset site = EraPresets.Find(id)!;
            await Assert.That(site.VideoCodec).IsEqualTo("flv").Because(id);
            await Assert.That(site.Encoder).IsEqualTo("flv").Because(id);
            await Assert.That(site.RecompressContainer).IsEqualTo("flv").Because(id);
            await Assert.That(site.AudioCodec).IsEqualTo("libmp3lame").Because(id);
        }

        // 2010s sites served H.264 in MP4; YouTube's 2014-2016 window was VP9.
        await Assert.That(EraPresets.Find("bilibili14")!.VideoCodec).IsEqualTo("libx264");
        await Assert.That(EraPresets.Find("youtube15")!.VideoCodec).IsEqualTo("libvpx-vp9");
        await Assert.That(EraPresets.Find("youtube15")!.RecompressContainer).IsEqualTo("webm");
    }

    [Test]
    public async Task TencentKeptTheHigherFrameRate()
    {
        // The one portal that pushed 25 fps among the 320x240 FLV sites; the
        // rest stayed at 12-15. (CCTV is 25 as well, but for a different reason:
        // it was PAL broadcast material, not a portal ladder.)
        EraPreset qq = EraPresets.Find("qq06")!;
        await Assert.That(qq.RcFps).IsEqualTo(25.0);
        foreach (string id in new[] { "sina06", "tudou06", "youku07", "ku6_07", "56_07" })
            await Assert.That(EraPresets.Find(id)!.RcFps).IsLessThan(25.0).Because(id);
    }

    [Test]
    public async Task NativeAndModernRenderingDifferOnlyInTheCodec()
    {
        EraPreset youku = EraPresets.Find("youku07")!;

        string native = youku.Fragment(native: true);
        string modern = youku.Fragment(native: false);

        await Assert.That(native).Contains("-c:v flv");
        await Assert.That(modern).Contains("-c:v libx264");
        // The same ladder: same scaler expression, same target bitrate.
        await Assert.That(native).Contains("scale=352:264");
        await Assert.That(modern).Contains("scale=352:264");
        await Assert.That(native).Contains("-b:v 320k");
        // ... and the containers each rendering has to land in.
        await Assert.That(youku.ContainerFor(native: true)).IsEqualTo("flv");
        await Assert.That(youku.ContainerFor(native: false)).IsEqualTo("mp4");
    }

    [Test]
    public async Task ASitePresetReshapesTheLiveConfiguration()
    {
        // v7.9.6.2: selecting a 2007 portal now rewrites the capture side too --
        // frame size, rate, ladder and ceiling. The encoder is left alone by
        // ApplyLive (ApplyEncoder owns that, and the CLI only takes it when the
        // user named none).
        var config = new Config
        {
            Encoder = new EncoderInfo { Name = "mpeg4", Extension = "avi", FormatName = "avi" },
            Width = 640,
            Height = 480,
            Fps = 25,
            Limit = 900,
        };

        bool applied = EraPresets.Find("youku07")!.ApplyLive(config);

        await Assert.That(applied).IsTrue();
        await Assert.That(config.Width).IsEqualTo(352);
        await Assert.That(config.Height).IsEqualTo(264);
        await Assert.That(config.Fps).IsEqualTo(15.0);
        await Assert.That(config.Limit).IsEqualTo(320.0);
        await Assert.That(config.FpsLevels).IsEquivalentTo(new[] { 15.0, 12.0, 10.0, 8.0, 5.0 });
        await Assert.That(config.AudioRate).IsEqualTo(22050);
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
        foreach (EraPreset era in EraPresets.All.Where(p => p.Kind == EraKind.Era))
        {
            await Assert.That(era.Encoder).IsNotNull().Because(era.Id);
            await Assert.That(era.Width).IsNotNull().Because(era.Id);
            await Assert.That(era.Fps).IsNotNull().Because(era.Id);
            // Applied to a fresh config, a real era still writes the live layer.
            await Assert.That(era.ApplyLive(new Config())).IsTrue().Because(era.Id);
        }

        // The same now holds for the site rows.
        foreach (EraPreset site in EraPresets.Sites)
            await Assert.That(site.ApplyLive(new Config())).IsTrue().Because(site.Id);
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
