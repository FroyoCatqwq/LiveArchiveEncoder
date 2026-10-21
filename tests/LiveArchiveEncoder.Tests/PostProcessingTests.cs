using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.PostProcess;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// The post-processing stages let a user write the middle of an FFmpeg command.
/// That is a shell-shaped hole unless the fragment is tokenised and vetted, so
/// these tests attack the vetting from both sides: fragments that must be
/// accepted, and fragments that would hijack the input or the output.
/// </summary>
public sealed class PostArgsTests
{
    // ---- tokenising ----

    [Test]
    public async Task SplitsOnWhitespace()
    {
        await Assert.That(PostArgs.Tokenize("-crf 28 -ac 1")).IsEquivalentTo(new[] { "-crf", "28", "-ac", "1" });
    }

    [Test]
    public async Task KeepsQuotedFilterChainsTogether()
    {
        await Assert.That(PostArgs.Tokenize("-vf \"scale=320:240,fps=15\" -c:v libx264")).IsEquivalentTo(new[] { "-vf", "scale=320:240,fps=15", "-c:v", "libx264" });
    }

    [Test]
    public async Task SingleQuotesWorkToo()
    {
        await Assert.That(PostArgs.Tokenize("-vf 'yadif,fps=15'")).IsEquivalentTo(new[] { "-vf", "yadif,fps=15" });
    }

    [Test]
    public async Task BackslashesAreLiteralWindowsPaths()
    {
        // Escaping backslashes would break "drawtext=fontfile=C:\Windows\...":
        // the separator has to survive tokenising untouched.
        await Assert.That(PostArgs.Tokenize(@"-vf ""drawtext=fontfile=C:\Windows\Fonts\msyh.ttc:text=hi""")).IsEquivalentTo(new[] { "-vf", @"drawtext=fontfile=C:\Windows\Fonts\msyh.ttc:text=hi" });
    }

    [Test]
    public async Task EmptyAndNullProduceNoTokens()
    {
        await Assert.That(PostArgs.Tokenize(null)).IsEmpty();
        await Assert.That(PostArgs.Tokenize("   ")).IsEmpty();
        await Assert.That(PostArgs.Tokenize("")).IsEmpty();
    }

    // ---- validation: accepted ----

    [Test]
    public async Task OrdinaryFragmentIsAccepted()
    {
        var (ok, error) = PostArgs.Validate(
            ["-vf", "scale=320:240,fps=15", "-c:v", "libx264", "-crf", "28", "-ac", "1", "-b:a", "32k"]);

        await Assert.That(ok).IsTrue().Because(error ?? "validation said nothing");
    }

    [Test]
    public async Task OptionValuesWithoutDashesAreAccepted()
    {
        // "-crf 28" ends with "28", which is a value, not an output path.
        var (ok, error) = PostArgs.Validate(["-crf", "28"]);

        await Assert.That(ok).IsTrue().Because(error ?? "validation said nothing");
    }

    // ---- validation: rejected ----

    [Test]
    public async Task EmptyFragmentIsRejected()
    {
        await Assert.That(PostArgs.Validate([]).Ok).IsFalse();
    }

    [Test]
    public async Task SecondInputIsRejected()
    {
        var (ok, error) = PostArgs.Validate(["-c:v", "libx264", "-i", "other.mp4"]);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).Contains("-i");
    }

    [Test]
    public async Task OutputFlagIsRejected()
    {
        await Assert.That(PostArgs.Validate(["-o", "out.mp4"]).Ok).IsFalse();
    }

    [Test]
    [Arguments("out.mp4")]
    [Arguments("videos/out.mp4")]
    [Arguments(@"C:\Temp\out.mp4")]
    public async Task TrailingOutputPathIsRejected(string trailing)
    {
        var (ok, error) = PostArgs.Validate(["-c:v", "libx264", trailing]);

        await Assert.That(ok).IsFalse().Because($"a trailing '{trailing}' must be rejected");
        await Assert.That(error).Contains(trailing);
    }

    [Test]
    public async Task BareContainerExtensionCountsAsAPath()
    {
        // "clip.rm" is a path even without a separator.
        await Assert.That(PostArgs.Validate(["-c", "copy", "clip.rm"]).Ok).IsFalse();
    }

    // ---- filter escaping ----

    [Test]
    public async Task FilterValueEscapesTheSpecialCharacters()
    {
        // Every one of these ends a drawtext option early if left alone.
        await Assert.That(PostArgs.EscapeFilterValue(@"a\b")).IsEqualTo(@"a\\b");
        await Assert.That(PostArgs.EscapeFilterValue("a:b")).IsEqualTo("a\\:b");
        await Assert.That(PostArgs.EscapeFilterValue("a'b")).IsEqualTo("a\\'b");
        await Assert.That(PostArgs.EscapeFilterValue("a,b")).IsEqualTo("a\\,b");
        await Assert.That(PostArgs.EscapeFilterValue("a;b")).IsEqualTo("a\\;b");
        await Assert.That(PostArgs.EscapeFilterValue("a[b]")).IsEqualTo("a\\[b\\]");
    }

    [Test]
    public async Task ChineseTextSurvivesEscapingUnchanged()
    {
        await Assert.That(PostArgs.EscapeFilterValue("测试水印")).IsEqualTo("测试水印");
    }
}

