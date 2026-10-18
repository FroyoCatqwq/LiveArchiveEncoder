using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.PostProcess;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// The post-processing stages let a user write the middle of an FFmpeg command.
/// That is a shell-shaped hole unless the fragment is tokenised and vetted, so
/// these tests attack the vetting from both sides: fragments that must be
/// accepted, and fragments that would hijack the input or the output.
/// </summary>
[TestFixture]
public sealed class PostArgsTests
{
    // ---- tokenising ----

    [Test]
    public void SplitsOnWhitespace()
    {
        Assert.That(PostArgs.Tokenize("-crf 28 -ac 1"),
            Is.EqualTo(new[] { "-crf", "28", "-ac", "1" }));
    }

    [Test]
    public void KeepsQuotedFilterChainsTogether()
    {
        Assert.That(PostArgs.Tokenize("-vf \"scale=320:240,fps=15\" -c:v libx264"),
            Is.EqualTo(new[] { "-vf", "scale=320:240,fps=15", "-c:v", "libx264" }));
    }

    [Test]
    public void SingleQuotesWorkToo()
    {
        Assert.That(PostArgs.Tokenize("-vf 'yadif,fps=15'"),
            Is.EqualTo(new[] { "-vf", "yadif,fps=15" }));
    }

    [Test]
    public void BackslashesAreLiteralWindowsPaths()
    {
        // Escaping backslashes would break "drawtext=fontfile=C:\Windows\...":
        // the separator has to survive tokenising untouched.
        Assert.That(PostArgs.Tokenize(@"-vf ""drawtext=fontfile=C:\Windows\Fonts\msyh.ttc:text=hi"""),
            Is.EqualTo(new[] { "-vf", @"drawtext=fontfile=C:\Windows\Fonts\msyh.ttc:text=hi" }));
    }

    [Test]
    public void EmptyAndNullProduceNoTokens()
    {
        Assert.That(PostArgs.Tokenize(null), Is.Empty);
        Assert.That(PostArgs.Tokenize("   "), Is.Empty);
        Assert.That(PostArgs.Tokenize(""), Is.Empty);
    }

    // ---- validation: accepted ----

    [Test]
    public void OrdinaryFragmentIsAccepted()
    {
        var (ok, error) = PostArgs.Validate(
            ["-vf", "scale=320:240,fps=15", "-c:v", "libx264", "-crf", "28", "-ac", "1", "-b:a", "32k"]);

        Assert.That(ok, Is.True, error);
    }

    [Test]
    public void OptionValuesWithoutDashesAreAccepted()
    {
        // "-crf 28" ends with "28", which is a value, not an output path.
        var (ok, error) = PostArgs.Validate(["-crf", "28"]);

        Assert.That(ok, Is.True, error);
    }

    // ---- validation: rejected ----

    [Test]
    public void EmptyFragmentIsRejected()
    {
        Assert.That(PostArgs.Validate([]).Ok, Is.False);
    }

    [Test]
    public void SecondInputIsRejected()
    {
        var (ok, error) = PostArgs.Validate(["-c:v", "libx264", "-i", "other.mp4"]);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("-i"));
    }

    [Test]
    public void OutputFlagIsRejected()
    {
        Assert.That(PostArgs.Validate(["-o", "out.mp4"]).Ok, Is.False);
    }

    [TestCase("out.mp4")]
    [TestCase("videos/out.mp4")]
    [TestCase(@"C:\Temp\out.mp4")]
    public void TrailingOutputPathIsRejected(string trailing)
    {
        var (ok, error) = PostArgs.Validate(["-c:v", "libx264", trailing]);

        Assert.That(ok, Is.False, $"a trailing '{trailing}' must be rejected");
        Assert.That(error, Does.Contain(trailing));
    }

    [Test]
    public void BareContainerExtensionCountsAsAPath()
    {
        // "clip.rm" is a path even without a separator.
        Assert.That(PostArgs.Validate(["-c", "copy", "clip.rm"]).Ok, Is.False);
    }

    // ---- filter escaping ----

    [Test]
    public void FilterValueEscapesTheSpecialCharacters()
    {
        // Every one of these ends a drawtext option early if left alone.
        Assert.That(PostArgs.EscapeFilterValue(@"a\b"), Is.EqualTo(@"a\\b"));
        Assert.That(PostArgs.EscapeFilterValue("a:b"), Is.EqualTo("a\\:b"));
        Assert.That(PostArgs.EscapeFilterValue("a'b"), Is.EqualTo("a\\'b"));
        Assert.That(PostArgs.EscapeFilterValue("a,b"), Is.EqualTo("a\\,b"));
        Assert.That(PostArgs.EscapeFilterValue("a;b"), Is.EqualTo("a\\;b"));
        Assert.That(PostArgs.EscapeFilterValue("a[b]"), Is.EqualTo("a\\[b\\]"));
    }

    [Test]
    public void ChineseTextSurvivesEscapingUnchanged()
    {
        Assert.That(PostArgs.EscapeFilterValue("测试水印"), Is.EqualTo("测试水印"));
    }
}

