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

        // v7.7: Turbo still behaves exactly as before, but it is on its way out
        // and anyone still scripting it deserves to know that up front.
        if (cli.Turbo) Console.Error.WriteLine($"WARNING: {L.T("turbo.deprecated")}");

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

        // Toolchain management never needs a working FFmpeg, so it runs before
        // discovery -- otherwise a missing ffmpeg would make "install one"
        // impossible, which is exactly the situation it exists to fix.
        if (cli.FfmpegList)
        {
            ToolchainConsole.PrintInstalled();
            return 0;
        }

        if (cli.FfmpegDelete is not null)
        {
            (bool deleted, string? deleteError) = FFmpeg.Toolchain.Delete(cli.FfmpegDelete);
            if (!deleted)
            {
                Console.Error.WriteLine($"ERROR: {deleteError}");
                return 1;
            }
            Console.WriteLine($"Removed '{cli.FfmpegDelete}' from {FFmpeg.Toolchain.Home(cli.FfmpegDelete)}");
            return 0;
        }

        if (cli.FfmpegDownload is not null)
        {
            FFmpeg.FfmpegDist? dist = FFmpeg.FfmpegCatalog.Find(cli.FfmpegDownload);
            if (dist is null)
            {
                Console.Error.WriteLine(
                    $"ERROR: unknown source '{cli.FfmpegDownload}'; choose one of {FFmpeg.FfmpegCatalog.Ids}");
                return 2;
            }
            string? proxy = cli.FfmpegProxy ?? ConfigStore.Load().FfmpegProxy;
            Console.WriteLine($"{dist.Label}\n{dist.Homepage}");

            if (!dist.AutoDownload && cli.FfmpegArchive is null)
            {
                // Say exactly what to click and what to type afterwards; a bare
                // "cannot download" leaves the user stuck.
                // The SourceForge folder -- and therefore the file name -- is
                // per platform, so name the one this build can actually use.
                string folder = dist.Platform == FFmpeg.DistPlatform.Windows
                    ? "windows-desktop" : "linux-desktop";
                string pattern = dist.Platform == FFmpeg.DistPlatform.Windows
                    ? "ffmpeg-master-windows-desktop-*.7z" : "ffmpeg-master-linux-desktop-*.7z";
                Console.WriteLine();
                Console.WriteLine($"'{dist.Id}' has to be downloaded by hand: its host blocks "
                    + "automated downloads (a Cloudflare interstitial is served instead of the archive).");
                Console.WriteLine($" 1. open {dist.Homepage}");
                Console.WriteLine($" 2. download the newest {pattern}");
                Console.WriteLine($" 3. LiveArchiveEncoder --ffmpeg-download {dist.Id} "
                    + "--ffmpeg-archive <path to the .7z>");
                Console.WriteLine($"(the '{folder}' folder is the one for this platform)");
                return 1;
            }

            (bool installed, string? installError) =
                FFmpeg.Toolchain.Install(dist, proxy, cli.FfmpegArchive, Console.WriteLine);
            if (!installed)
            {
                Console.Error.WriteLine($"ERROR: {installError}");
                return 1;
            }
            UserConfig remembered = ConfigStore.Load();
            remembered.FfmpegSource = dist.Id;
            if (proxy is not null) remembered.FfmpegProxy = proxy;
            ConfigStore.Save(remembered);
            return 0;
        }

        string ffmpeg;
        string ffprobe;
        bool btbn;
        HashSet<string> availableFilters;
        HashSet<string> availableScaleAlgorithms;

        string? source = cli.FfmpegSource;
        if (string.IsNullOrWhiteSpace(source)) source = ConfigStore.Load().FfmpegSource;

        try
        {
            ffmpeg = FFmpegDiscovery.FindFFmpeg(cli.FFmpeg, source);
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
                cli.Language, source);
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
                                        or ArgumentException or TimeoutException
                                        or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"ERROR: {exc.Message}");
            return 1;
        }
    }
}
