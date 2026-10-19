using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.FFmpeg;
using LiveArchiveEncoder.Localization;

namespace LiveArchiveEncoder.UI;

/// <summary>CLI entry path, ported from cli_main().</summary>
public static class Cli
{
    public static int Run(
        CliOptions args, string ffmpeg, string ffprobe,
        HashSet<string> availableFilters, HashSet<string> availableScaleAlgorithms,
        CancellationToken cancellationToken = default)
    {
        // Loaded once: the post-processing stages are sticky (a user sets the
        // recompress arguments in the menu and then runs the CLI), so the CLI has
        // to consult the same file the interactive shell writes.
        UserConfig stored = ConfigStore.Load();

        // DetectLegacyEncoders and DetectAudioEncoders are independent `ffmpeg
        // -encoders` runs. Overlap them so two process startups cost one.
        Task<List<EncoderInfo>> encodersTask =
            Task.Run(() => FFmpegDiscovery.DetectLegacyEncoders(ffmpeg), cancellationToken);
        Task<HashSet<string>> audioEncodersTask =
            Task.Run(() => FFmpegDiscovery.DetectAudioEncoders(ffmpeg), cancellationToken);

        List<EncoderInfo> encoders = encodersTask.GetAwaiter().GetResult();

        // An era preset reshapes the live pass (encoder, frame size, rate,
        // bitrate ceiling) and pre-fills the recompress arguments. It never
        // arms the recompress stage on its own -- that stays opt-in.
        PostProcess.EraPreset? era = null;
        if (args.Era is { Length: > 0 } eraId)
        {
            era = PostProcess.EraPresets.Find(eraId);
            if (era is null)
            {
                Console.Error.WriteLine($"ERROR: unknown era preset '{eraId}'; "
                    + $"choose one of {PostProcess.EraPresets.Ids}");
                return 2;
            }
        }

        // --encoder wins over the preset; the preset wins over the built-in default.
        string wantedEncoder = era is not null && !args.EncoderExplicit ? era.Encoder : args.Encoder;

        EncoderInfo? encoder = encoders.FirstOrDefault(
            e => string.Equals(e.Name, wantedEncoder, StringComparison.OrdinalIgnoreCase));
        if (encoder is null)
        {
            Console.Error.WriteLine(
                $"ERROR: {wantedEncoder} is not detected as a legacy/classic video encoder.");
            return 2;
        }
        encoder.Caps = FFmpegCapabilities.ProbeEncoderCaps(ffmpeg, encoder);

        var src = new FileInfo(args.Input ?? "");
        if (!src.Exists)
        {
            Console.Error.WriteLine($"ERROR: input not found: {src.FullName}");
            return 2;
        }

        FileInfo dst = !string.IsNullOrEmpty(args.Output)
            ? new FileInfo(args.Output)
            : DefaultOutput(src, encoder);
        if (string.Equals(Path.GetFullPath(dst.FullName), Path.GetFullPath(src.FullName),
                StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("ERROR: output must differ from input");
            return 2;
        }

        try { Validation.ValidateOutputExtension(dst, encoder); }
        catch (ArgumentException exc) { Console.Error.WriteLine($"ERROR: {exc.Message}"); return 2; }

        // Era values are the fallback, not an override: an explicit flag still wins.
        double effectiveFps = era?.Fps ?? args.Fps;
        int effectiveWidth = era?.Width ?? args.Width;
        int effectiveHeight = era?.Height ?? args.Height;
        double effectiveLimit = era?.LimitKbps ?? args.Limit;
        string effectiveScaleAlgo = era?.ScaleAlgo ?? args.ScaleAlgo;

        (double baseFps, bool fpsExact) = EncoderMath.ChooseBaseFps(effectiveFps, encoder.Caps);
        if (!fpsExact)
            Console.WriteLine(L.T("note.fpsunsupported", encoder.Name, $"{effectiveFps:G6}", $"{baseFps:G6}"));

        (int width, int height, bool sizeExact) = EncoderMath.NormalizeDimensions(
            encoder.Name, effectiveWidth, effectiveHeight);
        if (!sizeExact)
            Console.WriteLine(L.T("note.specialsizes", encoder.Name, width, height));

        // The ladder is written FPS[:drop kbps[:rise kbps]] per entry; a bare FPS
        // keeps the threshold derived from limit/recover, so the old
        // "--fps-levels 13,8,5,3" still means exactly what it always did.
        (List<double> levelFps, List<FpsRungSetting> levelRungs, string? levelError) =
            EncoderMath.ParseLevelSpec(args.FpsLevels);
        if (levelError is not null)
        {
            Console.Error.WriteLine($"ERROR: --fps-levels: {levelError}");
            return 2;
        }

        double recover = args.Recover ?? effectiveLimit * 0.75;

        List<double> levels = EncoderMath.SelectLevels(baseFps, levelFps);
        // A rung faster than the base rate is a mistake: the ladder only ever
        // throttles downwards, and a value above baseFps can never be selected.
        // Only a ladder the user typed is validated. The built-in one starts at
        // 13 fps and an encoder may snap the base rate lower than that
        // (mpeg2video lands on 12); rejecting the default there would break
        // every encoder whose rate list does not contain the default.
        if (args.FpsLevelsExplicit)
        {
            foreach (double candidate in levelFps)
            {
                if (candidate <= baseFps + 1e-9) continue;
                Console.Error.WriteLine(
                    $"ERROR: --fps-levels: {L.T("param.levelstoofast", $"{candidate:G6}", $"{baseFps:G6}")}");
                return 2;
            }
        }
        List<FpsRungSetting> levelKbps = EncoderMath.PruneRungs(levelRungs, levels);

        HashSet<string> audioAvailable = audioEncodersTask.GetAwaiter().GetResult();
        string? audioEncoder = string.IsNullOrEmpty(args.AudioEncoder)
            ? EncoderMath.ChooseAudioEncoder(encoder.FormatName, audioAvailable)
            : args.AudioEncoder;
        if (Constants.NoAudioFormats.Contains(encoder.FormatName)) audioEncoder = null;
        if (audioEncoder is not null && !audioAvailable.Contains(audioEncoder))
        {
            Console.Error.WriteLine($"ERROR: audio encoder not found: {audioEncoder}");
            return 2;
        }

        int? audioRate = era?.AudioRate ?? args.AudioRate;
        int audioChannels = era?.AudioChannels ?? args.AudioChannels;
        if (encoder.Name == "amv")
        {
            if (audioAvailable.Contains("adpcm_ima_amv")) audioEncoder = "adpcm_ima_amv";
            audioRate = 22050;
        }
        else if (encoder.Name == "roqvideo" && audioEncoder == "roq_dpcm")
        {
            audioRate ??= 22050;
            if (audioChannels is not (1 or 2))
            {
                Console.Error.WriteLine("ERROR: roq_dpcm only supports mono or stereo; use --audio-channels 1 or 2");
                return 2;
            }
            if (audioRate != 22050)
            {
                Console.Error.WriteLine(
                    "ERROR: roq_dpcm requires --audio-rate 22050 (or leave it unset for automatic correction)");
                return 2;
            }
        }

        string deinterlace = args.Deinterlace.ToLowerInvariant();
        if (deinterlace != "off" && !availableFilters.Contains(deinterlace))
        {
            Console.Error.WriteLine($"ERROR: FFmpeg filter '{deinterlace}' is not available in this build");
            return 2;
        }

        Config config;
        try
        {
            string scaleAlgo = EncoderMath.CanonicalScaleAlgo(effectiveScaleAlgo);
            if (!availableScaleAlgorithms.Contains(scaleAlgo))
                throw new ArgumentException($"scale algorithm '{scaleAlgo}' is not exposed by this FFmpeg build");

            config = new Config
            {
                InputPath = src,
                OutputPath = dst,
                StartTimeSeconds = args.StartTime,
                EndTimeSeconds = args.EndTime,
                Encoder = encoder,
                AudioEncoder = audioEncoder,
                Fps = baseFps,
                FpsLevels = levels,
                FpsLevelKbps = levelKbps,
                Limit = effectiveLimit,
                Recover = recover,
                Window = args.Window,
                Qv = args.Qv ?? EncoderMath.DefaultQuality(encoder.Name),
                Threads = args.Threads,
                BufferFrames = args.BufferFrames,
                AudioKbps = era?.AudioKbps ?? args.AudioKbps,
                AudioChannels = audioChannels,
                AudioRate = audioRate,
                Width = width,
                Height = height,
                Crop43 = !args.NoCrop,
                ScaleAlgo = scaleAlgo,
                Deinterlace = deinterlace,
                DeintMode = args.DeintMode,
                DeintParity = args.DeintParity,
                DeintMarkedOnly = args.DeintMarkedOnly,
                Hwaccel = args.Hwaccel,
                X264Preset = args.X264Preset,
                X264Tune = args.X264Tune,
                X264Profile = args.X264Profile,
                X264Level = args.X264Level,
                Turbo = args.Turbo,
                Parallel = args.Parallel,
                // --lang wins; without it the stored preference is preserved so a
                // CLI run never silently resets the language chosen in the UI.
                Language = args.Language ?? stored.Language,
                FfmpegSource = args.FfmpegSource,
                FfmpegProxy = args.FfmpegProxy,
                Play = args.Play,
                PlayerPath = args.PlayerPath,
                Watermark = ResolveWatermark(args),
                Recompress = ResolveRecompress(args, era),
                EraPreset = era?.Id ?? (args.Era is null ? stored.EraPreset : null),
            };
            Validation.ValidateCommonConfig(config);
            if (config.EndTimeSeconds is not null && config.EndTimeSeconds <= config.StartTimeSeconds)
                throw new ArgumentException("end time must be later than start time");

            // Check the post-processing stages before the live pass, not after:
            // a live encode runs at the source's pace, so discovering a bad
            // recompress fragment twenty minutes in would waste the whole run.
            ValidatePostProcessing(config);
        }
        catch (ArgumentException exc)
        {
            Console.Error.WriteLine($"ERROR: {exc.Message}");
            return 2;
        }

        dst.Directory?.Create();

        // Remember the run so the next interactive session starts from it. A CLI
        // invocation must never fail because the settings file could not be
        // written, so the result is ignored.
        ConfigStore.Save(ConfigStore.Capture(config));

        var options = new Encoding.RealtimeOptions
        {
            FFmpeg = ffmpeg,
            FFprobe = ffprobe,
            SourcePath = src.FullName,
            DestinationPath = dst.FullName,
            Encoder = encoder,
            Fps = config.Fps,
            FpsLevels = levels,
            FpsLevelKbps = levelKbps,
            Quality = config.Qv,
            Threads = config.Threads,
            LimitKbps = config.Limit,
            RecoverKbps = config.Recover,
            WindowSeconds = config.Window,
            BufferFrames = config.BufferFrames,
            AudioEncoder = audioEncoder,
            AudioKbps = config.AudioKbps,
            AudioChannels = config.AudioChannels,
            AudioRate = config.AudioRate,
            Hwaccel = config.Hwaccel,
            Width = config.Width,
            Height = config.Height,
            Crop43 = config.Crop43,
            ScaleAlgo = config.ScaleAlgo,
            Deinterlace = config.Deinterlace,
            DeintMode = config.DeintMode,
            DeintParity = config.DeintParity,
            DeintMarkedOnly = config.DeintMarkedOnly,
            X264Preset = encoder.Name == "libx264" ? config.X264Preset : null,
            X264Tune = encoder.Name == "libx264" ? config.X264Tune : null,
            X264Profile = encoder.Name == "libx264" ? config.X264Profile : null,
            X264Level = encoder.Name == "libx264" ? config.X264Level : null,
            StartSeconds = config.StartTimeSeconds,
            EndSeconds = config.EndTimeSeconds,
            AvailableFilters = availableFilters,
            AvailableScaleAlgorithms = availableScaleAlgorithms,
            Verbose = !args.Quiet,
            AudioMode = args.AudioMode,
            Turbo = args.Turbo,
            Parallel = args.Parallel,
        };

        Encoding.EncodeResult result = Encoding.RealtimeEncoder.RunSplit(options, cancellationToken);
        if (!args.Quiet) Report.PrintResult(dst.FullName, result);

        // ---- v7.6: watermark, then the video-site recompress pass ----
        string finalPath = dst.FullName;
        if (PostProcess.PostPipeline.Needed(config))
        {
            PostProcess.PostProcessOutcome post =
                PostProcess.PostPipeline.Run(ffmpeg, dst.FullName, config);
            finalPath = post.FinalPath;

            if (!args.Quiet)
            {
                if (post.WatermarkRan && post.WatermarkPath is { Length: > 0 } wm)
                    Console.WriteLine(L.T("post.watermark", wm));
                if (post.RecompressRan && post.RecompressPath is { Length: > 0 } rc)
                    Console.WriteLine(L.T("post.recompress", rc));
                if (post.IntermediateRemoved)
                    Console.WriteLine(L.T("post.intermediate"));
            }

            if (post.Failed)
            {
                string stage = post.FailedStage!;
                string detail = (stage == "watermark" ? post.WatermarkError : post.RecompressError) ?? "";
                Console.Error.WriteLine($"ERROR: {L.T("post.failed", stage, detail)}");
                // Say the live file survived: losing a long encode and then
                // discovering the watermark typo is bad enough without also
                // wondering whether the recording is gone.
                Console.Error.WriteLine(L.T("post.kept", dst.FullName));
                return 1;
            }
        }

        // Only ffplay/external do anything; "off" is a no-op so scripting a run
        // never pops a window.
        if (args.Play != PlayMode.Off)
        {
            (bool played, string? playError) =
                Player.Launch(args.Play, finalPath, ffmpeg, args.PlayerPath);
            if (!played && playError is not null)
                Console.Error.WriteLine($"ERROR: {playError}");
        }
        return 0;
    }

    /// <summary>
    /// CLI-only values, exactly like every other flag in this file.
    ///
    /// Deliberately <em>not</em> seeded from the settings file. The CLI has never
    /// read the stored config for defaults -- scripted runs must be predictable
    /// -- and these two stages are the worst possible place to break that rule:
    /// a recompress pass silently re-encodes an already lossy file, so a setting
    /// that stuck from yesterday's interactive session would keep degrading
    /// today's output. The interactive shell keeps its own state; a CLI run
    /// writes what it was given, same as turbo.
    /// </summary>
    private static WatermarkSettings ResolveWatermark(CliOptions args)
    {
        var wm = new WatermarkSettings
        {
            Enabled = false,
            Text = "",
            Position = "top-right",
            FontSize = 0,
            Margin = 0,
            FontColor = "white",
        };

        if (args.Watermark is { Length: > 0 } text)
        {
            wm.Text = text;
            wm.Enabled = true;
        }
        if (args.WatermarkPosition is { Length: > 0 } pos) wm.Position = pos;
        if (args.WatermarkSize is { } size) wm.FontSize = size;
        if (args.WatermarkColor is { Length: > 0 } colour) wm.FontColor = colour;
        if (args.WatermarkFont is { Length: > 0 } font) wm.FontFile = font;

        if (args.NoWatermark) wm.Enabled = false;
        return wm;
    }

    /// <summary>
    /// Same rule as <see cref="ResolveWatermark"/>: what the command line says,
    /// nothing else. <c>--recompress-args</c> arms the stage on its own -- nobody
    /// writes out a full argument list and then expects it not to run -- while
    /// <c>--recompress</c> alone reuses the arguments of the era preset when one
    /// was given.
    /// </summary>
    private static RecompressSettings ResolveRecompress(CliOptions args, PostProcess.EraPreset? era)
    {
        var rc = new RecompressSettings
        {
            Enabled = false,
            Args = era?.RecompressArgs ?? "",
            Container = era?.RecompressContainer ?? "mp4",
            KeepIntermediate = false,
        };

        if (args.RecompressArgs is { Length: > 0 } a) rc.Args = a;
        if (args.RecompressContainer is { Length: > 0 } c) rc.Container = c;
        if (args.RecompressKeep) rc.KeepIntermediate = true;

        if (args.Recompress) rc.Enabled = true;
        if (args.NoRecompress) rc.Enabled = false;
        return rc;
    }

    /// <summary>
    /// Reject the post-processing settings that cannot work, before anything
    /// expensive happens.
    /// </summary>
    private static void ValidatePostProcessing(Config config)
    {
        if (config.Recompress.Enabled)
        {
            (_, string? error) = PostProcess.PostArgs.Parse(config.Recompress.Args);
            if (error is not null)
                throw new ArgumentException(L.T("post.args.bad", error));

            if (string.IsNullOrWhiteSpace(config.Recompress.Container))
                throw new ArgumentException(L.T("rc.args.empty"));
        }

        if (config.Watermark.Enabled && string.IsNullOrWhiteSpace(config.Watermark.Text))
            throw new ArgumentException(L.T("wm.empty"));
    }

    private static FileInfo DefaultOutput(FileInfo src, EncoderInfo encoder) =>
        new(Path.Combine(src.DirectoryName ?? ".", $"{Path.GetFileNameWithoutExtension(src.Name)}_live.{encoder.Extension}"));
}
