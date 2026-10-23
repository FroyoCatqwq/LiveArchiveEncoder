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

        // v7.9.1: settings file -> command line. Reads one JSON file and prints
        // the equivalent command line; no FFmpeg, no encode, so it works on a
        // box that has none installed.
        if (cli.EmitArgsFile is { Length: > 0 } emitFile)
        {
            if (!File.Exists(emitFile))
            {
                Console.Error.WriteLine($"ERROR: settings file not found: {emitFile}");
                return 2;
            }
            // v7.9.3.1: TryLoad, not Load -- Load answers "defaults" for a file
            // it cannot parse, so a truncated settings file used to print the
            // command line of the *defaults* and look like a success.
            if (ConfigStore.TryLoad(emitFile) is not { } stored)
            {
                Console.Error.WriteLine($"ERROR: cannot read {emitFile} as a settings file");
                return 2;
            }
            Console.Out.WriteLine(CliArgs.Join(CliArgs.FromUserConfig(stored)));
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
        FFmpeg.Capabilities caps;

        string? source = cli.FfmpegSource;
        if (!source.HasText) source = ConfigStore.Load().FfmpegSource;

        try
        {
            ffmpeg = FFmpegDiscovery.FindFFmpeg(cli.FFmpeg, source);
            btbn = FFmpegDiscovery.IsBtbn;
            ffprobe = FFmpegDiscovery.FindFFprobe(cli.FFprobe, ffmpeg);
            // v7.9.5: one cached sweep of the binary instead of six launches per
            // run. The second run on a box pays nothing at all for this.
            caps = FFmpeg.BuildProfileStore.For(ffmpeg);
        }
        catch (Exception exc) when (exc is FileNotFoundException or InvalidOperationException or TimeoutException)
        {
            Console.Error.WriteLine($"ERROR: {exc.Message}");
            return 2;
        }

        // Completion needs the live encoder list, so it runs after discovery.
        if (cli.Completion is not null)
        {
            Console.Out.Write(Completion.Generate(cli.Completion, caps.Encoders.Select(e => e.Name)));
            return 0;
        }

        if (cli.ListFilters)
        {
            Report.PrintFilterList(ffmpeg, caps.Filters, caps.ScaleAlgorithms);
            return 0;
        }

        // v7.9: the indexes --video-stream/--audio-stream take are exactly the
        // numbers this prints, so the two halves cannot disagree.
        if (cli.ListStreams)
        {
            if (string.IsNullOrEmpty(cli.Input))
            {
                Console.Error.WriteLine("ERROR: --list-streams requires an input");
                return 2;
            }
            try
            {
                Console.Out.WriteLine(
                    LiveArchiveEncoder.Encoding.StreamSelection.Describe(
                        FFmpeg.Probe.Source(ffprobe, cli.Input)));
                return 0;
            }
            catch (Exception exc) when (exc is InvalidOperationException
                                            or ArgumentException or TimeoutException)
            {
                // A big programme stream on a share can take minutes to probe,
                // so a timeout here is a fact about the source, not a crash.
                Console.Error.WriteLine($"ERROR: {exc.Message}");
                return 1;
            }
        }

        // v7.9.1: the conversion exits resolve a run first, which needs an
        // input; falling into the interactive UI instead would silently do
        // nothing useful, so they say what is missing.
        if (string.IsNullOrEmpty(cli.Input) && (cli.SaveConfig is { Length: > 0 } || cli.EmitArgs))
        {
            Console.Error.WriteLine("ERROR: --save-config / --emit-args require an input and an output");
            return 2;
        }

        // v7.9.5: same reasoning for the standalone recompress pass -- the menu
        // would be a detour, and "it did nothing" is the worst answer.
        if (string.IsNullOrEmpty(cli.Input) && cli.RecompressOnly)
        {
            Console.Error.WriteLine("ERROR: --recompress-only requires an input file");
            return 2;
        }

        if (string.IsNullOrEmpty(cli.Input) && !cli.NoCui)
        {
            return Cui.Run(ffmpeg, ffprobe, btbn, caps, cli.Language, source);
        }

        if (string.IsNullOrEmpty(cli.Input))
        {
            Console.Error.WriteLine("ERROR: input is required in --no-cui mode");
            return 2;
        }

        try
        {
            return Cli.Run(cli, ffmpeg, ffprobe, caps, Interrupt.BeginOperation());
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
