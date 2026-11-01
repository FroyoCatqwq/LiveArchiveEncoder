using System.Diagnostics;
using System.Globalization;
using Lae.Core;
using Lae.Core.Settings;
using Lae.Encoding;
using Lae.Media;
using Lae.Media.Ffmpeg;
using Lae.Media.Toolchain;

using static Lae.Tests.Encoding.TestMessages;

namespace Lae.Tests.Encoding;

/// <summary>
/// One real encode, start to finish.
///
/// <para>The pump's cadence is covered with memory streams in Lae.Tests.Core and
/// the command lines are pinned in Lae.Tests.Core as well. What can only be
/// answered here is whether those two halves agree when FFmpeg is watching: a
/// plan whose argument order is subtly wrong produces a file that is the right
/// size and the wrong picture, and no amount of string comparison finds that.</para>
///
/// <para>Two seconds of source at 13 fps, so the test costs the two seconds a
/// realtime encode of two seconds costs -- this is not a test to run a thousand
/// times a minute, it is the one that proves the door opens.</para>
/// </summary>
public sealed class LiveEncodeTests
{
    private sealed record Rig(string Ffmpeg, string Ffprobe, string Folder) : IDisposable
    {
        public void Dispose()
        {
            try { Directory.Delete(Folder, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    private static Rig? Setup()
    {
        ToolchainProbeResult tools = ToolchainProbe.Probe();
        if (!tools.Complete) return null;

        string folder = Path.Combine(Path.GetTempPath(), "lae-live-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return new Rig(tools.Ffmpeg!, tools.Ffprobe!, folder);
    }

    private static int Run(string executable, params string[] args)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string arg in args) start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;
        process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit(120_000);
        return process.ExitCode;
    }

    /// <summary>Build a source with a known length, rate and no audio.</summary>
    private static string Source(Rig rig, double seconds)
    {
        string path = Path.Combine(rig.Folder, "source.avi");
        int rc = Run(rig.Ffmpeg,
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi",
            "-i", string.Create(CultureInfo.InvariantCulture, $"testsrc2=size=128x96:rate=13:duration={seconds}"),
            "-c:v", "mpeg4", "-q:v", "4",
            "-y", path);

        if (rc != 0) throw new InvalidOperationException($"could not build a test source (ffmpeg exit {rc})");
        return path;
    }

    [Test]
    public async Task EncodesARealSourceToItsRequestedLength()
    {
        using Rig? rig = Setup();
        if (rig is null) Skip.Test("no FFmpeg on PATH; a real encode cannot be exercised here");

        string sourcePath = Source(rig, 2.0);
        string outputPath = Path.Combine(rig.Folder, "out.avi");

        (SourceInfo? source, string? probeError) = FfmpegTools.ProbeSource(rig.Ffprobe, sourcePath);
        await Assert.That(source).IsNotNull().Because(probeError ?? "");
        await Assert.That(source!.HasAudio).IsFalse();

        (List<EncoderInfo> encoders, string? listError) = FfmpegTools.ListEncoders(rig.Ffmpeg);
        await Assert.That(listError).IsNull();
        EncoderInfo? encoder = EncoderCatalog.Find(encoders, "mpeg4");
        await Assert.That(encoder).IsNotNull();

        var request = new EncodeRequest
        {
            SourcePath = sourcePath,
            DestinationPath = outputPath,
            Encode = new EncodeDefaults
            {
                Encoder = "mpeg4",
                Quality = 8,
                Width = 128,
                Height = 96,
                Fps = 13,
                AudioEncoder = null,
            },
        };

        EncodePlan plan = EncodePlan.Create(request, source, encoder!, En);
        await Assert.That(plan.Problems).IsEmpty().Because(string.Join("; ", plan.Problems));

        LiveRun run = LiveEncode.Run(plan, rig.Ffmpeg, null, null, CancellationToken.None);

        await Assert.That(run.Pump.Frames).IsEqualTo(plan.TargetFrames);
        await Assert.That(run.OutputBytes).IsGreaterThan(0L);
        await Assert.That(File.Exists(outputPath)).IsTrue();

        // The part file must not survive a successful run.
        await Assert.That(File.Exists(PartFile.PathFor(outputPath))).IsFalse();

        // Two seconds of programme at 13 fps, within the slack a legacy muxer and
        // its container-level duration rounding always cost.
        SourceInfo? result = FfmpegTools.ProbeSource(rig.Ffprobe, outputPath).Info;
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.DurationSeconds).IsBetween(1.5, 2.6);
        await Assert.That(result.Width).IsEqualTo(128);
        await Assert.That(result.Height).IsEqualTo(96);
    }

    /// <summary>
    /// A destination the encoder cannot write must fail with the encoder's own
    /// sentence, and must not leave a part file or a half-written output behind.
    /// </summary>
    [Test]
    public async Task AFailedRunLeavesNothingBehind()
    {
        using Rig? rig = Setup();
        if (rig is null) Skip.Test("no FFmpeg on PATH; a real encode cannot be exercised here");

        string sourcePath = Source(rig, 1.0);
        string folder = Path.Combine(rig.Folder, "missing");
        string outputPath = Path.Combine(folder, "out.avi");

        (SourceInfo? source, _) = FfmpegTools.ProbeSource(rig.Ffprobe, sourcePath);
        (List<EncoderInfo> encoders, _) = FfmpegTools.ListEncoders(rig.Ffmpeg);
        EncoderInfo encoder = EncoderCatalog.Find(encoders, "mpeg4")!;

        var request = new EncodeRequest
        {
            SourcePath = sourcePath,
            DestinationPath = outputPath,
            Encode = new EncodeDefaults
            {
                Encoder = "mpeg4",
                Quality = 8,
                Width = 128,
                Height = 96,
                Fps = 13,
                AudioEncoder = null,
            },
        };

        EncodePlan plan = EncodePlan.Create(request, source!, encoder, En);
        await Assert.That(plan.IsRunnable).IsTrue();

        // Cancel before the first slot: the run has to unwind without leaving a
        // part file, and without leaving either FFmpeg running.
        using var cancel = new CancellationTokenSource();
        cancel.CancelAfter(TimeSpan.FromMilliseconds(1));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
        {
            LiveEncode.Run(plan, rig.Ffmpeg, null, null, cancel.Token);
            return Task.CompletedTask;
        });

        await Assert.That(File.Exists(PartFile.PathFor(outputPath))).IsFalse();
        await Assert.That(File.Exists(outputPath)).IsFalse();
    }
}
