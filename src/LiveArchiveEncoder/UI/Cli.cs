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
            // v7.9.7: ids are site/year now. The pre-rewrite spellings still
            // resolve (cctv03 -> cctv/2003); say so once, because a script that
            // prints its own settings should not keep emitting a dead id.
            if (!args.Quiet && PostProcess.EraPresets.IsAlias(eraId)
                && PostProcess.EraPresets.Canonical(eraId) is { } renamed)
            {
                Console.WriteLine(L.T("note.eraalias", eraId.Trim(), renamed));
            }
            era = PostProcess.EraPresets.Find(eraId);
            if (era is null)
            {
                Console.Error.WriteLine($"ERROR: unknown era preset '{eraId}'; "
                    + $"sites are {PostProcess.EraPresets.SitesList} "
                    + $"(ids look like cctv/2003)");
                return 2;
            }
        }

        // v7.9.7: the H.264 encoder to use wherever this run says H.264 -- the
        // user's --h264 first, then the preset's own choice, then libx264.
        string h264Impl = era?.EffectiveH264(args.H264Impl)
            ?? args.H264Impl
            ?? stored.H264Impl
            ?? "libx264";
        if (era?.H264Impl is { Length: > 0 } presetH264 && !args.H264Explicit && !args.Quiet)
            Console.WriteLine(L.T("note.erah264", era.Id, presetH264));

        // A preset's second pass is rendered with the codec that site actually
        // served unless --era-modern asked for a viewing copy (v7.9.5.1). Said
        // out loud: the picture difference between those two is the whole point.
        if (era is not null && !args.Quiet)
        {
            string nativeCodec = string.Equals(era.VideoCodec, "libx264", StringComparison.OrdinalIgnoreCase)
                ? h264Impl
                : era.VideoCodec;
            Console.WriteLine(L.T(eraNative ? "note.eranative" : "note.eramodern",
                era.Id, eraNative ? nativeCodec : h264Impl,
                eraNative ? era.NativeContainer : EraPreset.ModernContainer));
        }

        // --encoder wins over the preset; the preset wins over the built-in
        // default. v7.9.5.1: a site preset names the codec that site served, so
        // it does speak for the live encoder -- but only when the build has it.
        string wantedEncoder = args.Encoder;
        if (era is { Encoder: { Length: > 0 } presetEncoder } && !args.EncoderExplicit)
        {
            // An H.264 row names "the H.264 encoder of that period", so --h264
            // moves the live pass too; a FLV, WMV or RealVideo row keeps its own
            // codec. The test is the *live* encoder, not the delivery codec --
            // a generic year captures with rv20 and delivers H.264.
            bool h264Row = presetEncoder is "libx264" or "libopenh264";
            string candidate = h264Row ? h264Impl : presetEncoder;
            if (encoders.Any(e => string.Equals(e.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                wantedEncoder = candidate;
            else
                Console.WriteLine(L.T("note.eracodecmissing", era.Id, candidate));
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

        // v7.9.6.3: a network source is no longer forced to a single pass. The
        // old rule ("N readers on one flaky link is the stall itself") is true
        // of a congested Wi-Fi hop and wrong about a gigabit LAN, so the
        // decision moved to the encoder, where the source duration and bitrate
        // are known and the link can actually be measured (NetworkProbe). The
        // stability hint stays: it is advice about the link, not a policy.
        string sourceForPolicy = urlInput ? args.Input! : src!.FullName;
        bool networkSource = Remote.Smb.IsRemotePath(sourceForPolicy);
        if (networkSource) Console.WriteLine(Remote.Smb.StabilityHint(sourceForPolicy));
        int parallel = args.Parallel;

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

        // v7.9.6.3: a preset captured at another frame shape. Resolved here --
        // after the encoder is known -- because whether a shape is available is
        // a fact about the encoder (H.261/H.263 only exist at CIF sizes), and
        // asking FFmpeg to find that out twenty minutes into a run is not an
        // error message, it is a wasted afternoon.
        (int W, int H)? eraSize = null;
        if (era is not null && PostProcess.EraAspects.TryParse(args.EraAspect, out PostProcess.EraAspect wantedAspect)
            && wantedAspect != PostProcess.EraAspect.Source)
        {
            // An explicit --width/--height is a size the user typed; mixing one
            // of those with a shape would produce a pair neither asked for, so
            // the shape steps aside and says nothing (the flag is documented as
            // a fallback, like every other preset value).
            if (args.WidthExplicit || args.HeightExplicit)
            {
                if (!args.Quiet) Console.WriteLine(L.T("note.eraaspectoverridden"));
            }
            else
            {
                eraSize = PostProcess.EraAspects.Resolve(era, wantedAspect, encoder.Name, out string? aspectReason);
                if (eraSize is null)
                {
                    Console.Error.WriteLine($"ERROR: --era-aspect {PostProcess.EraAspects.Label(wantedAspect)}: "
                        + $"{aspectReason}");
                    return 2;
                }
                if (!args.Quiet)
                {
                    Console.WriteLine(L.T("note.eraaspect", era.Id, PostProcess.EraAspects.Label(wantedAspect),
                        $"{eraSize.Value.W}x{eraSize.Value.H}"));
                }
            }
        }

        // Era values are the fallback, not an override: an explicit flag still
        // wins (v7.9.3.1 -- this used to read `era?.Fps ?? args.Fps`, and since
        // every one of those properties has a non-nullable default, the preset
        // won every time. The manual has always said the opposite: "预设是兜底，
        // 不是覆盖").
        double effectiveFps = args.FpsExplicit ? args.Fps : era?.Fps ?? args.Fps;
        int effectiveWidth = args.WidthExplicit ? args.Width : eraSize?.W ?? era?.Width ?? args.Width;
        int effectiveHeight = args.HeightExplicit ? args.Height : eraSize?.H ?? era?.Height ?? args.Height;
        double effectiveLimit = args.LimitExplicit ? args.Limit : era?.LimitKbps ?? args.Limit;
        string effectiveScaleAlgo = args.ScaleAlgoExplicit
            ? args.ScaleAlgo
            : era?.ScaleAlgo ?? args.ScaleAlgo;

        // Caps are probed above and every container swap copies the reference
        // (EncoderInfo.WithContainer), so this is never null here.
        (double baseFps, bool fpsExact) = EncoderMath.ChooseBaseFps(effectiveFps, encoder.Caps!);
        if (!fpsExact)
            Console.WriteLine(L.T("note.fpsunsupported", encoder.Name, $"{effectiveFps:G6}", $"{baseFps:G6}"));

        // v7.9.6: an explicit pixel format is checked against what this encoder
        // declares, and against the container, before anything is probed or
        // encoded. Both failures are argument errors (exit 2), not run failures:
        // nothing has been written yet.
        if (args.PixFmt is { Length: > 0 } rawPix)
        {
            string? pixError = PixelFormatCatalog.Validate(rawPix, encoder.Caps!);
            if (pixError is not null)
            {
                Console.Error.WriteLine($"ERROR: {pixError}");
                return 2;
            }
            string pixName = PixelFormatCatalog.Normalise(rawPix);
            string? conflict = PixelFormatUtil.PaletteContainerConflict(pixName, encoder.FormatName);
            if (conflict is not null)
            {
                Console.Error.WriteLine($"ERROR: {conflict}");
                return 2;
            }
        }

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
        //
        // v7.9.6.1: an era preset carries the ladder of its period as well as
        // its base rate. It is a fallback like every other preset value, so it
        // only applies when the user typed neither --fps nor --fps-levels --
        // pinning a base rate without pinning the rungs means "derive them from
        // what I pinned", which is the behaviour scripts already rely on.
        bool eraLadder = !args.FpsLevelsExplicit && !args.FpsExplicit
            && era?.FpsLevels is { Length: > 1 };
        (List<double> levelFps, List<FpsRungSetting> levelRungs, string? levelError) = eraLadder
            ? ([.. era!.FpsLevels!], [], null)
            : EncoderMath.ParseLevelSpec(args.FpsLevels);
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

        // v7.9.6.2: --audio-encoder wins, then the preset's own codec (a 2007
        // portal took mp3, not whatever the container prefers), then the
        // container's default. The preset value used to be ignored here even
        // though the menu honoured it -- the two paths disagreed.
        string? audioEncoder = args.AudioEncoder is { Length: > 0 } explicitAudio
            ? explicitAudio
            : era?.AudioEncoder is { Length: > 0 } eraAudio
                && audioAvailable.Any(x => string.Equals(x, eraAudio, StringComparison.OrdinalIgnoreCase))
                ? eraAudio
                : EncoderMath.ChooseAudioEncoder(encoder.FormatName, audioAvailable);
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

        // v7.9.8.2: the era's deinterlacer, the same fallback rule as every other
        // preset value -- but the *whole* deinterlace block follows it. Naming a
        // filter by hand means the mode, the parity and the marked-only flag are
        // the caller's too; half a preset and half a flag is how a run ends up
        // deinterlacing with a filter that did not exist in the year it claims.
        string deinterlace = (args.DeinterlaceExplicit
                ? args.Deinterlace
                : era?.Deinterlace ?? args.Deinterlace)
            .ToLowerInvariant();
        if (deinterlace != "off" && !availableFilters.Contains(deinterlace))
        {
            Console.Error.WriteLine($"ERROR: FFmpeg filter '{deinterlace}' is not available in this build");
            return 2;
        }

        Config config;
        // The second pass is resolved before the Config literal so a bad codec /
        // container pair is reported as an argument error, not lost in a chain.
        (RecompressSettings recompress, string? recompressError) =
            ResolveRecompress(args, era, eraNative, eraSize, h264Impl);
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
                Window = args.WindowExplicit ? args.Window : era?.WindowSeconds ?? args.Window,
                // v7.9.8.2: the preset's own quality, read in the scale of the
                // encoder that won -- an era row states it twice for exactly
                // this reason. Falling through to the encoder's default is the
                // pre-v7.9.8.2 behaviour: 28 for libx264 no matter which year
                // was asked for, which for a 2007 row is a third of its rate.
                Qv = args.Qv ?? era?.QualityFor(encoder.Name) ?? EncoderMath.DefaultQuality(encoder.Name),
                Threads = args.Threads,
                BufferFrames = args.BufferFramesExplicit
                    ? args.BufferFrames
                    : era?.BufferFrames ?? args.BufferFrames,
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
                PixFmt = args.PixFmt,
                ColorRange = args.ColorRange,
                ColorSpace = args.ColorSpace,
                ColorPrimaries = args.ColorPrimaries,
                ColorTransfer = args.ColorTransfer,
                Dither = args.Dither,
                Deinterlace = deinterlace,
                // The block travels with the filter: see the note where
                // `deinterlace` is resolved above.
                DeintMode = !args.DeinterlaceExplicit && era is not null ? era.DeintMode : args.DeintMode,
                DeintParity = !args.DeinterlaceExplicit && era is not null ? era.DeintParity : args.DeintParity,
                DeintMarkedOnly = !args.DeinterlaceExplicit && era is not null
                    ? era.DeintMarkedOnly
                    : args.DeintMarkedOnly,
                Hwaccel = args.Hwaccel,
                X264Preset = args.X264Preset,
                X264Tune = args.X264Tune,
                X264Profile = args.X264Profile,
                X264Level = args.X264Level,
                // The effective count, not the flag: the settings file must not
                // remember a shard count the next run would have to undo again.
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
                H264Impl = h264Impl,
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
            // v7.9.8.3: the subtitle request lives on the command line only -- the
            // Config the encoder receives is assembled further down, so a settings
            // file written here used to forget every --subtitle / --subtitle-aspect
            // and the "equivalent command line" lost them too. Fold the flags into
            // the snapshot before capturing it.
            if (args.SubtitleFiles.Count > 0 || args.SubtitleStream is not null)
            {
                config.Subtitles = new Configuration.SubtitleConfig
                {
                    Mode = args.SubtitleMode,
                    Files = [.. args.SubtitleFiles],
                    Stream = args.SubtitleStream,
                    Codec = args.SubtitleCodec,
                    FontDir = args.SubtitleFontDir,
                    FallbackFont = args.SubtitleFont,
                    BoxAspect = args.SubtitleAspect ?? config.Subtitles.BoxAspect,
                };
            }

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

        // v7.9.8.3: nnedi needs a weights file, and "which file" is a question
        // only this layer can answer (an explicit flag, then the settings file,
        // then the managed install). Resolved once here so shards inherit it.
        string? nnediWeights = null;
        if (Constants.NeedsWeights(config.Deinterlace))
        {
            nnediWeights = FFmpeg.Nnedi.Resolve(args.NnediWeights ?? config.NnediWeights);
            if (nnediWeights is null)
            {
                Console.Error.WriteLine($"ERROR: {L.T("nnedi.missing")}");
                return 2;
            }
            if (!args.Quiet) Console.WriteLine(L.T("nnedi.inuse", nnediWeights));
        }

        var options = new Encoding.RealtimeOptions
        {
            FFmpeg = ffmpeg,
            FFprobe = ffprobe,
            SourcePath = urlInput ? args.Input! : src!.FullName,
            DestinationPath = dst.FullName,
            Encoder = encoder,
            // v7.9.6.3: an iperf3 server, when the user named one, is an extra
            // link reference the encoder may quote alongside its own
            // measurement of the share itself.
            Iperf3Host = args.Iperf3Host,
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
                BoxAspect = args.SubtitleAspect ?? config.Subtitles.BoxAspect,
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
            PixFmt = config.PixFmt,
            ColorRange = config.ColorRange,
            ColorSpace = config.ColorSpace,
            ColorPrimaries = config.ColorPrimaries,
            ColorTransfer = config.ColorTransfer,
            Dither = config.Dither,
            Deinterlace = config.Deinterlace,
            NnediWeights = nnediWeights,
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

        // v7.9.6: a declared pixel format is not a written one -- libx264 lists
        // grey and then encodes 4:2:0. One ffprobe call settles it.
        if (!args.Quiet && config.PixFmt is { Length: > 0 })
            Report.CheckOutputPixFmt(ffprobe, dst, config.PixFmt);

        // ---- v7.6: the video-site recompress pass ----
        string finalPath = dst.FullName;
        if (PostProcess.PostPipeline.Needed(config))
        {
            PostProcess.PostProcessOutcome post =
                PostProcess.PostPipeline.Run(ffmpeg, dst.FullName, config);
            finalPath = post.FinalPath;

            if (!args.Quiet)
            {
                // v7.9.8.3: one line per hop; a chain's intermediate files are
                // products in their own right and the user may want them.
                foreach (string hop in post.Passes) Console.WriteLine(L.T("post.recompress", hop));
            }

            if (post.Failed)
            {
                string stage = post.StageName;
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
        string h264Impl = era?.EffectiveH264(args.H264Impl) ?? args.H264Impl ?? "libx264";
        // The second pass is the only pass here, so the encoder whose limits
        // matter is the one the preset delivers with.
        (int W, int H)? eraSize = null;
        if (era is not null && PostProcess.EraAspects.TryParse(args.EraAspect, out PostProcess.EraAspect aspect)
            && aspect != PostProcess.EraAspect.Source)
        {
            eraSize = PostProcess.EraAspects.Resolve(era, aspect, era.VideoCodec, out string? reason);
            if (eraSize is null)
            {
                Console.Error.WriteLine($"ERROR: --era-aspect {PostProcess.EraAspects.Label(aspect)}: {reason}");
                return 2;
            }
        }

        (RecompressSettings settings, string? resolveError) = ResolveRecompress(args, era, eraNative, eraSize, h264Impl);
        if (resolveError is not null)
        {
            Console.Error.WriteLine($"ERROR: {L.T("post.args.bad", resolveError)}");
            return 2;
        }

        List<RecompressPass> stages = settings.Stages();
        if (!settings.Enabled || stages.Count == 0)
        {
            Console.Error.WriteLine(
                "ERROR: --recompress-only needs --recompress-args, --recompress-codec or --era "
                + "(nothing to apply)");
            return 2;
        }

        // The build has to have every encoder the chain names, or the failure
        // arrives as an FFmpeg message twenty seconds into the last pass. Only
        // checked for codecs this tool knows by name; "copy" and anything newer
        // are left alone.
        for (int i = 0; i < stages.Count; i++)
        {
            (List<string> tokens, string? fragmentError) = PostProcess.PostArgs.Parse(stages[i].Args);
            if (fragmentError is not null)
            {
                Console.Error.WriteLine($"ERROR: {L.T("post.args.bad", fragmentError)}");
                return 2;
            }
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
        }

        // Every hop derives its own name except the last, which honours
        // --output when one was given.
        var outputs = new List<FileInfo>();
        for (int i = 0; i < stages.Count; i++)
        {
            string container = stages[i].Container.Or("mp4").TrimStart('.').ToLowerInvariant();
            bool last = i == stages.Count - 1;
            outputs.Add(last && !string.IsNullOrEmpty(args.Output)
                ? new FileInfo(args.Output)
                : new FileInfo(PostProcess.Recompress.DeriveOutputPath(src.FullName, container, i + 1)));
        }

        FileInfo dst = outputs[^1];
        if (string.Equals(Path.GetFullPath(dst.FullName), src.FullName, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("ERROR: output must differ from input");
            return 2;
        }

        if (!args.Quiet)
        {
            Console.WriteLine(L.T("rc.only.input", src.FullName));
            for (int i = 0; i < stages.Count; i++)
            {
                Console.WriteLine($" {string.Join(' ', PostProcess.Recompress.BuildArgv(
                    ffmpeg, i == 0 ? src.FullName : outputs[i - 1].FullName,
                    outputs[i].FullName, stages[i]).Argv)}");
            }
        }

        string hopInput = src.FullName;
        for (int i = 0; i < stages.Count; i++)
        {
            (bool ok, string? error) = PostProcess.Recompress.Apply(
                ffmpeg, hopInput, outputs[i].FullName, stages[i]);
            if (!ok)
            {
                string stage = stages.Count > 1 ? $"recompress {i + 1}/{stages.Count}" : "recompress";
                Console.Error.WriteLine($"ERROR: {L.T("post.failed", stage, error ?? "")}");
                return 1;
            }
            hopInput = outputs[i].FullName;
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
        CliOptions args, PostProcess.EraPreset? era, bool eraNative, (int W, int H)? eraSize = null,
        string? h264Impl = null)
    {
        var rc = new RecompressSettings { Enabled = false };

        // One entry per transcode, in the order written. Without any flags the
        // chain is the single era pass (or a single blank mp4 pass that the
        // Enabled flag keeps from running).
        int count = Math.Max(1, Math.Max(args.RecompressArgs.Count, args.RecompressCodecs.Count));
        for (int i = 0; i < count; i++)
        {
            string? fragment = i < args.RecompressArgs.Count ? args.RecompressArgs[i] : null;
            string? codecName = i < args.RecompressCodecs.Count ? args.RecompressCodecs[i] : null;
            string? container = i < args.RecompressContainers.Count ? args.RecompressContainers[i] : null;

            if (fragment is null && codecName is not null)
            {
                SecondPassVideoCodec? codec = RecompressCodecs.FindVideo(codecName);
                if (codec is null)
                    return (rc, $"unknown recompress codec '{codecName}' (pass {i + 1}); "
                        + $"known: {RecompressCodecs.VideoIdList}");

                string audio = args.RecompressAudioCodec ?? RecompressCodecs.RecommendedAudio(codec.Name);
                if (!RecompressCodecs.IsCopyAudio(audio) && RecompressCodecs.FindAudio(audio) is null)
                    return (rc, $"unknown audio codec '{audio}' (pass {i + 1}); "
                        + $"known: {RecompressCodecs.AudioIdList}, or copy");

                fragment = string.Join(' ',
                    RecompressCodecs.VideoArgs(codec, args.RecompressQuality ?? codec.DefaultQuality),
                    RecompressCodecs.AudioArgs(audio, args.RecompressAudioKbps, null, null));
                container ??= RecompressCodecs.DefaultContainer(codec.Name, audio) ?? codec.Containers[0];
            }

            if (fragment is null)
            {
                if (i > 0)
                    return (rc, $"pass {i + 1} has no arguments; give another --recompress-args "
                        + "or --recompress-codec for it");
                // Pass 1 with nothing typed: the era preset's own second pass, or
                // an empty fragment that Enabled=false keeps from running.
                fragment = era?.RecompressArgsFor(eraNative, eraSize, h264Impl) ?? "";
                container ??= era?.ContainerFor(eraNative) ?? "mp4";
            }

            (RecompressPass pass, string? error) = BuildPass(
                fragment, container, args, era, i == 0, codecName is not null,
                containerPinned: i < args.RecompressContainers.Count);
            if (error is not null) return (rc, count > 1 ? $"{error} (pass {i + 1})" : error);

            if (i == 0)
            {
                rc.Args = pass.Args;
                rc.Container = pass.Container;
            }
            else
            {
                rc.Extra.Add(pass);
            }
        }

        // A pair the muxer cannot hold is a fact about FFmpeg, not a matter of
        // taste; BuildPass already refused it, so only the enable flags are left.
        if (args.Recompress) rc.Enabled = true;
        if (args.NoRecompress) rc.Enabled = false;
        return (rc, null);
    }

    /// <summary>
    /// Validate one pass and settle its container.
    ///
    /// <para>The container follows the codecs when the user did not pin one --
    /// a hand-written fragment that names VP9 should not land in
    /// <c>clip_web.mp4</c>. Only codecs this tool knows by name are checked;
    /// <c>copy</c> and anything newer are left to FFmpeg.</para>
    /// </summary>
    private static (RecompressPass Pass, string? Error) BuildPass(
        string fragment, string? container, CliOptions args,
        PostProcess.EraPreset? era, bool firstPass, bool codecNamed, bool containerPinned)
    {
        var pass = new RecompressPass { Args = fragment, Container = container ?? "mp4" };
        if (fragment.Trim().Length == 0) return (pass, null);

        (List<string> tokens, string? parseError) = PostProcess.PostArgs.Parse(fragment);
        if (parseError is not null) return (pass, L.T("post.args.bad", parseError));

        if (RecompressCodecs.DetectVideoCodec(tokens) is not { } video) return (pass, null);
        string? audio = RecompressCodecs.DetectAudioCodec(tokens);

        // v7.9.8.3: every hop infers its container, not just the first one. A
        // chain whose second pass is named by codec (`--recompress-codec flv`
        // after a libx264 pass) used to be refused with "flv cannot be written to
        // .mp4" simply because the container had defaulted before the codec was
        // consulted. The era keeps the last word on pass 1: its container is part
        // of the preset, not something to guess.
        bool fromEra = firstPass && era is not null && !containerPinned && !codecNamed;
        if (!containerPinned && !codecNamed && !fromEra)
            pass.Container = RecompressCodecs.DefaultContainer(video, audio) ?? pass.Container;

        string? mismatch = RecompressCodecs.ValidateContainer(video, audio, pass.Container);
        return mismatch is not null ? (pass, L.T("rc.container.mismatch", mismatch)) : (pass, null);
    }

    /// <summary>
    /// Reject the post-processing settings that cannot work, before anything
    /// expensive happens.
    /// </summary>
    private static void ValidatePostProcessing(Config config)
    {
        if (!config.Recompress.Enabled) return;

        List<RecompressPass> stages = config.Recompress.Stages();
        for (int i = 0; i < stages.Count; i++)
        {
            (_, string? error) = PostProcess.PostArgs.Parse(stages[i].Args);
            if (error is not null)
            {
                throw new ArgumentException(stages.Count > 1
                    ? L.T("post.args.bad", error) + $" (pass {i + 1})"
                    : L.T("post.args.bad", error));
            }

            if (!stages[i].Container.HasText)
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