/// <summary>
/// The watermark filter has to survive real FFmpeg parsing: a font path with a
/// drive colon, CJK text, and x/y expressions that depend on the text size.
/// Built once here and exercised against FFmpeg by the e2e harness.
/// </summary>
public sealed class WatermarkTests
{
    [Test]
    public async Task FilterCarriesTextPositionAndSize()
    {
        var settings = new WatermarkSettings
        {
            Enabled = true,
            Text = "优酷",
            Position = "top-right",
            FontSize = 16,
            Margin = 10,
            FontColor = "white",
        };

        string filter = Watermark.BuildFilter(settings, 240, "C:/Fonts/msyh.ttc");

        await Assert.That(filter).StartsWith("drawtext=");
        await Assert.That(filter).Contains("fontfile='C\\:/Fonts/msyh.ttc'");
        await Assert.That(filter).Contains("text='优酷'");
        await Assert.That(filter).Contains("fontsize=16");
        await Assert.That(filter).Contains("fontcolor=white");
        // top-right pins the right edge of the text to the frame width.
        await Assert.That(filter).Contains("x=W-tw-10");
        await Assert.That(filter).Contains("y=10");
    }

    [Test]
    public async Task FontSizeScalesDownForSmallFrames()
    {
        var settings = new WatermarkSettings { Enabled = true, Text = "x" };

        string small = Watermark.BuildFilter(settings, 120, null);
        string large = Watermark.BuildFilter(settings, 720, null);

        await Assert.That(small).Contains("fontsize=8").Because("120 / 15 floors at 8");
        await Assert.That(large).Contains("fontsize=48").Because("720 / 15");
    }

    [Test]
    public async Task ExplicitFontSizeWinsOverTheAutomaticOne()
    {
        var settings = new WatermarkSettings { Enabled = true, Text = "x", FontSize = 24 };

        await Assert.That(Watermark.BuildFilter(settings, 120, null)).Contains("fontsize=24");
    }

    [Test]
    [Arguments("top-left", "x=6", "y=6")]
    [Arguments("top-right", "x=W-tw-6", "y=6")]
    [Arguments("bottom-left", "x=6", "y=H-th-6")]
    [Arguments("bottom-right", "x=W-tw-6", "y=H-th-6")]
    [Arguments("center", "x=(W-tw)/2", "y=(H-th)/2")]
    public async Task EveryPositionProducesExpressions(string position, string x, string y)
    {
        var settings = new WatermarkSettings { Enabled = true, Text = "x", Position = position };

        string filter = Watermark.BuildFilter(settings, 240, null);

        await Assert.That(filter).Contains($"{x}:");
        await Assert.That(filter).Contains($"{y}:");
    }

    [Test]
    public async Task UnknownPositionFallsBackToTopRight()
    {
        var settings = new WatermarkSettings { Enabled = true, Text = "x", Position = "somewhere" };

        await Assert.That(Watermark.BuildFilter(settings, 240, null)).Contains("x=W-tw-");
    }

    [Test]
    public async Task OutputPathInsertsWmBeforeTheExtension()
    {
        await Assert.That(Watermark.DeriveOutputPath(Path.Combine("d", "clip_live.rm"))).IsEqualTo(Path.Combine("d", "clip_live_wm.mp4"));
    }

