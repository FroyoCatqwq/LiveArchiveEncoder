using System.Text.Json;
using Lae.Core.Settings;

namespace Lae.Tests.Core;

/// <summary>
/// The settings document and the v7 converter.
///
/// <para>These tests exist because the settings file is the one artefact a user
/// has invested in: it holds years of "the bitrate I like". v7 lost parts of it
/// more than once -- a field missing from the hand-written <c>Capture</c> method
/// reset itself on the next save, and a null list crashed one read path but not
/// the other. Both are structural impossibilities here, and these tests are what
/// keeps them that way.</para>
/// </summary>
public sealed class SettingsTests
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
    };

    /// <summary>A v7 file with one of everything, so a dropped field shows up.</summary>
    private static LegacyV7Settings SampleV7() => new()
    {
        Version = 6,
        InputPath = @"C:\media\in.mkv",
        OutputPath = @"C:\media\out.avi",
        OutputIsAuto = false,
        Encoder = "mpeg4",
        Container = "avi",
        AudioEncoder = "libmp3lame",
        StartTimeSeconds = 90,
        EndTimeSeconds = 240,
        Fps = 25,
        FpsLevels = [25, 20, 15, 10],
        FpsLevelKbps = [new LegacyRung { Fps = 25, DropAboveKbps = 900, RiseBelowKbps = 700 }],
        Limit = 800,
        Recover = 600,
        Window = 2,
        Qv = 12,
        Threads = 8,
        BufferFrames = 6,
        AudioKbps = 64,
        AudioChannels = 2,
        AudioRate = 44100,
        Width = 640,
        Height = 480,
        FitMode = "fit",
        CropAnchor = "top",
        PadColor = "0x101010",
        ScaleAlgo = "lanczos",
        PixFmt = "yuv420p",
        ColorRange = "tv",
        Dither = "auto",
        Deinterlace = "yadif",
        DeintMode = "send_field",
        DeintParity = "tff",
        DeintMarkedOnly = true,
        Hwaccel = "off",
        X264Preset = "veryfast",
        X264Tune = "film",
        X264Profile = "baseline",
        X264Level = "3.1",
        Parallel = 4,
        Language = "zh",
        FfmpegSource = "btbn",
        FfmpegProxy = "http://127.0.0.1:10808",
        GhProxy = "off",
        NnediWeights = @"C:\weights\nnedi3_weights.bin",
        AutoUpdateCheck = false,
        PlayMode = "ffplay",
        PlayerPath = @"C:\bin\mpv.exe",
        Recompress = new LegacyRecompress
        {
            Enabled = true,
            Args = "-c:v libx264 -crf 28",
            Container = "mp4",
            Extra = [new LegacyPass { Args = "-c:v libopenh264 -b:v 400k", Container = "webm" }],
        },
        Subtitles = new LegacySubtitles
        {
            Mode = "copy",
            Files = [@"C:\subs\a.ass", "   ", null],
            Stream = 3,
            Codec = "ass",
            FontDir = @"C:\fonts",
            FallbackFont = "SimHei",
            BoxAspect = "4:3",
        },
        EraPreset = "sohu/2012",
        EraNative = false,
        EraAspect = "16:9",
        H264Impl = "libopenh264",
        CustomEras =
        [
            new LegacyCustomEra
            {
                Id = "My Site/2012",
                Site = "My Site",
                Year = 2012,
                Kind = "era",
                Encoder = "libx264",
                Width = 640,
                Height = 360,
                LimitKbps = 700,
                LiveQv = 4,
                LiveCrf = 26,
                Container = "mp4",
            },
        ],
    };

    [Test]
    public async Task SchemaVersionMatchesTheDocumentConstant()
    {
        await Assert.That(new SettingsDocument().SchemaVersion)
            .IsEqualTo(SettingsDocument.CurrentSchemaVersion);
    }

    /// <summary>
    /// A v8 file must survive the round trip. Value equality on a record that
    /// contains lists would compare list references, so each interesting family
    /// is asserted through its scalar members.
    /// </summary>
    [Test]
    public async Task DocumentSurvivesAJsonRoundTrip()
    {
        var original = new SettingsDocument
        {
            Toolchain = new ToolchainSettings { FfmpegSource = "gyan", FfmpegPath = @"D:\ff\ffmpeg.exe" },
            Network = new NetworkSettings { Proxy = "http://127.0.0.1:10808", GithubFront = "off" },
            Ui = new UiSettings { Language = "zh", PlayMode = "external", PlayerPath = @"C:\mpv.exe", CheckForUpdates = false },
            Defaults = new EncodeDefaults
            {
                Encoder = "libx264",
                Quality = 17,
                LimitKbps = 720,
                FpsLevels = [25, 12.5],
                Deinterlace = new DeinterlaceSettings { Filter = "yadif", FieldMode = "send_field" },
                Color = new ColorTags { Range = "tv", Space = "bt709" },
                Recompress = new RecompressChain
                {
                    Enabled = true,
                    Passes = [new RecompressPass("-c:v libx264", "mp4")],
                },
            },
        };

        string json = JsonSerializer.Serialize(original, SettingsJsonContext.Default.SettingsDocument);
        SettingsDocument? read = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.SettingsDocument);

        await Assert.That(read).IsNotNull();
        await Assert.That(read!.Toolchain.FfmpegSource).IsEqualTo("gyan");
        await Assert.That(read.Toolchain.FfmpegPath).IsEqualTo(@"D:\ff\ffmpeg.exe");
        await Assert.That(read.Network.GithubFront).IsEqualTo("off");
        await Assert.That(read.Ui.Language).IsEqualTo("zh");
        await Assert.That(read.Ui.CheckForUpdates).IsFalse();
        await Assert.That(read.Defaults.Quality).IsEqualTo(17);
        await Assert.That(read.Defaults.LimitKbps).IsEqualTo(720.0);
        await Assert.That(read.Defaults.FpsLevels.Count).IsEqualTo(2);
        await Assert.That(read.Defaults.Deinterlace.FieldMode).IsEqualTo("send_field");
        await Assert.That(read.Defaults.Color.Space).IsEqualTo("bt709");
        await Assert.That(read.Defaults.Recompress.Active().Count).IsEqualTo(1);
    }

    /// <summary>
    /// Computed members are for the program, not for the file: writing
    /// <c>"any": true</c> into a document invites someone to edit it and expect
    /// it to mean something.
    /// </summary>
    [Test]
    public async Task ComputedMembersAreNotSerialised()
    {
        string json = JsonSerializer.Serialize(new SettingsDocument(), SettingsJsonContext.Default.SettingsDocument);

        await Assert.That(json.Contains("\"any\"", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task NullSubObjectsAreRepairedOnRead()
    {
        // Exactly what a hand-edited file can say, and what System.Text.Json
        // will happily write into a non-nullable property.
        const string Json = """{ "schemaVersion": 8, "toolchain": null, "defaults": null, "customPresets": null }""";

        SettingsDocument? read = JsonSerializer.Deserialize(Json, SettingsJsonContext.Default.SettingsDocument);
        SettingsDocument repaired = read!.Normalised();

        await Assert.That(repaired.Toolchain).IsNotNull();
        await Assert.That(repaired.Defaults).IsNotNull();
        await Assert.That(repaired.CustomPresets.Count).IsEqualTo(0);
        // The default ladder has to survive the repair, or the encoder has no
        // rung to start from.
        await Assert.That(repaired.Defaults.FpsLevels.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task V7FilesAreRecognisedByTheirVersionMember()
    {
        const string V7 = """{ "Version": 6, "Fps": 25 }""";
        const string V8 = """{ "schemaVersion": 8, "defaults": {} }""";

        await Assert.That(LegacyV7Converter.LooksLikeV7(V7)).IsTrue();
        await Assert.That(LegacyV7Converter.LooksLikeV7(V8)).IsFalse();
        await Assert.That(LegacyV7Converter.LooksLikeV7("not json at all")).IsFalse();
        await Assert.That(LegacyV7Converter.LooksLikeV7("[]")).IsFalse();
    }

    /// <summary>
    /// Every v7 value that has a v8 home must arrive intact. This is the test
    /// that fails when someone adds a field to the document and forgets the
    /// converter.
    /// </summary>
    [Test]
    public async Task ConversionCarriesEverySettingsValue()
    {
        MigrationOutcome outcome = LegacyV7Converter.Convert(SampleV7());
        SettingsDocument document = outcome.Document;
        EncodeDefaults d = document.Defaults;

        await Assert.That(document.SchemaVersion).IsEqualTo(SettingsDocument.CurrentSchemaVersion);
        await Assert.That(document.Toolchain.FfmpegSource).IsEqualTo("btbn");
        await Assert.That(document.Toolchain.NnediWeights).IsEqualTo(@"C:\weights\nnedi3_weights.bin");
        await Assert.That(document.Network.Proxy).IsEqualTo("http://127.0.0.1:10808");
        await Assert.That(document.Ui.Language).IsEqualTo("zh");
        await Assert.That(document.Ui.PlayMode).IsEqualTo("ffplay");
        await Assert.That(document.Ui.PlayerPath).IsEqualTo(@"C:\bin\mpv.exe");
        await Assert.That(document.Ui.CheckForUpdates).IsFalse();

        await Assert.That(d.Encoder).IsEqualTo("mpeg4");
        await Assert.That(d.Container).IsEqualTo("avi");
        await Assert.That(d.AudioEncoder).IsEqualTo("libmp3lame");
        await Assert.That(d.Fps).IsEqualTo(25.0);
        await Assert.That(d.FpsLevels.Count).IsEqualTo(4);
        await Assert.That(d.FpsThresholds.Count).IsEqualTo(1);
        await Assert.That(d.FpsThresholds[0].DropAboveKbps).IsEqualTo(900.0);
        await Assert.That(d.LimitKbps).IsEqualTo(800.0);
        await Assert.That(d.RecoverKbps).IsEqualTo(600.0);
        await Assert.That(d.WindowSeconds).IsEqualTo(2.0);
        await Assert.That(d.Quality).IsEqualTo(12);
        await Assert.That(d.Threads).IsEqualTo(8);
        await Assert.That(d.BufferFrames).IsEqualTo(6);
        await Assert.That(d.AudioKbps).IsEqualTo(64);
        await Assert.That(d.AudioChannels).IsEqualTo(2);
        await Assert.That(d.AudioRate).IsEqualTo(44100);
        await Assert.That(d.Width).IsEqualTo(640);
        await Assert.That(d.Height).IsEqualTo(480);
        await Assert.That(d.FitMode).IsEqualTo("fit");
        await Assert.That(d.CropAnchor).IsEqualTo("top");
        await Assert.That(d.PadColor).IsEqualTo("0x101010");
        await Assert.That(d.ScaleAlgo).IsEqualTo("lanczos");
        await Assert.That(d.PixFmt).IsEqualTo("yuv420p");
        await Assert.That(d.Color.Range).IsEqualTo("tv");
        await Assert.That(d.Deinterlace.Filter).IsEqualTo("yadif");
        await Assert.That(d.Deinterlace.FieldMode).IsEqualTo("send_field");
        await Assert.That(d.Deinterlace.Parity).IsEqualTo("tff");
        await Assert.That(d.Deinterlace.MarkedOnly).IsTrue();
        await Assert.That(d.X264.Preset).IsEqualTo("veryfast");
        await Assert.That(d.X264.Tune).IsEqualTo("film");
        await Assert.That(d.X264.Profile).IsEqualTo("baseline");
        await Assert.That(d.X264.Level).IsEqualTo("3.1");
        await Assert.That(d.Parallel).IsEqualTo(4);

        // v7's H.264 implementation hint has no v8 equivalent and must not be
        // carried: a run names the encoder, so there is nothing to switch between.
        // What it must do is say so, rather than drop it in silence.
        await Assert.That(outcome.Notes.Any(n => n.Contains("H.264 implementation", StringComparison.Ordinal))).IsTrue();

        await Assert.That(d.Subtitles.Mode).IsEqualTo("copy");
        await Assert.That(d.Subtitles.Stream).IsEqualTo(3);
        await Assert.That(d.Subtitles.Codec).IsEqualTo("ass");
        await Assert.That(d.Subtitles.FontDir).IsEqualTo(@"C:\fonts");
        await Assert.That(d.Subtitles.FallbackFont).IsEqualTo("SimHei");
        await Assert.That(d.Subtitles.BoxAspect).IsEqualTo("4:3");
        // Blank and null entries are not files.
        await Assert.That(d.Subtitles.Files.Count).IsEqualTo(1);
    }

    /// <summary>
    /// "off" records a decision, so it must not be normalised into "unset": null
    /// means "never asked", and that is what lets the downloader probe a route
    /// for itself.
    /// </summary>
    [Test]
    public async Task TheOffGithubFrontSurvivesAsADecision()
    {
        MigrationOutcome outcome = LegacyV7Converter.Convert(SampleV7());
        await Assert.That(outcome.Document.Network.GithubFront).IsEqualTo("off");
    }

    /// <summary>
    /// v7 kept the first pass at the top level of the block and later passes in a
    /// list. Both have to arrive, in order, as one chain.
    /// </summary>
    [Test]
    public async Task BothRecompressShapesBecomeOneOrderedChain()
    {
        RecompressChain chain = LegacyV7Converter.Convert(SampleV7()).Document.Defaults.Recompress;

        await Assert.That(chain.Enabled).IsTrue();
        await Assert.That(chain.Passes.Count).IsEqualTo(2);
        await Assert.That(chain.Passes[0].Args).IsEqualTo("-c:v libx264 -crf 28");
        await Assert.That(chain.Passes[0].Container).IsEqualTo("mp4");
        await Assert.That(chain.Passes[1].Container).IsEqualTo("webm");
    }

    /// <summary>An empty first pass is dropped; FFmpeg with no arguments is a copy.</summary>
    [Test]
    public async Task BlankPassesAreNotRun()
    {
        var chain = new RecompressChain
        {
            Enabled = true,
            Passes = [new RecompressPass("   ", "mp4"), new RecompressPass("-c:v copy", "mkv")],
        };

        await Assert.That(chain.Active().Count).IsEqualTo(1);
        await Assert.That(chain.Active()[0].Container).IsEqualTo("mkv");
    }

    /// <summary>
    /// The per-run state v7 persisted by accident is dropped -- and said out
    /// loud. Silently losing someone's trim range would look like a bug.
    /// </summary>
    [Test]
    public async Task DroppingPerRunStateIsReported()
    {
        MigrationOutcome outcome = LegacyV7Converter.Convert(SampleV7());
        string joined = string.Join('\n', outcome.Notes);

        await Assert.That(joined.Contains("input path", StringComparison.Ordinal)).IsTrue();
        await Assert.That(joined.Contains("output path", StringComparison.Ordinal)).IsTrue();
        await Assert.That(joined.Contains("trim range", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>v7.7's boolean crop flag has to keep meaning "stretch", not "crop".</summary>
    [Test]
    public async Task LegacyCropFlagBecomesStretch()
    {
        LegacyV7Settings old = SampleV7();
        old.Crop43 = false;
        old.FitMode = null;

        MigrationOutcome outcome = LegacyV7Converter.Convert(old);

        await Assert.That(outcome.Document.Defaults.FitMode).IsEqualTo("stretch");
        await Assert.That(string.Join('\n', outcome.Notes).Contains("stretch", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// A hand-edited ladder can be unsorted, duplicated, non-finite or a mile
    /// long. One rung that can never be reached is invisible until a bandwidth
    /// test, so it is repaired on read.
    /// </summary>
    [Test]
    public async Task TheLadderIsSortedAndDeduplicated()
    {
        LegacyV7Settings old = SampleV7();
        old.FpsLevels = [5, 25, 5, -3, double.NaN, 12.5];

        MigrationOutcome outcome = LegacyV7Converter.Convert(old);
        IReadOnlyList<double> ladder = outcome.Document.Defaults.FpsLevels;

        await Assert.That(ladder.Count).IsEqualTo(3);
        await Assert.That(ladder[0]).IsEqualTo(25.0);
        await Assert.That(ladder[1]).IsEqualTo(12.5);
        await Assert.That(ladder[2]).IsEqualTo(5.0);
    }

    [Test]
    public async Task AnUnusableLadderFallsBackToTheBuiltInRungs()
    {
        LegacyV7Settings old = SampleV7();
        old.FpsLevels = [0, -1, double.NaN];

        MigrationOutcome outcome = LegacyV7Converter.Convert(old);

        await Assert.That(outcome.Document.Defaults.FpsLevels.Count).IsEqualTo(4);
        await Assert.That(string.Join('\n', outcome.Notes).Contains("built-in", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// A stored preset is plain data: the id is a slug so it can be typed on a
    /// command line, and out-of-range numbers are clamped rather than trusted.
    ///
    /// <para>v8 has no built-in catalogue, so the old row's <c>site</c> and
    /// <c>kind</c> have nowhere to live; they survive as the preset's label and
    /// nothing else. The numbers are the user's own and are kept exactly.</para>
    /// </summary>
    [Test]
    public async Task CustomPresetsAreSluggedAndClamped()
    {
        MigrationOutcome outcome = LegacyV7Converter.Convert(SampleV7());
        CustomPreset preset = outcome.Document.CustomPresets[0];

        await Assert.That(preset.Id).IsEqualTo("my_site/2012");
        await Assert.That(preset.Label).IsEqualTo("My Site 2012");
        await Assert.That(preset.RcCrf).IsEqualTo(28);
        await Assert.That(preset.RcWidth).IsEqualTo(320);
    }

    /// <summary>A preset with no usable id is dropped, and the drop is reported.</summary>
    [Test]
    public async Task ANameLessPresetIsDroppedAndReported()
    {
        LegacyV7Settings old = SampleV7();
        old.CustomEras = [new LegacyCustomEra { Id = "///" }];

        MigrationOutcome outcome = LegacyV7Converter.Convert(old);

        await Assert.That(outcome.Document.CustomPresets.Count).IsEqualTo(0);
        await Assert.That(string.Join('\n', outcome.Notes).Contains("no usable id", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// The converter must be idempotent: loading a v8 file, saving it and loading
    /// it again may not change a value. Cheap to assert, and it is what catches a
    /// "<c>with</c>" expression that quietly replaces a whole block.
    /// </summary>
    [Test]
    public async Task NormalisingIsIdempotent()
    {
        SettingsDocument once = LegacyV7Converter.Convert(SampleV7()).Document;
        SettingsDocument twice = once.Normalised();

        await Assert.That(twice.Defaults.Quality).IsEqualTo(once.Defaults.Quality);
        await Assert.That(twice.Defaults.FpsLevels.Count).IsEqualTo(once.Defaults.FpsLevels.Count);
        await Assert.That(twice.Toolchain.FfmpegSource).IsEqualTo(once.Toolchain.FfmpegSource);
        await Assert.That(twice.CustomPresets.Count).IsEqualTo(once.CustomPresets.Count);
    }

    /// <summary>
    /// A null collection nested inside the document is repaired like a top-level
    /// one.
    ///
    /// <para>The first version of <c>Normalised</c> only rebuilt its six direct
    /// members, so <c>"defaults": { "fpsLevels": null }</c> survived the read
    /// boundary as a null ladder and blew up inside whichever consumer walked it
    /// -- the same shape as the v7 crash the method exists to prevent, one level
    /// down. Every type that holds a list repairs its own.</para>
    /// </summary>
    [Test]
    public async Task ANestedNullCollectionIsRepairedToo()
    {
        const string json = """
            {
              "schemaVersion": 10,
              "defaults": {
                "fpsLevels": null,
                "fpsThresholds": null,
                "subtitles": { "files": null, "copyTracks": null },
                "recompress": { "passes": null }
              }
            }
            """;

        SettingsDocument? parsed = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.SettingsDocument);
        await Assert.That(parsed).IsNotNull();

        SettingsDocument repaired = parsed!.Normalised();

        // A missing ladder falls back to the built-in rungs rather than to an
        // empty list: an empty list would mean "never adapt", which is not what a
        // file that simply omitted the key asked for.
        await Assert.That(repaired.Defaults.FpsLevels.Count).IsEqualTo(new EncodeDefaults().FpsLevels.Count);
        await Assert.That(repaired.Defaults.FpsThresholds).IsNotNull();
        await Assert.That(repaired.Defaults.Subtitles.Files).IsNotNull();
        await Assert.That(repaired.Defaults.Subtitles.CopyTracks).IsNotNull();
        await Assert.That(repaired.Defaults.Recompress.Passes).IsNotNull();

        // And the result is usable: walking it must not throw.
        await Assert.That(repaired.Defaults.Recompress.Active().Count).IsEqualTo(0);
    }
}