/// <summary>
/// The watermark filter has to survive real FFmpeg parsing: a font path with a
/// drive colon, CJK text, and x/y expressions that depend on the text size.
/// Built once here and exercised against FFmpeg by the e2e harness.
/// </summary>
[TestFixture]
public sealed class WatermarkTests
{
    [Test]
    public void FilterCarriesTextPositionAndSize()
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

        string filter = Watermark.BuildFilter(settings, 320, 240, "C:/Fonts/msyh.ttc");

        Assert.That(filter, Does.StartWith("drawtext="));
        Assert.That(filter, Does.Contain("fontfile='C\\:/Fonts/msyh.ttc'"));
        Assert.That(filter, Does.Contain("text='优酷'"));
        Assert.That(filter, Does.Contain("fontsize=16"));
        Assert.That(filter, Does.Contain("fontcolor=white"));
        // top-right pins the right edge of the text to the frame width.
        Assert.That(filter, Does.Contain("x=W-tw-10"));
        Assert.That(filter, Does.Contain("y=10"));
    }

    [Test]
    public void FontSizeScalesDownForSmallFrames()
    {
        var settings = new WatermarkSettings { Enabled = true, Text = "x" };

        string small = Watermark.BuildFilter(settings, 160, 120, null);
        string large = Watermark.BuildFilter(settings, 1280, 720, null);

        Assert.That(small, Does.Contain("fontsize=8"), "120 / 15 floors at 8");
        Assert.That(large, Does.Contain("fontsize=48"), "720 / 15");
    }

    [Test]
    public void ExplicitFontSizeWinsOverTheAutomaticOne()
    {
        var settings = new WatermarkSettings { Enabled = true, Text = "x", FontSize = 24 };

        Assert.That(Watermark.BuildFilter(settings, 160, 120, null), Does.Contain("fontsize=24"));
    }

    [TestCase("top-left", "x=6", "y=6")]
    [TestCase("top-right", "x=W-tw-6", "y=6")]
    [TestCase("bottom-left", "x=6", "y=H-th-6")]
    [TestCase("bottom-right", "x=W-tw-6", "y=H-th-6")]
    [TestCase("center", "x=(W-tw)/2", "y=(H-th)/2")]
    public void EveryPositionProducesExpressions(string position, string x, string y)
    {
        var settings = new WatermarkSettings { Enabled = true, Text = "x", Position = position };

        string filter = Watermark.BuildFilter(settings, 320, 240, null);

        Assert.That(filter, Does.Contain($"{x}:"));
        Assert.That(filter, Does.Contain($"{y}:"));
    }

    [Test]
    public void UnknownPositionFallsBackToTopRight()
    {
        var settings = new WatermarkSettings { Enabled = true, Text = "x", Position = "somewhere" };

        Assert.That(Watermark.BuildFilter(settings, 320, 240, null), Does.Contain("x=W-tw-"));
    }

    [Test]
    public void OutputPathInsertsWmBeforeTheExtension()
    {
        Assert.That(Watermark.DeriveOutputPath(Path.Combine("d", "clip_live.rm")),
            Is.EqualTo(Path.Combine("d", "clip_live_wm.mp4")));
    }

    [Test]
    public void EmptyTextIsReportedAsAnErrorNotRun()
    {
        // The stage must fail before FFmpeg is started, or a misconfigured run
        // wastes a whole live encode.
        var settings = new WatermarkSettings { Enabled = true, Text = "   " };

        (bool ok, string? error) = Watermark.Apply(
            "ffmpeg-does-not-exist", "in.mp4", "out.mp4", settings, 320, 240);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("empty").IgnoreCase);
    }
}

