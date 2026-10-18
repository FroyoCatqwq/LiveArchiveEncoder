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
        // DetectLegacyEncoders and DetectAudioEncoders are independent `ffmpeg
        // -encoders` runs. Overlap them so two process startups cost one.
        Task<List<EncoderInfo>> encodersTask =
            Task.Run(() => FFmpegDiscovery.DetectLegacyEncoders(ffmpeg), cancellationToken);
        Task<HashSet<string>> audioEncodersTask =
            Task.Run(() => FFmpegDiscovery.DetectAudioEncoders(ffmpeg), cancellationToken);

        List<EncoderInfo> encoders = encodersTask.GetAwaiter().GetResult();
        EncoderInfo? encoder = encoders.FirstOrDefault(
            e => string.Equals(e.Name, args.Encoder, StringComparison.OrdinalIgnoreCase));
        if (encoder is null)
        {
            Console.Error.WriteLine(
                $"ERROR: {args.Encoder} is not detected as a legacy/classic video encoder.");
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

        (double baseFps, bool fpsExact) = EncoderMath.ChooseBaseFps(args.Fps, encoder.Caps);
        if (!fpsExact)
            Console.WriteLine(L.T("note.fpsunsupported", encoder.Name, $"{args.Fps:G6}", $"{baseFps:G6}"));

        (int width, int height, bool sizeExact) = EncoderMath.NormalizeDimensions(
            encoder.Name, args.Width, args.Height);
        if (!sizeExact)
            Console.WriteLine(L.T("note.specialsizes", encoder.Name, width, height));

        List<double> levels = EncoderMath.SelectLevels(
            baseFps,
            string.IsNullOrWhiteSpace(args.FpsLevels)
                ? null
                : args.FpsLevels.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)));

        double recover = args.EffectiveRecover;
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

        int? audioRate = args.AudioRate;
        int audioChannels = args.AudioChannels;
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
            string scaleAlgo = EncoderMath.CanonicalScaleAlgo(args.ScaleAlgo);
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
                Limit = args.Limit,
                Recover = recover,
                Window = args.Window,
                Qv = args.Qv ?? EncoderMath.DefaultQuality(encoder.Name),
                Threads = args.Threads,
                BufferFrames = args.BufferFrames,
                AudioKbps = args.AudioKbps,
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
                Language = args.Language ?? ConfigStore.Load().Language,
                FfmpegSource = args.FfmpegSource,
                FfmpegProxy = args.FfmpegProxy,
                Play = args.Play,
                PlayerPath = args.PlayerPath,
            };
            Validation.ValidateCommonConfig(config);
            if (config.EndTimeSeconds is not null && config.EndTimeSeconds <= config.StartTimeSeconds)
                throw new ArgumentException("end time must be later than start time");
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

        // Only ffplay/external do anything; "off" is a no-op so scripting a run
        // never pops a window.
        if (args.Play != PlayMode.Off)
        {
            (bool played, string? playError) =
                Player.Launch(args.Play, dst.FullName, ffmpeg, args.PlayerPath);
            if (!played && playError is not null)
                Console.Error.WriteLine($"ERROR: {playError}");
        }
        return 0;
    }

    private static FileInfo DefaultOutput(FileInfo src, EncoderInfo encoder) =>
        new(Path.Combine(src.DirectoryName ?? ".", $"{Path.GetFileNameWithoutExtension(src.Name)}_live.{encoder.Extension}"));
}
