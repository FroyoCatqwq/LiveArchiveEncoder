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
        if (!string.IsNullOrWhiteSpace(ffmpegSource)) c.FfmpegSource = ffmpegSource;

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
            Console.WriteLine($" 6. {L.T("menu.filters")}");
            Console.WriteLine($" 7. {L.T("menu.showconfig")}");
            Console.WriteLine($" 8. {L.T("menu.start")}");
            Console.WriteLine($" 9. {L.Pad(L.T("menu.turbo"), 22)} = {(c.Turbo ? L.T("turbo.on") : L.T("turbo.off"))}");
            Console.WriteLine($"10. {L.Pad(L.T("menu.language"), 22)} = {c.Language}");
            Console.WriteLine($"11. {L.Pad(L.T("menu.parallel"), 22)} = {(c.Parallel > 1 ? c.Parallel.ToString(CultureInfo.InvariantCulture) : L.T("menu.parallel.off"))}");
            Console.WriteLine($"12. {L.Pad(L.T("menu.toolchain"), 22)} = {c.FfmpegSource}");
            Console.WriteLine($"13. {L.Pad(L.T("menu.play"), 22)} = {Player.Describe(c.Play, c.PlayerPath, FFmpegDiscovery.FindFFplay(ffmpeg))}");
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
                    Report.PrintFilterList(ffmpeg, availableFilters, availableScaleAlgorithms);
                    break;
                case "7":
                    Report.PrintConfig(c, ffmpeg, btbn, encoders.Count, availableFilters, availableScaleAlgorithms);
                    break;
                case "8":
                    StartEncoding(c, ffmpeg, ffprobe, btbn, encoders, audioAvailable,
                        availableFilters, availableScaleAlgorithms);
                    break;
                case "9":
                    c.Turbo = !c.Turbo;
                    Console.WriteLine(c.Turbo ? L.T("turbo.enabled") : L.T("turbo.disabled"));
                    Persist(c);
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
            c.Encoder = chosen;

            (double baseFps, bool exact) = EncoderMath.ChooseBaseFps(c.Fps, chosen.Caps);
            if (!exact)
            {
                Console.WriteLine(L.T("encoder.fpsadjusted", chosen.Name, $"{c.Fps:G6}", $"{baseFps:G6}"));
                c.Fps = baseFps;
            }
            c.FpsLevels = EncoderMath.SelectLevels(c.Fps, null);
            (c.Width, c.Height, bool sizeExact) = EncoderMath.NormalizeDimensions(chosen.Name, c.Width, c.Height);
            if (!sizeExact) Console.WriteLine(L.T("encoder.sizesdjusted", chosen.Name, c.Width, c.Height));

            c.Qv = EncoderMath.DefaultQuality(chosen.Name);
            c.AudioEncoder = EncoderMath.ChooseAudioEncoder(chosen.FormatName, audioAvailable);

            if (chosen.Name == "amv")
            {
                Console.WriteLine(L.T("encoder.amvnote"));
                if (audioAvailable.Contains("adpcm_ima_amv")) c.AudioEncoder = "adpcm_ima_amv";
                c.AudioRate = 22050;
            }
            else if (chosen.Name == "roqvideo")
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
                Console.WriteLine(L.T("encoder.videoonly", chosen.Name, chosen.FormatName));
            if (chosen.Name != "libx264")
            {
                c.X264Preset = "medium";
                c.X264Tune = "";
                c.X264Profile = "";
                c.X264Level = "";
            }
            if (c.OutputIsAuto && c.InputPath is not null)
                c.OutputPath = DefaultOutput(c.InputPath, chosen);
        }
        catch (Exception exc) when (exc is ArgumentException or InvalidOperationException or FormatException)
        {
            Console.WriteLine(L.T("encoder.invalid", exc.Message));
        }
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
            Row(5, L.T("param.levels"), string.Join(",", c.FpsLevels.Select(x => x.ToString("G6"))));
            Row(6, qualityLabel, c.Qv.ToString(CultureInfo.InvariantCulture));
            Row(7, L.T("param.threads"), c.Threads.ToString(CultureInfo.InvariantCulture));
            Row(8, L.T("param.buffer"), c.BufferFrames.ToString(CultureInfo.InvariantCulture));
            Row(9, L.T("param.size"), $"{c.Width}x{c.Height}");
            Row(10, L.T("param.crop43"), L.YesNo(c.Crop43));
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
                        break;
                    case "5":
                        string levelsRaw = ReadRaw(L.T("param.prompt.levels", string.Join(",", c.FpsLevels.Select(x => x.ToString("G6")))));
                        if (levelsRaw.Length > 0)
                        {
                            c.FpsLevels = EncoderMath.SelectLevels(
                                c.Fps, levelsRaw.Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)));
                        }
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
                    case "10": c.Crop43 = !c.Crop43; break;
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
                    case "17":
                        c.AudioChannels = ReadInt(L.T("param.prompt.audiochannels", c.AudioChannels), c.AudioChannels);
                        break;
                    case "18":
                        string rateRaw = ReadRaw(L.T("param.prompt.audiorate",
                            c.AudioRate?.ToString(CultureInfo.InvariantCulture) ?? L.T("value.auto")));
                        c.AudioRate = rateRaw.Length > 0 ? int.Parse(rateRaw, CultureInfo.InvariantCulture) : null;
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
                        c.AudioEncoder = audioRaw.Length > 0 ? audioRaw : null;
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
                Crop43 = c.Crop43,
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

            if (c.Play != PlayMode.Off)
            {
                Console.Write(L.T("play.ask"));
                string answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
                if (answer is "" or "y" or "yes")
                {
                    (bool played, string? playError) = Player.Launch(
                        c.Play, c.OutputPath.FullName, ffmpeg, c.PlayerPath);
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
}
