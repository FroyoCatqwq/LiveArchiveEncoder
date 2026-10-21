using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Encoding;
using LiveArchiveEncoder.FFmpeg;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// Picking one video and one audio stream out of a multi-stream source (v7.9).
///
/// No ffprobe here: <see cref="StreamSelection"/> is pure policy over a
/// <see cref="SourceInfo"/>, which is exactly the part worth pinning -- the
/// end-to-end suite proves the probe half on a real four-stream file.
/// </summary>
public sealed class StreamSelectionTests
{
    private static SourceStream Video(int index, string pixFmt = "yuv420p", string codec = "h264") =>
        new(index, "video", codec, "und", null,
            new StreamInfo { Width = 320, Height = 240, PixFmt = pixFmt });

    private static SourceStream Audio(int index, string codec = "ac3", string lang = "eng") =>
        new(index, "audio", codec, lang, null);

    private static SourceInfo Source(params SourceStream[] streams) => new()
    {
        Video = streams.First(s => s.Type == "video").Video!,
        Duration = 19.01,
        HasAudio = streams.Any(s => s.Type == "audio"),
        Streams = streams,
    };

    private static SourceInfo Typical() => Source(Video(0), Audio(1), Video(2), Audio(3, "aac", "jpn"));

    // ------------------------------------------------------------------- maps

    /// <summary>
    /// Null keeps the v7.8 argument byte for byte: every existing caller has to
    /// behave exactly as before.
    /// </summary>
    [Test]
    public async Task VideoMap_DefaultIsTheFirstVideoStream() =>
        await Assert.That(StreamSelection.VideoMap(null)).IsEqualTo("0:v:0");

    [Test]
    [Arguments(0)]
    [Arguments(2)]
    [Arguments(17)]
    public async Task VideoMap_PickIsAnAbsoluteIndex(int index) =>
        await Assert.That(StreamSelection.VideoMap(index)).IsEqualTo($"0:{index}");

    /// <summary>
    /// The source is input 0 in the standalone audio pass and input 1 in the
    /// inline mux, where input 0 is the raw video pipe.
    /// </summary>
    [Test]
    public async Task AudioMap_KeepsTheInputNumber()
    {
        await Assert.That(StreamSelection.AudioMap(0, null)).IsEqualTo("0:a:0");
        await Assert.That(StreamSelection.AudioMap(1, null)).IsEqualTo("1:a:0");
        await Assert.That(StreamSelection.AudioMap(0, 3)).IsEqualTo("0:3");
        await Assert.That(StreamSelection.AudioMap(1, 3)).IsEqualTo("1:3");
    }

    // -------------------------------------------------------------- validation

    [Test]
    public async Task Check_AcceptsThePicksThatExist()
    {
        SourceInfo source = Typical();
        await Assert.That(StreamSelection.Check(source, 0, 1)).IsNull();
        await Assert.That(StreamSelection.Check(source, 2, 3)).IsNull();
        await Assert.That(StreamSelection.Check(source, null, null)).IsNull();
    }

    /// <summary>An index past the last stream is refused, with the inventory.</summary>
    [Test]
    public async Task Check_RejectsAnOutOfRangeIndex()
    {
        string? error = StreamSelection.Check(Typical(), 99, null);
        await Assert.That(error).IsNotNull();
        await Assert.That(error!).Contains("--video-stream 99");
        await Assert.That(error!).Contains("streams in this source:");
    }

    /// <summary>
    /// The trap this guards: <c>-map 0:1</c> is accepted by FFmpeg and quietly
    /// encodes an audio stream as the video track. A pick of the wrong type has
    /// to be an error, never a silent fallback.
    /// </summary>
    [Test]
    public async Task Check_RejectsAnIndexOfTheWrongType()
    {
        await Assert.That(StreamSelection.Check(Typical(), 1, null)).Contains("does not name a video stream");
        await Assert.That(StreamSelection.Check(Typical(), null, 0)).Contains("does not name an audio stream");
        await Assert.That(StreamSelection.Check(Typical(), null, 2)).Contains("--audio-stream 2");
    }

    // ---------------------------------------------------------------- metadata

    /// <summary>
    /// The pixel format drives the transport format choice, so the selected
    /// stream's own metadata has to win over the first stream's.
    /// </summary>
    [Test]
    public async Task Resolve_RePointsVideoMetadataAtThePick()
    {
        SourceInfo source = Source(Video(0, "yuv420p"), Audio(1), Video(2, "yuv444p"));

        await Assert.That(StreamSelection.Resolve(source, null, null).Video.PixFmt).IsEqualTo("yuv420p");
        await Assert.That(StreamSelection.Resolve(source, 0, 1).Video.PixFmt).IsEqualTo("yuv420p");
        await Assert.That(StreamSelection.Resolve(source, 2, 1).Video.PixFmt).IsEqualTo("yuv444p");
    }

    [Test]
    public async Task Resolve_LeavesDurationAndAudioAlone()
    {
        SourceInfo source = Typical();
        SourceInfo picked = StreamSelection.Resolve(source, 2, 3);

        await Assert.That(picked.Duration).IsEqualTo(source.Duration);
        await Assert.That(picked.HasAudio).IsEqualTo(source.HasAudio);
        await Assert.That(picked.Streams.Count).IsEqualTo(source.Streams.Count);
    }

    [Test]
    public async Task Resolve_ThrowsForABadPick()
    {
        await Assert.That(() => StreamSelection.Resolve(Typical(), 1, null))
            .Throws<ArgumentException>();
        await Assert.That(() => StreamSelection.Resolve(Typical(), null, 42))
            .Throws<ArgumentException>();
    }

    // ----------------------------------------------------------------- listing

    /// <summary>
    /// The listing is what the user reads to make the pick at all, so it has to
    /// carry the index, the type and enough to tell two audio tracks apart.
    /// </summary>
    [Test]
    public async Task Describe_ShowsIndexTypeCodecAndLanguage()
    {
        string text = StreamSelection.Describe(Typical());

        await Assert.That(text).Contains("0");
        await Assert.That(text).Contains("video");
        await Assert.That(text).Contains("h264");
        await Assert.That(text).Contains("320x240");
        await Assert.That(text).Contains("jpn");
        // Every stream gets its own line, so the indexes line up with ffprobe.
        await Assert.That(text.Split('\n').Length - 1).IsEqualTo(4);
    }
}
