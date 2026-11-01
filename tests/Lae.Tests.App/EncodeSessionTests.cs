using Lae.App;
using Lae.App.Ui;
using Lae.Core;
using Lae.Core.Settings;
using Lae.Encoding;
using Lae.Media.Localization;
using Lae.Media.Toolchain;

namespace Lae.Tests.App;

/// <summary>
/// <c>lae encode</c>, driven without FFmpeg.
///
/// <para>What a user scripts against is the triple 0 / 1 / 2 and the sentences
/// next to them, so those are what these tests pin. The command line itself is
/// System.CommandLine's job and is exercised by the end-to-end suite; what is
/// tested here is the layer between the two, which used to be untestable because
/// it lived inside a closure that called <c>Console</c> and <c>Process</c>.</para>
/// </summary>
public sealed class EncodeSessionTests
{
    private const string Source = "/media/in.mkv";

    private static EncoderInfo Mpeg4() => new()
    {
        Name = "mpeg4",
        Extension = "avi",
        FormatName = "avi",
        Codec = "mpeg4",
        Family = "mpeg4",
    };

    private static SourceInfo SourceInfo(params SourceStream[] extra) => new()
    {
        DurationSeconds = 10,
        Width = 640,
        Height = 480,
        PixelFormat = "yuv420p",
        HasAudio = false,
        Streams =
        [
            new SourceStream(0, "video", "h264", null, null, new StreamInfo { Width = 640, Height = 480 }),
            .. extra,
        ],
    };

    /// <summary>The dependencies, with each one replaceable by a test.</summary>
    private sealed class Deps : IEncodeRunner
    {
        public bool ToolchainPresent { get; init; } = true;
        public bool SourceReadable { get; init; } = true;
        public string? EncoderListError { get; init; }
        public List<EncoderInfo> Encoders { get; init; } = [Mpeg4()];

        /// <summary>What the probe should report; tests that need a subtitle track say so here.</summary>
        public SourceInfo ProbedSource { get; init; } = SourceInfo();

        /// <summary>What the unwrap should answer; the default is "it worked".</summary>
        public Func<string, string?>? OnExtract { get; init; }

        /// <summary>The unwrap call the session made, so a test can read the index and the path.</summary>
        public (string Ffmpeg, string Source, int Index, string Destination)? Unwrapped { get; private set; }
        public Action<EncodePlan, string>? OnRun { get; init; }
        public bool Ran { get; private set; }

        /// <summary>What the post chain should answer; the default pretends every hop succeeded.</summary>
        public Func<PostChainPlan, PostChainOutcome>? OnRecompress { get; init; }

        /// <summary>The chain the session handed over, so a test can read what it planned.</summary>
        public PostChainPlan? Chain { get; private set; }

        /// <summary>A warning the chain says out loud, so the event stream has one to carry.</summary>
        public string? ChainWarning { get; init; }

        public TimeSpan ChainBudget { get; private set; }

        public EncodeDependencies Build() => new()
        {
            ProbeToolchain = _ => ToolchainPresent
                ? new ToolchainProbeResult { Ffmpeg = "/usr/bin/ffmpeg", Ffprobe = "/usr/bin/ffprobe" }
                : new ToolchainProbeResult(),
            ProbeSource = (_, _, _) => SourceReadable
                ? (ProbedSource, null)
                : (null, "no such stream"),
            ListEncoders = (_, _) => (Encoders, EncoderListError),
            ExtractSubtitle = (ffmpeg, source, index, destination, _) =>
            {
                Unwrapped = (ffmpeg, source, index, destination);
                return OnExtract?.Invoke(destination);
            },
            Runner = this,
            RunPostChain = (plan, _, budget, _, warn, token) =>
            {
                Chain = plan;
                ChainBudget = budget;
                token.ThrowIfCancellationRequested();
                if (ChainWarning is { } warning) warn?.Invoke(warning);

                return OnRecompress?.Invoke(plan) ?? new PostChainOutcome
                {
                    FinalPath = plan.FinalPath,
                    Written = [.. plan.Steps.Select(s => s.OutputPath)],
                };
            },

            // The real English resources, not a stub that echoes keys: the tests
            // below quote the sentence a user reads, and a stub would let a
            // misplaced placeholder pass as "the message was printed".
            Messages = I18NextMessages.Create("en"),
        };

