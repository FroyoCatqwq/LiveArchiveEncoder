using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Encoding;
using LiveArchiveEncoder.FFmpeg;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// Subtitles (v7.9 goal 2): the parts that are pure policy -- filter registry
/// parsing, filter-argument escaping, codec naming and plan validation -- run
/// here without FFmpeg; the end-to-end suite burns a real file and checks the
/// picture changed.
/// </summary>
public sealed class SubtitleTrackTests
{
    private static SourceStream Video(int index) =>
        new(index, "video", "h264", "und", null,
            new StreamInfo { Width = 320, Height = 240, PixFmt = "yuv420p" });

    private static SourceStream Audio(int index) => new(index, "audio", "aac", "und", null);

    private static SourceStream Sub(int index, string codec) =>
        new(index, "subtitle", codec, "und", null);

    private static SourceInfo Source(params SourceStream[] streams) => new()
    {
        Video = streams.First(s => s.Type == "video").Video!,
        Duration = 19.01,
        HasAudio = true,
        Streams = streams,
    };

    /// <summary>Two video, one audio, one text subtitle, in that order.</summary>
    private static SourceInfo Typical() => Source(Video(0), Audio(1), Video(2), Sub(3, "subrip"));

    private static readonly HashSet<string> Filters = ["scale", "subtitles"];
    private static readonly HashSet<string> Encoders =
        ["ass", "srt", "subrip", "mov_text", "webvtt", "dvdsub", "xsub"];

    // -------------------------------------------------------- filter registry

    /// <summary>
    /// The regression this guards: the flag column of <c>ffmpeg -filters</c> is
    /// two characters wide in a current build, and a three-character pattern
    /// silently dropped every unflagged filter -- including <c>subtitles</c>,
    /// which is how this bug was found.
    /// </summary>
    [Test]
    public async Task ParseFilterNames_ReadsTwoAndThreeCharFlagColumns()
    {
        // Verbatim shapes: " .. subtitles" from a current build, " TS allpass"
        // from the same output, and the legend block that must not match.
        string blob = """
            Filters:
              T.. = Timeline support
              .S. = Slice threading
              ..C = Command support
              A = Audio input/output
              V = Video input/output
             .. allpass           A->A       Apply a two-pole all-pass filter.
             T. virtualbass       A->A       Audio Virtual Bass.
             .. ass               V->V       Render ASS subtitles onto input video.
             .. subtitles         V->V       Render text subtitles onto input video.
             .. scale             V->V       Scale the input video size.
            """;

        HashSet<string> found = FFmpegCapabilities.ParseFilterNames(blob);

        await Assert.That(found).Contains("allpass");
        await Assert.That(found).Contains("virtualbass");
        await Assert.That(found).Contains("ass");
        await Assert.That(found).Contains("subtitles");
        await Assert.That(found).Contains("scale");
        // The legend must not contribute entries.
        await Assert.That(found).DoesNotContain("T..");
        await Assert.That(found).DoesNotContain("=");
    }

    // --------------------------------------------------------------- escaping

    /// <summary>
    /// Both levels of filtergraph parsing treat <c>\</c> and <c>:</c> as
    /// special; the measured-safe spelling quotes the whole path, doubles the
    /// separators and escapes the drive colon.
    /// </summary>
    [Test]
    public async Task EscapeFilterPath_QuotesAndDoublesSeparators()
    {
        await Assert.That(SubtitleTrack.EscapeFilterPath(@"C:\Users\x\a.srt"))
            .IsEqualTo("'C\\:\\\\Users\\\\x\\\\a.srt'");
        await Assert.That(SubtitleTrack.EscapeFilterPath(@"D:\影视库\clip.ass"))
            .IsEqualTo("'D\\:\\\\影视库\\\\clip.ass'");
    }

    [Test]
    public async Task AppendBurn_AddsFontsdirAndKeepsOrder()
    {
        // The fonts directory may or may not exist on this machine (it is seeded
        // on first use), so assert the shape rather than the exact string.
        string vf = SubtitleTrack.AppendBurn("scale=320:240", ["a.ass", "b.ass"]);

        await Assert.That(vf).Contains("subtitles='a.ass'");
        await Assert.That(vf).Contains(",subtitles='b.ass'");
        // Always after the base chain, never instead of it.
        await Assert.That(vf).StartsWith("scale=320:240,");
    }

    // ------------------------------------------------------------------ codec

    [Test]
    [Arguments("a.ass", "ass")]
    [Arguments("a.ssa", "ass")]
    [Arguments("b.srt", "subrip")]
    [Arguments("c.vtt", "webvtt")]
    public async Task CodecForFile_FollowsTheExtension(string file, string codec) =>
        await Assert.That(SubtitleTrack.CodecForFile(file)).IsEqualTo(codec);

