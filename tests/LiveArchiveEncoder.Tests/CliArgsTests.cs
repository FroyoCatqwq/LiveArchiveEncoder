using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.UI;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.1: the settings-file to command-line conversion. The property that
/// matters is the round trip -- a settings snapshot emitted as argv must parse
/// back into the same options -- so most of these tests feed the emitted
/// tokens straight into <see cref="CliParser.Parse(string[])"/>.
/// </summary>
public sealed class CliArgsTests
{
    private static UserConfig Rich() => new()
    {
        InputPath = "in.rm",
        OutputPath = "out.avi",
        Encoder = "mpeg4",
        AudioEncoder = "mp2",
        Container = "avi",
        StartTimeSeconds = 2.5,
        EndTimeSeconds = 8,
        Fps = 25,
        FpsLevels = [25.0, 15.0, 10.0],
        FpsLevelKbps =
        [
            new FpsRungSetting { Fps = 25.0 },
            new FpsRungSetting { Fps = 15.0, DropAboveKbps = 120, RiseBelowKbps = 110 },
            new FpsRungSetting { Fps = 10.0 },
        ],
        Limit = 900,
        Recover = 500,
        Window = 3,
        Qv = 4,
        Threads = 4,
        BufferFrames = 3,
        AudioKbps = 64,
        AudioChannels = 2,
        AudioRate = 44100,
        VideoStream = 2,
        AudioStream = 3,
        Width = 320,
        Height = 240,
        FitMode = "fit",
        CropAnchor = "left",
        PadColor = "0x404040",
        ScaleAlgo = "bicubic",
        PixFmt = "yuv420p10le",
        ColorRange = "tv",
        ColorSpace = "bt709",
        ColorPrimaries = "bt709",
        ColorTransfer = "smpte2084",
        Dither = "bayer",
        Deinterlace = "yadif",
        DeintMode = "send_field",
        DeintParity = "tff",
        DeintMarkedOnly = true,
        Hwaccel = "auto",
        X264Preset = "fast",
        X264Tune = "film",
        X264Profile = "high",
        X264Level = "4.1",
        Parallel = 4,
        Language = "zh",
        FfmpegSource = "path",
        FfmpegProxy = "http://127.0.0.1:7890",
        PlayMode = "ffplay",
        PlayerPath = "mpc.exe",
        Recompress = new RecompressSettings
        {
            Enabled = true,
            Args = "-c:v libx264 -crf 28",
            Container = "mkv",
        },
        Subtitles = new SubtitleConfig
        {
            Mode = "copy",
            Files = ["clip.ass"],
            Codec = "ass",
        },
    };

    [Test]
    public async Task DefaultsEmitOnlyThePositionals()
    {
        var cfg = new UserConfig { InputPath = "in.rm", OutputPath = "out.avi" };

        List<string> argv = CliArgs.FromUserConfig(cfg);

        await Assert.That(argv).IsEquivalentTo(new[] { "in.rm", "out.avi" });
    }

