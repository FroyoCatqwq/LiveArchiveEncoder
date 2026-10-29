using System.CommandLine;
using Lae.Core;
using Lae.Core.Settings;
using Lae.Media.Ffmpeg;
using Lae.Media.Toolchain;

namespace Lae.App.Commands;

/// <summary>
/// <c>lae encode</c> — one source, one output, one realtime pass.
///
/// <para>What exists today is the planning half: the tool is found, the source is
/// probed, the encoder is resolved and the two FFmpeg command lines are built.
/// <c>--dry-run</c> prints them. The pumping half (pacing frames between the two
/// processes) is the next piece of M1, and until it lands this command says so
/// rather than pretending.</para>
/// </summary>
public static class EncodeCommand
{
    public static Command Build(ISettingsStore store)
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "The source to re-encode.",
        };

        var outOption = new Option<string?>("--out")
        {
            Description = "Output path. Defaults to the source name with the encoder's extension.",
        };
        var encoderOption = new Option<string?>("--encoder")
        {
            Description = "Video encoder, e.g. mpeg4, libx264, flv. Defaults to the stored one.",
        };
        var containerOption = new Option<string?>("--container")
        {
            Description = "Container extension, e.g. avi, mkv, mp4. Defaults to the encoder's own.",
        };
        var fromOption = new Option<double?>("--from")
        {
            Description = "Start offset in seconds.",
        };
        var toOption = new Option<double?>("--to")
        {
            Description = "End offset in seconds.",
        };
        var qualityOption = new Option<int?>("--quality")
        {
            Description = "Quality in the encoder's own scale (2..31 for the mpeg family, 0..51 for x264).",
        };
        var sizeOption = new Option<string?>("--size")
        {
            Description = "Output frame size as WIDTHxHEIGHT.",
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Print the two FFmpeg command lines and stop.",
        };

        var command = new Command("encode", "Re-encode one source at archive pace.")
        {
            inputArgument,
            outOption,
            encoderOption,
            containerOption,
            fromOption,
            toOption,
            qualityOption,
            sizeOption,
            dryRunOption,
        };

        command.SetAction((ParseResult parseResult) =>
        {
            string input = parseResult.GetValue(inputArgument)!;
            SettingsDocument settings = store.Load();

            // The toolchain is resolved before anything else: "no FFmpeg" is a
            // setup problem with its own command, not an encode failure.
            string? directory = ToolchainDirectory(settings);
            ToolchainProbeResult tools = ToolchainProbe.Probe(directory);
            if (tools.Ffmpeg is null || tools.Ffprobe is null)
            {
                foreach (string line in tools.Missing()) Console.Error.WriteLine($"ERROR: missing {line}");
                Console.Error.WriteLine("Run 'lae setup' for what to do about it.");
                return ExitCodes.Failed;
            }

            (SourceInfo? source, string? probeError) =
                FfmpegTools.ProbeSource(tools.Ffprobe, input);
            if (source is null)
            {
                Console.Error.WriteLine($"ERROR: cannot read '{input}': {probeError}");
                return ExitCodes.Failed;
            }

            EncodeRequest request = BuildRequest(
                parseResult, input, settings,
                new EncodeOptions(outOption, encoderOption, containerOption, fromOption, toOption, qualityOption, sizeOption),
                out string? requestError);
            if (requestError is not null)
            {
                Console.Error.WriteLine($"ERROR: {requestError}");
                return ExitCodes.Usage;
            }

            (List<EncoderInfo> encoders, string? listError) =
                FfmpegTools.ListEncoders(tools.Ffmpeg!);
            if (listError is not null)
            {
                Console.Error.WriteLine($"ERROR: {listError}");
                return ExitCodes.Failed;
            }

            EncoderInfo? encoder = EncoderCatalog.Find(encoders, request.Encode.Encoder);
            if (encoder is null)
            {
                Console.Error.WriteLine(
                    $"ERROR: this FFmpeg build has no encoder named '{request.Encode.Encoder}'.");
                Console.Error.WriteLine("Try one of: mpeg4, mpeg1video, flv, libx264.");
                return ExitCodes.Usage;
            }

            EncodePlan plan = EncodePlan.Create(request, source, encoder);

            foreach (string note in plan.Notes) Console.WriteLine($"note: {note}");

            if (!plan.IsRunnable)
            {
                foreach (string problem in plan.Problems) Console.Error.WriteLine($"ERROR: {problem}");
                return ExitCodes.Usage;
            }

            PrintPlan(plan, source, tools.Ffmpeg);

            if (parseResult.GetValue(dryRunOption)) return ExitCodes.Ok;

            Console.Error.WriteLine();
            Console.Error.WriteLine("ERROR: the encode pump is not wired yet; --dry-run is all this build can do.");
            return ExitCodes.Failed;
        });

        return command;
    }

    /// <summary>The option instances, bundled so request building does not need six parameters.</summary>
    private sealed record EncodeOptions(
        Option<string?> Out,
        Option<string?> Encoder,
        Option<string?> Container,
        Option<double?> From,
        Option<double?> To,
        Option<int?> Quality,
        Option<string?> Size);

    /// <summary>
    /// Lays the command line over the stored defaults.
    ///
    /// <para>Only the fields this command actually spells are touched, and the
    /// result is a <c>with</c> expression, so a field the command does not know
    /// about survives untouched. Two things are deliberately <em>not</em> taken
    /// from the settings: the recompress chain and the subtitle block. A CLI run
    /// re-encodes its own output or burns subtitles only when it says so on the
    /// command line -- that rule is older than the settings file and it is what
    /// keeps a scripted run predictable.</para>
    /// </summary>
    private static EncodeRequest BuildRequest(
        ParseResult parseResult, string input, SettingsDocument settings, EncodeOptions options,
        out string? error)
    {
        error = null;

        EncodeDefaults encode = settings.Defaults with
        {
            Encoder = parseResult.GetValue(options.Encoder) ?? settings.Defaults.Encoder ?? "mpeg4",
            Recompress = new RecompressChain(),
            Subtitles = new SubtitleDefaults(),
        };

        if (parseResult.GetValue(options.Container) is { } container)
            encode = encode with { Container = container };

        if (parseResult.GetValue(options.Quality) is { } quality)
            encode = encode with { Quality = quality };

        if (parseResult.GetValue(options.Size) is { } size)
        {
            string[] parts = size.Split('x', 'X');
            if (parts.Length != 2
                || !int.TryParse(parts[0], out int width)
                || !int.TryParse(parts[1], out int height)
                || width <= 0 || height <= 0)
            {
                error = $"--size wants WIDTHxHEIGHT, got '{size}'";
                return new EncodeRequest { SourcePath = input, DestinationPath = input };
            }

            encode = encode with { Width = width, Height = height };
        }

        string destination = parseResult.GetValue(options.Out)
                             ?? DeriveDestination(input, encode.Container ?? "avi");

        return new EncodeRequest
        {
            SourcePath = input,
            DestinationPath = destination,
            Encode = encode,
            StartSeconds = parseResult.GetValue(options.From) ?? 0,
            EndSeconds = parseResult.GetValue(options.To),
        };
    }

    /// <summary>
    /// The default output name: same folder, same stem, the new extension. It is
    /// never the input path itself, which the request validation would refuse.
    /// </summary>
    private static string DeriveDestination(string input, string container)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(input);
        return Path.Combine(folder, $"{stem}.{container}");
    }

    private static void PrintPlan(EncodePlan plan, SourceInfo source, string ffmpeg)
    {
        Console.WriteLine($"source     {source.Width}x{source.Height} {source.PixelFormat}"
                        + $", {source.DurationSeconds:G6}s"
                        + (source.HasAudio ? $", audio {source.AudioRate}Hz x{source.AudioChannels}" : ", no audio"));
        Console.WriteLine($"output     {plan.Width}x{plan.Height} {plan.TransportPixelFormat}"
                        + $", {plan.Container} ({plan.Muxer}), {plan.Fps:G6} fps");
        Console.WriteLine($"span       {plan.SpanSeconds:G6}s -> {plan.TargetFrames} frames");
        Console.WriteLine($"frame      {plan.FrameSize} bytes on the pipe");
        Console.WriteLine();
        Console.WriteLine("decoder:");
        Console.WriteLine("  " + Quote(plan.DecodeCommand(ffmpeg)));
        Console.WriteLine("encoder:");
        Console.WriteLine("  " + Quote(plan.EncodeCommand(ffmpeg)));
    }

    /// <summary>
    /// Renders a command line the way it would be typed. A dry run exists to be
    /// pasted into a shell and tried, so arguments with spaces get quoted.
    /// </summary>
    private static string Quote(IReadOnlyList<string> command)
        => string.Join(' ', command.Select(a => a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a));

    /// <summary>
    /// The folder an encode would look in, or null for "whatever is on PATH".
    /// Only an explicit path can name a folder; a distribution id points at a
    /// managed install, which does not exist until the toolchain command runs.
    /// </summary>
    private static string? ToolchainDirectory(SettingsDocument settings)
    {
        string? explicitPath = settings.Toolchain.FfmpegPath;
        if (string.IsNullOrWhiteSpace(explicitPath)) return null;
        if (File.Exists(explicitPath)) return Path.GetDirectoryName(explicitPath);
        return Directory.Exists(explicitPath) ? explicitPath : null;
    }
}
