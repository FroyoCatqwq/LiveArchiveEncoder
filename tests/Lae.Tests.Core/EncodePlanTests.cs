using Lae.Core;
using Lae.Core.Settings;

namespace Lae.Tests.Core;

/// <summary>
/// The planning step: request + source + encoder, in; two FFmpeg command lines, out.
///
/// <para>This is the part of v7 that could only be tested by running a real
/// encode, because deciding and spawning lived in the same 465-line method. Here
/// the decisions are a pure function, so the argument layout can be pinned
/// exactly -- which is what lets the v7-vs-v8 comparison matrix compare commands
/// before it compares pixels.</para>
/// </summary>
public sealed class EncodePlanTests
{
    private static EncoderInfo Encoder(string name = "mpeg4", string ext = "avi", string format = "avi") => new()
    {
        Name = name,
        Extension = ext,
        FormatName = format,
        Codec = name,
        Family = "mpeg4",
    };

    private static SourceInfo Source(bool audio = true) => new()
    {
        DurationSeconds = 300,
        Width = 640,
        Height = 480,
        PixelFormat = "yuv420p",
        HasAudio = audio,
        AudioRate = 48000,
        AudioChannels = 2,
        Streams = audio
            ? [
                new SourceStream(0, "video", "h264", null, null, new StreamInfo { Width = 640, Height = 480 }),
                new SourceStream(1, "audio", "aac", "jpn", null),
            ]
            : [new SourceStream(0, "video", "h264", null, null, new StreamInfo { Width = 640, Height = 480 })],
    };

    private static EncodeRequest Request(EncodeDefaults? encode = null) => new()
    {
        SourcePath = "/media/in.mkv",
        DestinationPath = "/media/out.avi",
        Encode = encode ?? new EncodeDefaults { Encoder = "mpeg4", Quality = 8 },
    };

    /// <summary>The same source, with subtitle tracks added at the given indices.</summary>
    private static SourceInfo WithSubs(params (int Index, string Codec)[] tracks)
    {
        SourceInfo plain = Source();
        return plain with
        {
            Streams =
            [
                .. plain.Streams,
                .. tracks.Select(t => new SourceStream(t.Index, "subtitle", t.Codec, "chi", null)),
            ],
        };
    }