    [Test]
    public async Task ARichSnapshotParsesBackIntoTheSameOptions()
    {
        List<string> argv = CliArgs.FromUserConfig(Rich());

        (CliOptions? opts, string? error) = CliParser.Parse([.. argv]);

        await Assert.That(error).IsNull();
        await Assert.That(opts).IsNotNull();
        await Assert.That(opts!.Input).IsEqualTo("in.rm");
        await Assert.That(opts.Output).IsEqualTo("out.avi");
        await Assert.That(opts.Encoder).IsEqualTo("mpeg4");
        await Assert.That(opts.EncoderExplicit).IsTrue();
        await Assert.That(opts.AudioEncoder).IsEqualTo("mp2");
        await Assert.That(opts.Container).IsEqualTo("avi");
        await Assert.That(opts.StartTime).IsEqualTo(2.5);
        await Assert.That(opts.EndTime).IsEqualTo(8.0);
        await Assert.That(opts.Fps).IsEqualTo(25.0);
        await Assert.That(opts.FpsLevels).IsEqualTo("25,15:120:110,10");
        await Assert.That(opts.Limit).IsEqualTo(900.0);
        await Assert.That(opts.Recover).IsEqualTo(500.0);
        await Assert.That(opts.Window).IsEqualTo(3.0);
        await Assert.That(opts.Qv).IsEqualTo(4);
        await Assert.That(opts.Threads).IsEqualTo(4);
        await Assert.That(opts.BufferFrames).IsEqualTo(3);
        await Assert.That(opts.AudioKbps).IsEqualTo(64);
        await Assert.That(opts.AudioChannels).IsEqualTo(2);
        await Assert.That(opts.AudioRate).IsEqualTo(44100);
        await Assert.That(opts.VideoStream).IsEqualTo(2);
        await Assert.That(opts.AudioStream).IsEqualTo(3);
        await Assert.That(opts.Width).IsEqualTo(320);
        await Assert.That(opts.Height).IsEqualTo(240);
        await Assert.That(opts.FitMode).IsEqualTo("fit");
        await Assert.That(opts.CropAnchor).IsEqualTo("left");
        await Assert.That(opts.PadColor).IsEqualTo("0x404040");
        await Assert.That(opts.ScaleAlgo).IsEqualTo("bicubic");
        // v7.9.6: the picture format and colour tags survive the round trip.
        await Assert.That(opts.PixFmt).IsEqualTo("yuv420p10le");
        await Assert.That(opts.ColorRange).IsEqualTo("tv");
        await Assert.That(opts.ColorSpace).IsEqualTo("bt709");
        await Assert.That(opts.ColorPrimaries).IsEqualTo("bt709");
        await Assert.That(opts.ColorTransfer).IsEqualTo("smpte2084");
        await Assert.That(opts.Dither).IsEqualTo("bayer");
        await Assert.That(opts.Deinterlace).IsEqualTo("yadif");
        await Assert.That(opts.DeintMode).IsEqualTo("send_field");
        await Assert.That(opts.DeintParity).IsEqualTo("tff");
        await Assert.That(opts.DeintMarkedOnly).IsTrue();
        await Assert.That(opts.Hwaccel).IsEqualTo("auto");
        await Assert.That(opts.X264Preset).IsEqualTo("fast");
        await Assert.That(opts.X264Tune).IsEqualTo("film");
        await Assert.That(opts.X264Profile).IsEqualTo("high");
        await Assert.That(opts.X264Level).IsEqualTo("4.1");
        await Assert.That(opts.Parallel).IsEqualTo(4);
        await Assert.That(opts.Language).IsEqualTo("zh");
        await Assert.That(opts.FfmpegSource).IsEqualTo("path");
        await Assert.That(opts.FfmpegProxy).IsEqualTo("http://127.0.0.1:7890");
        await Assert.That(opts.Play).IsEqualTo(PlayMode.Ffplay);
        await Assert.That(opts.PlayerPath).IsEqualTo("mpc.exe");
        await Assert.That(opts.Recompress).IsTrue();
        await Assert.That(opts.RecompressArgs).IsEqualTo("-c:v libx264 -crf 28");
        await Assert.That(opts.RecompressContainer).IsEqualTo("mkv");
        await Assert.That(opts.SubtitleFiles).IsEquivalentTo(new[] { "clip.ass" });
        await Assert.That(opts.SubtitleMode).IsEqualTo("copy");
        await Assert.That(opts.SubtitleCodec).IsEqualTo("ass");
    }

    [Test]
    public async Task TimecodesAreEmittedInTheFormTheParserReads()
    {
        var cfg = new UserConfig
        {
            InputPath = "in.rm",
            OutputPath = "out.avi",
            StartTimeSeconds = 2.5,
            EndTimeSeconds = 8,
        };

        List<string> argv = CliArgs.FromUserConfig(cfg);

        await Assert.That(argv).Contains("--start-time").And.Contains("00:00:02.500");
        await Assert.That(argv).Contains("--end-time").And.Contains("00:00:08");
    }

    [Test]
    public async Task TheEraLabelIsNeverEmitted()
    {
        // A preset is a starting point, not a setting: the resolved numbers go
        // out as plain flags, because --era would override them on the way in.
        var cfg = new UserConfig
        {
            InputPath = "in.rm",
            OutputPath = "out.avi",
            EraPreset = "flv07",
            Fps = 15,
            Width = 352,
            Height = 264,
            Limit = 300,
        };

        List<string> argv = CliArgs.FromUserConfig(cfg);

        await Assert.That(argv.Contains("--era")).IsFalse();
        await Assert.That(argv.Contains("--era=flv07")).IsFalse();
        await Assert.That(argv).Contains("--fps").And.Contains("15");
        await Assert.That(argv).Contains("--width").And.Contains("352");
    }

