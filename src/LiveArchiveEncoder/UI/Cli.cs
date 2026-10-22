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

        // v7.8: the container is part of the encoder selection, so it is
        // applied before anything reads FormatName -- the output extension
        // check, the audio codec choice and the muxer argument all follow it.
        if (args.Container is { Length: > 0 } wantedContainer)
        {
            EncoderInfo? retargeted = EncoderMath.ResolveContainer(encoder, wantedContainer);
            if (retargeted is null)
            {
                string allowed = string.Join(", ",
                    EncoderMath.AllowedContainers(encoder).Select(x => x.Format));
                Console.Error.WriteLine($"ERROR: encoder '{encoder.Name}' cannot write "
                    + $"'{wantedContainer}'; it supports: {allowed}");
                return 2;
            }
            encoder = retargeted;
            encoder.Caps ??= FFmpegCapabilities.ProbeEncoderCaps(ffmpeg, encoder);
        }

        // v7.9: an HTTP(S) URL is an input too. It has no FileInfo and no
        // existence check -- the probe inside the encoder decides whether it is
        // any good, and its error names the URL either way.
        bool urlInput = Remote.HttpSource.IsUrl(args.Input);
        var src = urlInput ? null : new FileInfo(args.Input ?? "");
        if (src is not null && !src.Exists)
        {
            Console.Error.WriteLine($"ERROR: input not found: {src.FullName}");
            return 2;
        }

        // v7.9.1: a network drive is a single-pass encode on purpose. The hint
        // prints for every network source (stability is the user's to manage);
        // an explicit --parallel N is overridden rather than honoured, because
        // N concurrent readers on one flaky link is the stall itself.
        string sourceForPolicy = urlInput ? args.Input! : src!.FullName;
        bool networkSource = Remote.Smb.IsRemotePath(sourceForPolicy);
        if (networkSource)
        {
            Console.WriteLine(Remote.Smb.StabilityHint(sourceForPolicy));
            if (args.Parallel > 1) Console.WriteLine(L.T("parallel.networkoff"));
        }
        int parallel = networkSource ? 1 : args.Parallel;

        FileInfo dst = !string.IsNullOrEmpty(args.Output)
            ? new FileInfo(args.Output)
            : DefaultOutput(src, encoder, urlInput ? args.Input! : null);
        if (!urlInput && string.Equals(Path.GetFullPath(dst.FullName), Path.GetFullPath(src!.FullName),
                StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("ERROR: output must differ from input");
            return 2;
        }

        try { Validation.ValidateOutputExtension(dst, encoder); }
        catch (ArgumentException exc) { Console.Error.WriteLine($"ERROR: {exc.Message}"); return 2; }

        // Era values are the fallback, not an override: an explicit flag still
        // wins (v7.9.3.1 -- this used to read `era?.Fps ?? args.Fps`, and since
        // every one of those properties has a non-nullable default, the preset
        // won every time. The manual has always said the opposite: "预设是兜底，
        // 不是覆盖").
        double effectiveFps = args.FpsExplicit ? args.Fps : era?.Fps ?? args.Fps;
        int effectiveWidth = args.WidthExplicit ? args.Width : era?.Width ?? args.Width;
        int effectiveHeight = args.HeightExplicit ? args.Height : era?.Height ?? args.Height;
        double effectiveLimit = args.LimitExplicit ? args.Limit : era?.LimitKbps ?? args.Limit;
        string effectiveScaleAlgo = args.ScaleAlgoExplicit
            ? args.ScaleAlgo
            : era?.ScaleAlgo ?? args.ScaleAlgo;

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

        // An explicit --audio-rate is validated; an era preset's rate is a
        // suggestion and gets snapped, because a preset is written for a period
        // rather than for the codec the container happens to force. The same
        // split applies to the bitrate: mp2 refuses 16 kbps at 44.1 kHz and
        // wmav2 refuses anything under 24, so a preset value is clamped into
        // the band its (already snapped) rate allows, while an explicit
        // --audio-kbps is reported as the error it is.
        int? audioRate = args.AudioRate ?? era?.AudioRate;
        bool audioRateExplicit = args.AudioRate is not null;
        // Same rule as the picture: an explicit flag beats the preset.
        // --audio-rate needs no flag because null already means "not given".
        int audioChannels = args.AudioChannelsExplicit
            ? args.AudioChannels
            : era?.AudioChannels ?? args.AudioChannels;
        int audioKbps = args.AudioKbpsExplicit ? args.AudioKbps : era?.AudioKbps ?? args.AudioKbps;
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

        if (!audioRateExplicit) audioRate = AudioCatalog.NearestRate(audioEncoder, audioRate, encoder.FormatName);
        audioChannels = AudioCatalog.ClampChannels(audioEncoder, audioChannels);
        if (era is not null) audioKbps = AudioCatalog.NearestKbps(audioEncoder, audioKbps, audioRate);
        string? audioError = AudioCatalog.Validate(
            audioEncoder, encoder.FormatName, audioRate, audioChannels, audioKbps);
        if (audioError is not null)
        {
            Console.Error.WriteLine($"ERROR: {audioError}");
            return 2;
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
                // A URL is not a FileInfo and the interactive shell cannot
                // browse to one, so it stays out of the persisted input slot:
                // the run itself carries it in RealtimeOptions.SourcePath.
                InputPath = urlInput ? null : src,
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
                AudioKbps = audioKbps,
                AudioChannels = audioChannels,
                AudioRate = audioRate,
                // v7.9: which source streams to use. An index that does not
                // name a stream of the right type is refused by the encoder,
                // against the source's own probe.
                VideoStream = args.VideoStream,
                AudioStream = args.AudioStream,
                Container = args.Container,
                Width = width,
                Height = height,
                FitMode = args.FitMode,
                CropAnchor = args.CropAnchor,
                PadColor = args.PadColor,
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
                // The effective count, not the flag: a network source forces a
                // single pass, and the settings file must not remember a shard
                // count the next run would have to undo again.
                Parallel = parallel,
                // --lang wins; without it the stored preference is preserved so a
                // CLI run never silently resets the language chosen in the UI.
                Language = args.Language ?? stored.Language,
                FfmpegSource = args.FfmpegSource,
                FfmpegProxy = args.FfmpegProxy,
                Play = args.Play,
                PlayerPath = args.PlayerPath,
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

        // v7.9 #2: same rule for subtitles -- a typo in a sidecar path must
        // cost nothing, and the encoder re-checks the rest at probe time.
        foreach (string file in args.SubtitleFiles)
        {
            if (!File.Exists(file))
            {
                Console.Error.WriteLine($"ERROR: subtitle file not found: {file}");
                return 2;
            }
        }
        }
        catch (ArgumentException exc)
        {
            Console.Error.WriteLine($"ERROR: {exc.Message}");
            return 2;
        }

        // ---- v7.9.1: settings-file conversion ----
        // Both exits sit after validation (a bad setting must still be the
        // error it is) and before anything expensive: capturing a command line
        // as a settings file must not cost a twenty-minute encode, and printing
        // the equivalent command line must not either.
        if (args.SaveConfig is { Length: > 0 } || args.EmitArgs)
        {
            UserConfig captured = ConfigStore.Capture(config);
            if (args.SaveConfig is { Length: > 0 } savePath)
            {
                if (!ConfigStore.Save(captured, savePath))
                {
                    Console.Error.WriteLine($"ERROR: could not write the settings file: {savePath}");
                    return 2;
                }
                if (!args.Quiet) Console.WriteLine(L.T("config.saved", savePath));
            }
            if (args.EmitArgs)
                Console.WriteLine(CliArgs.Join(CliArgs.FromUserConfig(captured)));
            // The default-path save below is deliberately skipped: a conversion
            // must not disturb the settings the interactive shell left behind.
            return 0;
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
            SourcePath = urlInput ? args.Input! : src!.FullName,
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
            VideoStream = config.VideoStream,
            AudioStream = config.AudioStream,
            Subtitles = new Encoding.SubtitleSettings(
                args.SubtitleFiles, args.SubtitleStream,
                Encoding.SubtitleTrack.ParseMode(args.SubtitleMode), args.SubtitleCodec),
            // Detected once here (and inherited by shards) instead of once per
            // shard: the copy path needs the build's own encoder list.
            AvailableSubtitleEncoders = args.SubtitleFiles.Count > 0 || args.SubtitleStream is not null
                ? FFmpeg.FFmpegCapabilities.DetectSubtitleEncoders(ffmpeg)
                : null,
            Hwaccel = config.Hwaccel,
            Width = config.Width,
            Height = config.Height,
            FitMode = config.FitMode,
            CropAnchor = config.CropAnchor,
            PadColor = config.PadColor,
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
                Parallel = parallel,
            };

        Encoding.EncodeResult result = Encoding.RealtimeEncoder.RunSplit(options, cancellationToken);
        if (!args.Quiet) Report.PrintResult(dst.FullName, result);

        // ---- v7.6: the video-site recompress pass ----
        string finalPath = dst.FullName;
        if (PostProcess.PostPipeline.Needed(config))
        {
            PostProcess.PostProcessOutcome post =
                PostProcess.PostPipeline.Run(ffmpeg, dst.FullName, config);
            finalPath = post.FinalPath;

            if (!args.Quiet)
            {
                if (post.RecompressRan && post.RecompressPath is { Length: > 0 } rc)
                    Console.WriteLine(L.T("post.recompress", rc));
            }

            if (post.Failed)
            {
                string stage = post.FailedStage!;
                string detail = post.RecompressError ?? "";
                Console.Error.WriteLine($"ERROR: {L.T("post.failed", stage, detail)}");
                // Say the live file survived: losing a long encode and then
                // discovering the recompress typo is bad enough without also
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
    /// -- and this stage is the worst possible place to break that rule:
    /// a recompress pass silently re-encodes an already lossy file, so a setting
    /// that stuck from yesterday's interactive session would keep degrading
    /// today's output. The interactive shell keeps its own state; a CLI run
    /// writes exactly what it was given.
    /// </summary>
    private static RecompressSettings ResolveRecompress(CliOptions args, PostProcess.EraPreset? era)
    {
        var rc = new RecompressSettings
        {
            Enabled = false,
            Args = era?.RecompressArgs ?? "",
            Container = era?.RecompressContainer ?? "mp4",
        };

        if (args.RecompressArgs is { Length: > 0 } a) rc.Args = a;
        if (args.RecompressContainer is { Length: > 0 } c) rc.Container = c;

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

            if (!config.Recompress.Container.HasText)
                throw new ArgumentException(L.T("rc.args.empty"));
        }
    }

    private static FileInfo DefaultOutput(FileInfo? src, EncoderInfo encoder, string? url = null)
    {
        // v7.9: a URL has no directory to write next to, so the product lands in
        // the working directory under the name the URL ends with.
        if (url is not null)
        {
            string stem = Remote.HttpSource.FileNameFromUrl(url);
            int dot = stem.LastIndexOf('.');
            if (dot > 0) stem = stem[..dot];
            return new(Path.Combine(".", $"{stem}_live.{encoder.Extension}"));
        }
        return new(Path.Combine(src!.DirectoryName ?? ".", $"{Path.GetFileNameWithoutExtension(src.Name)}_live.{encoder.Extension}"));
    }
}
