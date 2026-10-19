using System.Globalization;
using System.Text.RegularExpressions;
using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.FFmpeg;
using LiveArchiveEncoder.Localization;

namespace LiveArchiveEncoder.UI;

/// <summary>Interactive menu shell, ported from run_cui / choose_encoder / edit_settings.</summary>
public static class Cui
{
    public static int Run(
        string ffmpeg, string ffprobe, bool btbn,
        HashSet<string> availableFilters, HashSet<string> availableScaleAlgorithms,
        string? language = null, string? ffmpegSource = null)
    {
        (string version, _, bool likelyGpl) = FFmpegDiscovery.BuildInfo(ffmpeg);
        List<EncoderInfo> encoders = FFmpegDiscovery.DetectLegacyEncoders(ffmpeg);
        HashSet<string> audioAvailable = FFmpegDiscovery.DetectAudioEncoders(ffmpeg);

        // Restore the previous session before anything is printed, so the banner
        // itself is already in the language the user left behind.
        var c = new Config();
        UserConfig stored = ConfigStore.Load();
        bool restored = stored.Encoder is not null || stored.SavedAt is not null;
        if (restored) ConfigStore.Apply(stored, c, encoders);
        if (ffmpegSource.HasText) c.FfmpegSource = ffmpegSource;

        // Precedence: --lang on the command line beats the stored preference,
        // which beats the built-in default.
        L.SetLanguage(language ?? c.Language);
        c.Language = L.Lang;

        Console.WriteLine($"\n{L.T("app.title")}");
        Console.WriteLine("============================");
        Console.WriteLine($"{L.Pad("FFmpeg", 17)}: {version}");
        Console.WriteLine($"{L.Pad(L.T("banner.executable"), 17)}: {ffmpeg}");
        string sourceState = Toolchain.IsInstalled(c.FfmpegSource)
            ? L.T("tc.installed") : L.T("tc.notinstalled");
        Console.WriteLine($"{L.Pad(L.T("banner.source"), 17)}: {c.FfmpegSource} ({sourceState})");
        Console.WriteLine($"{L.Pad(L.T("banner.btbn"), 17)}: {(btbn ? L.T("value.yes") : L.T("value.fallback"))}");
        Console.WriteLine($"{L.Pad(L.T("banner.gpl"), 17)}: {(likelyGpl ? L.T("value.yes") : L.T("value.notdetected"))}");
        Console.WriteLine(
            $"{L.Pad(L.T("banner.legacy"), 17)}: {L.T("banner.legacycount", encoders.Count)}");
        Console.WriteLine($"{L.Pad(L.T("app.impairments"), 17)}: {L.T("app.impairments.value")}");
        Console.WriteLine(
            $"{L.Pad(L.T("banner.deinterlacers"), 17)}: {(string.Join(", ", Constants.DeinterlaceFilters.Where(f => availableFilters.Contains(f))) is var d && d.Length > 0 ? d : L.T("value.none"))}");
        Console.WriteLine($"{L.Pad(L.T("banner.scalers"), 17)}: {string.Join(", ", availableScaleAlgorithms.OrderBy(x => x, StringComparer.Ordinal))}");

        if (restored)
        {
            Console.WriteLine($"{L.Pad(L.T("config.restored"), 17)}: {ConfigStore.DefaultPath}"
                + (stored.SavedAt is null ? "" : $" (saved {stored.SavedAt.Value.LocalDateTime:G})"));
        }
        Console.WriteLine($"{L.Pad(L.T("config.settingsfile"), 17)}: {ConfigStore.DefaultPath}");
        while (true)
        {
            Console.WriteLine($"\n{L.T("menu.heading")}");
            Console.WriteLine($" 1. {L.Pad(L.T("menu.input"), 22)} = {c.InputPath?.FullName ?? L.T("menu.notselected")}");
            Console.WriteLine($" 2. {L.Pad(L.T("menu.output"), 22)} = {c.OutputPath?.FullName ?? L.T("menu.auto")}");
            Console.WriteLine($" 3. {L.Pad(L.T("menu.encoder"), 22)} = {c.Encoder?.Name ?? L.T("menu.notselected")}");
            Console.WriteLine($" 4. {L.T("menu.parameters")}");
            Console.WriteLine($" 5. {L.T("menu.rescan")}");
            // v7.8: the audio encoder is picked from what the selected video
            // encoder and container can actually hold, so it gets its own entry
            // instead of being a free-text field in the parameters menu.
            Console.WriteLine($" 6. {L.Pad(L.T("menu.audioencoder"), 22)} = {c.AudioEncoder ?? L.T("value.none")}");
            Console.WriteLine($" 7. {L.T("menu.filters")}");
            Console.WriteLine($" 8. {L.T("menu.showconfig")}");
            Console.WriteLine($" 9. {L.T("menu.start")}");
            // No Turbo entry: it is deprecated as of v7.7 and only reachable
            // from the command line (or from a settings file written earlier).
            Console.WriteLine($"10. {L.Pad(L.T("menu.language"), 22)} = {c.Language}");
            Console.WriteLine($"11. {L.Pad(L.T("menu.parallel"), 22)} = {(c.Parallel > 1 ? c.Parallel.ToString(CultureInfo.InvariantCulture) : L.T("menu.parallel.off"))}");
            Console.WriteLine($"12. {L.Pad(L.T("menu.toolchain"), 22)} = {c.FfmpegSource}");
            Console.WriteLine($"13. {L.Pad(L.T("menu.play"), 22)} = {Player.Describe(c.Play, c.PlayerPath, FFmpegDiscovery.FindFFplay(ffmpeg))}");
            Console.WriteLine($"14. {L.Pad(L.T("era.menu"), 22)} = {DescribeEra(c)}");
            Console.WriteLine($"15. {L.Pad(L.T("wm.menu"), 22)} = {DescribeWatermark(c)}");
            Console.WriteLine($"16. {L.Pad(L.T("rc.menu"), 22)} = {DescribeRecompress(c)}");
            Console.WriteLine($" 0. {L.T("menu.exit")}");
            Console.Write(L.T("menu.select"));

            string pick = (Console.ReadLine() ?? "").Trim();
            if (pick == "0")
            {
                Persist(c);
                return 0;
            }
            switch (pick)
            {
                case "1":
                    FileInfo? selected = InputFileMenu();
                    if (selected is not null)
                    {
                        c.InputPath = selected;
                        if (c.Encoder is not null && c.OutputIsAuto)
                            c.OutputPath = DefaultOutput(selected, c.Encoder);
                    }
                    break;
                case "2":
                    Console.Write(L.T("output.prompt"));
                    string rawOut = (Console.ReadLine() ?? "").Trim().Trim('"');
                    if (rawOut.Length > 0)
                    {
                        c.OutputPath = new FileInfo(rawOut);
                        c.OutputIsAuto = false;
                    }
                    else if (c.InputPath is not null && c.Encoder is not null)
                    {
                        c.OutputPath = DefaultOutput(c.InputPath, c.Encoder);
                        c.OutputIsAuto = true;
                    }
                    break;
                case "3":
                    ChooseEncoder(c, encoders, audioAvailable, ffmpeg);
                    break;
                case "4":
                    EditSettings(c, availableFilters, availableScaleAlgorithms, ffmpeg);
                    break;
                case "5":
                    try
                    {
                        encoders = FFmpegDiscovery.DetectLegacyEncoders(ffmpeg);
                        Console.WriteLine(L.T("encoder.rescanok", encoders.Count));
                    }
                    catch (Exception exc) { Console.WriteLine($"{L.T("error.prefix")}{exc.Message}"); }
                    break;
                case "6":
                    ScanAudioEncoders(c, ffmpeg, ref audioAvailable);
                    Persist(c);
                    break;
                case "7":
                    Report.PrintFilterList(ffmpeg, availableFilters, availableScaleAlgorithms);
                    break;
                case "8":
                    Report.PrintConfig(c, ffmpeg, btbn, encoders.Count, availableFilters, availableScaleAlgorithms);
                    break;
                case "9":
                    StartEncoding(c, ffmpeg, ffprobe, btbn, encoders, audioAvailable,
                        availableFilters, availableScaleAlgorithms);
                    break;
                case "10":
                    Console.Write(L.T("lang.prompt", c.Language));
                    c.Language = L.SetLanguage((Console.ReadLine() ?? "").Trim());
                    Console.WriteLine(L.T("lang.switched", c.Language));
                    Persist(c);
                    break;
                case "11":
                    c.Parallel = ReadInt(L.T("parallel.prompt", c.Parallel), c.Parallel);
                    if (c.Parallel < 1 || c.Parallel > Constants.MaxParallelShards)
                    {
                        c.Parallel = Math.Clamp(c.Parallel, 1, Constants.MaxParallelShards);
                        Console.WriteLine(L.T("parallel.clamped", Constants.MaxParallelShards));
                    }
                    Console.WriteLine(c.Parallel > 1 ? L.T("parallel.set", c.Parallel) : L.T("parallel.off"));
                    Persist(c);
                    break;
                case "12":
                    {
                        // The toolchain menu can install or select a different
                        // build, which invalidates every capability probe done
                        // so far -- re-resolve instead of keeping stale lists.
                        (string newSource, string? newProxy) =
                            ToolchainConsole.Menu(c.FfmpegSource, c.FfmpegProxy);
                        c.FfmpegSource = newSource;
                        c.FfmpegProxy = newProxy;
                        Persist(c);
                        RefreshToolchain(c, ref ffmpeg, ref ffprobe, ref btbn,
                            ref encoders, ref audioAvailable,
                            ref availableFilters, ref availableScaleAlgorithms);
                        break;
                    }
                case "13":
                    EditPlayback(c, ffmpeg);
                    Persist(c);
                    break;
                case "14":
                    PickEraPreset(c);
                    Persist(c);
                    break;
                case "15":
                    EditWatermark(c);
                    Persist(c);
                    break;
                case "16":
                    EditRecompress(c);
                    Persist(c);
                    break;
                default:
                    Console.WriteLine(L.T("menu.invalid"));
                    break;
            }
        }
    }

