using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.FFmpeg;
using LiveArchiveEncoder.Localization;
using LiveArchiveEncoder.UI;

namespace LiveArchiveEncoder;

public static class Program
{
    public static int Main(string[] args)
    {
        if (Environment.Version.Major < 10)
        {
            Console.Error.WriteLine($"ERROR: .NET 10+ is required; found {Environment.Version}");
            return 2;
        }

        Interrupt.Attach();

        (CliOptions? options, string? parseError) = CliParser.Parse(args);
        if (parseError is not null)
        {
            Console.Error.WriteLine($"ERROR: {parseError}");
            return 2;
        }
        CliOptions cli = options!;

        // Language is applied before any other output so even parse errors and
        // the CUI banner follow it.
        L.SetLanguage(cli.Language);

        // argparse prints help without touching FFmpeg first, and so do we.
        if (cli.Help)
        {
            Console.Out.Write(CliParser.Usage());
            return 0;
        }

        // Purely local diagnostic: no FFmpeg discovery, no I/O.
        if (cli.CpuInfo)
        {
            CpuInfo.Print();
            return 0;
        }

        string ffmpeg;
        string ffprobe;
        bool btbn;
        HashSet<string> availableFilters;
        HashSet<string> availableScaleAlgorithms;

        try
        {
            ffmpeg = FFmpegDiscovery.FindFFmpeg(cli.FFmpeg);
            btbn = FFmpegDiscovery.IsBtbn;
            ffprobe = FFmpegDiscovery.FindFFprobe(cli.FFprobe, ffmpeg);
            availableFilters = FFmpegCapabilities.DetectVideoFilters(ffmpeg);
            availableFilters = FFmpegCapabilities.VerifyNamedFilters(
                ffmpeg, availableFilters, ["scale", .. Constants.DeinterlaceFilters]);
            availableScaleAlgorithms = FFmpegCapabilities.DetectScaleAlgorithms(ffmpeg);
        }
        catch (Exception exc) when (exc is FileNotFoundException or InvalidOperationException or TimeoutException)
        {
            Console.Error.WriteLine($"ERROR: {exc.Message}");
            return 2;
        }

        // Completion needs the live encoder list, so it runs after discovery.
        if (cli.Completion is not null)
        {
            Console.Out.Write(Completion.Generate(
                cli.Completion,
                FFmpegDiscovery.DetectLegacyEncoders(ffmpeg).Select(e => e.Name)));
            return 0;
        }

        if (cli.ListFilters)
        {
            Report.PrintFilterList(ffmpeg, availableFilters, availableScaleAlgorithms);
            return 0;
        }

        if (string.IsNullOrEmpty(cli.Input) && !cli.NoCui)
        {
            return Cui.Run(ffmpeg, ffprobe, btbn, availableFilters, availableScaleAlgorithms,
                cli.Language);
        }

        if (string.IsNullOrEmpty(cli.Input))
        {
            Console.Error.WriteLine("ERROR: input is required in --no-cui mode");
            return 2;
        }

        try
        {
            return Cli.Run(cli, ffmpeg, ffprobe, availableFilters, availableScaleAlgorithms,
                Interrupt.BeginOperation());
        }
        catch (OperationCanceledException)
        {
            // Interrupt.Request() already reported it on the way in.
            return 130;
        }
        catch (Exception exc) when (exc is FileNotFoundException or InvalidOperationException
                                        or ArgumentException or TimeoutException)
        {
            Console.Error.WriteLine($"ERROR: {exc.Message}");
            return 1;
        }
    }
}
