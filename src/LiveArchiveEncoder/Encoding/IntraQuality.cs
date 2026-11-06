using System.Globalization;
using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.FFmpeg;

namespace LiveArchiveEncoder.Encoding;

/// <summary>
/// v7.9.8.11.1: intra-only encoders (MJPEG) cannot spend bits across frames.
///
/// <para>FFmpeg's MJPEG encoder has no rate control: <c>-q:v</c> fixes the
/// quantiser, so every frame costs roughly the same number of bytes and the
/// bitrate is <c>bytesPerFrame * fps * 8 / 1000</c>. Measured on 256x192
/// yuvj420p (2026-11-07) q:v 6 costs 6616 B/frame -- 688 kbps at 13 fps and
/// still 159 kbps at the ladder's last rung of 3 fps. The old controller read
/// that as "always over the 200 kbps limit", walked to the deepest rung and
/// could never climb back above the 150 kbps recover point.</para>
///
/// <para>Two things follow. The limit has to reach the encoder as a quantiser,
/// chosen before the run starts (the command line cannot be changed mid-pipe);
/// and a rung is only worth descending to when it can actually get under the
/// limit. Both are done here.</para>
/// </summary>
public static class IntraQuality
{
    private const int FramesPerProbe = 4;

    /// <summary>Encoders with no inter-frame prediction.</summary>
    public static bool IsIntraOnly(string? encoder) =>
        string.Equals(encoder?.Trim(), "mjpeg", StringComparison.OrdinalIgnoreCase);

    public static double KbpsFor(double bytesPerFrame, double fps) =>
        bytesPerFrame * Math.Max(0.0, fps) * 8.0 / 1000.0;

    /// <summary>
    /// The shallowest rung whose predicted bitrate fits the limit. Returns 0
    /// when even the slowest rung cannot -- dropping frames then only makes the
    /// picture stutter without ever satisfying the controller.
    /// </summary>
    public static int RungFor(double bytesPerFrame, IReadOnlyList<double> levels, double limitKbps)
    {
        for (int i = 0; i < levels.Count; i++)
        {
            if (KbpsFor(bytesPerFrame, levels[i]) <= limitKbps) return i;
        }
        return 0;
    }

    /// <summary>
    /// Pick the best quantiser that fits <paramref name="budgetKbps"/> at
    /// <paramref name="fps"/>, by encoding a few real frames from this source.
    /// A quality the user typed is measured but never overridden.
    /// </summary>
    public static (int Quality, double BytesPerFrame, double Kbps) Calibrate(
        string ffmpeg,
        string sourcePath,
        string encoderName,
        VideoFilter vf,
        string transportFmt,
        StreamInfo stream,
        int? videoStream,
        double startSeconds,
        int? requestedQuality,
        double budgetKbps,
        double fps,
        ColorOverride? colors = null,
        Action<string>? log = null)
    {
        double budgetBytes = Math.Max(1.0, budgetKbps) * 1000.0 / 8.0 / Math.Max(0.1, fps);

        if (requestedQuality is { } pinned)
        {
            double bytes = Measure(ffmpeg, sourcePath, encoderName, vf, transportFmt, stream,
                videoStream, startSeconds, pinned, colors);
            return (pinned, bytes, KbpsFor(bytes, fps));
        }

        // q:v is monotonically related to frame size: binary search for the
        // best picture that still fits, five short probes at most.
        int low = 2, high = 31, best = 31;
        double bestBytes = Measure(ffmpeg, sourcePath, encoderName, vf, transportFmt, stream,
            videoStream, startSeconds, high, colors);
        for (int step = 0; step < 5 && low <= high; step++)
        {
            int q = (low + high) / 2;
            double bytes = Measure(ffmpeg, sourcePath, encoderName, vf, transportFmt, stream,
                videoStream, startSeconds, q, colors);
            if (!double.IsFinite(bytes)) break;
            if (bytes <= budgetBytes)
            {
                best = q;
                bestBytes = bytes;
                high = q - 1;
            }
            else
            {
                low = q + 1;
            }
        }

        double kbps = KbpsFor(bestBytes, fps);
        if (kbps > budgetKbps)
            log?.Invoke($"NOTE: {encoderName} cannot reach {budgetKbps:G4} kbps at {fps:G4} fps "
                + $"(best is q:v {best}, about {kbps:G4} kbps); the frame-rate ladder is "
                + "calibrated to what is physically reachable.");
        return (best, bestBytes, kbps);
    }

    private static double Measure(
        string ffmpeg, string sourcePath, string encoderName, VideoFilter vf,
        string transportFmt, StreamInfo stream, int? videoStream, double startSeconds,
        int quality, ColorOverride? colors)
    {
        string temp = Path.Combine(Path.GetTempPath(),
            "lae_qv_" + Guid.NewGuid().ToString("N") + ".mjpg");
        try
        {
            var cmd = new List<string> { ffmpeg, "-hide_banner", "-loglevel", "error" };
            if (startSeconds > 0) cmd.AddRange(["-ss", ArgsBuilder.FloatArg(startSeconds)]);
            cmd.AddRange(["-i", sourcePath]);
            if (!vf.IsGraph) cmd.AddRange(["-map", StreamSelection.VideoMap(videoStream)]);
            cmd.AddRange(vf.Args());
            cmd.AddRange(["-an", "-frames:v", FramesPerProbe.ToString(CultureInfo.InvariantCulture),
                "-c:v", encoderName, "-q:v", quality.ToString(CultureInfo.InvariantCulture)]);
            cmd.AddRange(ArgsBuilder.EncoderPixFmtArgs(encoderName, transportFmt, false));
            cmd.AddRange(ArgsBuilder.ColorArgs(stream, colors, encoderName));
            cmd.AddRange(["-f", "mjpeg", "-y", temp]);

            CaptureResult result = ProcessRunner.Run(cmd, 30);
            if (result.ExitCode != 0 || !File.Exists(temp)) return double.NaN;
            return new FileInfo(temp).Length / (double)FramesPerProbe;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return double.NaN;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }
}
