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

            // Whole-slot boundaries, clamped so no shard ever asks for a time
            // past the end of the source (see ShardPlanner).
            List<ShardRange> ranges = ShardPlanner.Plan(o.StartSeconds, span, o.Fps, shards);
            shards = ranges.Count;

            if (o.Verbose)
            {
                Console.WriteLine(
                    $"{L.Pad(L.T("shard.heading"), 17)}: {shards} shards x {ranges[0].Slots} slots "
                    + $"({ranges[0].Slots / o.Fps:F3}s each), format '{o.Encoder.FormatName}'");
            }

            var results = new EncodeResult[shards];
            var failures = new List<string>();
            Lock sync = new();
            var progress = new ShardProgress(shards, o.Verbose);

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
                            Hwaccel = o.Hwaccel,
                            Width = o.Width,
                            Height = o.Height,
                            FitMode = o.FitMode,
                            CropAnchor = o.CropAnchor,
                            PadColor = o.PadColor,
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
                            Progress = (done, target) => progress.Report(i, done, target),
                        };
                        results[i] = RealtimeEncoder.RunSingle(shardOptions, cancellationToken);
                        progress.Finish(i, results[i].Slots);
                    }
                    catch (Exception ex)
                    {
                        lock (sync) failures.Add($"shard {i}: {ex.Message}");
                    }
                });

            progress.Render(force: true);
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

    /// <summary>
    /// One aggregate progress line for all shards.
    ///
    /// A shard cannot print its own status: seven of them writing \r lines to the
    /// same console is unreadable. Instead every shard reports its slot counter
    /// here and the last one to move renders a single line that is throttled to
    /// five updates a second.
    /// </summary>
    private sealed class ShardProgress
    {
        private readonly Lock _gate = new();
        private readonly bool _verbose;
        private readonly int _shards;
        private readonly int[] _slots;
        private readonly int[] _targets;
        private readonly bool[] _finished;
        private readonly long _started = Environment.TickCount64;
        private int _done;
        private long _lastRender;
        private int _width;

        public ShardProgress(int shards, bool verbose)
        {
            _shards = shards;
            _verbose = verbose;
            _slots = new int[shards];
            _targets = new int[shards];
            _finished = new bool[shards];
        }

        public void Report(int shard, int slots, int? target)
        {
            lock (_gate)
            {
                _slots[shard] = slots;
                if (target is not null) _targets[shard] = Math.Max(target.Value, slots);
                MaybeRender(false);
            }
        }

        /// <summary>Mark a shard done. <paramref name="slots"/> is its real final
        /// count: the throttled reports can lag behind by up to 0.2s, and a
        /// finished shard must never be shown as one slot short.</summary>
        public void Finish(int shard, int slots)
        {
            lock (_gate)
            {
                _slots[shard] = Math.Max(_slots[shard], slots);
                if (_targets[shard] < _slots[shard]) _targets[shard] = _slots[shard];
                if (!_finished[shard])
                {
                    _finished[shard] = true;
                    _done++;
                }
                MaybeRender(false);
            }
        }

        /// <summary>Force a final render. Always terminates the line.</summary>
        public void Render(bool force)
        {
            lock (_gate) MaybeRender(force);
        }

        private void MaybeRender(bool force)
        {
            if (!_verbose) return;

            long now = Environment.TickCount64;
            if (!force && now - _lastRender < 200) return;
            _lastRender = now;

            int slots = 0;
            int targets = 0;
            for (int i = 0; i < _shards; i++)
            {
                slots += _slots[i];
                targets += _targets[i];
            }

            double elapsed = (now - _started) / 1000.0;
            string text = targets > 0
                ? L.T("shard.progress", _done, _shards, slots, targets,
                    Math.Clamp((int)(slots * 100.0 / targets), 0, 100), Timecode.Format(elapsed))
                : L.T("shard.progress.open", _done, _shards, slots, Timecode.Format(elapsed));

            string line = $"{L.Pad(L.T("shard.label"), 17)}: {text}";
            if (line.Length < _width) line += new string(' ', _width - line.Length);
            _width = line.Length;
            Console.Write('\r' + line);
            if (force) Console.WriteLine();
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
