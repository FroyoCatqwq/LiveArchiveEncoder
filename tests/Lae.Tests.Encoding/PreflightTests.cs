using Lae.Core;
using Lae.Core.Settings;
using Lae.Encoding;

using static Lae.Tests.Encoding.TestMessages;

namespace Lae.Tests.Encoding;

/// <summary>
/// The preflight's decisions, with the runner replaced by a fake.
///
/// <para>The trial itself is a real encode, and that is covered by
/// <see cref="LiveEncodeTests"/> where FFmpeg is available. What is tested here
/// is everything around it: which plan comes back, what happens when hardware
/// decoding fails, and the difference between "the user pressed Ctrl-C" and
/// "this machine cannot do it" -- a distinction a real FFmpeg cannot be asked
/// to produce on demand.</para>
/// </summary>
public sealed class PreflightTests
{
    private static EncoderInfo Encoder() => new()
    {
        Name = "mpeg4",
        Extension = "avi",
        FormatName = "avi",
        Codec = "mpeg4",
        Family = "mpeg4",
    };

    private static SourceInfo Source() => new()
    {
        DurationSeconds = 300,
        Width = 640,
        Height = 480,
        PixelFormat = "yuv420p",
        HasAudio = false,
        Streams = [new SourceStream(0, "video", "h264", null, null, new StreamInfo { Width = 640, Height = 480 })],
    };