    /// <summary>
    /// Finds a contiguous run of arguments. Pinning a whole command line would
    /// break on every unrelated addition; pinning the order of the parts that
    /// matter is what catches a real regression.
    /// </summary>
    private static bool HasRun(IReadOnlyList<string> args, params string[] expected)
    {
        for (int start = 0; start + expected.Length <= args.Count; start++)
        {
            bool match = true;
            for (int i = 0; i < expected.Length; i++)
            {
                if (args[start + i] != expected[i]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }

    [Test]
    public async Task APlainRunProducesTwoCommandsThatAgree()
    {
        EncodePlan plan = EncodePlan.Create(Request(), Source(audio: false), Encoder());

        await Assert.That(plan.IsRunnable).IsTrue();
        await Assert.That(plan.TransportPixelFormat).IsEqualTo("yuv420p");
        // The archive default frame size, not a modern one.
        await Assert.That(plan.Width).IsEqualTo(Constants.DefaultWidth);
        await Assert.That(plan.Height).IsEqualTo(Constants.DefaultHeight);
        await Assert.That(plan.Container).IsEqualTo("avi");
        await Assert.That(plan.Muxer).IsEqualTo("avi");
        await Assert.That(plan.SpanSeconds).IsEqualTo(300.0);

        // The decoder writes raw frames; the encoder reads them. Both sides must
        // name the same format and size or the pipe is meaningless.
        await Assert.That(HasRun(plan.DecoderArgs, "-pix_fmt", "yuv420p", "-f", "rawvideo", "pipe:1")).IsTrue();
        await Assert.That(HasRun(plan.EncoderArgs, "-f", "rawvideo", "-pix_fmt", "yuv420p")).IsTrue();
        await Assert.That(HasRun(plan.EncoderArgs, "-s:v", "256x192")).IsTrue();

        // The encoder side reads the pipe, then encodes, then names the muxer.
        await Assert.That(HasRun(plan.EncoderArgs, "-i", "pipe:0")).IsTrue();
        await Assert.That(HasRun(plan.EncoderArgs, "-c:v", "mpeg4")).IsTrue();
        await Assert.That(HasRun(plan.EncoderArgs, "-q:v", "8")).IsTrue();
        await Assert.That(HasRun(plan.EncoderArgs, "-f", "avi", "/media/out.avi")).IsTrue();

        // No audio in the source: no second read, no audio map.
        await Assert.That(plan.HasAudio).IsFalse();
        await Assert.That(plan.EncoderArgs.Contains("-c:a")).IsFalse();
    }

    /// <summary>
    /// Seeking happens on the decoder's input (<c>-ss</c> before <c>-i</c>, which
    /// decodes from the requested point instead of discarding frames), and the
    /// length is capped after the input.
    /// </summary>
    [Test]
    public async Task TheTrimRangeLandsInTheRightPlaces()
    {
        EncodeRequest request = Request() with { StartSeconds = 60, EndSeconds = 200 };
        EncodePlan plan = EncodePlan.Create(request, Source(audio: false), Encoder());

        await Assert.That(plan.SpanSeconds).IsEqualTo(140.0);
        await Assert.That(HasRun(plan.DecoderArgs, "-ss", "60.000000", "-i", "/media/in.mkv")).IsTrue();
        await Assert.That(HasRun(plan.DecoderArgs, "-i", "/media/in.mkv", "-t", "140.000000")).IsTrue();
    }

    /// <summary>
    /// The pipe carries video only, so audio is read from the source a second
    /// time inside the encoder. Both the input and the map have to be there or
    /// the output is silent.
    /// </summary>
    [Test]
    public async Task AudioIsReadASecondTimeAndMapped()
    {
        EncodePlan plan = EncodePlan.Create(Request(), Source(audio: true), Encoder());

        await Assert.That(plan.HasAudio).IsTrue();
        await Assert.That(HasRun(plan.EncoderArgs, "-i", "pipe:0", "-i", "/media/in.mkv")).IsTrue();
        await Assert.That(HasRun(plan.EncoderArgs, "-map", "0:v:0")).IsTrue();
        await Assert.That(HasRun(plan.EncoderArgs, "-map", "1:a")).IsTrue();
        await Assert.That(HasRun(plan.EncoderArgs, "-c:a", "libmp3lame")).IsTrue();
        await Assert.That(HasRun(plan.EncoderArgs, "-ac", "1")).IsTrue();
    }

    /// <summary>A named stream is addressed by its absolute index, the number the user typed.</summary>
    [Test]
    public async Task AStreamPickBecomesAnAbsoluteMap()
    {
        EncodeRequest request = Request() with { VideoStream = 2, AudioStream = 3 };
        var source = new SourceInfo
        {
            DurationSeconds = 60,
            Width = 640,
            Height = 480,
            HasAudio = true,
            AudioRate = 48000,
            AudioChannels = 2,
            Streams =
            [
                new SourceStream(0, "video", "h264", null, null, new StreamInfo()),
                new SourceStream(1, "audio", "aac", null, null),
                new SourceStream(2, "video", "mjpeg", null, null, new StreamInfo()),
                new SourceStream(3, "audio", "ac3", null, null),
            ],
        };

        EncodePlan plan = EncodePlan.Create(request, source, Encoder());

        await Assert.That(plan.IsRunnable).IsTrue();
        await Assert.That(HasRun(plan.DecoderArgs, "-map", "0:2")).IsTrue();
        await Assert.That(HasRun(plan.EncoderArgs, "-map", "1:3")).IsTrue();
    }

    /// <summary>A stale index that names the wrong kind of stream is a refusal, not a guess.</summary>
    [Test]
    public async Task APickOfTheWrongKindIsRefused()
    {
        EncodeRequest request = Request() with { VideoStream = 1 };
        EncodePlan plan = EncodePlan.Create(request, Source(audio: true), Encoder());

        await Assert.That(plan.IsRunnable).IsFalse();
        await Assert.That(string.Join('\n', plan.Problems).Contains("not video", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// An explicit container the encoder cannot write is refused with the legal
    /// list. Silently substituting the default would produce a file whose
    /// extension contradicts its contents.
    /// </summary>
    [Test]
    public async Task AnImpossibleContainerNamesTheAlternatives()
    {
        EncodeRequest request = Request() with { Encode = new EncodeDefaults { Encoder = "mpeg4", Container = "flv" } };
        EncodePlan plan = EncodePlan.Create(request, Source(audio: false), Encoder());

        await Assert.That(plan.IsRunnable).IsFalse();
        string joined = string.Join('\n', plan.Problems);
        await Assert.That(joined.Contains("cannot write 'flv'", StringComparison.Ordinal)).IsTrue();
        await Assert.That(joined.Contains("avi", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// An encode writes to <c>name.part.ext</c> and is renamed afterwards, so the
    /// run retargets the plan instead of building a second one. Exactly one
    /// argument may move: the output path, which is the last one by construction.
    /// </summary>
    /// <summary>
    /// A trimmed run has to end where the trim says, and the audio is the half
    /// that does not end by itself.
    ///
    /// <para>The pump stops feeding video and the encoder sees its pipe close; the
    /// audio arrives from a second read of the source and keeps going to the end
    /// of the file. Without a cap the container duration follows the audio, so a
    /// one-second excerpt comes out three seconds long. This was found by an
    /// end-to-end run of a trimmed encode, not by reading the arguments.</para>
    /// </summary>
    [Test]
    public async Task ATrimmedRunCapsTheSecondInputToo()
    {
        EncodeRequest request = Request() with { StartSeconds = 1, EndSeconds = 2 };
        EncodePlan plan = EncodePlan.Create(request, Source(audio: true), Encoder());

        await Assert.That(plan.IsRunnable).IsTrue();
        await Assert.That(plan.SpanSeconds).IsEqualTo(1.0);
        await Assert.That(HasRun(plan.EncoderArgs, "-t", "1.000000")).IsTrue();
    }

    /// <summary>
    /// Without audio there is nothing to cap: a <c>-t</c> that only bounds the
    /// pipe would shorten nothing and give a second reason to truncate.
    /// </summary>
    [Test]
    public async Task ASilentRunNeedsNoCap()
    {
        EncodePlan plan = EncodePlan.Create(Request(), Source(audio: false), Encoder());

        await Assert.That(plan.EncoderArgs.Contains("-t")).IsFalse();
    }

    [Test]
    public async Task RetargetingMovesOnlyTheOutputPath()
    {
        EncodePlan plan = EncodePlan.Create(Request(), Source(audio: false), Encoder());
        EncodePlan part = plan.WritingTo("/media/out.part.avi");

        await Assert.That(part.DestinationPath).IsEqualTo("/media/out.part.avi");
        await Assert.That(part.EncoderArgs[^1]).IsEqualTo("/media/out.part.avi");
        await Assert.That(plan.DestinationPath).IsEqualTo("/media/out.avi");

        // Everything before the path is untouched, and the original plan is not
        // mutated by the copy.
        await Assert.That(part.EncoderArgs.Count).IsEqualTo(plan.EncoderArgs.Count);
        await Assert.That(part.EncoderArgs.Take(plan.EncoderArgs.Count - 1).ToArray())
            .IsEquivalentTo(plan.EncoderArgs.Take(plan.EncoderArgs.Count - 1).ToArray());
        await Assert.That(plan.EncoderArgs[^1]).IsEqualTo("/media/out.avi");
    }

    [Test]
    public async Task AChosenContainerSelectsItsMuxer()
    {
        EncodeRequest request = Request() with { Encode = new EncodeDefaults { Encoder = "mpeg4", Container = "mkv" } };
        request = request with { DestinationPath = "/media/out.mkv" };
        EncodePlan plan = EncodePlan.Create(request, Source(audio: false), Encoder());

        await Assert.That(plan.IsRunnable).IsTrue();
        await Assert.That(plan.Container).IsEqualTo("mkv");
        await Assert.That(plan.Muxer).IsEqualTo("matroska");
        await Assert.That(HasRun(plan.EncoderArgs, "-f", "matroska", "/media/out.mkv")).IsTrue();
    }

    /// <summary>
    /// With no output named, the plan picks the name -- and the extension is the
    /// container it settled on, not a guess made before the encoder was known.
    /// The old order wrote an MP4 into a file called in.avi.
    /// </summary>
    [Test]
    public async Task ADerivedNameUsesTheContainerThePlanChose()
    {
        EncodeRequest request = Request(new EncodeDefaults { Encoder = "libx264" })
            with { DestinationPath = null };

        EncodePlan plan = EncodePlan.Create(request, Source(audio: false), Encoder("libx264", "mp4", "mp4"));

        await Assert.That(plan.IsRunnable).IsTrue();
        await Assert.That(plan.Container).IsEqualTo("mp4");
        await Assert.That(Path.GetFileName(plan.DestinationPath)).IsEqualTo("in.mp4");

        // The encoder command line ends with the same path, which is what makes
        // WritingTo exact.
        await Assert.That(plan.EncoderArgs[^1]).IsEqualTo(plan.DestinationPath);
    }

    /// <summary>
    /// A derived name can land on the source -- an .avi encoded to mpeg4 is the
    /// obvious case -- and that is a refusal with instructions, not an overwrite.
    /// </summary>
    [Test]
    public async Task ADerivedNameThatCollidesWithTheSourceIsRefused()
    {
        EncodeRequest request = Request() with
        {
            SourcePath = "/media/in.avi",
            DestinationPath = null,
        };

        EncodePlan plan = EncodePlan.Create(request, Source(audio: false), Encoder());

        await Assert.That(plan.IsRunnable).IsFalse();
        await Assert.That(plan.Problems.Any(p => p.Contains("--out", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>
    /// A window of zero does not mean "measure instantly": the ladder's window
    /// refuses to be built from it, and that refusal would arrive after both
    /// children had been started. It is caught at validation instead.
    /// </summary>
    [Test]
    public async Task AWindowOfZeroIsRefusedBeforeAnythingStarts()
    {
        EncodeRequest request = Request(new EncodeDefaults { Encoder = "mpeg4", WindowSeconds = 0 });

        EncodePlan plan = EncodePlan.Create(request, Source(), Encoder());

        await Assert.That(plan.IsRunnable).IsFalse();
        await Assert.That(plan.Problems.Any(p => p.Contains("window", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task ABufferOfZeroIsRefusedBeforeAnythingStarts()
    {
        EncodeRequest request = Request(new EncodeDefaults { Encoder = "mpeg4", BufferFrames = 0 });

        EncodePlan plan = EncodePlan.Create(request, Source(), Encoder());

        await Assert.That(plan.IsRunnable).IsFalse();
        await Assert.That(plan.Problems.Any(p => p.Contains("buffer", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>
    /// The recompress chain does not belong to the live plan and must not disturb
    /// it: the hops run after the recording exists, read it, and never appear in
    /// either of the two capture command lines.
    ///
    /// <para>v7.9.8.6 shipped the opposite mistake -- the stored chain was merged
    /// into the run, so every plain encode silently recompressed itself. What is
    /// asserted here is that arming a chain changes nothing about the capture;
    /// <c>RecompressPlan</c> is what turns it into work.</para>
    /// </summary>
    [Test]
    public async Task AnArmedChainDoesNotDisturbTheLivePlan()
    {
        EncodeRequest plain = Request() with
        {
            Encode = new EncodeDefaults { Encoder = "mpeg4" },
        };

        EncodeRequest armed = plain with
        {
            Encode = plain.Encode with
            {
                Recompress = new RecompressChain { Enabled = true, Passes = [new RecompressPass("-c:v copy", "mp4")] },
            },
        };

        EncodePlan before = EncodePlan.Create(plain, Source(), Encoder());
        EncodePlan after = EncodePlan.Create(armed, Source(), Encoder());

        await Assert.That(after.IsRunnable).IsTrue();
        await Assert.That(after.DestinationPath).IsEqualTo(before.DestinationPath);
        await Assert.That(after.DecoderArgs.SequenceEqual(before.DecoderArgs)).IsTrue();
        await Assert.That(after.EncoderArgs.SequenceEqual(before.EncoderArgs)).IsTrue();
    }

    /// <summary>
    /// Subtitles, by contrast, are wired: the burn lands on the decoder's filter
    /// chain, so the encoder command is unchanged and the picture is what
    /// differs.
    /// </summary>
    [Test]
    public async Task ABurnedSubtitleReachesTheDecoderChainAndNotTheEncoder()
    {
        EncodeRequest request = Request() with
        {
            Encode = new EncodeDefaults
            {
                Encoder = "mpeg4",
                Subtitles = new SubtitleDefaults { Mode = "burn", Files = ["/s/a.ass"] },
            },
        };

        EncodePlan plan = EncodePlan.Create(request, Source(), Encoder());

        await Assert.That(plan.IsRunnable).IsTrue();
        await Assert.That(plan.DecoderArgs.Any(a => a.Contains("subtitles=filename=", StringComparison.Ordinal))).IsTrue();
        await Assert.That(plan.EncoderArgs.Any(a => a.Contains("subtitles", StringComparison.Ordinal))).IsFalse();
    }

    /// <summary>
    /// A frame size the transport format cannot represent is rounded, and said
    /// out loud. The pacing loop counts pipe bytes using this size, so it has to
    /// be the size FFmpeg actually emits.
    /// </summary>
    [Test]
    public async Task AnIllegalFrameSizeIsRoundedAndReported()
    {
        EncodeRequest request = Request() with
        {
            Encode = new EncodeDefaults { Encoder = "mpeg4", Width = 321, Height = 241 },
        };
        EncodePlan plan = EncodePlan.Create(request, Source(audio: false), Encoder());

        await Assert.That(plan.Width % 2).IsEqualTo(0);
        await Assert.That(plan.Height % 2).IsEqualTo(0);
        await Assert.That(plan.Notes.Count).IsGreaterThan(0);
        await Assert.That(string.Join('\n', plan.Notes).Contains("321x241", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>The pacing target and the pipe frame size are what the pump needs to know.</summary>
    [Test]
    public async Task FrameCountAndSizeMatchThePlan()
    {
        EncodePlan plan = EncodePlan.Create(Request(), Source(audio: false), Encoder());

        // 300 s at 13 fps.
        await Assert.That(plan.TargetFrames).IsEqualTo(3900L);
        // yuv420p is 1.5 bytes per pixel at whatever size the plan settled on.
        await Assert.That(plan.FrameSize).IsEqualTo(plan.Width * plan.Height * 3 / 2);

        await Assert.That(plan.DecodeCommand("ffmpeg")[0]).IsEqualTo("ffmpeg");
        await Assert.That(plan.DecodeCommand("ffmpeg").Count).IsEqualTo(plan.DecoderArgs.Count + 1);
    }

    /// <summary>
    /// A CRF encoder is pinned to the ceiling with VBV; a bitrate-only encoder is
    /// told a bitrate and nothing else. Both were measured the hard way.
    /// </summary>
    [Test]
    public async Task TheQualityFlagsFollowTheEncoderScale()
    {
        EncodeRequest request = Request() with
        {
            Encode = new EncodeDefaults { Encoder = "libx264", Quality = 20, LimitKbps = 480 },
        };

        EncodePlan plan = EncodePlan.Create(
            request, Source(audio: false), Encoder("libx264", "mp4", "mp4"));

        await Assert.That(HasRun(plan.EncoderArgs, "-crf", "20")).IsTrue();
        await Assert.That(HasRun(plan.EncoderArgs, "-maxrate", "480k")).IsTrue();
        await Assert.That(HasRun(plan.EncoderArgs, "-bufsize", "960k")).IsTrue();

        // VBV stays off the mpeg family: a fixed quantiser and a tight buffer are
        // contradictory requests, and FFmpeg reports "rc buffer underflow" on
        // every frame while overshooting anyway.
        EncodePlan mpeg = EncodePlan.Create(Request(), Source(audio: false), Encoder());
        await Assert.That(mpeg.EncoderArgs.Contains("-maxrate")).IsFalse();
        await Assert.That(mpeg.EncoderArgs.Contains("-bufsize")).IsFalse();
    }

    /// <summary>
    /// The plan carries the ladder, and the ladder is built from the settings the
    /// plan was made from -- the argument list never spells a ceiling, so this is
    /// the only place the run could learn it.
    /// </summary>
    [Test]
    public async Task TheLadderComesFromTheSettingsThePlanWasMadeFrom()
    {
        EncodeRequest request = Request() with
        {
            Encode = new EncodeDefaults
            {
                Encoder = "mpeg4", LimitKbps = 120, RecoverKbps = 90, FpsLevels = [13, 9, 4],
            },
        };

        EncodePlan plan = EncodePlan.Create(request, Source(), Encoder());

        await Assert.That(plan.BuildLadder()).IsNotNull();
        await Assert.That(string.Join(',', plan.BuildLadder()!.Rungs.Select(r => r.Fps))).IsEqualTo("13,9,4");
        await Assert.That(plan.BuildLadder()!.Rungs[1].DropAboveKbps).IsEqualTo(120.0);
    }

    /// <summary>
    /// A recovery rate that is not below the ceiling is clamped, and the clamp is
    /// reported: the ladder has no usable gap otherwise, and quietly using a
    /// different number than the one typed is how a setting stops meaning
    /// anything.
    /// </summary>
    [Test]
    public async Task AnImpossibleRecoveryRateIsClampedAndReported()
    {
        EncodeRequest request = Request() with
        {
            Encode = new EncodeDefaults { Encoder = "mpeg4", LimitKbps = 60, RecoverKbps = 150 },
        };

        EncodePlan plan = EncodePlan.Create(request, Source(), Encoder());

        await Assert.That(plan.BuildLadder()!.Rungs[1].RiseBelowKbps).IsEqualTo(45.0);
        await Assert.That(string.Join('\n', plan.Notes).Contains("recovery", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>The encoder reports its own output on stdout, which is where the ladder reads it.</summary>
    [Test]
    public async Task TheEncoderIsAskedForItsProgressStream()
    {
        EncodePlan plan = EncodePlan.Create(Request(), Source(), Encoder());

        await Assert.That(HasRun(plan.EncoderArgs, "-progress", "pipe:1")).IsTrue();

        // And -y, or one killed run leaves a part file that blocks the next one.
        await Assert.That(plan.EncoderArgs[0]).IsEqualTo("-hide_banner");
        await Assert.That(plan.EncoderArgs.Contains("-y")).IsTrue();
    }

    /// <summary>
    /// A carried track comes out of the second input, by absolute index, and one
    /// <c>-c:s</c> covers the lot.
    ///
    /// <para>Matroska is the container that can hold a PGS track as it is, which is
    /// the whole reason this path exists: libass cannot draw a bitmap track, and
    /// re-encoding one into text is not a thing.</para>
    /// </summary>
    [Test]
    public async Task ACarriedTrackBecomesAMapFromTheSecondInput()
    {
        EncodeRequest request = Request() with
        {
            DestinationPath = "/media/out.mkv",
            Encode = new EncodeDefaults
            {
                Encoder = "mpeg4",
                Quality = 8,
                Container = "mkv",
                Subtitles = new SubtitleDefaults { Mode = "copy", CopyTracks = [2, 3] },
            },
        };

        EncodePlan plan = EncodePlan.Create(
            request, WithSubs((2, "hdmv_pgs_subtitle"), (3, "hdmv_pgs_subtitle")), Encoder());

        await Assert.That(plan.Problems.Count).IsEqualTo(0);
        await Assert.That(plan.HasSubtitleTracks).IsTrue();
        await Assert.That(plan.SubtitleTracks.Count).IsEqualTo(2);

        await Assert.That(HasRun(plan.EncoderArgs, "-map", "1:2")).IsTrue();
        await Assert.That(HasRun(plan.EncoderArgs, "-map", "1:3")).IsTrue();
        await Assert.That(HasRun(plan.EncoderArgs, "-c:s", "copy")).IsTrue();

        // Two inputs: the pipe on 0, the source on 1.
        await Assert.That(plan.EncoderArgs.Count(a => a == "-i")).IsEqualTo(2);
    }

    /// <summary>
    /// A source with no audio still needs the second input when it carries a
    /// track: the pipe holds video only. This is the line that would have been
    /// forgotten.
    /// </summary>
    [Test]
    public async Task CarryingATrackNeedsTheSourceEvenWithoutAudio()
    {
        EncodeRequest request = Request() with
        {
            // The name has to agree with the container: a plan that wrote
            // matroska into out.avi is what the mismatch check refuses now.
            DestinationPath = "/media/out.mkv",
            Encode = new EncodeDefaults
            {
                Encoder = "mpeg4",
                Quality = 8,
                Container = "mkv",
                Subtitles = new SubtitleDefaults { Mode = "copy", CopyTracks = [1] },
            },
        };

        SourceInfo silent = Source(audio: false) with
        {
            Streams =
            [
                .. Source(audio: false).Streams,
                new SourceStream(1, "subtitle", "subrip", "chi", null),
            ],
        };

        EncodePlan plan = EncodePlan.Create(request, silent, Encoder());

        await Assert.That(plan.Problems.Count).IsEqualTo(0);
        await Assert.That(plan.HasAudio).IsFalse();
        await Assert.That(plan.EncoderArgs.Count(a => a == "-i")).IsEqualTo(2);
        await Assert.That(HasRun(plan.EncoderArgs, "-map", "1:1")).IsTrue();
    }

    [Test]
    public async Task ABitmapTrackCannotBeCarriedIntoMp4()
    {
        EncodeRequest request = Request() with
        {
            Encode = new EncodeDefaults
            {
                Encoder = "mpeg4",
                Quality = 8,
                Container = "mp4",
                Subtitles = new SubtitleDefaults { Mode = "copy", CopyTracks = [2] },
            },
        };

        EncodePlan plan = EncodePlan.Create(request, WithSubs((2, "hdmv_pgs_subtitle")), Encoder());

        await Assert.That(plan.IsRunnable).IsFalse();
        await Assert.That(plan.Problems.Any(p => p.Contains("mkv", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>
    /// AVI has no subtitle muxer at all, and that is a different sentence from
    /// "this codec does not fit": nothing about the track is the problem.
    /// </summary>
    [Test]
    public async Task AnAviCannotCarryAnySubtitleTrack()
    {
        EncodeRequest request = Request() with
        {
            Encode = new EncodeDefaults
            {
                Encoder = "mpeg4",
                Quality = 8,
                Subtitles = new SubtitleDefaults { Mode = "copy", CopyTracks = [2] },
            },
        };

        EncodePlan plan = EncodePlan.Create(request, WithSubs((2, "subrip")), Encoder());

        await Assert.That(plan.IsRunnable).IsFalse();
        await Assert.That(plan.Problems.Any(p => p.Contains("no subtitle muxer", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>
    /// An encoder with no container table writes exactly one container, and asking
    /// for another is refused instead of silently ignored.
    ///
    /// <para>qtrle is the clearest case: QuickTime's RLE codec lives in .mov and
    /// nowhere else. The old code returned the encoder's own container without a
    /// word, so <c>--container avi</c> produced a .mov and reported success -- a
    /// file whose extension contradicts its contents, the sort of thing found a
    /// year later.</para>
    /// </summary>
    [Test]
    public async Task AContainerTheEncoderCannotWriteIsRefused()
    {
        EncodeRequest request = Request() with
        {
            DestinationPath = "/media/out.mov",
            Encode = new EncodeDefaults { Encoder = "qtrle", Quality = 8, Container = "avi" },
        };

        EncodePlan plan = EncodePlan.Create(request, Source(), Encoder("qtrle", "mov", "mov"));

        await Assert.That(plan.IsRunnable).IsFalse();
        await Assert.That(plan.Problems.Any(p => p.Contains("only write .mov", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>The one container it can write is accepted without a comment.</summary>
    [Test]
    public async Task TheOneContainerAnEncoderHasIsAccepted()
    {
        EncodeRequest request = Request() with
        {
            DestinationPath = "/media/out.mov",
            Encode = new EncodeDefaults { Encoder = "qtrle", Quality = 8, Container = "mov" },
        };

        EncodePlan plan = EncodePlan.Create(request, Source(), Encoder("qtrle", "mov", "mov"));

        await Assert.That(plan.IsRunnable).IsTrue();
        await Assert.That(plan.Container).IsEqualTo("mov");
        await Assert.That(plan.Muxer).IsEqualTo("mov");
    }

    /// <summary>
    /// A named output whose extension contradicts the container is refused.
    ///
    /// <para><c>--encoder flv --out clip.avi</c> used to write an FLV stream into
    /// a file called .avi and report success: the same lie about a file that
    /// <see cref="EncodePlan.ResolveContainer"/> refuses when it comes from the
    /// container side. Found by running the built binary against a real source and
    /// reading the command line it printed.</para>
    /// </summary>
    [Test]
    public async Task AnOutputNameThatContradictsTheContainerIsRefused()
    {
        EncodeRequest request = Request() with
        {
            DestinationPath = "/media/out.avi",
            Encode = new EncodeDefaults { Encoder = "flv", Quality = 8 },
        };

        EncodePlan plan = EncodePlan.Create(request, Source(), Encoder("flv", "flv", "flv"));

        await Assert.That(plan.IsRunnable).IsFalse();
        await Assert.That(plan.Problems.Any(p => p.Contains("out.avi", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>Asking for the container that matches the name is the way through.</summary>
    [Test]
    public async Task MatchingContainerAndNameRunWithoutAWord()
    {
        EncodeRequest request = Request() with
        {
            DestinationPath = "/media/out.avi",
            Encode = new EncodeDefaults { Encoder = "flv", Quality = 8, Container = "avi" },
        };

        EncodePlan plan = EncodePlan.Create(request, Source(), Encoder("flv", "flv", "flv"));

        await Assert.That(plan.IsRunnable).IsTrue();
        await Assert.That(plan.Container).IsEqualTo("avi");
        await Assert.That(plan.Muxer).IsEqualTo("avi");
    }
}