[TestFixture]
public sealed class RecompressTests
{
    private static RecompressSettings Settings(string args, string container = "mp4") => new()
    {
        Enabled = true,
        Args = args,
        Container = container,
    };

    [Test]
    public void ArgvWrapsTheFragmentBetweenInputAndOutput()
    {
        (List<string> argv, string? error) = Recompress.BuildArgv(
            "ffmpeg", "in.rm", "out.mp4", Settings("-c:v libx264 -crf 28"));

        Assert.That(error, Is.Null);
        Assert.That(argv[0], Is.EqualTo("ffmpeg"));
        Assert.That(argv, Does.Contain("-i").And.Contain("in.rm"));
        Assert.That(argv[^1], Is.EqualTo("out.mp4"), "output is always last");
        // Nothing the user wrote may come after the output.
        Assert.That(argv.IndexOf("out.mp4"), Is.EqualTo(argv.Count - 1));
    }

    [Test]
    public void BadFragmentStopsTheBuild()
    {
        (List<string> argv, string? error) = Recompress.BuildArgv(
            "ffmpeg", "in.rm", "out.mp4", Settings("-i other.mp4"));

        Assert.That(error, Is.Not.Null);
        Assert.That(argv, Is.Empty);
    }

    [Test]
    public void DisabledStageIsANoOp()
    {
        (bool ok, string? error) = Recompress.Apply(
            "ffmpeg-does-not-exist", "in.rm", "out.mp4",
            new RecompressSettings { Enabled = false, Args = "-c:v libx264" });

        Assert.That(ok, Is.True);
        Assert.That(error, Is.Null);
    }

    [Test]
    public void MissingInputIsReportedNotThrown()
    {
        // A stage that throws would take the live product's reporting down with
        // it; it has to come back as an error message instead.
        (bool ok, string? error) = Recompress.Apply(
            "ffmpeg-does-not-exist", "no-such-file.rm", "out.mp4",
            Settings("-c:v libx264"));

        Assert.That(ok, Is.False);
        Assert.That(error, Is.Not.Empty);
    }

    [Test]
    public void WebOutputReplacesTheWmMark()
    {
        string dir = Path.Combine("d", "sub");

        Assert.That(Recompress.DeriveOutputPath(Path.Combine(dir, "clip_live_wm.mp4"), "mp4"),
            Is.EqualTo(Path.Combine(dir, "clip_live_web.mp4")));
        Assert.That(Recompress.DeriveOutputPath(Path.Combine(dir, "clip_live.rm"), "mp4"),
            Is.EqualTo(Path.Combine(dir, "clip_live_web.mp4")));
        Assert.That(Recompress.DeriveOutputPath(Path.Combine(dir, "clip_live.rm"), ".mkv"),
            Is.EqualTo(Path.Combine(dir, "clip_live_web.mkv")));
    }
}

[TestFixture]
public sealed class EraPresetTests
{
    [Test]
    public void IdsAreUniqueAndLowercase()
    {
        List<string> ids = EraPresets.All.Select(p => p.Id).ToList();

        Assert.That(ids, Is.Unique);
        Assert.That(ids, Is.All.Match(@"^[a-z0-9_]+$"));
    }

    [Test]
    public void EveryPresetCoversBothLayers()
    {
        foreach (EraPreset preset in EraPresets.All)
        {
            Assert.That(preset.Encoder, Is.Not.Empty, preset.Id);
            Assert.That(preset.Width, Is.Positive, preset.Id);
            Assert.That(preset.Height, Is.Positive, preset.Id);
            Assert.That(preset.Fps, Is.Positive, preset.Id);
            Assert.That(preset.LimitKbps, Is.Positive, preset.Id);
            Assert.That(preset.RecompressArgs, Is.Not.Empty, preset.Id);
            Assert.That(preset.RecompressContainer, Is.Not.Empty, preset.Id);
        }
    }

    [Test]
    public void RecompressArgumentsPassTheOwnValidator()
    {
        // A preset that its own pipeline would reject would fail at encode time,
        // which is exactly the failure the validator exists to move earlier.
        foreach (EraPreset preset in EraPresets.All)
        {
            (List<string> tokens, string? error) = PostArgs.Parse(preset.RecompressArgs);
            Assert.That(error, Is.Null, $"{preset.Id}: {error}");
            Assert.That(tokens, Is.Not.Empty, preset.Id);
        }
    }

