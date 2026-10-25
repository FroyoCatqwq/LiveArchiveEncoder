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

        // v7.9.5.1: fonts are local files, so both font actions run before any
        // FFmpeg discovery -- importing a font must work on a box that has no
        // FFmpeg at all yet.
        if (cli.FontImport is { Length: > 0 } importPath)
        {
            (int copied, int skipped, string target) =
                PostProcess.SubtitleFonts.Import([importPath]);
            if (copied == 0 && skipped == 0)
            {
                Console.Error.WriteLine($"ERROR: {L.T("font.import.none", importPath)}");
                return 2;
            }
            Console.WriteLine(L.T("font.import", copied, skipped, target));
            return 0;
        }

        if (cli.ListSubtitleFonts)
        {
            if (cli.SubtitleFiles.Count == 0)
            {
                Console.Error.WriteLine("ERROR: --list-subtitle-fonts needs --subtitle FILE");
                return 2;
            }
            Console.WriteLine(L.T("font.list.heading",
                string.Join(", ", cli.SubtitleFiles.Select(Path.GetFileName))));
            foreach (PostProcess.FontLookup lookup in
                     PostProcess.SubtitleFonts.Check(cli.SubtitleFiles, cli.SubtitleFontDir))
            {
                if (lookup.FoundAt is null)
                {
                    Console.WriteLine(L.T("font.missing", lookup.Required));
                    continue;
                }
                Console.WriteLine(L.T("font.found", lookup.Required,
                    lookup.FoundAt, lookup.Portable ? L.T("font.portable") : ""));
            }
            Console.WriteLine(L.T("font.import.hint", PostProcess.FontResolver.FontDirectory));
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

        // v7.9.5.1: --era can name a preset the user wrote, which lives in the
        // settings file -- so the id can only be judged after that file is read.
        // It is still a usage error (exit 2), not "enter the menu and wait".
        UserConfig storedConfig = ConfigStore.Load();
        PostProcess.EraPresets.Register(storedConfig);
        if (cli.Era is { Length: > 0 } eraId && PostProcess.EraPresets.Find(eraId) is null)
        {
            Console.Error.WriteLine($"ERROR: unknown era preset '{eraId}'; "
                + $"choose one of {PostProcess.EraPresets.Ids}");
            return 2;
        }

        string ffmpeg;
        string ffprobe;
        bool btbn;
        FFmpeg.Capabilities caps;

        string? source = cli.FfmpegSource;
        if (!source.HasText) source = storedConfig.FfmpegSource;

        static (string FFmpeg, string FFprobe, bool Btbn, FFmpeg.Capabilities Caps)
            Discover(CliOptions cli, string? source)
        {
            string found = FFmpegDiscovery.FindFFmpeg(cli.FFmpeg, source);
            bool isBtbn = FFmpegDiscovery.IsBtbn;
            string probe = FFmpegDiscovery.FindFFprobe(cli.FFprobe, found);
            return (found, probe, isBtbn, FFmpeg.BuildProfileStore.For(found));
        }

        try
        {
            (ffmpeg, ffprobe, btbn, caps) = Discover(cli, source);
        }
        catch (Exception exc) when (exc is FileNotFoundException or InvalidOperationException or TimeoutException)
        {
            // v7.9.6.3: on a machine that has never had FFmpeg, "Cannot find
            // ffmpeg; add FFmpeg/bin to PATH" is a dead end -- the tool can
            // fetch its own copy, it just never asked. A wrong explicit path is
            // still an error, and a redirected stdin cannot answer, so only the
            // "nothing configured, somebody is watching" case prompts.
            bool explicitPath = cli.FFmpeg.HasText || cli.FFprobe.HasText;
            if (exc is not FileNotFoundException || explicitPath || !FirstRun.CanAsk()
                || !FirstRun.OfferInstall())
            {
                Console.Error.WriteLine($"ERROR: {exc.Message}");
                return 2;
            }

            // The install just wrote the source into the settings file; read it
            // back rather than assume, then discover again against it.
            source = ConfigStore.Load().FfmpegSource;
            try
            {
                (ffmpeg, ffprobe, btbn, caps) = Discover(cli, source);
            }
            catch (Exception retry) when (retry is FileNotFoundException
                                              or InvalidOperationException or TimeoutException)
            {
                Console.Error.WriteLine($"ERROR: {retry.Message}");
                return 2;
            }
        }

        // v7.9.6.3: measure the link behind a source and print the verdict that
        // --parallel would use. Nothing is written and no encode starts, so it
        // is safe to run against a production share.
        if (cli.NetTest is { Length: > 0 } netTarget)
        {
            double required = cli.Limit;
            try
            {
                // The source's own bitrate is what the encoder has to be fed at;
                // the output ceiling is only the floor.
                if (FFmpeg.Probe.Source(ffprobe, netTarget).BitRateKbps is { } sourceKbps)
                    required = Math.Max(required, sourceKbps);
            }
            catch (Exception exc) when (exc is InvalidOperationException
                                            or ArgumentException or TimeoutException)
            {
                Console.WriteLine(L.T("nettest.noprobe", exc.Message));
            }

            Console.WriteLine(L.T("nettest.heading", netTarget, required));
            Remote.NetworkMeasurement? measured =
                Remote.NetworkProbe.Measure(netTarget, required, cli.Iperf3Host);
            if (measured is null)
            {
                Console.Error.WriteLine($"ERROR: {L.T("nettest.failed", netTarget)}");
                return 1;
            }
            Console.WriteLine($"  {measured.Summary}");
            Console.WriteLine(L.T(measured.Adequate ? "nettest.adequate" : "nettest.tooslow",
                Remote.NetworkProbe.HeadroomFactor, Remote.NetworkProbe.JitterLimitPercent));
            return measured.Adequate ? 0 : 1;
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

        // v7.9.6: what can actually travel down the raw video pipe, with the
        // selected encoder's own column. Handled before the CUI branch so it
        // works without an input file.
        if (cli.ListPixFmts)
        {
            Configuration.EncoderInfo? selected = caps.Encoders.FirstOrDefault(
                e => string.Equals(e.Name, cli.Encoder, StringComparison.OrdinalIgnoreCase));
            Report.PrintPixelFormats(selected?.Name,
                selected is null ? null : caps.EncoderCaps(selected));
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