        public LiveRun Run(
            EncodePlan plan, string ffmpeg,
            Action<long, long?>? progress, Action<string>? warn, CancellationToken token)
        {
            Ran = true;
            OnRun?.Invoke(plan, ffmpeg);
            token.ThrowIfCancellationRequested();

            return new LiveRun
            {
                Pump = new PumpStats
                {
                    Frames = plan.TargetFrames,
                    Fresh = plan.TargetFrames,
                    Repeated = 0,
                    Seconds = plan.SpanSeconds,
                    SourceEnded = false,
                },
                OutputPath = plan.DestinationPath,
                OutputBytes = 1024 * 300,
                SpanSeconds = plan.SpanSeconds,
                HasAudio = plan.HasAudio,
            };
        }
    }

    private static (int Code, BufferedTerminal Terminal) Run(
        Deps deps, EncodeOverrides? overrides = null, bool dryRun = false,
        SettingsDocument? settings = null, IEventSink? events = null)
    {
        var terminal = new BufferedTerminal();
        int code = EncodeSession.Run(
            Source, overrides ?? new EncodeOverrides { Output = "/media/out.avi" },
            settings ?? new SettingsDocument(), deps.Build(), terminal, dryRun, CancellationToken.None,
            events);

        return (code, terminal);
    }