    [Test]
    public void ErasAdvanceInResolutionAndBitrate()
    {
        // The point of the table: later eras are bigger and fatter. A regression
        // here means someone reordered the history.
        for (int i = 1; i < EraPresets.All.Count; i++)
        {
            EraPreset older = EraPresets.All[i - 1];
            EraPreset newer = EraPresets.All[i];

            Assert.That(newer.Width * newer.Height, Is.GreaterThan(older.Width * older.Height),
                $"{newer.Id} should have more pixels than {older.Id}");
            Assert.That(newer.LimitKbps, Is.GreaterThan(older.LimitKbps),
                $"{newer.Id} should allow more bitrate than {older.Id}");
        }
    }

    [Test]
    public void LiveLayerAppliesWithoutArmingRecompress()
    {
        // Picking an era must not silently start re-encoding the output: that
        // stage is opt-in, and "default off" is a user-visible promise.
        var config = new Config();
        EraPresets.Find("flv07")!.ApplyLive(config);

        Assert.That(config.Width, Is.EqualTo(352));
        Assert.That(config.Height, Is.EqualTo(264));
        Assert.That(config.Fps, Is.EqualTo(15));
        Assert.That(config.Limit, Is.EqualTo(300));
        Assert.That(config.ScaleAlgo, Is.EqualTo("bicubic"));
        Assert.That(config.Recompress.Enabled, Is.False, "a preset must not arm recompress");
    }

    [Test]
    public void FindIsCaseInsensitiveAndRejectsUnknowns()
    {
        Assert.That(EraPresets.Find("FLV07")!.Id, Is.EqualTo("flv07"));
        Assert.That(EraPresets.Find("nope"), Is.Null);
        Assert.That(EraPresets.Find(""), Is.Null);
        Assert.That(EraPresets.Find(null), Is.Null);
    }
}

[TestFixture]
public sealed class PostProcessingConfigTests
{
    [Test]
    public void BothStagesAreOffByDefault()
    {
        var config = new Config();

        Assert.That(config.Watermark.Enabled, Is.False, "watermark defaults to off");
        Assert.That(config.Recompress.Enabled, Is.False, "recompress defaults to off");
    }

    [Test]
    public void WatermarkAndRecompressRoundTripThroughUserConfig()
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
        UserConfig roundTripped = RoundTrip(stored);

        var restored = new Config();
        ConfigStore.Apply(roundTripped, restored, []);

        Assert.That(restored.Watermark.Enabled, Is.True);
        Assert.That(restored.Watermark.Text, Is.EqualTo("新浪播客"));
        Assert.That(restored.Watermark.Position, Is.EqualTo("bottom-left"));
        Assert.That(restored.Watermark.FontSize, Is.EqualTo(20));
        Assert.That(restored.Watermark.FontColor, Is.EqualTo("yellow"));
        Assert.That(restored.Recompress.Enabled, Is.True);
        Assert.That(restored.Recompress.Args, Does.Contain("crf 28"));
        Assert.That(restored.Recompress.Container, Is.EqualTo("mkv"));
        Assert.That(restored.Recompress.KeepIntermediate, Is.True);
        Assert.That(restored.EraPreset, Is.EqualTo("flv07"));
    }

    [Test]
    public void AVersion1ConfigFileStillLoadsWithBothStagesOff()
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

            Assert.That(loaded.Encoder, Is.EqualTo("rv20"));
            Assert.That(loaded.Watermark, Is.Not.Null);
            Assert.That(loaded.Watermark.Enabled, Is.False);
            Assert.That(loaded.Recompress, Is.Not.Null);
            Assert.That(loaded.Recompress.Enabled, Is.False);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void PositionIsNormalisedAndUnknownValuesFallBack()
    {
        Assert.That(WatermarkSettings.CanonicalPosition("TOP_RIGHT"), Is.EqualTo("top-right"));
        Assert.That(WatermarkSettings.CanonicalPosition("center"), Is.EqualTo("center"));
        Assert.That(WatermarkSettings.CanonicalPosition(null), Is.EqualTo("top-right"));
        Assert.That(WatermarkSettings.CanonicalPosition("nonsense"), Is.EqualTo("top-right"));
    }

    private static UserConfig RoundTrip(UserConfig cfg)
    {
        string path = Path.Combine(Path.GetTempPath(), $"lae-rt-{Guid.NewGuid():N}.json");
        try
        {
            Assert.That(ConfigStore.Save(cfg, path), Is.True);
            return ConfigStore.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
