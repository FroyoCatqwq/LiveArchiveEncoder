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
    public async Task WebOutputReplacesTheLiveExtension()
    {
        string dir = Path.Combine("d", "sub");

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
            // v7.9.7: built-ins are "site/year"; a hand-written preset may be a
            // bare slug. Nothing else is allowed, because the id travels on a
            // command line unquoted.
            await Assert.That(System.Text.RegularExpressions.Regex
                .IsMatch(id, "^[a-z0-9_]+(/[0-9]{4})?$")).IsTrue().Because(id);
        }
    }

    [Test]
    public async Task EveryPresetCoversTheLayersItClaims()
    {
        // v7.9.6.2: every built-in preset owns both layers -- an era describes a
        // period, a site describes one portal, and both say how the picture was
        // captured as well as how it was served. What separates them is the
        // container and the codec of the second pass, not the scope.
        foreach (EraPreset preset in EraPresets.All)
        {
            await Assert.That(preset.Encoder).IsNotEmpty().Because(preset.Id);
            await Assert.That(preset.Width).IsPositive().Because(preset.Id);
            await Assert.That(preset.Height).IsPositive().Because(preset.Id);
            await Assert.That(preset.Fps).IsPositive().Because(preset.Id);
            await Assert.That(preset.LimitKbps).IsPositive().Because(preset.Id);
            await Assert.That(preset.AudioKbps).IsPositive().Because(preset.Id);
            await Assert.That(preset.AudioRate).IsPositive().Because(preset.Id);
            await Assert.That(preset.AudioChannels).IsPositive().Because(preset.Id);

            // Both kinds carry the recompress layer -- that is what makes them
            // usable with --recompress-only.
            await Assert.That(preset.RecompressArgs).IsNotEmpty().Because(preset.Id);
            await Assert.That(preset.RecompressContainer).IsNotEmpty().Because(preset.Id);
        }
    }

    [Test]
    public async Task BothRenderingModesPassTheOwnValidator()
    {
        // A preset that its own pipeline would reject would fail at encode time,
        // which is exactly the failure the validator exists to move earlier.
        foreach (EraPreset preset in EraPresets.All)
        {
            foreach (bool native in new[] { true, false })
            {
                (List<string> tokens, string? error) = PostArgs.Parse(preset.Fragment(native));
                await Assert.That(error).IsNull().Because($"{preset.Id} native={native}: {error}");
                await Assert.That(tokens).IsNotEmpty().Because(preset.Id);

                // ... and the pair has to be one FFmpeg can actually mux. The
                // modern rendering is libx264 + aac in MP4 by definition.
                string video = native ? preset.VideoCodec : "libx264";
                string audio = native ? preset.AudioCodec : "aac";
                await Assert.That(RecompressCodecs.ValidateContainer(
                        video, audio, preset.ContainerFor(native)))
                    .IsNull().Because($"{preset.Id} native={native}");
            }
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
        //
        // The five era rows, in order. Site presets own a live layer since
        // v7.9.6.2 as well, but they are a dozen parallel ladders of the same
        // years (sina06 and sohu06 are both 320x240), so monotonicity only
        // holds for the era table.
        IReadOnlyList<EraPreset> eras = [.. EraPresets.All.Where(p => p.Kind == EraKind.Era)];
        for (int i = 1; i < eras.Count; i++)
        {
            EraPreset older = eras[i - 1];
            EraPreset newer = eras[i];

            await Assert.That(newer.Width!.Value * newer.Height!.Value)
                .IsGreaterThan(older.Width!.Value * older.Height!.Value)
                .Because($"{newer.Id} should have more pixels than {older.Id}");
            await Assert.That(newer.LimitKbps!.Value).IsGreaterThan(older.LimitKbps!.Value)
                .Because($"{newer.Id} should allow more bitrate than {older.Id}");
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
        // v7.9.7: the id is "site/year" now, and the pre-rewrite spelling still
        // resolves to it -- a settings file from v7.9.6 must keep working.
        await Assert.That(EraPresets.Find("GENERIC/2007")!.Id).IsEqualTo("generic/2007");
        await Assert.That(EraPresets.Find("flv07")!.Id).IsEqualTo("generic/2007");
        await Assert.That(EraPresets.Find("YOUKU07")!.Id).IsEqualTo("youku/2007");
        await Assert.That(EraPresets.Find("nope")).IsNull();
        await Assert.That(EraPresets.Find("")).IsNull();
        await Assert.That(EraPresets.Find(null)).IsNull();
    }

    [Test]
    public async Task EveryOldIdResolvesToABuiltInRow()
    {
        // The alias table is the compatibility promise: every id v7.9.6 printed
        // must still name a preset, and the target must exist.
        foreach ((string oldId, string newId) in EraPresets.Aliases)
        {
            await Assert.That(EraPresets.Canonical(oldId)).IsEqualTo(newId).Because(oldId);
            await Assert.That(EraPresets.Find(oldId)).IsNotNull().Because(oldId);
            await Assert.That(EraPresets.IsAlias(oldId)).IsTrue().Because(oldId);
            await Assert.That(EraPresets.Find(newId)!.BuiltIn).IsTrue().Because(newId);
        }
    }

    [Test]
    public async Task SiteSlashYearIsTheOnlyBuiltInShape()
    {
        foreach (EraPreset preset in EraPresets.All.Where(p => p.BuiltIn))
        {
            await Assert.That(preset.Id).IsEqualTo($"{preset.Site}/{preset.Year}").Because(preset.Id);
            await Assert.That(preset.Year).IsGreaterThanOrEqualTo(1990).Because(preset.Id);
            await Assert.That(preset.Year).IsLessThanOrEqualTo(2100).Because(preset.Id);
        }
        // Every built-in site has at least one year, and every group is ordered.
        foreach ((string site, IReadOnlyList<EraPreset> rows) in EraPresets.Groups)
        {
            await Assert.That(rows.Count).IsGreaterThan(0).Because(site);
            await Assert.That(rows.All(r => string.Equals(r.Site, site, StringComparison.OrdinalIgnoreCase)))
                .IsTrue().Because(site);
        }
    }
}

public sealed class PostProcessingConfigTests
{
    [Test]
    public async Task RecompressIsOffByDefault()
    {
        var config = new Config();

        await Assert.That(config.Recompress.Enabled).IsFalse().Because("recompress defaults to off");
    }

    [Test]
    public async Task RecompressRoundTripThroughUserConfig()
    {
        var source = new Config
        {
            Recompress = new RecompressSettings
            {
                Enabled = true,
                Args = "-vf scale=320:240,fps=15 -c:v libx264 -crf 28",
                Container = "mkv",
            },
            EraPreset = "flv07",
        };

        UserConfig stored = ConfigStore.Capture(source);
        UserConfig roundTripped = await RoundTrip(stored);

        var restored = new Config();
        ConfigStore.Apply(roundTripped, restored, []);

        await Assert.That(restored.Recompress.Enabled).IsTrue();
        await Assert.That(restored.Recompress.Args).Contains("crf 28");
        await Assert.That(restored.Recompress.Container).IsEqualTo("mkv");
        await Assert.That(restored.EraPreset).IsEqualTo("flv07");
    }

    [Test]
    public async Task AVersion1ConfigFileStillLoadsWithRecompressOff()
    {
        // The v7.5 shape, before the stage existed. It must read back as a
        // normal config with the stage at its defaults, not as nulls.
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
            await Assert.That(loaded.Recompress).IsNotNull();
            await Assert.That(loaded.Recompress.Enabled).IsFalse();
        }
        finally
        {
            File.Delete(path);
        }
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
/// Where the subtitle burn path looks for fonts. The live surface is the
/// directory contract: <see cref="FontResolver.FontDirectory"/> answers from
/// LAE_FONT_DIR, falls back to the pre-retirement LAE_WATERMARK_FONT_DIR so a
/// setup written for the watermark era keeps working, and otherwise points at
/// the profile directory's fonts folder.
///
/// The font-picking members that used to live here (Resolve, Find, PickFrom,
/// the CJK marker tables) were archived with the watermark in v7.9.1 -- see
/// archive/retired/RetiredFontPicking.cs.
///
/// Not in parallel: the directory override is a process-wide environment
/// variable, and TUnit's default parallelism would let one test's finally
/// block restore it under another test's feet.
/// </summary>
[NotInParallel]
public sealed class FontResolverTests
{
    [Test]
    public async Task DirectoryEnvOverrideWins()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"lae-fonts-{Guid.NewGuid():N}");
        string? previous = Environment.GetEnvironmentVariable(FontResolver.DirectoryEnvironmentVariable);
        Environment.SetEnvironmentVariable(FontResolver.DirectoryEnvironmentVariable, dir);
        try
        {
            await Assert.That(FontResolver.FontDirectory).IsEqualTo(dir);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FontResolver.DirectoryEnvironmentVariable, previous);
        }
    }

    [Test]
    public async Task LegacyDirectoryEnvOverrideStillHonoured()
    {
        // v7.9.1: LAE_WATERMARK_FONT_DIR predates the watermark removal; a
        // machine that exported it must not silently lose its font directory.
        string dir = Path.Combine(Path.GetTempPath(), $"lae-fonts-legacy-{Guid.NewGuid():N}");
        string? previous = Environment.GetEnvironmentVariable(FontResolver.DirectoryEnvironmentVariable);
        string? legacy = Environment.GetEnvironmentVariable(FontResolver.LegacyDirectoryEnvironmentVariable);
        Environment.SetEnvironmentVariable(FontResolver.DirectoryEnvironmentVariable, null);
        Environment.SetEnvironmentVariable(FontResolver.LegacyDirectoryEnvironmentVariable, dir);
        try
        {
            await Assert.That(FontResolver.FontDirectory).IsEqualTo(dir);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FontResolver.DirectoryEnvironmentVariable, previous);
            Environment.SetEnvironmentVariable(FontResolver.LegacyDirectoryEnvironmentVariable, legacy);
        }
    }

    [Test]
    public async Task NewNameBeatsTheLegacyOne()
    {
        string current = Path.Combine(Path.GetTempPath(), $"lae-fonts-new-{Guid.NewGuid():N}");
        string legacy = Path.Combine(Path.GetTempPath(), $"lae-fonts-old-{Guid.NewGuid():N}");
        string? previous = Environment.GetEnvironmentVariable(FontResolver.DirectoryEnvironmentVariable);
        string? legacyPrevious = Environment.GetEnvironmentVariable(FontResolver.LegacyDirectoryEnvironmentVariable);
        Environment.SetEnvironmentVariable(FontResolver.DirectoryEnvironmentVariable, current);
        Environment.SetEnvironmentVariable(FontResolver.LegacyDirectoryEnvironmentVariable, legacy);
        try
        {
            await Assert.That(FontResolver.FontDirectory).IsEqualTo(current);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FontResolver.DirectoryEnvironmentVariable, previous);
            Environment.SetEnvironmentVariable(FontResolver.LegacyDirectoryEnvironmentVariable, legacyPrevious);
        }
    }

    [Test]
    public async Task DefaultIsTheFontsFolderUnderTheProfile()
    {
        string? previous = Environment.GetEnvironmentVariable(FontResolver.DirectoryEnvironmentVariable);
        string? legacy = Environment.GetEnvironmentVariable(FontResolver.LegacyDirectoryEnvironmentVariable);
        Environment.SetEnvironmentVariable(FontResolver.DirectoryEnvironmentVariable, null);
        Environment.SetEnvironmentVariable(FontResolver.LegacyDirectoryEnvironmentVariable, null);
        try
        {
            await Assert.That(FontResolver.FontDirectory)
                .IsEqualTo(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LiveArchiveEncoder", "fonts"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(FontResolver.DirectoryEnvironmentVariable, previous);
            Environment.SetEnvironmentVariable(FontResolver.LegacyDirectoryEnvironmentVariable, legacy);
        }
    }
}
