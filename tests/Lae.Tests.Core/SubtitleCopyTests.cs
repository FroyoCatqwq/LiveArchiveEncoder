using Lae.Core;

namespace Lae.Tests.Core;

/// <summary>
/// Carrying a subtitle track into the output, which is the only thing that works
/// for a bitmap track and the only thing that keeps the original cue timing.
///
/// <para>The table is the feature. A run that asks for a PGS track in an MP4 is
/// asking for something no muxer can do, and the answer has to name the container
/// that can -- before the encode, not two seconds into it.</para>
/// </summary>
public sealed class SubtitleCopyTests
{
    private static SourceInfo Source()
    {
        var streams = new List<SourceStream>
        {
            new(0, "video", "h264", null, null, new StreamInfo { Width = 1920, Height = 1080 }),
            new(1, "audio", "aac", "jpn", null),
            new(2, "subtitle", "hdmv_pgs_subtitle", "chi", "simplified"),
            new(3, "subtitle", "hdmv_pgs_subtitle", "chi", "traditional"),
        };

        return new SourceInfo
        {
            DurationSeconds = 60,
            Width = 1920,
            Height = 1080,
            PixelFormat = "yuv420p",
            HasAudio = true,
            AudioRate = 48000,
            AudioChannels = 2,
            Streams = streams,
        };
    }

    [Test]
    public async Task MatroskaKeepsABitmapTrackAsItIs()
    {
        (string? codec, string? problem) = SubtitleCopy.Resolve("mkv", "hdmv_pgs_subtitle", 2);

        await Assert.That(codec).IsEqualTo("copy");
        await Assert.That(problem).IsNull();
    }

    [Test]
    [Arguments("mp4", "subrip", "mov_text")]
    [Arguments("mov", "ass", "mov_text")]
    [Arguments("webm", "subrip", "webvtt")]
    [Arguments("mkv", "subrip", "copy")]
    public async Task ATextTrackIsConvertedWhereItMustBeAndCopiedWhereItCanBe(
        string container, string sourceCodec, string expected)
    {
        (string? codec, string? problem) = SubtitleCopy.Resolve(container, sourceCodec, 2);

        await Assert.That(problem).IsNull();
        await Assert.That(codec).IsEqualTo(expected);
    }

    /// <summary>
    /// The case this feature exists for: a WEB-DL with PGS tracks, aimed at an
    /// MP4. The refusal has to say "mkv", because that is the answer.
    /// </summary>
    [Test]
    public async Task ABitmapTrackCannotGoIntoMp4AndTheRefusalSaysMkv()
    {
        (string? codec, string? problem) = SubtitleCopy.Resolve("mp4", "hdmv_pgs_subtitle", 3);

        await Assert.That(codec).IsNull();
        await Assert.That(problem).IsNotNull();
        await Assert.That(problem!).Contains("track 3");
        await Assert.That(problem!).Contains("mkv");
    }

    [Test]
    [Arguments("avi")]
    [Arguments("flv")]
    [Arguments("mpeg")]
    [Arguments("rm")]
    [Arguments("asf")]
    public async Task ContainersWithoutASubtitleMuxerAreRefused(string container)
    {
        (string? codec, string? problem) = SubtitleCopy.Resolve(container, "subrip", 2);

        await Assert.That(codec).IsNull();
        await Assert.That(problem!).Contains("no subtitle muxer");
    }

    [Test]
    public async Task EveryNamedTrackIsCarried()
    {
        (IReadOnlyList<SubtitleCarry> tracks, IReadOnlyList<string> problems) =
            SubtitleCopy.Resolve("mkv", Source(), [2, 3]);

        await Assert.That(problems.Count).IsEqualTo(0);
        await Assert.That(tracks.Count).IsEqualTo(2);
        await Assert.That(tracks[0].Index).IsEqualTo(2);
        await Assert.That(tracks[1].Index).IsEqualTo(3);
        await Assert.That(tracks.All(t => t.Codec == "copy")).IsTrue();
    }

    /// <summary>
    /// The track indices are absolute, so naming the video stream is the same
    /// mistake as naming it for a burn -- and it has to be caught by the same
    /// rule, not by FFmpeg later.
    /// </summary>
    [Test]
    public async Task NamingANonSubtitleStreamIsAProblem()
    {
        (IReadOnlyList<SubtitleCarry> tracks, IReadOnlyList<string> problems) =
            SubtitleCopy.Resolve("mkv", Source(), [0]);

        await Assert.That(tracks.Count).IsEqualTo(0);
        await Assert.That(problems.Count).IsEqualTo(1);
        await Assert.That(problems[0]).Contains("that stream is video");
    }

    /// <summary>Asking for the same track twice is one track, not an error.</summary>
    [Test]
    public async Task ADuplicateIndexIsCollapsed()
    {
        (IReadOnlyList<SubtitleCarry> tracks, _) = SubtitleCopy.Resolve("mkv", Source(), [2, 2]);

        await Assert.That(tracks.Count).IsEqualTo(1);
    }
}
