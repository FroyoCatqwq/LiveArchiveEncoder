using System.Globalization;
using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.FFmpeg;
using LiveArchiveEncoder.Localization;

namespace LiveArchiveEncoder.Encoding;

/// <summary>
/// Splits a time range into N shards, encodes them concurrently and rejoins the
/// results with <c>-f concat -c copy</c>.
///
/// Measured on a 320x240 h264 source (2026-10-17, see
/// tests/PARALLEL_SPLIT_VERIFICATION.md): the realtime pipeline spends ~98.6% of
/// its wall clock asleep, so sharding only pays off together with
/// <see cref="RealtimeOptions.Turbo"/>. On its own it is still useful for long
/// ranges where a single pass would otherwise run for minutes.
///
/// Two hard constraints are enforced here, both learned the hard way:
///   1. Only <see cref="Constants.ConcatSafeFormats"/> may be sharded. RoQ loses
///      the second shard entirely (250 frames -> 125) and RM drifts by ~0.03s
///      per join. Anything unlisted falls back to a single pass.
///   2. A shard shorter than <see cref="Constants.MinShardSeconds"/> is pointless:
///      each shard spawns its own decoder and encoder process, so below a few
///      seconds the spawn cost exceeds the saving.
/// </summary>
public static class ParallelEncoder
{
    public static EncodeResult Run(RealtimeOptions o, CancellationToken cancellationToken = default)
    {
        var src = new FileInfo(Path.GetFullPath(o.SourcePath));
        var dst = new FileInfo(Path.GetFullPath(o.DestinationPath));

        SourceInfo source = Probe.Source(o.FFprobe, src);
        (double? resolvedEnd, double? duration) =
            Timecode.ResolveRange(o.StartSeconds, o.EndSeconds, source.Duration);

        string? blocked = BlockReason(o, duration);
        if (blocked is not null)
        {
            if (o.Verbose) Console.WriteLine(L.T("note.shardingskipped", blocked));
            return RealtimeEncoder.RunSingle(o, cancellationToken);
        }

        int shards = o.Parallel;
        string stageDir = Path.Combine(
            Path.GetTempPath(), "lae_shards_" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(stageDir);

        try
        {
            double span = duration!.Value;
            int fpsSlots = (int)Math.Ceiling(span * o.Fps - 1e-9);
            if (fpsSlots < shards) shards = Math.Max(1, fpsSlots);

            // Distribute whole output slots, not raw seconds. A shard boundary that
            // lands mid-slot would make the shard slot counts fail to add up to the
            // total, silently shortening or lengthening the joined file.
            int baseSlots = fpsSlots / shards;
            int remainder = fpsSlots % shards;

            var ranges = new (double Start, double End, int Slots)[shards];
            double cursor = o.StartSeconds;
            for (int i = 0; i < shards; i++)
            {
                int count = baseSlots + (i < remainder ? 1 : 0);
                double end = cursor + count / o.Fps;
                ranges[i] = (cursor, end, count);
                cursor = end;
            }

            if (o.Verbose)
            {
                Console.WriteLine(
                    $"Sharding         : {shards} shards x {ranges[0].Slots} slots "
                    + $"({ranges[0].Slots / o.Fps:F3}s each), format '{o.Encoder.FormatName}'");
            }

            var results = new EncodeResult[shards];
            var failures = new List<string>();
            var sync = new object();

            Parallel.For(0, shards,
                new ParallelOptions { MaxDegreeOfParallelism = shards, CancellationToken = cancellationToken },
                i =>
                {
                    try
                    {
                        string shardPath = Path.Combine(stageDir, $"s{i:D3}{dst.Extension}");
                        var shardOptions = new RealtimeOptions
                        {
                            FFmpeg = o.FFmpeg,
                            FFprobe = o.FFprobe,
                            SourcePath = o.SourcePath,
                            DestinationPath = shardPath,
                            Encoder = o.Encoder,
                            Fps = o.Fps,
                            FpsLevels = o.FpsLevels,
                            Quality = o.Quality,
                            Threads = o.Threads,
                            LimitKbps = o.LimitKbps,
                            RecoverKbps = o.RecoverKbps,
                            WindowSeconds = o.WindowSeconds,
                            BufferFrames = o.BufferFrames,
                            AudioEncoder = o.AudioEncoder,
                            AudioKbps = o.AudioKbps,
                            AudioChannels = o.AudioChannels,
                            AudioRate = o.AudioRate,
                            HwaccelCuda = o.HwaccelCuda,
                            Width = o.Width,
                            Height = o.Height,
                            Crop43 = o.Crop43,
                            ScaleAlgo = o.ScaleAlgo,
                            Deinterlace = o.Deinterlace,
                            DeintMode = o.DeintMode,
                            DeintParity = o.DeintParity,
                            DeintMarkedOnly = o.DeintMarkedOnly,
                            X264Preset = o.X264Preset,
                            X264Tune = o.X264Tune,
                            X264Profile = o.X264Profile,
                            X264Level = o.X264Level,
                            StartSeconds = ranges[i].Start,
                            EndSeconds = ranges[i].End,
                            AvailableFilters = o.AvailableFilters,
                            AvailableScaleAlgorithms = o.AvailableScaleAlgorithms,
                            // Shards are quiet; the caller prints one summary.
                            Verbose = false,
                            AudioMode = o.AudioMode,
                            Turbo = o.Turbo,
                            // Never recurse into sharding a shard.
                            Parallel = 1,
                        };
                        results[i] = RealtimeEncoder.RunSingle(shardOptions, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        lock (sync) failures.Add($"shard {i}: {ex.Message}");
                    }
                });

            if (failures.Count > 0)
                throw new InvalidOperationException("shard encoding failed: " + string.Join(" | ", failures));

            string listPath = Path.Combine(stageDir, "shards.txt");
            File.WriteAllLines(listPath,
                Enumerable.Range(0, shards).Select(i =>
                    $"file '{Path.Combine(stageDir, $"s{i:D3}{dst.Extension}").Replace("'", "'\\''")}'"));

            dst.Directory?.Create();
            CaptureResult join = ProcessRunner.Run([
                o.FFmpeg, "-hide_banner", "-loglevel", "error",
                "-f", "concat", "-safe", "0", "-i", listPath,
                "-c", "copy", "-y", dst.FullName,
            ], timeoutSeconds: Math.Max(120, (int)span * 2));

            if (join.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"shard join failed (code {join.ExitCode}): {join.Stderr.Trim()}");
            }

            dst.Refresh();
            if (!dst.Exists || dst.Length == 0)
                throw new InvalidOperationException("shard join produced an empty file");

            // Safety net: the duration check alone would have caught the RoQ data
            // loss (a 10s result collapsing to 5.05s) had RoQ not been blocked.
            double joined = Probe.Duration(o.FFprobe, dst) ?? 0.0;
            double tolerance = Math.Max(0.5, span * 0.02);
            if (Math.Abs(joined - span) > tolerance)
            {
                throw new InvalidOperationException(
                    $"shard join lost data: expected {span:F3}s, got {joined:F3}s "
                    + $"(tolerance {tolerance:F3}s). Format '{o.Encoder.FormatName}' is not "
                    + "safe to concatenate; rerun without --parallel.");
            }

            long totalBytes = dst.Length;
            double avgTotalKbps = totalBytes * 8.0 / Math.Max(0.001, joined) / 1000.0;
            bool hasAudio = results[0].AvgTotalKbps != results[0].AvgVideoKbps;

            return new EncodeResult
            {
                Duration = joined,
                StartTimeSeconds = o.StartSeconds,
                EndTimeSeconds = resolvedEnd,
                SourcePixFmt = results[0].SourcePixFmt,
                TransportPixFmt = results[0].TransportPixFmt,
                OutputWidth = results[0].OutputWidth,
                OutputHeight = results[0].OutputHeight,
                AvgVideoKbps = Math.Max(0.0, avgTotalKbps - (hasAudio ? o.AudioKbps : 0.0)),
                AvgTotalKbps = avgTotalKbps,
                Fresh = results.Sum(r => r.Fresh),
                Repeated = results.Sum(r => r.Repeated),
                Deinterlace = results[0].Deinterlace,
                ScaleAlgo = results[0].ScaleAlgo,
                Slots = results.Sum(r => r.Slots),
                ElapsedSeconds = results.Max(r => r.ElapsedSeconds),
            };
        }
        finally
        {
            try { Directory.Delete(stageDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Why sharding must not be used, or null when it is allowed.</summary>
    private static string? BlockReason(RealtimeOptions o, double? duration)
    {
        if (o.Parallel <= 1) return null;
        if (duration is null) return "source duration is unknown";
        if (!Constants.ConcatSafeFormats.Contains(o.Encoder.FormatName))
        {
            return $"format '{o.Encoder.FormatName}' cannot be joined safely "
                   + "(concat would drop or drift data)";
        }
        if (duration.Value / o.Parallel < Constants.MinShardSeconds)
        {
            return $"each shard would be under {Constants.MinShardSeconds:0.#}s, "
                   + "which costs more in process startup than it saves";
        }
        return null;
    }
}
