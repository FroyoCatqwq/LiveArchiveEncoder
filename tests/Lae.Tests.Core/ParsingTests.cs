using Lae.Core;

namespace Lae.Tests.Core;

/// <summary>
/// The two parsers v8 needs before it can plan anything: what the source
/// contains, and which encoders this FFmpeg build has.
///
/// <para>Both are pure, which is the whole reason they exist as separate types --
/// in v7 they were inside the methods that spawned the processes, so a parsing
/// mistake could only be found on a machine with that build and that file.</para>
/// </summary>
public sealed class ParsingTests
{
    /// <summary>Trimmed from real <c>ffmpeg -encoders</c> output, indentation included.</summary>
    private const string EncodersOutput = """
        Encoders:
         V..... = Video
         A..... = Audio
         S..... = Subtitle
         .F.... = Frame-level multithreading
         ..S... = Slice-level multithreading
         ...X.. = Codec is experimental
         ....B. = Supports draw_horiz_band
         .....D = Supports direct rendering method 10
         ------
         V....D a64multi             Multicolor charset for Commodore 64 (codec a64_multi)
         V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
         V....D mpeg4                MPEG-4 part 2
         V....X roqvideo             id RoQ video (codec roq)
         A....D aac                  AAC (Advanced Audio Coding)
         A....D libmp3lame           libmp3lame MP3 (MPEG audio layer 3)
        """;

    /// <summary>
    /// The encoder rows are indented by one space. Treating a leading space as
    /// "not an encoder" is the mistake this test exists for: it produced an empty
    /// catalogue, and the program then told the user their FFmpeg had no encoders.
    /// </summary>
    [Test]
    public async Task IndentedEncoderRowsAreRead()
    {
        List<EncoderInfo> video = EncoderCatalog.Parse(EncodersOutput);

        await Assert.That(video.Count).IsEqualTo(3);
        await Assert.That(video[0].Name).IsEqualTo("a64multi");
        await Assert.That(video[1].Name).IsEqualTo("libx264");
        await Assert.That(video[2].Name).IsEqualTo("mpeg4");
        await Assert.That(video[1].Description.Contains("H.264", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task AudioEncodersComeBackOnlyWhenAskedFor()
    {
        List<EncoderInfo> all = EncoderCatalog.Parse(EncodersOutput, videoOnly: false);

        await Assert.That(all.Any(e => e.Name == "aac")).IsTrue();
        await Assert.That(all.Any(e => e.Name == "libmp3lame")).IsTrue();
        await Assert.That(EncoderCatalog.Parse(EncodersOutput).Any(e => e.Name == "aac")).IsFalse();
    }

    /// <summary>
    /// An experimental encoder is refused by FFmpeg without <c>-strict -2</c>, so
    /// offering it would only produce a failure later.
    /// </summary>
    [Test]
    public async Task ExperimentalEncodersAreNotOffered()
    {
        await Assert.That(EncoderCatalog.Parse(EncodersOutput).Any(e => e.Name == "roqvideo")).IsFalse();
    }

    /// <summary>The archive tables decide the container, and an unknown encoder still gets one.</summary>
    [Test]
    public async Task TheContainerComesFromTheArchiveTables()
    {
        List<EncoderInfo> encoders = EncoderCatalog.Parse(EncodersOutput);
        EncoderInfo x264 = encoders.First(e => e.Name == "libx264");
        EncoderInfo mpeg4 = encoders.First(e => e.Name == "mpeg4");

        await Assert.That(mpeg4.Extension).IsEqualTo("avi");
        await Assert.That(x264.FormatName).IsNotEmpty();
    }

    [Test]
    public async Task FindingAnEncoderIgnoresCase()
    {
        List<EncoderInfo> encoders = EncoderCatalog.Parse(EncodersOutput);

        await Assert.That(EncoderCatalog.Find(encoders, "LIBX264")).IsNotNull();
        await Assert.That(EncoderCatalog.Find(encoders, "nope")).IsNull();
        await Assert.That(EncoderCatalog.Find(encoders, null)).IsNull();
    }

    private const string ProbeJson = """
        {
          "streams": [
            {
              "index": 0, "codec_type": "video", "codec_name": "h264",
              "width": 640, "height": 480, "pix_fmt": "yuv420p",
              "color_range": "tv", "color_space": "bt709",
              "duration": "3.023000"
            },
            {
              "index": 1, "codec_type": "audio", "codec_name": "aac",
              "sample_rate": "48000", "channels": 2,
              "tags": { "language": "jpn", "title": "Main" }
            },
            { "index": 2, "codec_type": "subtitle", "codec_name": "ass" }
          ],
          "format": { "duration": "3.100000", "bit_rate": "198400" }
        }
        """;

    [Test]
    public async Task TheProbeIsReadIntoSourceInfo()
    {
        SourceInfo? info = FfprobeJson.TryParse(ProbeJson, out string? error);

        await Assert.That(error).IsNull();
        await Assert.That(info).IsNotNull();
        await Assert.That(info!.Width).IsEqualTo(640);
        await Assert.That(info.Height).IsEqualTo(480);
        await Assert.That(info.PixelFormat).IsEqualTo("yuv420p");
        await Assert.That(info.ColorRange).IsEqualTo("tv");
        await Assert.That(info.HasAudio).IsTrue();
        await Assert.That(info.AudioRate).IsEqualTo(48000);
        await Assert.That(info.AudioChannels).IsEqualTo(2);
        await Assert.That(info.Streams.Count).IsEqualTo(3);

        // The video stream's own duration wins over the container's.
        await Assert.That(info.DurationSeconds).IsEqualTo(3.023);
    }

    /// <summary>
    /// A container that stores no per-stream duration (a VOB on a share is the
    /// case that forced this) falls back to the format's.
    /// </summary>
    [Test]
    public async Task TheContainerDurationIsTheFallback()
    {
        const string NoStreamDuration = """
            {
              "streams": [ { "index": 0, "codec_type": "video", "width": 720, "height": 576, "pix_fmt": "yuv420p" } ],
              "format": { "duration": "12.500000" }
            }
            """;

        SourceInfo? info = FfprobeJson.TryParse(NoStreamDuration, out _);

        await Assert.That(info!.DurationSeconds).IsEqualTo(12.5);
        await Assert.That(info.HasAudio).IsFalse();
    }

    [Test]
    public async Task ASourceWithNoVideoIsRefusedWithAReason()
    {
        const string AudioOnly = """
            { "streams": [ { "index": 0, "codec_type": "audio", "codec_name": "mp3" } ] }
            """;

        SourceInfo? info = FfprobeJson.TryParse(AudioOnly, out string? error);

        await Assert.That(info).IsNull();
        await Assert.That(error).IsEqualTo("the source has no video stream");
    }

    [Test]
    public async Task GarbageIsRefusedWithAReason()
    {
        await Assert.That(FfprobeJson.TryParse("not json", out string? error)).IsNull();
        await Assert.That(error).IsEqualTo("ffprobe did not return valid JSON");
    }
}