    private static void Persist(Config c)
    {
        if (ConfigStore.Save(ConfigStore.Capture(c))) return;
        Console.WriteLine(L.T("config.savefailed", ConfigStore.DefaultPath));
    }

    private static FileInfo DefaultOutput(FileInfo src, EncoderInfo encoder) =>
        new(Path.Combine(src.DirectoryName ?? ".", $"{Path.GetFileNameWithoutExtension(src.Name)}_live.{encoder.Extension}"));

    /// <summary>
    /// Re-resolve everything that depends on which FFmpeg is in use. Called after
    /// the toolchain menu, because installing or selecting another build changes
    /// the encoder list, the filter list and the scaler list at the same time.
    /// </summary>
    private static void RefreshToolchain(
        Config c, ref string ffmpeg, ref string ffprobe, ref bool btbn,
        ref List<EncoderInfo> encoders, ref HashSet<string> audioAvailable,
        ref HashSet<string> availableFilters, ref HashSet<string> availableScaleAlgorithms)
    {
        if (!Toolchain.IsInstalled(c.FfmpegSource))
        {
            Console.WriteLine(L.T("tc.notinstalled.hint", c.FfmpegSource));
            return;
        }

        try
        {
            ffmpeg = FFmpegDiscovery.FindFFmpeg(null, c.FfmpegSource);
            btbn = FFmpegDiscovery.IsBtbn;
            ffprobe = FFmpegDiscovery.FindFFprobe(null, ffmpeg);
            availableFilters = FFmpegCapabilities.DetectVideoFilters(ffmpeg);
            availableFilters = FFmpegCapabilities.VerifyNamedFilters(
                ffmpeg, availableFilters, ["scale", .. Constants.DeinterlaceFilters]);
            availableScaleAlgorithms = FFmpegCapabilities.DetectScaleAlgorithms(ffmpeg);
            encoders = FFmpegDiscovery.DetectLegacyEncoders(ffmpeg);
            audioAvailable = FFmpegDiscovery.DetectAudioEncoders(ffmpeg);

            // The selected encoder may not exist in the new build; dropping it
            // beats starting an encode that is guaranteed to fail.
            if (c.Encoder is not null
                && !encoders.Any(e => string.Equals(e.Name, c.Encoder.Name, StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine(L.T("tc.encodergone", c.Encoder.Name));
                c.Encoder = null;
            }
            Console.WriteLine(L.T("tc.refreshed", ffmpeg));
        }
        catch (Exception exc) when (exc is FileNotFoundException or InvalidOperationException or TimeoutException)
        {
            Console.WriteLine($"{L.T("error.prefix")}{exc.Message}");
        }
    }

    private static void EditPlayback(Config c, string ffmpeg)
    {
        Console.Write(L.T("play.prompt.mode", Player.ModeName(c.Play)));
        string raw = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        if (raw.Length == 0) return;

        PlayMode mode = Player.ParseMode(raw);
        if (mode == PlayMode.Off && raw is not ("off" or "0" or "none"))
        {
            Console.WriteLine(L.T("play.badmode"));
            return;
        }
        c.Play = mode;

        if (mode == PlayMode.External)
        {
            Console.Write(L.T("play.prompt.player", c.PlayerPath ?? L.T("play.assoc")));
            string entered = (Console.ReadLine() ?? "").Trim().Trim('"');
            // "-" clears the player so playback falls back to the shell association.
            if (entered == "-") c.PlayerPath = null;
            else if (entered.Length > 0) c.PlayerPath = entered;
        }

        Console.WriteLine(L.T("play.set", Player.Describe(c.Play, c.PlayerPath, FFmpegDiscovery.FindFFplay(ffmpeg))));
    }

    private static FileInfo? InputFileMenu()
    {
        Console.Write(L.T("input.prompt"));
        string raw = (Console.ReadLine() ?? "").Trim().Trim('"');
        if (raw.Length == 0) return null;

        var path = new FileInfo(raw);
        if (path.Exists) return path;
        if (!Directory.Exists(raw))
        {
            Console.WriteLine(L.T("input.notfound"));
            return null;
        }

        var files = new DirectoryInfo(raw).EnumerateFiles()
            .Where(p => Constants.VideoExtensions.Contains(p.Extension.ToLowerInvariant()))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0)
        {
            Console.WriteLine(L.T("input.novideo"));
            return null;
        }

        for (int i = 0; i < Math.Min(200, files.Count); i++)
            Console.WriteLine($" {i + 1,3}. {files[i].Name}");

        Console.Write(L.T("input.selectnumber"));
        if (!int.TryParse((Console.ReadLine() ?? "").Trim(), out int idx))
        {
            Console.WriteLine(L.T("input.invalid"));
            return null;
        }
        idx -= 1;
        if (idx < 0 || idx >= Math.Min(200, files.Count))
        {
            Console.WriteLine(L.T("input.invalid"));
            return null;
        }
        return files[idx];
    }

    private static void ChooseEncoder(
        Config c, List<EncoderInfo> encoders, HashSet<string> audioAvailable, string ffmpeg)
    {
        Console.WriteLine($"\n{L.T("encoder.heading")}");
        for (int i = 0; i < encoders.Count; i++)
        {
            EncoderInfo item = encoders[i];
            string pix = item.Caps is { SupportedPixFmts.Count: > 0 } caps
                ? string.Join(",", caps.SupportedPixFmts) : "?";
            string marker = c.Encoder is not null && item.Name == c.Encoder.Name ? " <" : "";
            Console.WriteLine($" {i + 1,3}. {item.Name,-14} [{item.Family,-18}] {item.Description}{marker}");
            Console.WriteLine($"       {L.T("encoder.pixfmts")}: {pix}");
        }

        Console.Write(L.T("encoder.prompt"));
        string raw = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        if (raw == "r") return;

        try
        {
            if (!int.TryParse(raw, out int idx) || idx < 1 || idx > encoders.Count)
                throw new ArgumentException(L.T("encoder.outofrange"));

            EncoderInfo chosen = encoders[idx - 1];
            chosen.Caps ??= FFmpegCapabilities.ProbeEncoderCaps(ffmpeg, chosen);

            // v7.8: a container the new encoder cannot write is dropped rather
            // than carried over -- otherwise selecting mpeg1video after libx264
            // would leave ".mkv" on a file the muxer will refuse.
            if (c.Container is { Length: > 0 } wanted
                && EncoderMath.ResolveContainer(chosen, wanted) is null)
            {
                Console.WriteLine(L.T("encoder.containerdropped", wanted, chosen.Name));
                c.Container = null;
            }
            c.Encoder = EncoderMath.ResolveContainer(chosen, c.Container) ?? chosen;
            EncoderInfo effective = c.Encoder;

            IReadOnlyList<(string Ext, string Format)> containers =
                EncoderMath.AllowedContainers(effective);
            if (containers.Count > 1)
            {
                Console.WriteLine(L.T("encoder.containers",
                    string.Join(", ", containers.Select(x => x.Format))));
            }

            (double baseFps, bool exact) = EncoderMath.ChooseBaseFps(c.Fps, chosen.Caps);
            if (!exact)
            {
                Console.WriteLine(L.T("encoder.fpsadjusted", chosen.Name, $"{c.Fps:G6}", $"{baseFps:G6}"));
                c.Fps = baseFps;
            }
            c.FpsLevels = EncoderMath.SelectLevels(c.Fps, null);
            c.FpsLevelKbps = EncoderMath.PruneRungs(c.FpsLevelKbps, c.FpsLevels);
            (c.Width, c.Height, bool sizeExact) = EncoderMath.NormalizeDimensions(chosen.Name, c.Width, c.Height);
            if (!sizeExact) Console.WriteLine(L.T("encoder.sizesdjusted", chosen.Name, c.Width, c.Height));

            c.Qv = EncoderMath.DefaultQuality(effective.Name);

            // Audio follows the container: whatever was selected stays only if
            // it still muxes, otherwise the container's own preference wins.
            if (c.AudioEncoder is { Length: > 0 } current
                && !AudioCatalog.SupportsContainer(current, effective.FormatName))
            {
                c.AudioEncoder = null;
            }
            c.AudioEncoder = EncoderMath.ChooseAudioEncoder(effective.FormatName, audioAvailable);
            (c.AudioRate, c.AudioChannels) = AudioCatalog.Effective(
                c.AudioEncoder, c.AudioRate, c.AudioChannels);

            if (effective.Name == "amv")
            {
                Console.WriteLine(L.T("encoder.amvnote"));
                if (audioAvailable.Contains("adpcm_ima_amv")) c.AudioEncoder = "adpcm_ima_amv";
                c.AudioRate = 22050;
            }
            else if (effective.Name == "roqvideo")
            {
                if (audioAvailable.Contains("roq_dpcm"))
                {
                    c.AudioEncoder = "roq_dpcm";
                    c.AudioRate = 22050;
                    c.AudioChannels = Math.Min(Math.Max(c.AudioChannels, 1), 2);
                    Console.WriteLine(L.T("encoder.roqnote"));
                }
                else
                {
                    c.AudioEncoder = null;
                    Console.WriteLine(L.T("encoder.roqnoaudio"));
                }
                string? warning = EncoderMath.ValidateRoqDimensions(c.Width, c.Height);
                if (warning is not null) Console.WriteLine($"WARNING: {warning}");
            }

            if (c.AudioEncoder is null)
                Console.WriteLine(L.T("encoder.videoonly", effective.Name, effective.FormatName));
            if (effective.Name != "libx264")
            {
                c.X264Preset = "medium";
                c.X264Tune = "";
                c.X264Profile = "";
                c.X264Level = "";
            }
            if (c.OutputIsAuto && c.InputPath is not null)
                c.OutputPath = DefaultOutput(c.InputPath, effective);
        }
        catch (Exception exc) when (exc is ArgumentException or InvalidOperationException or FormatException)
        {
            Console.WriteLine(L.T("encoder.invalid", exc.Message));
        }
    }

    /// <summary>
    /// v7.8 menu row 6: list the audio encoders this build has <em>and</em> the
    /// selected video encoder's container can hold, then pick one.
    ///
    /// The list is filtered twice on purpose. `ffmpeg -encoders` happily prints
    /// codecs the muxer will refuse (ac3 in Ogg, vorbis in ASF), and the
    /// sample-rate limits are invisible until FFmpeg aborts -- so both are shown
    /// here instead.
    /// </summary>
    private static void ScanAudioEncoders(Config c, string ffmpeg, ref HashSet<string> audioAvailable)
    {
        if (c.Encoder is null)
        {
            Console.WriteLine(L.T("audio.selectvideo"));
            return;
        }

        try
        {
            audioAvailable = FFmpegDiscovery.DetectAudioEncoders(ffmpeg);
        }
        catch (Exception exc) when (exc is InvalidOperationException or TimeoutException)
        {
            Console.WriteLine($"{L.T("error.prefix")}{exc.Message}");
            return;
        }

        string container = c.Encoder.FormatName;
        List<string> compatible = AudioCatalog.Compatible(container, audioAvailable);
        Console.WriteLine($"\n{L.T("audio.heading")}");
        Console.WriteLine(L.T("audio.scanned", container, audioAvailable.Count, compatible.Count));
        if (compatible.Count == 0)
        {
            Console.WriteLine(L.T("audio.none", container));
            return;
        }

        for (int i = 0; i < compatible.Count; i++)
        {
            string name = compatible[i];
            AudioCodecInfo? info = AudioCatalog.Find(name);
            string marker = string.Equals(c.AudioEncoder, name, StringComparison.OrdinalIgnoreCase) ? " <" : "";
            Console.WriteLine($" {i + 1,3}. {name,-16}{AudioCatalog.DescribeRates(name),-34}"
                + $"{(info is null ? "" : $"{info.MinChannels}-{info.MaxChannels}ch")}"
                + (info?.Note.Length > 0 ? $"  ({info.Note})" : "") + marker);
        }
        Console.WriteLine($" 0. {L.T("param.back")}");
        Console.Write(L.T("audio.prompt"));

        string raw = (Console.ReadLine() ?? "").Trim();
        if (raw.Length == 0 || raw == "0") return;

        // Every path below either assigns this or returns early, so there is no
        // "nothing picked" value to fall back to.
        string? pick;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
        {
            if (idx < 1 || idx > compatible.Count)
            {
                Console.WriteLine(L.T("audio.badpick"));
                return;
            }
            pick = compatible[idx - 1];
        }
        else if (audioAvailable.Contains(raw))
        {
            // Typing a name is allowed, but only inside the compatible list --
            // picking an unmuxable codec here would fail the same way it does
            // when the encoder starts, just later.
            if (!compatible.Contains(raw, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine(L.T("audio.nomux", raw, container,
                    string.Join(", ", compatible)));
                return;
            }
            pick = raw;
        }
        else
        {
            Console.WriteLine(L.T("audio.notfound", raw));
            return;
        }

        int? rateBefore = c.AudioRate;
        int channelsBefore = c.AudioChannels;
        c.AudioEncoder = pick;
        (c.AudioRate, c.AudioChannels) = AudioCatalog.Effective(pick, c.AudioRate, c.AudioChannels);
        if (c.AudioChannels != channelsBefore)
            Console.WriteLine(L.T("audio.channelsadjusted", channelsBefore, c.AudioChannels));
        if (c.AudioRate != rateBefore)
            Console.WriteLine(L.T("audio.rateadjusted",
                rateBefore?.ToString(CultureInfo.InvariantCulture) ?? L.T("value.auto"),
                c.AudioRate?.ToString(CultureInfo.InvariantCulture) ?? L.T("value.auto")));
        Console.WriteLine(L.T("audio.set", pick, AudioCatalog.DescribeRates(pick)));
    }

    private static void EditSettings(
        Config c, HashSet<string> availableFilters, HashSet<string> availableScaleAlgorithms,
        string ffmpeg)
    {
        var deintOptions = new List<string> { "off" };
        deintOptions.AddRange(Constants.DeinterlaceFilters.Where(f => availableFilters.Contains(f)));

        while (true)
        {
            string enc = c.Encoder?.Name ?? "(none)";
            string qualityLabel = enc == "libx264" ? "CRF" : "q:v";
            Console.WriteLine($"\n{L.T("param.heading")}");
            Row(1, L.T("param.limit"), $"{c.Limit:G6}");
            Row(2, L.T("param.recover"), $"{c.Recover:G6}");
            Row(3, L.T("param.window"), $"{c.Window:G6}");
            Row(4, L.T("param.basefps"), $"{c.Fps:G6}");
            Row(5, L.T("param.levels"), EncoderMath.FormatLadder(c.Fps, c.FpsLevels, c.FpsLevelKbps));
            Row(6, qualityLabel, c.Qv.ToString(CultureInfo.InvariantCulture));
            Row(7, L.T("param.threads"), c.Threads.ToString(CultureInfo.InvariantCulture));
            Row(8, L.T("param.buffer"), c.BufferFrames.ToString(CultureInfo.InvariantCulture));
            Row(9, L.T("param.size"), $"{c.Width}x{c.Height} ({DescribeAspect(c.Width, c.Height)})");
            Row(10, L.T("param.fitmode"), DescribeFit(c));
            Row(11, L.T("param.scalealgo"), c.ScaleAlgo);
            Row(12, L.T("param.deinterlace"), c.Deinterlace);
            Row(13, L.T("param.deintmode"), c.DeintMode);
            Row(14, L.T("param.parity"), c.DeintParity);
            Row(15, L.T("param.markedonly"), L.YesNo(c.DeintMarkedOnly));
            Row(16, L.T("param.audiokbps"), $"{c.AudioKbps}k");
            Row(17, L.T("param.audiochannels"), c.AudioChannels.ToString(CultureInfo.InvariantCulture));
            Row(18, L.T("param.audiorate"), c.AudioRate?.ToString(CultureInfo.InvariantCulture) ?? L.T("value.auto"));
            Row(19, L.T("param.hwaccel"), c.Hwaccel);
            Row(20, L.T("param.audioencoder"), c.AudioEncoder ?? L.T("value.none"));
            if (enc == "libx264")
            {
                Row(21, L.T("param.x264preset"), c.X264Preset);
                Row(22, L.T("param.x264tune"), c.X264Tune.Length == 0 ? L.T("value.none") : c.X264Tune);
                Row(23, L.T("param.x264profile"), c.X264Profile.Length == 0 ? L.T("value.auto") : c.X264Profile);
                Row(24, L.T("param.x264level"), c.X264Level.Length == 0 ? L.T("value.auto") : c.X264Level);
            }
            Row(25, L.T("param.start"), Timecode.Format(c.StartTimeSeconds));
            Row(26, L.T("param.end"), Timecode.Format(c.EndTimeSeconds));
            Row(27, L.T("param.levelkbps"), DescribeRungBitrates(c));
            Row(28, L.T("param.container"), DescribeContainer(c));
            Row(29, L.T("param.cropanchor"), c.FitMode == "crop" ? c.CropAnchor : "-");
            Row(30, L.T("param.padcolor"), c.FitMode == "fit" ? c.PadColor : "-");
            Row(31, L.T("param.sizepreset"), L.T("param.sizepreset.hint"));
            Console.WriteLine($"    {L.T("param.availdeint")}     = {string.Join(", ", deintOptions)}");
            Console.WriteLine($" 0. {L.T("param.back")}");
            Console.Write(L.T("param.select"));

            string pick = (Console.ReadLine() ?? "").Trim();
            try
            {
                switch (pick)
                {
                    case "0": return;
                    case "1": c.Limit = ReadDouble(L.T("param.prompt.limit", $"{c.Limit:G6}"), c.Limit); break;
                    case "2": c.Recover = ReadDouble(L.T("param.prompt.recover", $"{c.Recover:G6}"), c.Recover); break;
                    case "3": c.Window = ReadDouble(L.T("param.prompt.window", $"{c.Window:G6}"), c.Window); break;
                    case "4":
                        c.Fps = ReadDouble(L.T("param.prompt.fps", $"{c.Fps:G6}"), c.Fps);
                        if (c.Encoder?.Caps is not null)
                        {
                            (double fps, bool fpsExact) = EncoderMath.ChooseBaseFps(c.Fps, c.Encoder.Caps);
                            c.Fps = fps;
                            if (!fpsExact) Console.WriteLine(L.T("param.fpsadjusted", $"{c.Fps:G6}"));
                        }
                        c.FpsLevels = EncoderMath.SelectLevels(c.Fps, null);
                        // A rung above the new base rate cannot be reached, so
                        // its thresholds are dropped along with it.
                        c.FpsLevelKbps = EncoderMath.PruneRungs(c.FpsLevelKbps, c.FpsLevels);
                        break;
                    case "5":
                        string levelsRaw = ReadRaw(L.T("param.prompt.levels",
                            EncoderMath.FormatLadder(c.Fps, c.FpsLevels, c.FpsLevelKbps)));
                        if (levelsRaw.Length == 0) break;

                        (List<double> levelFps, List<FpsRungSetting> rungs, string? levelError) =
                            EncoderMath.ParseLevelSpec(levelsRaw);
                        if (levelError is not null) throw new FormatException(levelError);

                        // A motion level faster than the base rate is a typo: the
                        // ladder only ever throttles downwards.
                        foreach (double candidate in levelFps)
                        {
                            if (candidate > c.Fps + 1e-9)
                                throw new FormatException(L.T("param.levelstoofast", $"{candidate:G6}", $"{c.Fps:G6}"));
                        }
                        c.FpsLevels = EncoderMath.SelectLevels(c.Fps, levelFps);
                        c.FpsLevelKbps = EncoderMath.PruneRungs(rungs, c.FpsLevels);
                        break;
                    case "6":
                        c.Qv = ReadInt(L.T("param.prompt.quality", qualityLabel, c.Qv), c.Qv);
                        break;
                    case "7":
                        c.Threads = ReadInt(L.T("param.prompt.threads", c.Threads), c.Threads);
                        break;
                    case "8":
                        c.BufferFrames = ReadInt(L.T("param.prompt.buffer", c.BufferFrames), c.BufferFrames);
                        break;
                    case "9":
                        string sizeRaw = ReadRaw(L.T("param.prompt.size", c.Width, c.Height)).ToLowerInvariant();
                        if (sizeRaw.Length > 0)
                        {
                            Match m = Regex.Match(sizeRaw, @"^(\d+)x(\d+)$");
                            if (!m.Success) throw new FormatException(L.T("param.sizefmt"));
                            c.Width = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                            c.Height = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                        }
                        break;
                    case "10":
                        // v7.8: crop/fit/stretch replaced the old "crop 4:3"
                        // switch. "fit" is what keeps a 16:9 master whole inside
                        // a 4:3 frame instead of cutting its sides off.
                        string fitRaw = ReadRaw(L.T("param.prompt.fitmode",
                            string.Join(" / ", Constants.FitModes), c.FitMode)).ToLowerInvariant();
                        if (fitRaw.Length > 0)
                        {
                            if (!Constants.FitModes.Contains(fitRaw))
                                throw new FormatException(L.T("param.fitbad", string.Join(", ", Constants.FitModes)));
                            c.FitMode = fitRaw;
                        }
                        break;
                    case "11":
                        string scaleRaw = ReadRaw(L.T("param.prompt.scalealgo", c.ScaleAlgo,
                                string.Join(", ", availableScaleAlgorithms.OrderBy(x => x, StringComparer.Ordinal))))
                            .ToLowerInvariant();
                        if (scaleRaw.Length > 0)
                        {
                            string canonical = EncoderMath.CanonicalScaleAlgo(scaleRaw);
                            if (!availableScaleAlgorithms.Contains(canonical))
                                throw new FormatException(L.T("param.scalebad", canonical));
                            c.ScaleAlgo = canonical;
                        }
                        break;
                    case "12":
                        string deintRaw = ReadRaw(
                            L.T("param.prompt.deinterlace", string.Join(" / ", deintOptions), c.Deinterlace))
                            .ToLowerInvariant();
                        if (deintRaw.Length > 0)
                        {
                            if (!deintOptions.Contains(deintRaw))
                                throw new FormatException(L.T("param.deintbad", string.Join(", ", deintOptions)));
                            c.Deinterlace = deintRaw;
                        }
                        break;
                    case "13":
                        string modeRaw = ReadRaw(L.T("param.prompt.deintmode", c.DeintMode)).ToLowerInvariant();
                        if (modeRaw.Length > 0)
                        {
                            if (modeRaw is not ("send_frame" or "send_field"))
                                throw new FormatException(L.T("param.modebad"));
                            c.DeintMode = modeRaw;
                        }
                        break;
                    case "14":
                        string parityRaw = ReadRaw(L.T("param.prompt.parity", c.DeintParity)).ToLowerInvariant();
                        if (parityRaw.Length > 0)
                        {
                            if (parityRaw is not ("auto" or "tff" or "bff"))
                                throw new FormatException(L.T("param.paritybad"));
                            c.DeintParity = parityRaw;
                        }
                        break;
                    case "15": c.DeintMarkedOnly = !c.DeintMarkedOnly; break;
                    case "16":
                        c.AudioKbps = ReadInt(L.T("param.prompt.audiokbps", c.AudioKbps), c.AudioKbps);
                        break;
                    // Audio numbers are validated against the codec, not just
                    // for being positive: ac3 aborts on anything but 32/44.1/48
                    // kHz, and it used to do that halfway through an encode.
                    case "17":
                        c.AudioChannels = ReadInt(L.T("param.prompt.audiochannels", c.AudioChannels), c.AudioChannels);
                        int clamped = AudioCatalog.ClampChannels(c.AudioEncoder, c.AudioChannels);
                        if (clamped != c.AudioChannels)
                        {
                            Console.WriteLine(L.T("audio.channelsadjusted", c.AudioChannels, clamped));
                            c.AudioChannels = clamped;
                        }
                        break;
                    case "18":
                        string rateRaw = ReadRaw(L.T("param.prompt.audiorate",
                            c.AudioRate?.ToString(CultureInfo.InvariantCulture) ?? L.T("value.auto")));
                        int? requested = rateRaw.Length > 0
                            ? int.Parse(rateRaw, CultureInfo.InvariantCulture) : null;
                        int? snapped = AudioCatalog.NearestRate(c.AudioEncoder, requested);
                        if (snapped != requested)
                        {
                            Console.WriteLine(L.T("audio.rateadjusted",
                                requested?.ToString(CultureInfo.InvariantCulture) ?? L.T("value.auto"),
                                snapped?.ToString(CultureInfo.InvariantCulture) ?? L.T("value.auto")));
                        }
                        c.AudioRate = snapped;
                        break;
                    case "19":
                        // Cycle off -> auto -> each method this FFmpeg was built
                        // with -> off. Whether a method actually works is decided
                        // at encode time by decoding a frame.
                        c.Hwaccel = NextHwaccel(c.Hwaccel, ffmpeg);
                        break;
                    case "20":
                        string audioRaw = ReadRaw(
                            L.T("param.prompt.audioencoder", c.AudioEncoder ?? L.T("value.none"))).ToLowerInvariant();
                        if (audioRaw.Length == 0)
                        {
                            c.AudioEncoder = null;
                            break;
                        }
                        string? reject = c.Encoder is null
                            ? null
                            : AudioCatalog.Validate(audioRaw, c.Encoder.FormatName,
                                c.AudioRate, c.AudioChannels, c.AudioKbps);
                        if (reject is not null)
                        {
                            Console.WriteLine($"{L.T("error.prefix")}{reject}");
                            break;
                        }
                        c.AudioEncoder = audioRaw;
                        // The new codec may not accept the old numbers.
                        (c.AudioRate, c.AudioChannels) = AudioCatalog.Effective(
                            audioRaw, c.AudioRate, c.AudioChannels);
                        Console.WriteLine(L.T("audio.set", audioRaw, AudioCatalog.DescribeRates(audioRaw)));
                        break;
                    case "21" when enc == "libx264":
                        c.X264Preset = ReadRaw(L.T("param.prompt.preset", c.X264Preset)) is var p && p.Length > 0 ? p : c.X264Preset;
                        break;
                    case "22" when enc == "libx264":
                        c.X264Tune = ReadRaw(L.T("param.prompt.tune", c.X264Tune.Length == 0 ? L.T("value.none") : c.X264Tune));
                        break;
                    case "23" when enc == "libx264":
                        c.X264Profile = ReadRaw(L.T("param.prompt.profile", c.X264Profile.Length == 0 ? L.T("value.auto") : c.X264Profile));
                        break;
                    case "24" when enc == "libx264":
                        c.X264Level = ReadRaw(L.T("param.prompt.level", c.X264Level.Length == 0 ? L.T("value.auto") : c.X264Level));
                        break;
                    case "25":
                        string startRaw = ReadRaw(L.T("param.prompt.start", Timecode.Format(c.StartTimeSeconds)));
                        if (startRaw.Length > 0)
                        {
                            double newStart = Timecode.Parse(startRaw);
                            if (c.EndTimeSeconds is not null && c.EndTimeSeconds <= newStart)
                                throw new FormatException(L.T("param.timerangebad"));
                            c.StartTimeSeconds = newStart;
                        }
                        break;
                    case "26":
                        string endRaw = ReadRaw(L.T("param.prompt.end", Timecode.Format(c.EndTimeSeconds)));
                        if (endRaw.Length > 0)
                        {
                            double? newEnd = endRaw.ToLowerInvariant() is "end" or "none" or "eof"
                                ? null
                                : Timecode.Parse(endRaw);
                            if (newEnd is not null && newEnd <= c.StartTimeSeconds)
                                throw new FormatException(L.T("param.timerangebad"));
                            c.EndTimeSeconds = newEnd;
                        }
                        break;
                    case "27":
                        EditRungBitrates(c);
                        break;
                    case "28":
                        EditContainer(c);
                        break;
                    case "29":
                        string anchorRaw = ReadRaw(L.T("param.prompt.cropanchor",
                            string.Join(" / ", Constants.CropAnchors), c.CropAnchor)).ToLowerInvariant();
                        if (anchorRaw.Length > 0)
                        {
                            if (!Constants.CropAnchors.Contains(anchorRaw))
                                throw new FormatException(L.T("param.anchorbad", string.Join(", ", Constants.CropAnchors)));
                            c.CropAnchor = anchorRaw;
                        }
                        break;
                    case "30":
                        string padRaw = ReadRaw(L.T("param.prompt.padcolor", c.PadColor));
                        if (padRaw.Length > 0) c.PadColor = padRaw;
                        break;
                    case "31":
                        PickResolutionPreset(c);
                        break;
                    default:
                        Console.WriteLine(L.T("param.invalid"));
                        break;
                }
            }
            catch (Exception exc) when (exc is FormatException or ArgumentException or OverflowException)
            {
                Console.WriteLine(L.T("param.badvalue", exc.Message));
            }

            try { Validation.ValidateCommonConfig(c); }
            catch (ArgumentException exc) { Console.WriteLine(L.T("param.badconfig", exc.Message)); }
        }
    }

    /// <summary>"4:3", "16:9" or the rounded ratio when it is neither.</summary>
    private static string DescribeAspect(int width, int height)
    {
        if (width <= 0 || height <= 0) return "?";
        double ratio = (double)width / height;
        if (Math.Abs(ratio - 4.0 / 3.0) < 0.02) return "4:3";
        if (Math.Abs(ratio - 16.0 / 9.0) < 0.02) return "16:9";
        return $"{ratio:F2}:1";
    }

    private static string DescribeFit(Config c) => c.FitMode switch
    {
        "fit" => L.T("param.fit.fit", c.PadColor),
        "stretch" => L.T("param.fit.stretch"),
        _ => L.T("param.fit.crop", c.CropAnchor),
    };

    private static string DescribeContainer(Config c)
    {
        if (c.Encoder is null) return L.T("value.none");
        string current = $"{c.Encoder.FormatName} (.{c.Encoder.Extension})";
        IReadOnlyList<(string Ext, string Format)> allowed = EncoderMath.AllowedContainers(c.Encoder);
        return allowed.Count > 1
            ? $"{current}   [{string.Join(", ", allowed.Select(x => x.Format))}]"
            : current;
    }

    /// <summary>
    /// Row 28: pick another container for the current encoder. Only the
    /// combinations that actually mux are offered (mpeg1/2video gain AVI,
    /// libx264 gains AVI and Matroska).
    /// </summary>
    private static void EditContainer(Config c)
    {
        if (c.Encoder is null)
        {
            Console.WriteLine(L.T("encoder.selectfirst"));
            return;
        }

        IReadOnlyList<(string Ext, string Format)> allowed = EncoderMath.AllowedContainers(c.Encoder);
        if (allowed.Count < 2)
        {
            Console.WriteLine(L.T("container.only", c.Encoder.Name, c.Encoder.FormatName));
            return;
        }

        Console.WriteLine($"\n{L.T("container.heading", c.Encoder.Name)}");
        for (int i = 0; i < allowed.Count; i++)
        {
            string marker = allowed[i].Format == c.Encoder.FormatName ? " <" : "";
            Console.WriteLine($" {i + 1,2}. {allowed[i].Format,-10} (.{allowed[i].Ext}){marker}");
        }
        Console.Write(L.T("container.prompt"));

        string raw = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        if (raw.Length == 0) return;

        string wanted = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)
            && idx >= 1 && idx <= allowed.Count
                ? allowed[idx - 1].Format
                : raw;

        EncoderInfo? resolved = EncoderMath.ResolveContainer(c.Encoder, wanted);
        if (resolved is null)
        {
            Console.WriteLine(L.T("container.bad", wanted, string.Join(", ", allowed.Select(x => x.Format))));
            return;
        }

        c.Encoder = resolved;
        // "null" means the encoder's own default, so the file stays portable
        // and a later encoder change does not drag a stale override along.
        IReadOnlyList<(string Ext, string Format)> after = EncoderMath.AllowedContainers(resolved);
        c.Container = after[0].Format == resolved.FormatName ? null : resolved.FormatName;

        // A different muxer means a different set of audio codecs.
        if (c.AudioEncoder is { Length: > 0 } current
            && !AudioCatalog.SupportsContainer(current, resolved.FormatName))
        {
            Console.WriteLine(L.T("audio.replaced", current, resolved.FormatName));
            c.AudioEncoder = null;
        }
        (c.AudioRate, c.AudioChannels) = AudioCatalog.Effective(
            c.AudioEncoder, c.AudioRate, c.AudioChannels);

        if (c.OutputIsAuto && c.InputPath is not null)
            c.OutputPath = DefaultOutput(c.InputPath, resolved);
        Console.WriteLine(L.T("container.set", resolved.FormatName, resolved.Extension));
    }

    /// <summary>
    /// Row 31: frame-size presets, grouped by aspect ratio. Picking a 16:9 pair
    /// is the other half of "store the whole 16:9 picture inside a 4:3 frame" --
    /// the fit mode decides what happens to the leftover space.
    /// </summary>
    private static void PickResolutionPreset(Config c)
    {
        (int W, int H, string Label)[] presets = Constants.ResolutionPresets;
        Console.WriteLine($"\n{L.T("param.size.heading")}");
        for (int i = 0; i < presets.Length; i++)
        {
            string marker = presets[i].W == c.Width && presets[i].H == c.Height ? " <" : "";
            Console.WriteLine($" {i + 1,2}. {presets[i].W}x{presets[i].H,-6} {presets[i].Label}{marker}");
        }
        Console.WriteLine(L.T("param.size.free"));
        Console.Write(L.T("param.size.prompt"));

        string raw = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        if (raw.Length == 0) return;

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)
            && idx >= 1 && idx <= presets.Length)
        {
            ApplySize(c, presets[idx - 1].W, presets[idx - 1].H);
            return;
        }