    [Test]
    public async Task EmptyTextIsReportedAsAnErrorNotRun()
    {
        // The stage must fail before FFmpeg is started, or a misconfigured run
        // wastes a whole live encode.
        var settings = new WatermarkSettings { Enabled = true, Text = "   " };

        (bool ok, string? error) = Watermark.Apply(
            "ffmpeg-does-not-exist", "in.mp4", "out.mp4", settings, 320, 240);

        await Assert.That(ok).IsFalse();
        // TUnit has no case-insensitive Contains overload; a plain substring
        // check on the message is the same statement.
        await Assert.That(error!.Contains("empty", StringComparison.OrdinalIgnoreCase)).IsTrue();
    }
}

public sealed class RecompressTests
{
    private static RecompressSettings Settings(string args, string container = "mp4") => new()
    {
        Enabled = true,
        Args = args,
        Container = container,
    };

    [Test]
    public async Task ArgvWrapsTheFragmentBetweenInputAndOutput()
    {
        (List<string> argv, string? error) = Recompress.BuildArgv(
            "ffmpeg", "in.rm", "out.mp4", Settings("-c:v libx264 -crf 28"));

        await Assert.That(error).IsNull();
        await Assert.That(argv[0]).IsEqualTo("ffmpeg");
        await Assert.That(argv).Contains("-i").And.Contains("in.rm");
        await Assert.That(argv[^1]).IsEqualTo("out.mp4").Because("output is always last");
        // Nothing the user wrote may come after the output.
        await Assert.That(argv.IndexOf("out.mp4")).IsEqualTo(argv.Count - 1);
    }

    [Test]
    public async Task BadFragmentStopsTheBuild()
    {
        (List<string> argv, string? error) = Recompress.BuildArgv(
            "ffmpeg", "in.rm", "out.mp4", Settings("-i other.mp4"));

        await Assert.That(error).IsNotNull();
        await Assert.That(argv).IsEmpty();
    }

    [Test]
    public async Task DisabledStageIsANoOp()
    {
        (bool ok, string? error) = Recompress.Apply(
            "ffmpeg-does-not-exist", "in.rm", "out.mp4",
            new RecompressSettings { Enabled = false, Args = "-c:v libx264" });

        await Assert.That(ok).IsTrue();
        await Assert.That(error).IsNull();
    }

    [Test]
    public async Task MissingInputIsReportedNotThrown()
    {
        // A stage that throws would take the live product's reporting down with
        // it; it has to come back as an error message instead.
        (bool ok, string? error) = Recompress.Apply(
            "ffmpeg-does-not-exist", "no-such-file.rm", "out.mp4",
            Settings("-c:v libx264"));

        await Assert.That(ok).IsFalse();
        await Assert.That(error).IsNotEmpty();
    }

    [Test]
    public async Task WebOutputReplacesTheWmMark()
    {
        string dir = Path.Combine("d", "sub");

        await Assert.That(Recompress.DeriveOutputPath(Path.Combine(dir, "clip_live_wm.mp4"), "mp4")).IsEqualTo(Path.Combine(dir, "clip_live_web.mp4"));
        await Assert.That(Recompress.DeriveOutputPath(Path.Combine(dir, "clip_live.rm"), "mp4")).IsEqualTo(Path.Combine(dir, "clip_live_web.mp4"));
        await Assert.That(Recompress.DeriveOutputPath(Path.Combine(dir, "clip_live.rm"), ".mkv")).IsEqualTo(Path.Combine(dir, "clip_live_web.mkv"));
    }
}

public sealed class EraPresetTests
{
    [Test]
    public async Task IdsAreUniqueAndLowercase()
    {
        List<string> ids = EraPresets.All.Select(p => p.Id).ToList();

        await Assert.That(ids.Distinct().Count()).IsEqualTo(ids.Count);
        foreach (string id in ids)
        {
            await Assert.That(System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9_]+$")).IsTrue();
        }
    }

    [Test]
    public async Task EveryPresetCoversBothLayers()
    {
        foreach (EraPreset preset in EraPresets.All)
        {
            await Assert.That(preset.Encoder).IsNotEmpty().Because(preset.Id);
            await Assert.That(preset.Width).IsPositive().Because(preset.Id);
            await Assert.That(preset.Height).IsPositive().Because(preset.Id);
            await Assert.That(preset.Fps).IsPositive().Because(preset.Id);
            await Assert.That(preset.LimitKbps).IsPositive().Because(preset.Id);
            await Assert.That(preset.RecompressArgs).IsNotEmpty().Because(preset.Id);
            await Assert.That(preset.RecompressContainer).IsNotEmpty().Because(preset.Id);
        }
    }