    // ------------------------------------------------------------------- plan

    [Test]
    public async Task Plan_NothingAskedMeansNothingPlanned()
    {
        var none = new SubtitleSettings([], null, SubtitleMode.Burn, null);
        var off = new SubtitleSettings(["a.ass"], null, SubtitleMode.Off, null);

        await Assert.That(SubtitleTrack.Plan(Typical(), none, "avi", Filters, Encoders, "ff", "x.mp4")).IsNull();
        await Assert.That(SubtitleTrack.Plan(Typical(), off, "avi", Filters, Encoders, "ff", "x.mp4")).IsNull();
    }

    [Test]
    public async Task Plan_BurnsAFileWhenTheFilterExists()
    {
        string path = Path.GetTempFileName();
        try
        {
            var ask = new SubtitleSettings([path], null, SubtitleMode.Burn, null);
            SubtitlePlan? plan = SubtitleTrack.Plan(Typical(), ask, "avi", Filters, Encoders, "ff", "x.mp4");
            await Assert.That(plan).IsNotNull();
            await Assert.That(plan!.Mode).IsEqualTo(SubtitleMode.Burn);
            await Assert.That(plan.BurnFiles).IsEquivalentTo(new[] { path });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Plan_RefusesToBurnWithoutTheFilter()
    {
        string path = Path.GetTempFileName();
        try
        {
            var ask = new SubtitleSettings([path], null, SubtitleMode.Burn, null);
            await Assert.That(() => SubtitleTrack.Plan(
                    Typical(), ask, "avi", ["scale"], Encoders, "ff", "x.mp4"))
                .Throws<ArgumentException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Plan_RefusesBitmapSubtitles()
    {
        // dvdsub is a subpicture: carrying it works, rendering it is OCR.
        SourceInfo source = Source(Video(0), Sub(1, "dvdsub"));
        var burn = new SubtitleSettings([], 1, SubtitleMode.Burn, null);
        var copy = new SubtitleSettings([], 1, SubtitleMode.Copy, null);

        await Assert.That(() => SubtitleTrack.Plan(
                source, burn, "avi", Filters, Encoders, "ff", "x.mp4"))
            .Throws<ArgumentException>();

        SubtitlePlan? carried = SubtitleTrack.Plan(source, copy, "avi", Filters, Encoders, "ff", "x.mp4");
        await Assert.That(carried).IsNotNull();
        await Assert.That(carried!.CopyCodec).IsEqualTo("xsub");
    }

    [Test]
    public async Task Plan_CopyPicksTheContainerCodecFromTheBuild()
    {
        SourceInfo source = Source(Video(0), Audio(1), Sub(2, "ass"));
        var ask = new SubtitleSettings([], 2, SubtitleMode.Copy, null);

        // Matroska prefers ass; the build has it.
        SubtitlePlan? mkv = SubtitleTrack.Plan(source, ask, "matroska", Filters, Encoders, "ff", "x.mkv");
        await Assert.That(mkv!.CopyCodec).IsEqualTo("ass");
        await Assert.That(mkv.CopyStream).IsEqualTo(2);

        // MP4 only holds mov_text; without --subtitle-codec that is the pick.
        SubtitlePlan? mp4 = SubtitleTrack.Plan(source, ask, "mp4", Filters, Encoders, "ff", "x.mp4");
        await Assert.That(mp4!.CopyCodec).IsEqualTo("mov_text");
    }

    [Test]
    public async Task Plan_CopyFallsBackThroughTheContainerPreferences()
    {
        // This build cannot write ass, so Matroska's next choice wins.
        SourceInfo source = Source(Video(0), Audio(1), Sub(2, "subrip"));
        var ask = new SubtitleSettings([], 2, SubtitleMode.Copy, null);

        SubtitlePlan? plan = SubtitleTrack.Plan(
            source, ask, "matroska", Filters, ["srt", "subrip"], "ff", "x.mkv");
        await Assert.That(plan!.CopyCodec).IsEqualTo("srt");
    }

    [Test]
    public async Task Plan_CopyRefusesAContainerWithoutSubtitles()
    {
        var ask = new SubtitleSettings(["a.srt"], null, SubtitleMode.Copy, null);
        // rm, flv, asf, roq, amv: the archive formats have no subtitle track.
        foreach (string format in new[] { "rm", "flv", "asf", "roq", "amv" })
        {
            await Assert.That(() => SubtitleTrack.Plan(
                    Typical(), ask, format, Filters, Encoders, "ff", "x.mkv"))
                .Throws<ArgumentException>();
        }
    }

    [Test]
    public async Task Plan_CopyRefusesACodecTheBuildLacks()
    {
        var ask = new SubtitleSettings(["a.srt"], null, SubtitleMode.Copy, "fancy");
        await Assert.That(() => SubtitleTrack.Plan(
                Typical(), ask, "matroska", Filters, Encoders, "ff", "x.mkv"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Plan_RefusesTwoSourcesAtOnce()
    {
        var ask = new SubtitleSettings(["a.srt"], 3, SubtitleMode.Burn, null);
        await Assert.That(() => SubtitleTrack.Plan(
                Typical(), ask, "avi", Filters, Encoders, "ff", "x.mp4"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Plan_RefusesAnUnknownOrWrongTypeStream()
    {
        await Assert.That(() => SubtitleTrack.Plan(
                Typical(), new SubtitleSettings([], 9, SubtitleMode.Burn, null),
                "avi", Filters, Encoders, "ff", "x.mp4"))
            .Throws<ArgumentException>();
        // Stream 2 is the second video track, not a subtitle.
        await Assert.That(() => SubtitleTrack.Plan(
                Typical(), new SubtitleSettings([], 2, SubtitleMode.Copy, null),
                "matroska", Filters, Encoders, "ff", "x.mkv"))
            .Throws<ArgumentException>();
    }

    // ------------------------------------------------------------------- misc

    [Test]
    public async Task ParseMode_MapsTheThreeWords()
    {
        await Assert.That(SubtitleTrack.ParseMode("burn")).IsEqualTo(SubtitleMode.Burn);
        await Assert.That(SubtitleTrack.ParseMode("copy")).IsEqualTo(SubtitleMode.Copy);
        await Assert.That(SubtitleTrack.ParseMode("off")).IsEqualTo(SubtitleMode.Off);
    }

    [Test]
    public async Task ParseMode_UnknownMeansOff() =>
        await Assert.That(SubtitleTrack.ParseMode("squash")).IsEqualTo(SubtitleMode.Off);

    // -------------------------------------------------------- timeline shift

    /// <summary>
    /// v7.9.8.4: a run that starts at 00:22:45 hands FFmpeg <c>-ss 1365</c>,
    /// which rebases the picture to 00:00:00, while the subtitle file still
    /// counts from the beginning of the source. Without the shift the whole
    /// track lands 22 minutes late and no line is ever drawn -- which is exactly
    /// what a user reported.
    /// </summary>
    [Test]
    public async Task BurnInto_MovesTheBurnFilterOntoTheSourceClock()
    {
        var plan = new SubtitlePlan { Mode = SubtitleMode.Burn, BurnFiles = ["a.ass"] };

        VideoFilter shifted = SubtitleTrack.BurnInto("scale=320:240", plan, 320, 240, "yuv420p", "0:v", 1365);

        await Assert.That(shifted.Chain).StartsWith("scale=320:240,setpts=PTS+1365.000000/TB,subtitles=");
        // The offset has to come back off: "-t <duration>" is measured on the
        // timestamps that leave the chain, and leaving them 1365s ahead ends the
        // run on its first frame.
        await Assert.That(shifted.Chain).EndsWith(",setpts=PTS-1365.000000/TB");
    }

    [Test]
    public async Task BurnInto_FromTheStartChangesNothing()
    {
        var plan = new SubtitlePlan { Mode = SubtitleMode.Burn, BurnFiles = ["a.ass"] };

        VideoFilter plain = SubtitleTrack.BurnInto("scale=320:240", plan, 320, 240, "yuv420p", "0:v", 0);

        await Assert.That(plain.Chain).StartsWith("scale=320:240,subtitles=");
        await Assert.That(plain.Chain).DoesNotContain("setpts");
    }

    /// <summary>
    /// The boxed form renders inside a cropped branch of a filtergraph; the
    /// shift has to travel with the burn filter into that branch, or the text
    /// silently keeps the segment's clock while the picture has the source's.
    /// </summary>
    [Test]
    public async Task BurnInto_ShiftsInsideTheBoxGraphToo()
    {
        var plan = new SubtitlePlan
        {
            Mode = SubtitleMode.Burn,
            BurnFiles = ["a.ass"],
            BoxAspect = (4, 3),
        };

        VideoFilter filter = SubtitleTrack.BurnInto("scale=640:360", plan, 640, 360, "yuv420p", "0:v", 90);

        await Assert.That(filter.IsGraph).IsTrue();
        await Assert.That(filter.Graph).Contains("setpts=PTS+90.000000/TB,subtitles=");
        await Assert.That(filter.Graph).Contains(",setpts=PTS-90.000000/TB");
    }
}