        Match m = Regex.Match(raw, @"^(\d+)x(\d+)$");
        if (!m.Success)
        {
            Console.WriteLine(L.T("param.sizefmt"));
            return;
        }
        ApplySize(c, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    private static void ApplySize(Config c, int width, int height)
    {
        c.Width = width;
        c.Height = height;
        if (c.Encoder is not null)
        {
            (c.Width, c.Height, bool sizeExact) = EncoderMath.NormalizeDimensions(
                c.Encoder.Name, width, height);
            if (!sizeExact) Console.WriteLine(L.T("encoder.sizesdjusted", c.Encoder.Name, c.Width, c.Height));
        }
        Console.WriteLine(L.T("param.size.set", c.Width, c.Height, DescribeAspect(c.Width, c.Height)));
    }

    /// <summary>Row 27: "auto" until at least one rung carries an explicit threshold.</summary>
    private static string DescribeRungBitrates(Config c)
    {
        if (c.FpsLevelKbps.Count == 0) return L.T("value.auto");
        List<FpsRung> ladder = EncoderMath.BuildLadder(
            c.Fps, c.FpsLevels, c.FpsLevelKbps, c.Limit, c.Recover);
        return string.Join(", ", ladder.Skip(1).Select(
            r => $"{r.Fps:G6}:{r.DropAboveKbps:G6}/{r.RiseBelowKbps:G6}"));
    }

    /// <summary>
    /// Per-rung bitrate editor. The ladder itself is row 5; this is where the
    /// kbps that selects each rung are set, e.g. "drop to 12 fps at 128 kbps,
    /// climb back at 115".
    /// </summary>
    private static void EditRungBitrates(Config c)
    {
        while (true)
        {
            List<FpsRung> ladder = EncoderMath.BuildLadder(
                c.Fps, c.FpsLevels, c.FpsLevelKbps, c.Limit, c.Recover);

            Console.WriteLine($"\n{L.T("param.rung.heading")}");
            Console.WriteLine($" {L.T("param.rung.base")}: {c.Fps:G6}");
            for (int i = 1; i < ladder.Count; i++)
            {
                Console.WriteLine($" {i,2}. " + L.T("param.rung.row",
                    $"{ladder[i].Fps:G6}", $"{ladder[i].DropAboveKbps:G6}", $"{ladder[i].RiseBelowKbps:G6}"));
            }
            Console.WriteLine($" 0. {L.T("param.back")}");
            Console.Write(L.T("param.rung.pick"));

            string pick = (Console.ReadLine() ?? "").Trim();
            if (pick == "0") return;
            if (!int.TryParse(pick, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
                || index < 1 || index >= ladder.Count)
            {
                Console.WriteLine(L.T("param.invalid"));
                continue;
            }

            double fps = ladder[index].Fps;
            FpsRungSetting? rung = c.FpsLevelKbps.FirstOrDefault(x => Math.Abs(x.Fps - fps) < 1e-6);
            double dropNow = rung?.DropAboveKbps ?? ladder[index].DropAboveKbps;
            double riseNow = rung?.RiseBelowKbps ?? ladder[index].RiseBelowKbps;

            try
            {
                double drop = ReadDouble(L.T("param.rung.prompt.drop", $"{fps:G6}", $"{dropNow:G6}"), dropNow);
                double rise = ReadDouble(L.T("param.rung.prompt.rise", $"{fps:G6}", $"{riseNow:G6}"), riseNow);
                if (drop <= 0 || rise <= 0 || rise >= drop) throw new FormatException(L.T("param.rung.bad"));

                if (rung is null)
                {
                    rung = new FpsRungSetting { Fps = fps };
                    c.FpsLevelKbps.Add(rung);
                }
                rung.DropAboveKbps = drop;
                rung.RiseBelowKbps = rise;
            }
            catch (Exception exc) when (exc is FormatException or ArgumentException or OverflowException)
            {
                Console.WriteLine(L.T("param.badvalue", exc.Message));
            }
        }
    }

    /// <summary>" N. label               = value", padded by display width so the
    /// equals signs line up in English and Chinese alike.</summary>
    private static void Row(int number, string label, string value) =>
        Console.WriteLine($"{number,3}. {L.Pad(label, 29)} = {value}");

    private static string ReadRaw(string prompt)
    {
        Console.Write(prompt);
        return (Console.ReadLine() ?? "").Trim();
    }

    private static double ReadDouble(string prompt, double fallback)
    {
        string raw = ReadRaw(prompt);
        return raw.Length > 0 ? double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture) : fallback;
    }

    private static int ReadInt(string prompt, int fallback)
    {
        string raw = ReadRaw(prompt);
        return raw.Length > 0 ? int.Parse(raw, CultureInfo.InvariantCulture) : fallback;
    }

    private static void StartEncoding(
        Config c, string ffmpeg, string ffprobe, bool btbn, List<EncoderInfo> encoders,
        HashSet<string> audioAvailable, HashSet<string> availableFilters,
        HashSet<string> availableScaleAlgorithms)
    {
        if (c.InputPath is null || !c.InputPath.Exists)
        {
            Console.WriteLine(L.T("input.select"));
            return;
        }
        if (c.Encoder is null)
        {
            Console.WriteLine(L.T("encoder.selectfirst"));
            return;
        }

        c.OutputPath ??= DefaultOutput(c.InputPath, c.Encoder);
        c.OutputPath.Directory?.Create();
        if (string.Equals(Path.GetFullPath(c.OutputPath.FullName), Path.GetFullPath(c.InputPath.FullName),
                StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(L.T("output.sameasinput"));
            return;
        }

        try
        {
            Validation.ValidateOutputExtension(c.OutputPath, c.Encoder);
        }
        catch (ArgumentException exc)
        {
            Console.WriteLine($"{L.T("error.prefix")}{exc.Message}");
            return;
        }

        c.Encoder.Caps ??= FFmpegCapabilities.ProbeEncoderCaps(ffmpeg, c.Encoder);

        try
        {
            Validation.ValidateCommonConfig(c);
            if (c.Deinterlace != "off" && !availableFilters.Contains(c.Deinterlace))
                throw new ArgumentException($"FFmpeg filter '{c.Deinterlace}' is not available");
            if (!availableScaleAlgorithms.Contains(EncoderMath.CanonicalScaleAlgo(c.ScaleAlgo)))
                throw new ArgumentException($"scale algorithm '{c.ScaleAlgo}' is not exposed by this FFmpeg build");
            if (c.AudioEncoder is not null && !audioAvailable.Contains(c.AudioEncoder))
                throw new ArgumentException($"audio encoder '{c.AudioEncoder}' is not available");
        }
        catch (ArgumentException exc)
        {
            Console.WriteLine($"{L.T("error.prefix")}{exc.Message}");
            return;
        }

        Report.PrintConfig(c, ffmpeg, btbn, encoders.Count, availableFilters, availableScaleAlgorithms);
        Console.Write(L.T("start.confirm"));
        string confirm = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        if (confirm is not ("" or "y" or "yes")) return;

        try
        {
            // A fresh token per encode. The shell survives an interrupt, so a
            // cancelled token must never leak into the next run.
            CancellationToken cancellationToken = Interrupt.BeginOperation();

            var options = new Encoding.RealtimeOptions
            {
                FFmpeg = ffmpeg,
                FFprobe = ffprobe,
                SourcePath = c.InputPath.FullName,
                DestinationPath = c.OutputPath.FullName,
                Encoder = c.Encoder,
                Fps = c.Fps,
                FpsLevels = c.FpsLevels,
                FpsLevelKbps = c.FpsLevelKbps,
                Quality = c.Qv,
                Threads = c.Threads,
                LimitKbps = c.Limit,
                RecoverKbps = c.Recover,
                WindowSeconds = c.Window,
                BufferFrames = c.BufferFrames,
                AudioEncoder = c.AudioEncoder,
                AudioKbps = c.AudioKbps,
                AudioChannels = c.AudioChannels,
                AudioRate = c.AudioRate,
                Hwaccel = c.Hwaccel,
                Width = c.Width,
                Height = c.Height,
                FitMode = c.FitMode,
                CropAnchor = c.CropAnchor,
                PadColor = c.PadColor,
                ScaleAlgo = c.ScaleAlgo,
                Deinterlace = c.Deinterlace,
                DeintMode = c.DeintMode,
                DeintParity = c.DeintParity,
                DeintMarkedOnly = c.DeintMarkedOnly,
                X264Preset = c.Encoder.Name == "libx264" ? c.X264Preset : null,
                X264Tune = c.Encoder.Name == "libx264" ? c.X264Tune : null,
                X264Profile = c.Encoder.Name == "libx264" ? c.X264Profile : null,
                X264Level = c.Encoder.Name == "libx264" ? c.X264Level : null,
                StartSeconds = c.StartTimeSeconds,
                EndSeconds = c.EndTimeSeconds,
                AvailableFilters = availableFilters,
                AvailableScaleAlgorithms = availableScaleAlgorithms,
                Verbose = true,
                Turbo = c.Turbo,
                Parallel = c.Parallel,
            };

            // Keep the settings file in step even if the encode is interrupted.
            Persist(c);

            Encoding.EncodeResult result = Encoding.RealtimeEncoder.RunSplit(options, cancellationToken);
            Console.WriteLine();
            Report.PrintResult(c.OutputPath.FullName, result);

            // v7.6: watermark first, then the video-site recompress pass, in that
            // order -- the mark has to suffer the same transcode the picture does.
            string finalPath = c.OutputPath.FullName;
            if (PostProcess.PostPipeline.Needed(c))
            {
                PostProcess.PostProcessOutcome post =
                    PostProcess.PostPipeline.Run(ffmpeg, c.OutputPath.FullName, c);
                finalPath = post.FinalPath;

                if (post.WatermarkRan && post.WatermarkPath is { Length: > 0 } wm)
                    Console.WriteLine(L.T("post.watermark", wm));
                if (post.RecompressRan && post.RecompressPath is { Length: > 0 } rc)
                    Console.WriteLine(L.T("post.recompress", rc));
                if (post.IntermediateRemoved)
                    Console.WriteLine(L.T("post.intermediate"));

                if (post.Failed)
                {
                    string stage = post.FailedStage!;
                    string detail = (stage == "watermark" ? post.WatermarkError : post.RecompressError) ?? "";
                    Console.WriteLine($"{L.T("error.prefix")}{L.T("post.failed", stage, detail)}");
                    Console.WriteLine(L.T("post.kept", c.OutputPath.FullName));
                    finalPath = c.OutputPath.FullName;   // play what did survive
                }
            }

            if (c.Play != PlayMode.Off)
            {
                Console.Write(L.T("play.ask"));
                string answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
                if (answer is "" or "y" or "yes")
                {
                    (bool played, string? playError) = Player.Launch(
                        c.Play, finalPath, ffmpeg, c.PlayerPath);
                    if (!played && playError is not null)
                        Console.WriteLine($"{L.T("error.prefix")}{L.T("play.launchfail", playError)}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Interrupt.Request() already reported it on the way in.
        }
        catch (Exception exc)
        {
            Console.Error.WriteLine($"\n{L.T("error.prefix")}{exc.Message}");
        }
    }

    /// <summary>Cycle the hardware-decode setting for CUI row 19.</summary>
    private static string NextHwaccel(string current, string ffmpeg)
    {
        List<string> choices = ["off", "auto"];
        choices.AddRange(FFmpeg.Hwaccel.CompiledIn(ffmpeg)
            .Where(m => !choices.Contains(m, StringComparer.OrdinalIgnoreCase)));

        int index = choices.FindIndex(m => string.Equals(m, current, StringComparison.OrdinalIgnoreCase));
        return choices[(index + 1) % choices.Count];
    }

    // ---- v7.6: era preset / watermark / recompress menus ----

    private static string DescribeEra(Config c) =>
        c.EraPreset is { Length: > 0 } id && PostProcess.EraPresets.Find(id) is not null
            ? id
            : L.T("era.none");

    private static string DescribeWatermark(Config c) =>
        c.Watermark.Enabled
            ? $"\"{c.Watermark.Text}\""
            : L.T("wm.off");

    private static string DescribeRecompress(Config c) =>
        (c.Recompress.Enabled ? "on" : L.T("wm.off"))
        + (c.Recompress.Args.Length > 0 ? $" | {c.Recompress.Args}" : "");

    private static void PickEraPreset(Config c)
    {
        Console.WriteLine(L.T("era.choose", PostProcess.EraPresets.Ids));
        foreach (PostProcess.EraPreset preset in PostProcess.EraPresets.All)
            Console.WriteLine($"  {preset.Id,-14} {preset.YearFrom}-{preset.YearTo}  {L.T(preset.NoteKey)}");

        Console.Write(L.T("era.prompt", c.EraPreset ?? ""));
        string pick = (Console.ReadLine() ?? "").Trim();
        if (pick.Length == 0) return;

        PostProcess.EraPreset? chosen = PostProcess.EraPresets.Find(pick);
        if (chosen is null)
        {
            Console.WriteLine($"{L.T("error.prefix")}{L.T("era.unknown", pick, PostProcess.EraPresets.Ids)}");
            return;
        }

        // The preset writes the live layer straight into the config and pre-fills
        // the recompress arguments. It does not arm the recompress stage: that is
        // opt-in by design, and the menu is where a user turns it on.
        chosen.ApplyLive(c);
        c.Recompress.Args = chosen.RecompressArgs;
        c.Recompress.Container = chosen.RecompressContainer;
        c.EraPreset = chosen.Id;

        Console.WriteLine(L.T("era.applied", chosen.Id,
            c.Width, c.Height, $"{c.Fps:G6}", $"{c.Limit:G6}"));
        Console.WriteLine(L.T("rc.hint"));
    }

    private static void EditWatermark(Config c)
    {
        while (true)
        {
            Console.WriteLine($"\n== {L.T("wm.menu")} ==");
            Console.WriteLine($" 1. {L.Pad(L.T("wm.menu.enable"), 30)} = {DescribeWatermark(c)}");
            Console.WriteLine($" 2. {L.Pad(L.T("wm.menu.position"), 30)} = {c.Watermark.Position}");
            Console.WriteLine($" 3. {L.Pad(L.T("wm.menu.size"), 30)} = {(c.Watermark.FontSize > 0 ? c.Watermark.FontSize.ToString(CultureInfo.InvariantCulture) : "0" + L.T("wm.size.auto"))}");
            Console.WriteLine($" 4. {L.Pad(L.T("wm.menu.color"), 30)} = {c.Watermark.FontColor}");
            Console.WriteLine($" 5. {L.Pad(L.T("wm.menu.font"), 30)} = {c.Watermark.FontFile ?? L.T("value.auto")}");
            Console.WriteLine($" 0. {L.T("wm.menu.back")}");
            Console.Write(L.T("menu.select"));

            switch ((Console.ReadLine() ?? "").Trim())
            {
                case "0": return;
                case "1":
                    Console.Write(L.T("wm.prompt.text"));
                    string text = (Console.ReadLine() ?? "").Trim();
                    if (text.Length == 0 || text == "-")
                    {
                        c.Watermark.Enabled = false;
                        c.Watermark.Text = "";
                        Console.WriteLine(L.T("wm.off.set"));
                        break;
                    }
                    c.Watermark.Text = text;
                    c.Watermark.Enabled = true;
                    Console.WriteLine(L.T("wm.set", text));
                    break;
                case "2":
                    int p = WatermarkSettings.Positions
                        .ToList().IndexOf(WatermarkSettings.CanonicalPosition(c.Watermark.Position));
                    c.Watermark.Position = WatermarkSettings.Positions[(p + 1) % 5];
                    Console.WriteLine(L.T("wm.pos.set", c.Watermark.Position));
                    break;
                case "3":
                    Console.Write(L.T("wm.prompt.size", c.Watermark.FontSize));
                    if (int.TryParse((Console.ReadLine() ?? "").Trim(), out int size) && size >= 0)
                    {
                        c.Watermark.FontSize = size;
                        Console.WriteLine(L.T("wm.size.set", size, size == 0 ? L.T("wm.size.auto") : ""));
                    }
                    else Console.WriteLine($"{L.T("error.prefix")}{L.T("wm.badsize")}");
                    break;
                case "4":
                    Console.Write(L.T("wm.prompt.color", c.Watermark.FontColor));
                    string colour = (Console.ReadLine() ?? "").Trim();
                    if (colour.Length > 0)
                    {
                        c.Watermark.FontColor = colour;
                        Console.WriteLine(L.T("wm.color.set", colour));
                    }
                    break;
                case "5":
                    Console.Write(L.T("wm.prompt.font", c.Watermark.FontFile ?? L.T("value.auto")));
                    string font = (Console.ReadLine() ?? "").Trim().Trim('"');
                    if (font == "-")
                    {
                        c.Watermark.FontFile = null;
                        Console.WriteLine(L.T("wm.font.auto"));
                    }
                    else if (font.Length > 0)
                    {
                        c.Watermark.FontFile = font;
                        Console.WriteLine(L.T("wm.font.set", font));
                    }
                    break;
            }
        }
    }

    private static void EditRecompress(Config c)
    {
        while (true)
        {
            Console.WriteLine($"\n== {L.T("rc.menu")} ==");
            Console.WriteLine($" 1. {L.Pad(L.T("rc.menu.enable"), 30)} = {(c.Recompress.Enabled ? "on" : L.T("wm.off"))}");
            Console.WriteLine($" 2. {L.Pad(L.T("rc.menu.args"), 30)} = {(c.Recompress.Args.Length == 0 ? L.T("value.notselected") : c.Recompress.Args)}");
            Console.WriteLine($" 3. {L.Pad(L.T("rc.menu.container"), 30)} = {c.Recompress.Container}");
            Console.WriteLine($" 4. {L.Pad(L.T("rc.menu.keep"), 30)} = {L.YesNo(c.Recompress.KeepIntermediate)}");
            Console.WriteLine($" 0. {L.T("rc.menu.back")}");
            Console.Write(L.T("menu.select"));

            switch ((Console.ReadLine() ?? "").Trim())
            {
                case "0": return;
                case "1":
                    c.Recompress.Enabled = !c.Recompress.Enabled;
                    Console.WriteLine(c.Recompress.Enabled ? L.T("rc.on") : L.T("rc.off"));
                    if (c.Recompress.Enabled && c.Recompress.Args.Length == 0)
                        Console.WriteLine(L.T("rc.hint"));
                    break;
                case "2":
                    Console.WriteLine(L.T("rc.hint"));
                    Console.Write(L.T("rc.prompt.args"));
                    string fragment = (Console.ReadLine() ?? "").Trim();
                    if (fragment.Length == 0) break;

                    (List<string> _, string? error) = PostProcess.PostArgs.Parse(fragment);
                    if (error is not null)
                        Console.WriteLine($"{L.T("error.prefix")}{L.T("rc.args.rejected", error)}");
                    else
                    {
                        c.Recompress.Args = fragment;
                        Console.WriteLine(L.T("rc.args.set"));
                    }
                    break;
                case "3":
                    Console.Write(L.T("rc.prompt.container", c.Recompress.Container));
                    string container = (Console.ReadLine() ?? "").Trim().TrimStart('.').ToLowerInvariant();
                    if (container.Length > 0)
                    {
                        c.Recompress.Container = container;
                        Console.WriteLine(L.T("rc.container.set", container));
                    }
                    break;
                case "4":
                    c.Recompress.KeepIntermediate = !c.Recompress.KeepIntermediate;
                    Console.WriteLine(c.Recompress.KeepIntermediate ? L.T("rc.keep.on") : L.T("rc.keep.off"));
                    break;
            }
        }
    }
}
