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
/// its wall clock asleep, so sharding pays off for long ranges where a single
/// pass would otherwise run for minutes -- it overlaps the sleep with real work.
///
/// Three hard constraints are enforced here, all learned the hard way:
///   1. Only <see cref="Constants.ConcatSafeFormats"/> may be sharded. RoQ loses
///      the second shard entirely (250 frames -> 125) and RM drifts by ~0.03s
///      per join. Anything unlisted falls back to a single pass.
///   2. A shard shorter than <see cref="Constants.MinShardSeconds"/> is pointless:
///      each shard spawns its own decoder and encoder process, so below a few
///      seconds the spawn cost exceeds the saving.
///   3. Shards are staged as Matroska and remuxed to the target container only
///      after the join (v7.9.2). Joining FLV shards directly produces a file
///      whose picture stops partway through while the sound carries on -- see
///      the comment at the staging step for the measurement.
/// </summary>
public static class ParallelEncoder
{
    public static EncodeResult Run(RealtimeOptions o, CancellationToken cancellationToken = default)
    {
        var dst = new FileInfo(Path.GetFullPath(o.DestinationPath));

        // v7.9: the source may be an HTTP(S) URL; only a local path has a
        // FileInfo, and the shards themselves are always local files.
        string sourcePath = Remote.HttpSource.IsUrl(o.SourcePath)
            ? o.SourcePath
            : new FileInfo(Path.GetFullPath(o.SourcePath)).FullName;

        // v7.9: stream picks are validated against this probe, before any
        // shard is spawned -- a bad index must not cost N process starts.
        SourceInfo source = StreamSelection.Resolve(
            Probe.Source(o.FFprobe, sourcePath), o.VideoStream, o.AudioStream);
        (double? resolvedEnd, double? duration) =
            Timecode.ResolveRange(o.StartSeconds, o.EndSeconds, source.Duration);

        string? blocked = BlockReason(o, duration, source.Video.PixFmt);
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

            // v7.9.2: shards are staged as Matroska, never in the target
            // container. Measured on a 243s 196 Mbit/s rawvideo source split
            // into 5 shards: joining the FLV shards themselves with
            // `-f concat -c copy` silently corrupts the picture partway in --
            // 2921 of 3651 frames decode, the rest fail with "Bad picture
            // start code", and the audio still runs to the end, so the length
            // check below cannot see it. The very same shards, remuxed to
            // Matroska and joined there, come back whole (3651/3651, no decode
            // errors), and one final `-c copy` remux to the requested container
            // stays clean. Matroska is the only container that accepts every
            // legacy codec pair this tool writes, so it is always the staging
            // format regardless of what the user asked for.
            EncoderInfo staging = StagingEncoder(o.Encoder);

            Parallel.For(0, shards,
                new ParallelOptions { MaxDegreeOfParallelism = shards, CancellationToken = cancellationToken },
                i =>
                {
                    try
                    {
                        string shardPath = Path.Combine(stageDir, $"s{i:D3}.mkv");
                        var shardOptions = new RealtimeOptions
                        {
                            FFmpeg = o.FFmpeg,
                            FFprobe = o.FFprobe,
                            SourcePath = o.SourcePath,
                            DestinationPath = shardPath,
                            Encoder = staging,
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
                            // Every shard re-enters RunSingle, which probes
                            // again: the picks have to survive the round trip.
                            VideoStream = o.VideoStream,
                            AudioStream = o.AudioStream,
                            Subtitles = o.Subtitles,
                            AvailableSubtitleEncoders = o.AvailableSubtitleEncoders,
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
                    $"file '{Path.Combine(stageDir, $"s{i:D3}.mkv").Replace("'", "'\\''")}'"));

            string joinedPath = Path.Combine(stageDir, "joined.mkv");
            CaptureResult join = ProcessRunner.Run([
                o.FFmpeg, "-hide_banner", "-loglevel", "error",
                "-f", "concat", "-safe", "0", "-i", listPath,
                "-c", "copy", "-y", joinedPath,
            ], timeoutSeconds: Math.Max(120, (int)span * 2));

            if (join.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"shard join failed (code {join.ExitCode}): {join.Stderr.Trim()}");
            }

            dst.Directory?.Create();
            if (string.Equals(dst.Extension, ".mkv", StringComparison.OrdinalIgnoreCase))
            {
                // The join already is the final container: no second pass.
                File.Move(joinedPath, dst.FullName, overwrite: true);
            }
            else
            {
                // -map 0 keeps every stream the shards carried (a copied
                // subtitle track included); -c copy means this is a remux, not
                // a re-encode, so the picture is untouched.
                //
                // v7.9.4: AVI keeps its frame rate in the file header
                // (dwRate/dwScale), and a copied MPEG-4/MPEG-1 stream arrives
                // from the Matroska staging file with a 1/1000 time base but no
                // codec frame rate, so the muxer invents one. Measured
                // (2026-10-22): mkv -> avi -c copy of 13 fps mpeg4 probes back
                // as r_frame_rate 13/1 but avg_frame_rate 1000/1, and
                // mpeg1video as 48000/1001 / 1000/1. Restating the rate the
                // shards were encoded at fixes the header, and passthrough
                // makes sure no frame is dropped or duplicated on the way.
                List<string> remuxArgs = [
                    o.FFmpeg, "-hide_banner", "-loglevel", "error",
                    "-i", joinedPath, "-map", "0", "-c", "copy",
                ];
                if (string.Equals(o.Encoder.FormatName, "avi", StringComparison.OrdinalIgnoreCase))
                    remuxArgs.AddRange(["-fps_mode", "passthrough", "-r", ArgsBuilder.FpsArg(o.Fps)]);
                remuxArgs.AddRange(["-y", dst.FullName]);

                CaptureResult remux = ProcessRunner.Run(remuxArgs, timeoutSeconds: Math.Max(120, (int)span * 2));

                if (remux.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"shard join succeeded but remuxing to '{o.Encoder.FormatName}' failed "
                        + $"(code {remux.ExitCode}): {remux.Stderr.Trim()}");
                }
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

            // v7.9.2: the picture can go missing while the sound stays whole --
            // the flv bug above did exactly that, and the duration check passed
            // because FLV carries no per-stream duration. Every shard emits one
            // slot per 1/fps (a slot is fresh or repeated), so the joined file
            // must hold as many video packets as the shards produced slots.
            int expectedPackets = results.Sum(r => r.Slots);
            if (Probe.VideoPacketCount(o.FFprobe, dst) is { } actualPackets
                && expectedPackets > 0
                && Math.Abs(actualPackets - expectedPackets) > Math.Max(4, expectedPackets / 50))
            {
                throw new InvalidOperationException(
                    $"shard join lost video: the shards produced {expectedPackets} frames, "
                    + $"the joined file has {actualPackets}. Format '{o.Encoder.FormatName}' "
                    + "is not safe to concatenate here; rerun without --parallel.");
            }

            long totalBytes = dst.Length;
            double avgTotalKbps = totalBytes * 8.0 / Math.Max(0.001, joined) / 1000.0;
            bool hasAudio = results[0].HasAudio;

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
                HasAudio = hasAudio,
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
            // Diagnostics: a bad join is impossible to diagnose after the shards
            // are gone, and "the video stops 4/5 of the way in" is exactly that
            // kind of report. LAE_KEEP_SHARDS=1 leaves the stage directory and
            // says where it is.
            if (Environment.GetEnvironmentVariable("LAE_KEEP_SHARDS") is { Length: > 0 })
                Console.WriteLine($"Shard files kept: {stageDir}");
            else
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

    /// <summary>
    /// Container every shard is written in, whatever the user asked for.
    ///
    /// Public only so the guard test can pin it down: switching back to the
    /// target container reintroduces v7.9.1's silent corruption (see the
    /// measurement at the call site), and nothing else in the build would
    /// notice until someone encoded an hour-long recording again.
    /// </summary>
    public static EncoderInfo StagingEncoder(EncoderInfo encoder) =>
        encoder.WithContainer("mkv", "matroska");

    /// <summary>Why sharding must not be used, or null when it is allowed.</summary>
    private static string? BlockReason(RealtimeOptions o, double? duration, string sourcePixFmt)
    {
        if (o.Parallel <= 1) return null;
        // v7.9.1: a network source is a single pass on purpose. Every shard
        // opens its own decoder over the same link, and N concurrent readers
        // on a connection that comes and goes is exactly the stall this
        // avoids -- the shards then wait on each other instead of on work.
        if (Remote.Smb.IsRemotePath(o.SourcePath))
            return L.T("parallel.networkrefused");
        if (duration is null) return "source duration is unknown";
        // v7.9.4: a palette codec cannot go through the Matroska staging pass.
        // Matroska stores an unknown VfW codec as V_MS/VFW/FOURCC with a
        // BITMAPINFOHEADER, and that header carries no palette -- measured on
        // msrle (2026-10-22): the very same 2-second clip is YAVG 123 encoded
        // straight to AVI and YAVG 16 (pure black) after one hop through .mkv.
        // Since every shard is staged in Matroska, such an encoder is a single
        // pass or nothing.
        o.Encoder.Caps ??= FFmpeg.BuildProfileStore.For(o.FFmpeg).EncoderCaps(o.Encoder);
        if (PixelFormatUtil.CarriesPalette(
                PixelFormatUtil.ChooseTransportPixFmt(sourcePixFmt, o.Encoder.Caps)))
        {
            return $"{o.Encoder.Name} writes pal8 frames, and the Matroska staging pass "
                   + "drops their palette (the picture would come out black)";
        }
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