    private static (EncodePlan Plan, string Folder) Setup(string hwaccel = "off", double seconds = 600)
    {
        string folder = Path.Combine(Path.GetTempPath(), "lae-preflight-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);

        var request = new EncodeRequest
        {
            SourcePath = Path.Combine(folder, "in.mkv"),
            DestinationPath = Path.Combine(folder, "out.avi"),
            EndSeconds = seconds,
            Encode = new EncodeDefaults { Encoder = "mpeg4", Quality = 8, Fps = 25, Hwaccel = hwaccel },
        };

        EncodePlan plan = EncodePlan.Create(request, Source(), Encoder(), En);
        return (plan, folder);
    }

    /// <summary>A runner that records what it was asked to run and writes the file it was told to write.</summary>
    private sealed class FakeRunner
    {
        private readonly Func<EncodePlan, bool> _fail;

        public FakeRunner(Func<EncodePlan, bool>? fail = null) => _fail = fail ?? (_ => false);

        public List<EncodePlan> Calls { get; } = [];

        public LiveRun Run(
            EncodePlan plan, string ffmpeg,
            Action<long, long?>? progress, Action<string>? warn, CancellationToken token)
        {
            Calls.Add(plan);

            if (_fail(plan)) throw new InvalidOperationException("ffmpeg said no");

            // A trial that succeeded leaves a file behind; the preflight is
            // responsible for deleting it.
            File.WriteAllText(plan.DestinationPath, "trial");
            return new LiveRun
            {
                Pump = new PumpStats { Frames = 3, Fresh = 3, Repeated = 0, Seconds = 0.12, SourceEnded = false },
                OutputPath = plan.DestinationPath,
                OutputBytes = 5,
                SpanSeconds = plan.SpanSeconds,
                HasAudio = plan.HasAudio,
            };
        }
    }

    [Test]
    public async Task ATrialThatWorksHandsBackTheSamePlan()
    {
        (EncodePlan plan, string folder) = Setup();
        var runner = new FakeRunner();

        PreflightResult result = Preflight.Run(plan, "/usr/bin/ffmpeg", runner.Run, CancellationToken.None);

        using var _ = new Cleanup(folder);

        await Assert.That(result.Trialed).IsTrue();
        await Assert.That(result.Plan).IsSameReferenceAs(plan);
        await Assert.That(result.Notes).IsEmpty();

        // One call, three frames, and a destination the user never sees.
        await Assert.That(runner.Calls.Count).IsEqualTo(1);
        await Assert.That(runner.Calls[0].TargetFrames).IsEqualTo(3L);
        await Assert.That(runner.Calls[0].DestinationPath).IsNotEqualTo(plan.DestinationPath);

        // The trial's output is litter, not a deliverable.
        await Assert.That(File.Exists(runner.Calls[0].DestinationPath)).IsFalse();
    }

    [Test]
    public async Task ATrialThatFailsStopsTheRunWithItsOwnReason()
    {
        (EncodePlan plan, string folder) = Setup();
        var runner = new FakeRunner(_ => true);

        using var _ = new Cleanup(folder);

        InvalidOperationException? thrown = null;
        try
        {
            Preflight.Run(plan, "/usr/bin/ffmpeg", runner.Run, CancellationToken.None);
        }
        catch (InvalidOperationException exc)
        {
            thrown = exc;
        }

        await Assert.That(thrown).IsNotNull();
        await Assert.That(thrown!.Message).Contains("three-frame trial");
        await Assert.That(thrown.Message).Contains("ffmpeg said no");
        await Assert.That(runner.Calls.Count).IsEqualTo(1);
    }

    /// <summary>
    /// Hardware decoding is a request, not a promise: when the machine cannot do
    /// it, the recording still happens in software -- and the plan that runs is
    /// the one the preflight settled on, not the one the user asked for.
    /// </summary>
    [Test]
    public async Task HardwareDecodingThatFailsFallsBackToSoftware()
    {
        (EncodePlan plan, string folder) = Setup(hwaccel: "auto");

        // Fail only while the plan still asks for hardware.
        var runner = new FakeRunner(p => p.DecoderArgs.Contains("-hwaccel"));

        using var _ = new Cleanup(folder);

        PreflightResult result = Preflight.Run(plan, "/usr/bin/ffmpeg", runner.Run, CancellationToken.None);

        await Assert.That(result.Trialed).IsTrue();
        await Assert.That(result.Plan.DecoderArgs.Contains("-hwaccel")).IsFalse();
        await Assert.That(result.Plan.Encode.Hwaccel).IsEqualTo("off");
        await Assert.That(result.Notes.Count).IsEqualTo(1);
        await Assert.That(result.Notes[0]).Contains("software");
        await Assert.That(runner.Calls.Count).IsEqualTo(2);
    }

    /// <summary>
    /// When both attempts fail the hardware was never the problem, so the message
    /// has to be the software one -- that is the command the user would arrive at
    /// after "just turn hwaccel off", and it is the failure worth reading.
    /// </summary>
    [Test]
    public async Task SoftwareFailingTooIsReportedInsteadOfTheHardwareAttempt()
    {
        (EncodePlan plan, string folder) = Setup(hwaccel: "auto");
        var runner = new FakeRunner(_ => true);

        using var _ = new Cleanup(folder);

        InvalidOperationException? thrown = null;
        try
        {
            Preflight.Run(plan, "/usr/bin/ffmpeg", runner.Run, CancellationToken.None);
        }
        catch (InvalidOperationException exc)
        {
            thrown = exc;
        }

        await Assert.That(thrown).IsNotNull();
        await Assert.That(thrown!.Message).Contains("three-frame trial");
        await Assert.That(runner.Calls.Count).IsEqualTo(2);
    }

    [Test]
    public async Task CancellingTheTrialIsNotAVerdictOnThePlan()
    {
        (EncodePlan plan, string folder) = Setup();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        var runner = new FakeRunner(_ => throw new OperationCanceledException());

        using var _ = new Cleanup(folder);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
        {
            Preflight.Run(plan, "/usr/bin/ffmpeg", runner.Run, cancel.Token);
            return Task.CompletedTask;
        });

        // One attempt: cancelling must not be retried as a hardware fallback.
        await Assert.That(runner.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ARunAsShortAsATrialIsNotTrialledTwice()
    {
        (EncodePlan plan, string folder) = Setup(seconds: 0.1);
        var runner = new FakeRunner();

        using var _ = new Cleanup(folder);

        await Assert.That(plan.TargetFrames).IsLessThanOrEqualTo(3L);

        PreflightResult result = Preflight.Run(plan, "/usr/bin/ffmpeg", runner.Run, CancellationToken.None);

        await Assert.That(result.Trialed).IsFalse();
        await Assert.That(result.Plan).IsSameReferenceAs(plan);
        await Assert.That(result.Notes.Count).IsEqualTo(1);
        await Assert.That(runner.Calls.Count).IsEqualTo(0);
    }

    /// <summary>
    /// A landing place that refuses an invented name -- an archival mount that
    /// only allows the exact output name, a share with an extension whitelist --
    /// used to fail the trial for a reason that says nothing about the encode.
    /// The trial moves to the temporary folder, and the note says what that no
    /// longer proves.
    /// </summary>
    [Test]
    public async Task AFolderThatRefusesATrialFileMovesTheTrialAndSaysSo()
    {
        (EncodePlan plan, string folder) = Setup();
        using var _ = new Cleanup(folder);

        (string chosen, string? note) = Preflight.ProbeFolder(plan, _ => true, (_, _) => false);

        await Assert.That(chosen).IsEqualTo(Path.GetTempPath());
        await Assert.That(note).IsNotNull();
        await Assert.That(note!).Contains(Path.GetTempPath());
        await Assert.That(note).Contains("not proven");
    }

    /// <summary>
    /// A folder that is not there is not a permission problem: the trial stays
    /// where the recording would go, so FFmpeg's own sentence names the path.
    /// Moving it elsewhere would turn "no such directory" into a note about the
    /// filesystem.
    /// </summary>
    [Test]
    public async Task AMissingOutputFolderIsNotTreatedAsAPermissionProblem()
    {
        (EncodePlan plan, string folder) = Setup();
        using var _ = new Cleanup(folder);

        (string chosen, string? note) = Preflight.ProbeFolder(plan, _ => false, (_, _) => false);

        await Assert.That(chosen).IsEqualTo(folder);
        await Assert.That(note).IsNull();
    }

    private sealed class Cleanup(string folder) : IDisposable
    {
        public void Dispose()
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }
}