    [Test]
    public async Task RecompressArgumentsPassTheOwnValidator()
    {
        // A preset that its own pipeline would reject would fail at encode time,
        // which is exactly the failure the validator exists to move earlier.
        foreach (EraPreset preset in EraPresets.All)
        {
            (List<string> tokens, string? error) = PostArgs.Parse(preset.RecompressArgs);
            await Assert.That(error).IsNull().Because($"{preset.Id}: {error}");
            await Assert.That(tokens).IsNotEmpty().Because(preset.Id);
        }
    }

    [Test]
    public async Task ErasAdvanceInResolutionAndBitrate()
    {
        // The point of the table: later eras are bigger and fatter. A regression
        // here means someone reordered the history.
        for (int i = 1; i < EraPresets.All.Count; i++)
        {
            EraPreset older = EraPresets.All[i - 1];
            EraPreset newer = EraPresets.All[i];

            await Assert.That(newer.Width * newer.Height).IsGreaterThan(older.Width * older.Height).Because($"{newer.Id} should have more pixels than {older.Id}");
            await Assert.That(newer.LimitKbps).IsGreaterThan(older.LimitKbps).Because($"{newer.Id} should allow more bitrate than {older.Id}");
        }
    }

    [Test]
    public async Task LiveLayerAppliesWithoutArmingRecompress()
    {
        // Picking an era must not silently start re-encoding the output: that
        // stage is opt-in, and "default off" is a user-visible promise.
        var config = new Config();
        EraPresets.Find("flv07")!.ApplyLive(config);

        await Assert.That(config.Width).IsEqualTo(352);
        await Assert.That(config.Height).IsEqualTo(264);
        await Assert.That(config.Fps).IsEqualTo(15);
        await Assert.That(config.Limit).IsEqualTo(300);
        await Assert.That(config.ScaleAlgo).IsEqualTo("bicubic");
        await Assert.That(config.Recompress.Enabled).IsFalse().Because("a preset must not arm recompress");
    }

    [Test]
    public async Task FindIsCaseInsensitiveAndRejectsUnknowns()
    {
        await Assert.That(EraPresets.Find("FLV07")!.Id).IsEqualTo("flv07");
        await Assert.That(EraPresets.Find("nope")).IsNull();
        await Assert.That(EraPresets.Find("")).IsNull();
        await Assert.That(EraPresets.Find(null)).IsNull();
    }
}

public sealed class PostProcessingConfigTests
{
    [Test]
    public async Task BothStagesAreOffByDefault()
    {
        var config = new Config();

        await Assert.That(config.Watermark.Enabled).IsFalse().Because("watermark defaults to off");
        await Assert.That(config.Recompress.Enabled).IsFalse().Because("recompress defaults to off");
    }

    [Test]
    public async Task WatermarkAndRecompressRoundTripThroughUserConfig()
    {
        var source = new Config
        {
            Watermark = new WatermarkSettings
            {
                Enabled = true, Text = "新浪播客", Position = "bottom-left",
                FontSize = 20, Margin = 8, FontColor = "yellow", FontFile = "C:/f.ttf",
            },
            Recompress = new RecompressSettings
            {
                Enabled = true,
                Args = "-vf scale=320:240,fps=15 -c:v libx264 -crf 28",
                Container = "mkv",
                KeepIntermediate = true,
            },
            EraPreset = "flv07",
        };

        UserConfig stored = ConfigStore.Capture(source);
        UserConfig roundTripped = await RoundTrip(stored);

        var restored = new Config();
        ConfigStore.Apply(roundTripped, restored, []);

        await Assert.That(restored.Watermark.Enabled).IsTrue();
        await Assert.That(restored.Watermark.Text).IsEqualTo("新浪播客");
        await Assert.That(restored.Watermark.Position).IsEqualTo("bottom-left");
        await Assert.That(restored.Watermark.FontSize).IsEqualTo(20);
        await Assert.That(restored.Watermark.FontColor).IsEqualTo("yellow");
        await Assert.That(restored.Recompress.Enabled).IsTrue();
        await Assert.That(restored.Recompress.Args).Contains("crf 28");
        await Assert.That(restored.Recompress.Container).IsEqualTo("mkv");
        await Assert.That(restored.Recompress.KeepIntermediate).IsTrue();
        await Assert.That(restored.EraPreset).IsEqualTo("flv07");
    }

