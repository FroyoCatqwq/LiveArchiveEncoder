using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.FFmpeg;
using LiveArchiveEncoder.Localization;
using LiveArchiveEncoder.PostProcess;

namespace LiveArchiveEncoder.UI;

/// <summary>CLI entry path, ported from cli_main().</summary>
public static class Cli
{
    public static int Run(
        CliOptions args, string ffmpeg, string ffprobe, FFmpeg.Capabilities caps,
        CancellationToken cancellationToken = default)
    {
        // Loaded once: the post-processing stages are sticky (a user sets the
        // recompress arguments in the menu and then runs the CLI), so the CLI has
        // to consult the same file the interactive shell writes.
        UserConfig stored = ConfigStore.Load();
        // v7.9.5.1: the user's own era presets live in that file, so --era can
        // name one of them. Done before the lookup below and before any listing.
        EraPresets.Register(stored);

        // v7.9.5: the recompress pass on its own. No live encode, no encoder
        // discovery, no capability probe -- this path exists so an existing
        // file can be put through a site's transcode without re-recording it.
        if (args.RecompressOnly) return RunRecompressOnly(args, ffmpeg, caps);

        // Native rendering is the default: a 2007 preset in H.264 is a cleaner
        // picture than 2007 ever had. --era-modern asks for the viewing copy.
        // Deliberately not read from the settings file -- the CLI has never
        // taken its defaults from there, and a menu toggle from yesterday must
        // not change what today's script produces. (`stored.EraNative` is the
        // shell's memory, exposed as --era-native for a script that wants it.)
        bool eraNative = !args.EraModern;

        // v7.9.5: every capability this run needs comes from the build profile.
        // Both lists used to be a separate `ffmpeg -encoders` launch each.
        List<EncoderInfo> encoders = caps.Encoders;
        HashSet<string> audioAvailable = caps.AudioEncoders;
        HashSet<string> availableFilters = caps.Filters;
        HashSet<string> availableScaleAlgorithms = caps.ScaleAlgorithms;

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

        // A preset's second pass is rendered with the codec that site actually
        // served unless --era-modern asked for a viewing copy (v7.9.5.1). Said
        // out loud: the picture difference between those two is the whole point.
        if (era is not null && !args.Quiet)
        {
            Console.WriteLine(L.T(eraNative ? "note.eranative" : "note.eramodern",
                era.Id, eraNative ? era.VideoCodec : "libx264",
                eraNative ? era.NativeContainer : EraPreset.ModernContainer));
        }

        // --encoder wins over the preset; the preset wins over the built-in
        // default. v7.9.5.1: a site preset names the codec that site served, so
        // it does speak for the live encoder -- but only when the build has it.
        string wantedEncoder = args.Encoder;
        if (era is { Encoder: { Length: > 0 } presetEncoder } && !args.EncoderExplicit)
        {
            if (encoders.Any(e => string.Equals(e.Name, presetEncoder, StringComparison.OrdinalIgnoreCase)))
                wantedEncoder = presetEncoder;
            else
                Console.WriteLine(L.T("note.eracodecmissing", era.Id, presetEncoder));
        }

        EncoderInfo? encoder = encoders.FirstOrDefault(
            e => string.Equals(e.Name, wantedEncoder, StringComparison.OrdinalIgnoreCase));
        if (encoder is null)
        {
            Console.Error.WriteLine(
                $"ERROR: {wantedEncoder} is not detected as a legacy/classic video encoder.");
            return 2;
        }
        encoder.Caps = caps.EncoderCaps(encoder);

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
            encoder.Caps ??= caps.EncoderCaps(encoder);
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

        // v7.9.4: with no --container, the extension the user typed decides
        // between this encoder's containers. mpeg1video writes MPEG-PS by
        // default but takes AVI fine, and "out.avi" is a request, not a typo.
        if (!args.Container.HasText)
        {
            EncoderInfo byExtension = EncoderMath.ResolveByExtension(encoder, dst.Name);
            if (byExtension.FormatName != encoder.FormatName)
            {
                Console.WriteLine(L.T("note.containerfromext",
                    encoder.Name, dst.Extension, byExtension.FormatName));
                encoder = byExtension;
            }
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

        // Caps are probed above and every container swap copies the reference
        // (EncoderInfo.WithContainer), so this is never null here.
        (double baseFps, bool fpsExact) = EncoderMath.ChooseBaseFps(effectiveFps, encoder.Caps!);
        if (!fpsExact)
            Console.WriteLine(L.T("note.fpsunsupported", encoder.Name, $"{effectiveFps:G6}", $"{baseFps:G6}"));

        // v7.9.5: .h261/.h263/.a64 are bare bitstreams -- no timestamps, so the
        // rate the file was encoded at is not in the file. The picture is fine;
        // saying nothing would leave the rate looking verified when it is not.
        if (Constants.RateLessFormats.Contains(encoder.FormatName))
            Console.WriteLine(L.T("note.rateless", encoder.FormatName, $"{baseFps:G6}"));

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
        // "A preset's number is a suggestion" only applies when the preset
        // actually supplied one: a site preset (v7.9.5) leaves the live audio
        // alone, so the value here is the user's and must not be retuned.
        if (era?.AudioKbps is not null) audioKbps = AudioCatalog.NearestKbps(audioEncoder, audioKbps, audioRate);
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
        // The second pass is resolved before the Config literal so a bad codec /
        // container pair is reported as an argument error, not lost in a chain.
        (RecompressSettings recompress, string? recompressError) =
            ResolveRecompress(args, era, eraNative);
        try
        {
            if (recompressError is not null) throw new ArgumentException(recompressError);

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
                Recompress = recompress,
                EraPreset = era?.Id ?? (args.Era is null ? stored.EraPreset : null),
                EraNative = eraNative,
                // v7.9.5.1: the user's own presets travel with the run, so
                // saving the settings file keeps them.
                CustomEras = [.. EraPresets.Custom],
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

        // v7.9.5.1: an ASS file names its fonts. Say which of them are not in
        // the program's own directory, so "the subtitles are boxes" is answered
        // before the run rather than after it. System fonts are not consulted
        // here on purpose: the point is what travels with the tool.
        if (args.SubtitleMode == "burn" && !args.Quiet)
        {
            foreach (FontLookup lookup in
                     SubtitleFonts.Check(args.SubtitleFiles, args.SubtitleFontDir, includeSystem: false))
            {
                if (lookup.FoundAt is null)
                    Console.WriteLine(L.T("note.fontmissing", lookup.Required, FontResolver.FontDirectory));
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
                Encoding.SubtitleTrack.ParseMode(args.SubtitleMode), args.SubtitleCodec)
            {
                FontDir = args.SubtitleFontDir,
                FallbackFont = args.SubtitleFont,
            },
            // Detected once here (and inherited by shards) instead of once per
            // shard: the copy path needs the build's own encoder list.
            AvailableSubtitleEncoders = args.SubtitleFiles.Count > 0 || args.SubtitleStream is not null
                ? caps.SubtitleEncoders
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
    /// <c>--recompress-only</c>: put an existing file through the site pass and
    /// stop. Nothing else in the program is involved -- no live encode, no
    /// encoder choice, no shard planner -- which is the point: a recording made
    /// last year, or a file somebody sent you, can be "uploaded" without being
    /// recorded again first.
    /// </summary>
    private static int RunRecompressOnly(CliOptions args, string ffmpeg, FFmpeg.Capabilities caps)
    {
        if (string.IsNullOrEmpty(args.Input))
        {
            Console.Error.WriteLine("ERROR: --recompress-only requires an input file");
            return 2;
        }
        if (Remote.HttpSource.IsUrl(args.Input))
        {
            Console.Error.WriteLine(
                "ERROR: --recompress-only works on a local file; download the URL first");
            return 2;
        }

        var src = new FileInfo(args.Input);
        if (!src.Exists)
        {
            Console.Error.WriteLine($"ERROR: input not found: {src.FullName}");
            return 2;
        }

        // An era/site preset carries the fragment, so --era alone is enough.
        PostProcess.EraPreset? era = args.Era is { Length: > 0 } eraId
            ? PostProcess.EraPresets.Find(eraId)
            : null;
        if (args.Era is { Length: > 0 } && era is null)
        {
            Console.Error.WriteLine($"ERROR: unknown era preset '{args.Era}'; "
                + $"choose one of {PostProcess.EraPresets.Ids}");
            return 2;
        }

        bool eraNative = !args.EraModern;
        (RecompressSettings settings, string? resolveError) = ResolveRecompress(args, era, eraNative);
        if (resolveError is not null)
        {
            Console.Error.WriteLine($"ERROR: {L.T("post.args.bad", resolveError)}");
            return 2;
        }

        // Naming a fragment is the request; --no-recompress still wins, so a
        // script that turns the stage off keeps it off.
        if (!settings.Enabled)
        {
            Console.Error.WriteLine(
                "ERROR: --recompress-only needs --recompress-args, --recompress-codec or --era "
                + "(nothing to apply)");
            return 2;
        }

        (List<string> tokens, string? fragmentError) = PostProcess.PostArgs.Parse(settings.Args);
        if (fragmentError is not null)
        {
            Console.Error.WriteLine($"ERROR: {L.T("post.args.bad", fragmentError)}");
            return 2;
        }

        // The build has to have the encoder the fragment names, or the failure
        // arrives as an FFmpeg message twenty seconds in. Only checked for codecs
        // this tool knows by name; "copy" and anything newer are left alone.
        if (RecompressCodecs.DetectVideoCodec(tokens) is { } video && !caps.VideoEncoders.Contains(video))
        {
            Console.Error.WriteLine($"ERROR: {L.T("rc.codecmissing", video)}");
            return 2;
        }
        if (RecompressCodecs.DetectAudioCodec(tokens) is { } audio
            && !string.Equals(audio, RecompressCodecs.CopyAudio, StringComparison.Ordinal)
            && !caps.AudioEncoders.Contains(audio))
        {
            Console.Error.WriteLine($"ERROR: {L.T("rc.codecmissing", audio)}");
            return 2;
        }

        string container = settings.Container.Or("mp4").TrimStart('.').ToLowerInvariant();
        var dst = string.IsNullOrEmpty(args.Output)
            ? new FileInfo(PostProcess.Recompress.DeriveOutputPath(src.FullName, container))
            : new FileInfo(args.Output);

        if (string.Equals(Path.GetFullPath(dst.FullName), src.FullName, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("ERROR: output must differ from input");
            return 2;
        }

        if (!args.Quiet)
        {
            Console.WriteLine(L.T("rc.only.input", src.FullName));
            Console.WriteLine(L.T("rc.only.output", dst.FullName));
            Console.WriteLine($" {string.Join(' ', PostProcess.Recompress.BuildArgv(
                ffmpeg, src.FullName, dst.FullName, settings).Argv)}");
        }

        (bool ok, string? error) = PostProcess.Recompress.Apply(ffmpeg, src.FullName, dst.FullName, settings);
        if (!ok)
        {
            Console.Error.WriteLine($"ERROR: {L.T("post.failed", "recompress", error ?? "")}");
            return 1;
        }

        if (!args.Quiet) Console.WriteLine(L.T("rc.only.done", dst.FullName));
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
    private static (RecompressSettings Settings, string? Error) ResolveRecompress(
        CliOptions args, PostProcess.EraPreset? era, bool eraNative)
    {
        var rc = new RecompressSettings
        {
            Enabled = false,
            Args = era?.Fragment(eraNative) ?? "",
            Container = era?.ContainerFor(eraNative) ?? "mp4",
        };

        // v7.9.5.1: naming a codec is an alternative to writing a fragment, and
        // the container follows from the pair when the user did not pick one.
        if (args.RecompressCodec is { Length: > 0 } codecName)
        {
            SecondPassVideoCodec? codec = RecompressCodecs.FindVideo(codecName);
            if (codec is null)
                return (rc, $"unknown recompress codec '{codecName}'; known: {RecompressCodecs.VideoIdList}");

            string audio = args.RecompressAudioCodec ?? RecompressCodecs.RecommendedAudio(codec.Name);
            if (!RecompressCodecs.IsCopyAudio(audio) && RecompressCodecs.FindAudio(audio) is null)
                return (rc, $"unknown audio codec '{audio}'; known: {RecompressCodecs.AudioIdList}, or copy");

            rc.Args = string.Join(' ',
                RecompressCodecs.VideoArgs(codec, args.RecompressQuality ?? codec.DefaultQuality),
                RecompressCodecs.AudioArgs(audio, args.RecompressAudioKbps, null, null));
            rc.Container = args.RecompressContainer
                ?? RecompressCodecs.DefaultContainer(codec.Name, audio)
                ?? codec.Containers[0];
        }

        if (args.RecompressArgs is { Length: > 0 } a) rc.Args = a;
        if (args.RecompressContainer is { Length: > 0 } c) rc.Container = c;

        // A pair the muxer cannot hold is a fact about FFmpeg, not a matter of
        // taste: refuse it here, while there is nothing to undo. Only checked
        // when the fragment names a codec this tool knows.
        if (rc.Args.Length > 0)
        {
            (List<string> tokens, string? parseError) = PostProcess.PostArgs.Parse(rc.Args);
            if (parseError is not null) return (rc, L.T("post.args.bad", parseError));

            string? video = RecompressCodecs.DetectVideoCodec(tokens);
            if (video is not null)
            {
                string? audio = RecompressCodecs.DetectAudioCodec(tokens);

                // A hand-written fragment that names VP9 should not land in
                // "clip_web.mp4": the container follows the codec unless the
                // user pinned one.
                if (args.RecompressContainer is not { Length: > 0 }
                    && era is null && args.RecompressCodec is null)
                {
                    rc.Container = RecompressCodecs.DefaultContainer(video, audio) ?? rc.Container;
                }

                string? mismatch = RecompressCodecs.ValidateContainer(video, audio, rc.Container);
                if (mismatch is not null) return (rc, L.T("rc.container.mismatch", mismatch));
            }
        }

        if (args.Recompress) rc.Enabled = true;
        if (args.NoRecompress) rc.Enabled = false;
        return (rc, null);
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