    [Test]
    public async Task TheDerivedBaseRungStaysOutOfTheLadder()
    {
        // A run at 25 fps with the default ladder stores [25, 13, 8, 5, 3] --
        // the resolver prepends the base rate -- while the rungs are the four
        // default entries. Emitting the base back as a ladder entry would
        // invent a rung for it on the next parse.
        var cfg = new UserConfig
        {
            InputPath = "in.rm",
            OutputPath = "out.avi",
            Fps = 25,
            FpsLevels = [25.0, 13.0, 8.0, 5.0, 3.0],
            FpsLevelKbps =
            [
                new FpsRungSetting { Fps = 13.0 },
                new FpsRungSetting { Fps = 8.0 },
                new FpsRungSetting { Fps = 5.0 },
                new FpsRungSetting { Fps = 3.0 },
            ],
        };

        List<string> argv = CliArgs.FromUserConfig(cfg);

        await Assert.That(argv).DoesNotContain("--fps-levels");
        await Assert.That(argv).Contains("--fps").And.Contains("25");
    }

    [Test]
    public async Task ADisabledStageEmitsNothing()
    {
        var cfg = new UserConfig
        {
            InputPath = "in.rm",
            OutputPath = "out.avi",
            // Arguments left over from a preset prefill, never armed.
            Recompress = new RecompressSettings { Enabled = false, Args = "-c:v libx264" },
            Subtitles = new SubtitleConfig { Mode = "off", Files = ["clip.ass"], Stream = 2 },
        };

        List<string> argv = CliArgs.FromUserConfig(cfg);

        await Assert.That(argv).DoesNotContain("--recompress");
        await Assert.That(argv).DoesNotContain("--recompress-args");
        await Assert.That(argv).DoesNotContain("--subtitle");
        await Assert.That(argv).DoesNotContain("--subtitle-stream");
        await Assert.That(argv).DoesNotContain("--subtitle-mode");
    }

    [Test]
    public async Task ADerivedRecoverThresholdIsNotEmitted()
    {
        // Recover defaults to limit * 0.75; a snapshot holding exactly that
        // relation needs no --recover, anything else does.
        var derived = new UserConfig { InputPath = "in.rm", OutputPath = "out.avi", Limit = 400, Recover = 300 };
        var chosen = new UserConfig { InputPath = "in.rm", OutputPath = "out.avi", Limit = 400, Recover = 200 };

        await Assert.That(CliArgs.FromUserConfig(derived)).DoesNotContain("--recover");
        await Assert.That(CliArgs.FromUserConfig(chosen)).Contains("--recover").And.Contains("200");
    }

    [Test]
    public async Task TheJoinedLineQuotesOnlyWhatNeedsIt()
    {
        var cfg = new UserConfig
        {
            InputPath = @"C:\my clips\in.rm",
            OutputPath = @"C:\out dir\say ""hi"".avi",
        };

        string line = CliArgs.Join(CliArgs.FromUserConfig(cfg));

        await Assert.That(line).Contains("\"C:\\my clips\\in.rm\"");
        await Assert.That(line).Contains("\"C:\\out dir\\say \"\"hi\"\".avi\"");
        // ...and the argv tokens themselves stay raw, for the parser.
        await Assert.That(CliArgs.FromUserConfig(cfg)).IsEquivalentTo(
            new[] { @"C:\my clips\in.rm", @"C:\out dir\say ""hi"".avi" });
    }

    [Test]
    public async Task EverySubtitleFileIsItsOwnFlag()
    {
        var cfg = new UserConfig
        {
            InputPath = "in.rm",
            OutputPath = "out.avi",
            Subtitles = new SubtitleConfig { Mode = "burn", Files = ["a.ass", "b.srt"] },
        };

        List<string> argv = CliArgs.FromUserConfig(cfg);

        await Assert.That(argv).IsEquivalentTo(new[]
        {
            "in.rm", "out.avi", "--subtitle", "a.ass", "--subtitle", "b.srt",
        });
    }
}