    [Test]
    public async Task AVersion1ConfigFileStillLoadsWithBothStagesOff()
    {
        // The v7.5 shape, before the stages existed. It must read back as a
        // normal config with the new stages at their defaults, not as nulls.
        string legacyJson = """
            {
              "Version": 1,
              "Encoder": "rv20",
              "Fps": 13.0,
              "Limit": 200.0,
              "Language": "zh",
              "FfmpegSource": "btbn",
              "PlayMode": "off"
            }
            """;

        string path = Path.Combine(Path.GetTempPath(), $"lae-v1-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, legacyJson);
            UserConfig loaded = ConfigStore.Load(path);

            await Assert.That(loaded.Encoder).IsEqualTo("rv20");
            await Assert.That(loaded.Watermark).IsNotNull();
            await Assert.That(loaded.Watermark.Enabled).IsFalse();
            await Assert.That(loaded.Recompress).IsNotNull();
            await Assert.That(loaded.Recompress.Enabled).IsFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task PositionIsNormalisedAndUnknownValuesFallBack()
    {
        await Assert.That(WatermarkSettings.CanonicalPosition("TOP_RIGHT")).IsEqualTo("top-right");
        await Assert.That(WatermarkSettings.CanonicalPosition("center")).IsEqualTo("center");
        await Assert.That(WatermarkSettings.CanonicalPosition(null)).IsEqualTo("top-right");
        await Assert.That(WatermarkSettings.CanonicalPosition("nonsense")).IsEqualTo("top-right");
    }

    private static async Task<UserConfig> RoundTrip(UserConfig cfg)
    {
        string path = Path.Combine(Path.GetTempPath(), $"lae-rt-{Guid.NewGuid():N}.json");
        try
        {
            await Assert.That(ConfigStore.Save(cfg, path)).IsTrue();
            return ConfigStore.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>
/// v7.9 #4: a font named by the user. The bug this covers: a bare file name
/// (<c>--watermark-font simsun.ttc</c>) was handed to FFmpeg verbatim, and
/// FFmpeg resolves relative paths against its own working directory -- so the
/// font sitting in the bundled directory was never found and the only thing
/// that worked was an absolute path.
///
/// Not in parallel: the directory override is a process-wide environment
/// variable, and TUnit's default parallelism would let one test's finally
/// block restore it under another test's feet.
/// </summary>
[NotInParallel]
public sealed class FontResolverTests
{
    [Test]
    public async Task EmptyOrNullIsEmpty()
    {
        await Assert.That(FontResolver.Resolve(null)).IsEqualTo("");
        await Assert.That(FontResolver.Resolve("")).IsEqualTo("");
    }

    [Test]
    public async Task AnExistingPathIsUsedAsTyped()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lae-font-{Guid.NewGuid():N}.ttf");
        try
        {
            File.WriteAllText(path, "not a real font; existence is all Resolve checks");
            await Assert.That(FontResolver.Resolve(path)).IsEqualTo(Path.GetFullPath(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ABareNameIsLookedUpInTheFontsDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"lae-fonts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string font = Path.Combine(dir, "simsun.ttc");
        File.WriteAllText(font, "stub");
        string? previous = Environment.GetEnvironmentVariable(FontResolver.DirectoryEnvironmentVariable);
        Environment.SetEnvironmentVariable(FontResolver.DirectoryEnvironmentVariable, dir);
        try
        {
            // The regression: this used to come back as "simsun.ttc" and the
            // encode then failed with "no such file or directory".
            await Assert.That(FontResolver.Resolve("simsun.ttc")).IsEqualTo(font);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FontResolver.DirectoryEnvironmentVariable, previous);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task AnUnknownBareNameComesBackUnchangedForTheErrorMessage()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"lae-fonts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string? previous = Environment.GetEnvironmentVariable(FontResolver.DirectoryEnvironmentVariable);
        Environment.SetEnvironmentVariable(FontResolver.DirectoryEnvironmentVariable, dir);
        try
        {
            await Assert.That(FontResolver.Resolve("nope.ttf")).IsEqualTo("nope.ttf");
            // A rooted path is never rewritten, existing or not.
            await Assert.That(FontResolver.Resolve(@"C:\Windows\Fonts\simsun.ttc"))
                .IsEqualTo(@"C:\Windows\Fonts\simsun.ttc");
        }
        finally
        {
            Environment.SetEnvironmentVariable(FontResolver.DirectoryEnvironmentVariable, previous);
            Directory.Delete(dir, recursive: true);
        }
    }
}