    [Test]
    public async Task AMachineWithoutFfmpegIsASetupProblem()
    {
        (int code, BufferedTerminal terminal) = Run(new Deps { ToolchainPresent = false });

        await Assert.That(code).IsEqualTo(ExitCodes.Failed);

        string errors = string.Join('\n', terminal.ErrorLines);
        await Assert.That(errors.Contains("missing ffmpeg", StringComparison.Ordinal)).IsTrue();
        await Assert.That(errors.Contains("lae setup", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task AnUnreadableSourceFailsWithTheProbeReason()
    {
        (int code, BufferedTerminal terminal) = Run(new Deps { SourceReadable = false });

        await Assert.That(code).IsEqualTo(ExitCodes.Failed);
        await Assert.That(terminal.ErrorLines.Any(l => l.Contains("no such stream", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>
    /// A request that cannot be honoured is a usage error (2), not a failure (1):
    /// the distinction is what lets a script tell "I typed it wrong" from "this
    /// machine cannot do it".
    /// </summary>
    [Test]
    public async Task AMalformedSizeIsAUsageError()
    {
        (int code, BufferedTerminal terminal) =
            Run(new Deps(), new EncodeOverrides { Output = "/media/out.avi", Size = "320" });

        await Assert.That(code).IsEqualTo(ExitCodes.Usage);
        await Assert.That(terminal.ErrorLines.Any(l => l.Contains("WIDTHxHEIGHT", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task AnUnknownEncoderIsAUsageErrorThatNamesTheCommonOnes()
    {
        (int code, BufferedTerminal terminal) =
            Run(new Deps { Encoders = [] }, new EncodeOverrides { Output = "/media/out.avi", Encoder = "notacodec" });

        await Assert.That(code).IsEqualTo(ExitCodes.Usage);
        await Assert.That(terminal.ErrorLines.Any(l => l.Contains("no encoder named 'notacodec'", StringComparison.Ordinal))).IsTrue();
        await Assert.That(terminal.ErrorLines.Any(l => l.Contains("mpeg4", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task AFailedEncoderListIsAFailureNotAnEmptyMachine()
    {
        (int code, BufferedTerminal terminal) = Run(new Deps { EncoderListError = "ffmpeg did not answer within 60s" });

        await Assert.That(code).IsEqualTo(ExitCodes.Failed);
        await Assert.That(terminal.ErrorLines.Any(l => l.Contains("did not answer", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task WritingOverTheSourceIsAUsageError()
    {
        var deps = new Deps();
        (int code, BufferedTerminal terminal) =
            Run(deps, new EncodeOverrides { Output = Source });

        await Assert.That(code).IsEqualTo(ExitCodes.Usage);
        await Assert.That(terminal.ErrorLines.Any(l => l.Contains("the output path is the input path", StringComparison.Ordinal))).IsTrue();
        await Assert.That(deps.Ran).IsFalse();
    }

    [Test]
    public async Task ADryRunPrintsBothCommandsAndRunsNothing()
    {
        var deps = new Deps();
        (int code, BufferedTerminal terminal) = Run(deps, dryRun: true);

        await Assert.That(code).IsEqualTo(ExitCodes.Ok);
        await Assert.That(deps.Ran).IsFalse();

        string output = string.Join('\n', terminal.OutputLines);
        await Assert.That(output.Contains("decoder:", StringComparison.Ordinal)).IsTrue();
        await Assert.That(output.Contains("encoder:", StringComparison.Ordinal)).IsTrue();
        await Assert.That(output.Contains("pipe:1", StringComparison.Ordinal)).IsTrue();
        await Assert.That(output.Contains("pipe:0", StringComparison.Ordinal)).IsTrue();

        // The notes are commentary and belong on stderr, so a dry run can be
        // redirected into a file and still be two command lines.
        await Assert.That(terminal.ErrorLines.All(l => !l.StartsWith("source", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task ASuccessfulRunReportsWhatItWrote()
    {
        (int code, BufferedTerminal terminal) = Run(new Deps());

        await Assert.That(code).IsEqualTo(ExitCodes.Ok);

        string output = string.Join('\n', terminal.OutputLines);
        await Assert.That(output.Contains("written", StringComparison.Ordinal)).IsTrue();
        await Assert.That(output.Contains("/media/out.avi", StringComparison.Ordinal)).IsTrue();
        await Assert.That(output.Contains("pacing", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// The chain is part of the plan or it is not the plan. A dry run that shows
    /// the capture and hides three transcodes would be pasted into a shell by
    /// someone who then wonders where the files came from.
    /// </summary>
    [Test]
    public async Task ADryRunShowsEveryHopOfTheChain()
    {
        var deps = new Deps();
        var overrides = new EncodeOverrides
        {
            Output = "/media/out.avi",
            RecompressArgs = ["-c:v libx264 -crf 30", "-c:v libvpx-vp9 -crf 34"],
        };

        (int code, BufferedTerminal terminal) = Run(deps, overrides, dryRun: true);

        await Assert.That(code).IsEqualTo(ExitCodes.Ok);
        await Assert.That(deps.Ran).IsFalse();

        string output = string.Join('\n', terminal.OutputLines);
        await Assert.That(output.Contains("recompress:", StringComparison.Ordinal)).IsTrue();
        await Assert.That(output.Contains("out_web.mp4", StringComparison.Ordinal)).IsTrue();
        await Assert.That(output.Contains("out_web2.webm", StringComparison.Ordinal)).IsTrue();
        await Assert.That(output.Contains("-nostdin", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// The container comes from the codec unless the user named one, and the
    /// second hop reads the first hop's product rather than the recording.
    /// </summary>
    [Test]
    public async Task AChainIsNumberedFromTheRecordingAndFeedsItself()
    {
        var deps = new Deps();
        var overrides = new EncodeOverrides
        {
            Output = "/media/out.avi",
            RecompressArgs = ["-c:v libx264 -crf 30", "-c:v libvpx-vp9 -crf 34"],
        };

        (int code, _) = Run(deps, overrides);

        await Assert.That(code).IsEqualTo(ExitCodes.Ok);
        await Assert.That(deps.Chain).IsNotNull();

        // Compared by name, not by full path: the plan keeps the path it was
        // handed, and every platform spells a rooted path slightly differently.
        PostChainPlan chain = deps.Chain!;
        await Assert.That(chain.Steps.Count).IsEqualTo(2);
        await Assert.That(Path.GetFileName(chain.Steps[0].InputPath)).IsEqualTo("out.avi");
        await Assert.That(Path.GetFileName(chain.Steps[0].OutputPath)).IsEqualTo("out_web.mp4");
        await Assert.That(Path.GetFileName(chain.Steps[1].InputPath)).IsEqualTo("out_web.mp4");
        await Assert.That(Path.GetFileName(chain.Steps[1].OutputPath)).IsEqualTo("out_web2.webm");
        await Assert.That(chain.Steps[1].Container).IsEqualTo("webm");
    }

    [Test]
    public async Task AFinishedChainReportsEveryProduct()
    {
        var deps = new Deps();
        var overrides = new EncodeOverrides
        {
            Output = "/media/out.avi",
            RecompressArgs = ["-c:v libx264 -crf 30"],
        };

        (int code, BufferedTerminal terminal) = Run(deps, overrides);

        await Assert.That(code).IsEqualTo(ExitCodes.Ok);

        string output = string.Join('\n', terminal.OutputLines);
        await Assert.That(output.Contains("recompress out_web.mp4", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// A hop that fails does not take the recording with it, but it is still a
    /// failure: a script that reads exit 0 must not go on to publish a chain that
    /// stopped at its first hop.
    /// </summary>
    [Test]
    public async Task AFailedHopFailsTheRunAndSaysTheRecordingSurvived()
    {
        var deps = new Deps
        {
            OnRecompress = plan => new PostChainOutcome
            {
                FinalPath = plan.LivePath,
                Written = [],
                FailedStep = 1,
                Error = "pass 1 (mp4) failed: FFmpeg exited with code 1; Unknown encoder 'libx264'",
            },
        };

        var overrides = new EncodeOverrides
        {
            Output = "/media/out.avi",
            RecompressArgs = ["-c:v libx264 -crf 30"],
        };

        (int code, BufferedTerminal terminal) = Run(deps, overrides);

        await Assert.That(code).IsEqualTo(ExitCodes.Failed);
        await Assert.That(deps.Ran).IsTrue();

        string errors = string.Join('\n', terminal.ErrorLines);
        await Assert.That(errors.Contains("Unknown encoder", StringComparison.Ordinal)).IsTrue();
        await Assert.That(errors.Contains("the recording is untouched", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// The stored chain is not this run's business. v7.9.8.6 shipped a release
    /// that merged it in, and every plain encode silently recompressed itself.
    /// </summary>
    [Test]
    public async Task TheStoredChainIsNeverRunUnasked()
    {
        var settings = new SettingsDocument
        {
            Defaults = new EncodeDefaults
            {
                Chain = new ChainSettings
                {
                    Enabled = true,
                    Steps = [new ChainStep { Args = "-c:v libx264 -crf 30", Container = "mp4" }],
                },
            },
        };

        var deps = new Deps();
        (int code, _) = Run(deps, settings: settings);

        await Assert.That(code).IsEqualTo(ExitCodes.Ok);
        await Assert.That(deps.Chain).IsNull();
    }

    [Test]
    public async Task AContainerWithoutAPassIsAUsageError()
    {
        var overrides = new EncodeOverrides
        {
            Output = "/media/out.avi",
            RecompressArgs = ["-c:v libx264 -crf 30"],
            RecompressContainers = ["mp4", "webm"],
        };

        (int code, BufferedTerminal terminal) = Run(new Deps(), overrides);

        await Assert.That(code).IsEqualTo(ExitCodes.Usage);
        await Assert.That(terminal.ErrorLines.Any(l => l.Contains("nothing for the extra one", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>
    /// A chain that cannot be honoured is a usage error before anything is
    /// spawned: "-i" in a fragment would add a second input, and the refusal has
    /// to cost a sentence rather than a full capture.
    /// </summary>
    [Test]
    public async Task AChainThatCannotRunFailsBeforeTheCapture()
    {
        var deps = new Deps();
        var overrides = new EncodeOverrides
        {
            Output = "/media/out.avi",
            RecompressArgs = ["-i /media/other.mkv -c:v libx264"],
        };

        (int code, BufferedTerminal terminal) = Run(deps, overrides);

        await Assert.That(code).IsEqualTo(ExitCodes.Usage);
        await Assert.That(deps.Ran).IsFalse();
        await Assert.That(terminal.ErrorLines.Any(l => l.Contains("'-i' is not allowed", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>
    /// An embedded text track is unwrapped and then burned like any other file.
    /// The burn has to show up on the <em>decoder</em> side, because that is what
    /// the whole subtitle design rests on: the encoder command line is identical
    /// with and without captions.
    /// </summary>
    [Test]
    public async Task AnEmbeddedTextTrackIsUnwrappedAndBurned()
    {
        var deps = new Deps { ProbedSource = SourceInfo(Subtitle(2, "subrip")) };
        var overrides = new EncodeOverrides { Output = "/media/out.avi", SubtitleTrack = 2 };

        (int code, BufferedTerminal terminal) = Run(deps, overrides);

        await Assert.That(code).IsEqualTo(ExitCodes.Ok);
        await Assert.That(deps.Unwrapped).IsNotNull();
        await Assert.That(deps.Unwrapped!.Value.Index).IsEqualTo(2);
        await Assert.That(deps.Unwrapped!.Value.Source).IsEqualTo(Source);

        string output = string.Join('\n', terminal.OutputLines);
        await Assert.That(output.Contains("subtitles=filename=", StringComparison.Ordinal)).IsTrue();
        await Assert.That(output).Contains(Path.GetFileName(deps.Unwrapped!.Value.Destination)!);

        // The encoder side is untouched: a burn is a picture, not a stream.
        int encoderAt = output.IndexOf("encoder:", StringComparison.Ordinal);
        await Assert.That(encoderAt).IsGreaterThan(0);
        await Assert.That(output[encoderAt..].Contains("subtitles", StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>
    /// A dry run promises two command lines and no processes. An unwrap is a
    /// process that writes a file, so the plan names the path that would be used
    /// and stops there.
    /// </summary>
    [Test]
    public async Task ADryRunNamesTheUnwrappedTrackWithoutUnwrappingIt()
    {
        var deps = new Deps { ProbedSource = SourceInfo(Subtitle(2, "subrip")) };
        var overrides = new EncodeOverrides { Output = "/media/out.avi", SubtitleTrack = 2 };

        (int code, BufferedTerminal terminal) = Run(deps, overrides, dryRun: true);

        await Assert.That(code).IsEqualTo(ExitCodes.Ok);
        await Assert.That(deps.Unwrapped).IsNull();
        await Assert.That(deps.Ran).IsFalse();

        string output = string.Join('\n', terminal.OutputLines);
        await Assert.That(output.Contains("lae-subs-2-", StringComparison.Ordinal)).IsTrue();
        await Assert.That(output.Contains("subtitles=filename=", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// A bitmap track is pictures of text: there is nothing for libass to lay out,
    /// and no argument recovers it. Refusing before the unwrap costs nothing;
    /// refusing after costs a full pass over the file.
    /// </summary>
    [Test]
    public async Task ABitmapTrackIsRefusedBeforeAnythingIsUnwrapped()
    {
        var deps = new Deps { ProbedSource = SourceInfo(Subtitle(3, "hdmv_pgs_subtitle")) };
        var overrides = new EncodeOverrides { Output = "/media/out.avi", SubtitleTrack = 3 };

        (int code, BufferedTerminal terminal) = Run(deps, overrides);

        await Assert.That(code).IsEqualTo(ExitCodes.Usage);
        await Assert.That(deps.Unwrapped).IsNull();
        await Assert.That(deps.Ran).IsFalse();
        await Assert.That(terminal.ErrorLines.Any(l => l.Contains("bitmap", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task ATrackThatIsNotASubtitleIsAUsageError()
    {
        var deps = new Deps { ProbedSource = SourceInfo(Subtitle(2, "subrip")) };
        var overrides = new EncodeOverrides { Output = "/media/out.avi", SubtitleTrack = 0 };

        (int code, BufferedTerminal terminal) = Run(deps, overrides);

        await Assert.That(code).IsEqualTo(ExitCodes.Usage);
        await Assert.That(deps.Unwrapped).IsNull();
        await Assert.That(terminal.ErrorLines.Any(l => l.Contains("that stream is video", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>
    /// An unwrap that cannot produce text is this machine's failure (1), not the
    /// user's typing (2): an empty track is a property of the recording.
    /// </summary>
    [Test]
    public async Task AnUnwrapThatProducesNothingIsAFailure()
    {
        var deps = new Deps
        {
            ProbedSource = SourceInfo(Subtitle(2, "subrip")),
            OnExtract = _ => "subtitle track 2 carries no text; nothing would be burned",
        };

        var overrides = new EncodeOverrides { Output = "/media/out.avi", SubtitleTrack = 2 };
        (int code, BufferedTerminal terminal) = Run(deps, overrides);

        await Assert.That(code).IsEqualTo(ExitCodes.Failed);
        await Assert.That(deps.Ran).IsFalse();
        await Assert.That(terminal.ErrorLines.Any(l => l.Contains("carries no text", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>
    /// The unwrapped copy is deleted whatever the run did -- including when the run
    /// threw. A subtitle file left beside the recording is a side effect nobody
    /// asked for, and the next run would find a mystery file.
    /// </summary>
    [Test]
    public async Task TheUnwrappedCopyIsDeletedAfterTheRun()
    {
        string folder = Path.Combine(Path.GetTempPath(), "lae-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);

        string? written = null;
        try
        {
            var deps = new Deps
            {
                ProbedSource = SourceInfo(Subtitle(2, "subrip")),
                OnExtract = destination =>
                {
                    File.WriteAllText(destination, "[Script Info]\n");
                    written = destination;
                    return null;
                },
            };

            var overrides = new EncodeOverrides { Output = Path.Combine(folder, "out.avi"), SubtitleTrack = 2 };
            (int code, _) = Run(deps, overrides);

            await Assert.That(code).IsEqualTo(ExitCodes.Ok);
            await Assert.That(written).IsNotNull();
            await Assert.That(File.Exists(written!)).IsFalse();
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Carrying a track does not touch the source: it is a map from the second
    /// input, so nothing is unwrapped and nothing has to be deleted afterwards.
    /// </summary>
    [Test]
    public async Task CarryingATrackMapsItWithoutUnwrappingAnything()
    {
        var deps = new Deps { ProbedSource = SourceInfo(Subtitle(2, "hdmv_pgs_subtitle")) };
        var overrides = new EncodeOverrides
        {
            Output = "/media/out.mkv",
            Container = "mkv",
            SubtitleCopyTracks = [2],
        };

        (int code, BufferedTerminal terminal) = Run(deps, overrides, dryRun: true);

        await Assert.That(code).IsEqualTo(ExitCodes.Ok);
        await Assert.That(deps.Unwrapped).IsNull();

        string output = string.Join('\n', terminal.OutputLines);
        await Assert.That(output.Contains("-c:s copy", StringComparison.Ordinal)).IsTrue();
        await Assert.That(output.Contains("-map 1:2", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>Three ways to say "subtitles"; a run picks one.</summary>
    [Test]
    public async Task BurningAndCarryingAtOnceIsAUsageError()
    {
        var deps = new Deps
        {
            ProbedSource = SourceInfo(Subtitle(2, "subrip"), Subtitle(3, "hdmv_pgs_subtitle")),
        };

        var overrides = new EncodeOverrides
        {
            Output = "/media/out.avi",
            SubtitleTrack = 2,
            SubtitleCopyTracks = [3],
        };

        (int code, BufferedTerminal terminal) = Run(deps, overrides);

        await Assert.That(code).IsEqualTo(ExitCodes.Usage);
        await Assert.That(deps.Ran).IsFalse();
        await Assert.That(terminal.ErrorLines.Any(l => l.Contains("pick one", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>
    /// <c>--recompress-only</c> skips the capture: no encoder is chosen, the fake
    /// runner never runs, and the file named on the command line is what the chain
    /// reads.
    /// </summary>
    [Test]
    public async Task RecompressOnlySkipsTheCaptureEntirely()
    {
        string file = Path.Combine(Path.GetTempPath(), "lae-only-" + Guid.NewGuid().ToString("N")[..8] + ".avi");
        File.WriteAllBytes(file, [0]);

        try
        {
            var deps = new Deps();
            var overrides = new EncodeOverrides
            {
                RecompressOnly = true,
                RecompressArgs = ["-c:v libx264 -crf 30"],
            };

            var terminal = new BufferedTerminal();
            int code = EncodeSession.Run(
                file, overrides, new SettingsDocument(), deps.Build(), terminal, dryRun: true, CancellationToken.None);

            await Assert.That(code).IsEqualTo(ExitCodes.Ok);
            await Assert.That(deps.Ran).IsFalse();

            string output = string.Join('\n', terminal.OutputLines);
            await Assert.That(output.Contains("recompress:", StringComparison.Ordinal)).IsTrue();
            await Assert.That(output.Contains("_web.mp4", StringComparison.Ordinal)).IsTrue();
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Test]
    public async Task RecompressOnlyWithoutAChainIsAUsageError()
    {
        var overrides = new EncodeOverrides { RecompressOnly = true };

        (int code, BufferedTerminal terminal) = Run(new Deps(), overrides);

        await Assert.That(code).IsEqualTo(ExitCodes.Usage);
        await Assert.That(terminal.ErrorLines.Any(l => l.Contains("--recompress", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task RecompressOnlyNeedsTheFileToExist()
    {
        var overrides = new EncodeOverrides
        {
            RecompressOnly = true,
            RecompressArgs = ["-c:v libx264 -crf 30"],
        };

        var terminal = new BufferedTerminal();
        int code = EncodeSession.Run(
            "/media/not-there.avi", overrides, new SettingsDocument(), new Deps().Build(),
            terminal, dryRun: false, CancellationToken.None);

        await Assert.That(code).IsEqualTo(ExitCodes.Usage);
        await Assert.That(terminal.ErrorLines.Any(l => l.Contains("there is no file", StringComparison.Ordinal))).IsTrue();
    }

    private static SourceStream Subtitle(int index, string codec)
        => new(index, "subtitle", codec, "chi", null);

    /// <summary>
    /// Records the stream so a test can read what the session said, in the order
    /// it said it. This is the consumer <c>--json-events</c> will be: if it cannot
    /// see the phases, neither can a GUI.
    /// </summary>
    private sealed class RecordingSink : IEventSink
    {
        public List<string> Events { get; } = [];

        public void Stage(string name) => Events.Add($"stage:{name}");

        public void Progress(long done, long? total) => Events.Add($"progress:{done}/{total?.ToString() ?? "-"}");

        public void Warn(string message) => Events.Add($"warn:{message}");

        public void Done(string summary, IReadOnlyList<string> artifacts, TimeSpan elapsed)
            => Events.Add($"done:{string.Join(",", artifacts)}");
    }

    /// <summary>
    /// The phases arrive in the order they happen, and a finished capture says so
    /// once, with its product. Before this, none of that was observable: the
    /// session printed and forgot.
    /// </summary>
    [Test]
    public async Task ARunEmitsItsStagesInOrderAndOneDone()
    {
        var deps = new Deps();
        var recorder = new RecordingSink();
        var overrides = new EncodeOverrides
        {
            Output = "/media/out.avi",
            RecompressArgs = ["-c:v libx264 -crf 30"],
        };

        (int code, _) = Run(deps, overrides, events: recorder);

        await Assert.That(code).IsEqualTo(ExitCodes.Ok);

        List<string> events = recorder.Events;
        int preflight = events.IndexOf("stage:preflight");
        int capture = events.IndexOf("stage:capture");
        int done = events.FindIndex(e => e.StartsWith("done:", StringComparison.Ordinal));
        int chain = events.IndexOf("stage:between-passes");

        await Assert.That(preflight).IsGreaterThanOrEqualTo(0).Because("the trial is a phase, and it is announced");
        await Assert.That(capture).IsGreaterThan(preflight);
        await Assert.That(done).IsGreaterThan(capture);
        await Assert.That(chain).IsGreaterThan(done).Because("the chain runs after the recording is written");

        await Assert.That(events[done]).IsEqualTo("done:/media/out.avi")
            .Because("a machine wants the artifact, not the sentence");
        await Assert.That(events.Count(e => e.StartsWith("done:", StringComparison.Ordinal))).IsEqualTo(1);
    }

    /// <summary>
    /// A warning travels as an event, and the default renderer still puts it on
    /// stderr with its prefix.
    ///
    /// <para>A supplied sink <em>replaces</em> the terminal one rather than
    /// shadowing it -- that is what makes <c>--json-events</c> a second renderer
    /// instead of a second set of print calls. A caller that wants both composes
    /// them.</para>
    /// </summary>
    [Test]
    public async Task AWarningIsAnEventAndTheDefaultRendererStillPrefixesIt()
    {
        var overrides = new EncodeOverrides
        {
            Output = "/media/out.avi",
            RecompressArgs = ["-c:v libx264 -crf 30"],
        };

        var recorder = new RecordingSink();
        (int recorded, _) = Run(new Deps { ChainWarning = "a leftover part file was removed" }, overrides, events: recorder);

        await Assert.That(recorded).IsEqualTo(ExitCodes.Ok);
        await Assert.That(recorder.Events).Contains("warn:a leftover part file was removed");

        (int printed, BufferedTerminal terminal) =
            Run(new Deps { ChainWarning = "a leftover part file was removed" }, overrides);

        await Assert.That(printed).IsEqualTo(ExitCodes.Ok);
        await Assert.That(terminal.ErrorLines.Any(l => l == "note: a leftover part file was removed")).IsTrue();
    }

    /// <summary>
    /// A cancel is not a failure and must not be reported as one: it has to reach
    /// the top of the program, which turns it into exit 130 and one line.
    /// </summary>
    [Test]
    public async Task ACancelledRunPropagatesInsteadOfReturningACode()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        var terminal = new BufferedTerminal();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
        {
            EncodeSession.Run(
                Source, new EncodeOverrides { Output = "/media/out.avi" },
                new SettingsDocument(), new Deps().Build(), terminal, dryRun: false, cancel.Token);
            return Task.CompletedTask;
        });
    }
}



