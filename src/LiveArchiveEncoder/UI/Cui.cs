using System.Globalization;
using System.Text.RegularExpressions;
using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.FFmpeg;
using LiveArchiveEncoder.Localization;
using LiveArchiveEncoder.PostProcess;

namespace LiveArchiveEncoder.UI;

/// <summary>Interactive menu shell, ported from run_cui / choose_encoder / edit_settings.</summary>
public static class Cui
{
    public static int Run(
        string ffmpeg, string ffprobe, bool btbn,
        FFmpeg.Capabilities caps,
        string? language = null, string? ffmpegSource = null)
    {
        // v7.9.5: all of this comes from one cached build profile instead of
        // three launches (`-version`, `-encoders` twice). A second start of the
        // shell does not touch FFmpeg at all before it prints the menu.
        (string version, bool likelyGpl) = (caps.Version, caps.Gpl);
        List<EncoderInfo> encoders = caps.Encoders;
        HashSet<string> audioAvailable = caps.AudioEncoders;
        HashSet<string> availableFilters = caps.Filters;
        HashSet<string> availableScaleAlgorithms = caps.ScaleAlgorithms;

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

        Console.WriteLine($"{L.Pad(L.T("config.settingsfile"), 17)}: {ConfigStore.DefaultPath}");
        // v7.9.5.1: the documented minimums, checked on the machine that is
        // actually here. A warning, not a refusal -- a slow box still makes a
        // correct file, it just cannot shard.
        foreach (string warning in EnvCheck.Warnings(c.OutputPath?.FullName ?? c.InputPath?.FullName))
            Console.WriteLine($"{L.Pad(L.T("env.heading"), 17)}: {warning}");
        if (restored)
        {
            // v7.9.3.1: this used to print the same path a second time as
            // "Restored settings: <path>". One line for the path, one for the
            // fact that it was restored, and only the date is new information.
            Console.WriteLine($"{L.Pad(L.T("config.restored"), 17)}: "
                + (stored.SavedAt is null ? L.T("value.yes") : $"{stored.SavedAt.Value.LocalDateTime:G}"));
        }
        // v7.9.1: a remembered shard count belongs to whatever input it was
        // chosen for. If that input is on a network drive, sharding is off for
        // the run -- said here, at restore, rather than at encode time.
        if (c.Parallel > 1 && Remote.Smb.IsRemotePath(c.InputPath?.FullName))
        {
            c.Parallel = 1;
            Console.WriteLine(L.T("parallel.networkoff"));
        }
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
            Console.WriteLine($"10. {L.Pad(L.T("menu.language"), 22)} = {c.Language}");
            Console.WriteLine($"11. {L.Pad(L.T("menu.parallel"), 22)} = {(c.Parallel > 1 ? c.Parallel.ToString(CultureInfo.InvariantCulture) : L.T("menu.parallel.off"))}");
            Console.WriteLine($"12. {L.Pad(L.T("menu.toolchain"), 22)} = {c.FfmpegSource}");
            Console.WriteLine($"13. {L.Pad(L.T("menu.play"), 22)} = {Player.Describe(c.Play, c.PlayerPath, FFmpegDiscovery.FindFFplay(ffmpeg))}");
            Console.WriteLine($"14. {L.Pad(L.T("era.menu"), 22)} = {DescribeEra(c)}");
            Console.WriteLine($"15. {L.Pad(L.T("rc.menu"), 22)} = {DescribeRecompress(c)}");
            Console.WriteLine($"16. {L.Pad(L.T("menu.streams"), 22)} = {DescribeStreams(c)}");
            Console.WriteLine($"17. {L.Pad(L.T("sub.menu"), 22)} = {DescribeSubtitles(c)}");
            // v7.9.3: the command line that reproduces this menu state, and a
            // shelf of named settings files to switch between.
            Console.WriteLine($"18. {L.Pad(L.T("menu.showargs"), 22)}");
            Console.WriteLine($"19. {L.Pad(L.T("menu.profiles"), 22)} = {DescribeProfiles()}");
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
                        // v7.9.1: a network input is a single-pass encode by
                        // default, and the user hears why here rather than
                        // twenty minutes into a stalled run.
                        if (Remote.Smb.IsRemotePath(selected.FullName))
                        {
                            Console.WriteLine(Remote.Smb.StabilityHint(selected.FullName));
                            if (c.Parallel > 1)
                            {
                                c.Parallel = 1;
                                Console.WriteLine(L.T("parallel.networkoff"));
                            }
                        }
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
                        // v7.9.5: a rescan drops the cached build profile, so a
                        // build replaced in place is picked up here too.
                        caps = FFmpeg.BuildProfileStore.For(ffmpeg, refresh: true);
                        encoders = caps.Encoders;
                        audioAvailable = caps.AudioEncoders;
                        availableFilters = caps.Filters;
                        availableScaleAlgorithms = caps.ScaleAlgorithms;
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
                    StartEncoding(c, ffmpeg, ffprobe, btbn, caps, encoders, audioAvailable,
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
                    // v7.9.1: the encoder refuses to shard a network source, so
                    // the menu does not store a number the run would ignore.
                    if (c.Parallel > 1 && Remote.Smb.IsRemotePath(c.InputPath?.FullName))
                    {
                        c.Parallel = 1;
                        Console.WriteLine(L.T("parallel.networkrefused"));
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
                        RefreshToolchain(c, ref ffmpeg, ref ffprobe, ref btbn, ref caps,
                            ref encoders, ref audioAvailable,
                            ref availableFilters, ref availableScaleAlgorithms);
                        break;
                    }
                case "13":
                    EditPlayback(c, ffmpeg);
                    Persist(c);
                    break;
                case "14":
                    PickEraPreset(c, encoders, audioAvailable);
                    Persist(c);
                    break;
                case "15":
                    EditRecompress(c, ffmpeg, caps);
                    Persist(c);
                    break;
                case "16":
                    EditStreams(c, ffprobe);
                    Persist(c);
                    break;
                case "17":
                    EditSubtitles(c, ffprobe);
                    Persist(c);
                    break;
                case "18":
                    Report.PrintCommandLine(c);
                    break;
                case "19":
                    // Loading a profile rewrites the live config, so persist it
                    // the same way every other menu item does.
                    if (ConfigFilesMenu(c, encoders)) Persist(c);
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

    /// <summary>Menu summary of the two stream picks; null means "first".</summary>
    private static string DescribeStreams(Config c) => L.T("stream.value",
        c.VideoStream?.ToString(CultureInfo.InvariantCulture) ?? L.T("stream.first"),
        c.AudioStream?.ToString(CultureInfo.InvariantCulture) ?? L.T("stream.first"));

    /// <summary>
    /// v7.9: pick which video and audio track of a multi-stream source to use.
    ///
    /// The source is probed here so the menu can show the indexes -- that is the
    /// whole point, since the number to type is the number ffprobe assigns.
    /// Leaving a prompt blank clears the pick, which is the v7.8 behaviour and
    /// has to stay reachable.
    /// </summary>
    private static void EditStreams(Config c, string ffprobe)
    {
        if (c.InputPath is null)
        {
            Console.WriteLine(L.T("stream.selectfirst"));
            return;
        }

        FFmpeg.SourceInfo source;
        try
        {
            source = FFmpeg.Probe.Source(ffprobe, c.InputPath.FullName);
        }
        catch (InvalidOperationException exc)
        {
            Console.WriteLine(L.T("stream.probefailed", exc.Message));
            return;
        }

        Console.WriteLine(L.T("stream.heading", c.InputPath.Name));
        Console.WriteLine(LiveArchiveEncoder.Encoding.StreamSelection.Describe(source));

        c.VideoStream = ReadStreamIndex(source, "video", c.VideoStream, "stream.prompt.video");
        c.AudioStream = ReadStreamIndex(source, "audio", c.AudioStream, "stream.prompt.audio");

        if (c.VideoStream is null && c.AudioStream is null)
            Console.WriteLine(L.T("stream.cleared"));
    }

    /// <summary>
    /// One index, validated against the source before it is stored: a wrong one
    /// would otherwise only surface minutes into the encode.
    /// </summary>
    private static int? ReadStreamIndex(FFmpeg.SourceInfo source, string type, int? current, string promptKey)
    {
        while (true)
        {
            Console.Write(L.T(promptKey, current?.ToString(CultureInfo.InvariantCulture) ?? L.T("stream.first")));
            string raw = (Console.ReadLine() ?? "").Trim();
            if (raw.Length == 0) return null;

            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                Console.WriteLine(L.T("menu.invalid"));
                continue;
            }
            if (LiveArchiveEncoder.Encoding.StreamSelection.Check(
                    source, type == "video" ? value : null, type == "audio" ? value : null) is not null)
            {
                Console.WriteLine(L.T("stream.invalid"));
                continue;
            }
            return value;
        }
    }

    /// <summary>
    /// v7.9.1: one-line summary for the main menu. "off" covers both an
    /// explicit off and a request with nothing picked, because that is what
    /// the encoder will do with it.
    /// </summary>
    private static string DescribeSubtitles(Config c)
    {
        SubtitleConfig s = c.Subtitles;
        if (!s.Active) return L.T("sub.off");

        string what = s.Files.Count > 0
            ? string.Join(", ", s.Files.Select(f => Path.GetFileName(f)))
            : L.T("sub.streamn", s.Stream!.Value);
        string mode = s.Mode == "copy" ? L.T("sub.mode.copy") : L.T("sub.mode.burn");
        string codec = s is { Mode: "copy", Codec: { Length: > 0 } code } ? $" ({code})" : "";
        return $"{mode}: {what}{codec}";
    }

    /// <summary>
    /// v7.9.3: how many named settings files are on the shelf.
    /// </summary>
    private static string DescribeProfiles()
    {
        int count = ConfigProfiles.List().Count;
        return count == 0 ? L.T("value.none") : L.T("profile.count", count);
    }

    /// <summary>
    /// v7.9.3: the settings-file shelf.
    ///
    /// Three ways in, one way out: load by number from the tool's own folder,
    /// load any path ("p", for a file written by <c>--save-config</c>), save the
    /// current state under a new name, or delete one. Everything ends with the
    /// live config updated, and the caller persists it.
    ///
    /// Returns true when the live config may have changed, so the main loop
    /// knows whether a write-back is needed.
    /// </summary>
    private static bool ConfigFilesMenu(Config c, List<EncoderInfo> encoders)
    {
        bool changed = false;
        while (true)
        {
            List<FileInfo> files = ConfigProfiles.List();
            Console.WriteLine($"\n{L.T("profile.heading")}");
            Console.WriteLine($"{L.Pad(L.T("profile.dir"), 8)}: {ConfigProfiles.Directory}");
            if (files.Count == 0) Console.WriteLine($"  {L.T("profile.empty")}");
            for (int i = 0; i < files.Count; i++)
            {
                Console.WriteLine("  " + L.T("profile.entry",
                    i + 1,
                    files[i].Name,
                    files[i].LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
            }

            Console.Write(L.T("profile.pick"));
            string pick = (Console.ReadLine() ?? "").Trim();
            if (pick.Length == 0 || pick == "0") return changed;

            switch (pick)
            {
                case "s":
                    Console.Write(L.T("profile.saveprompt"));
                    FileInfo? saved = ConfigProfiles.SaveAs(c, (Console.ReadLine() ?? "").Trim());
                    Console.WriteLine(saved is null
                        ? L.T("profile.badname")
                        : L.T("profile.saved", saved.Name));
                    break;

                case "p":
                    Console.Write(L.T("profile.pathprompt"));
                    string raw = (Console.ReadLine() ?? "").Trim().Trim('"');
                    if (raw.Length == 0) break;
                    var external = new FileInfo(raw);
                    if (!external.Exists)
                    {
                        Console.WriteLine(L.T("profile.notfound", raw));
                        break;
                    }
                    changed |= ApplyProfile(c, external, encoders);
                    break;

                case "d":
                    Console.Write(L.T("profile.deleteprompt"));
                    if (!TryPick((Console.ReadLine() ?? "").Trim(), files, out FileInfo? victim))
                        break;
                    Console.Write(L.T("profile.confirmdelete", victim!.Name));
                    string yes = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
                    if (yes is not ("y" or "yes")) break;
                    Console.WriteLine(ConfigProfiles.Delete(victim.Name)
                        ? L.T("profile.deleted", victim.Name)
                        : L.T("profile.deletefailed", victim.Name));
                    // Deleting does not touch the live config, so "changed" stays.
                    break;

                default:
                    if (TryPick(pick, files, out FileInfo? chosen))
                        changed |= ApplyProfile(c, chosen!, encoders);
                    break;
            }
        }
    }

    /// <summary>1-based menu index to entry, or false with the usual complaint.</summary>
    private static bool TryPick(string raw, List<FileInfo> files, out FileInfo? file)
    {
        file = null;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
            || index < 1 || index > files.Count)
        {
            Console.WriteLine(L.T("menu.invalid"));
            return false;
        }
        file = files[index - 1];
        return true;
    }

    /// <summary>
    /// Apply one settings file to the live config. Returns true when it was read
    /// -- a missing or unparsable file changes nothing and says so.
    /// </summary>
    private static bool ApplyProfile(Config c, FileInfo file, List<EncoderInfo> encoders)
    {
        if (ConfigProfiles.LoadInto(c, file, encoders) is null)
        {
            Console.WriteLine(L.T("profile.loadfailed", file.Name));
            return false;
        }
        Console.WriteLine(L.T("profile.loaded", file.Name, L.T("profile.summary",
            c.Encoder?.Name ?? L.T("value.none"), c.Width, c.Height,
            c.Fps.ToString("G6", CultureInfo.InvariantCulture),
            c.Encoder?.Extension ?? "?")));
        return true;
    }

    /// <summary>
    /// v7.9.1: the subtitle menu -- the CUI half of what
    /// --subtitle / --subtitle-stream / --subtitle-mode / --subtitle-codec
    /// spell on the command line.
    ///
    /// Files and an embedded stream stay mutually exclusive the way the
    /// encoder wants them: setting one clears the other instead of letting
    /// the run fail on a request that can never be honoured.
    /// </summary>
    private static void EditSubtitles(Config c, string ffprobe)
    {
        while (true)
        {
            SubtitleConfig s = c.Subtitles;
            Console.WriteLine($"\n== {L.T("sub.menu")} ==");
            Console.WriteLine($" 1. {L.Pad(L.T("sub.menu.mode"), 30)} = {s.Mode}");
            Console.WriteLine($" 2. {L.Pad(L.T("sub.menu.files"), 30)} = {(s.Files.Count == 0 ? L.T("value.notselected") : string.Join(", ", s.Files.Select(f => Path.GetFileName(f))))}");
            Console.WriteLine($" 3. {L.Pad(L.T("sub.menu.stream"), 30)} = {(s.Stream is int idx ? idx.ToString(CultureInfo.InvariantCulture) : L.T("value.notselected"))}");
            Console.WriteLine($" 4. {L.Pad(L.T("sub.menu.codec"), 30)} = {(s.Codec is { Length: > 0 } code ? code : L.T("value.auto"))}");
            Console.WriteLine($" 5. {L.Pad(L.T("sub.menu.fonts"), 30)} = {(s.FallbackFont is { Length: > 0 } f ? f : L.T("value.auto"))}");
            Console.WriteLine($" 0. {L.T("sub.menu.back")}");
            Console.Write(L.T("menu.select"));

            switch ((Console.ReadLine() ?? "").Trim())
            {
                case "0": return;
                case "1":
                    // Cycle burn -> copy -> off -> burn. The picks survive an
                    // "off" so flipping back does not lose the selection.
                    s.Mode = s.Mode switch
                    {
                        "burn" => "copy",
                        "copy" => "off",
                        _ => "burn",
                    };
                    Console.WriteLine(L.T("sub.mode.set", s.Mode));
                    if (s.Mode == "copy" && c.Encoder is not null
                        && !Constants.SubtitleCodecsByContainer.ContainsKey(c.Encoder.FormatName))
                    {
                        Console.WriteLine(L.T("sub.copy.nocontainer", c.Encoder.FormatName));
                    }
                    break;
                case "2":
                    Console.WriteLine(L.T("sub.files.hint", string.Join(" ", Constants.SubtitleFileExtensions)));
                    Console.Write(L.T("sub.prompt.files"));
                    string file = (Console.ReadLine() ?? "").Trim().Trim('"');
                    if (file == "-")
                    {
                        s.Files = [];
                        Console.WriteLine(L.T("sub.files.cleared"));
                        break;
                    }
                    if (file.Length == 0) break;
                    if (!Constants.SubtitleFileExtensions.Contains(
                            Path.GetExtension(file).ToLowerInvariant()))
                    {
                        Console.WriteLine($"{L.T("error.prefix")}{L.T("sub.badext",
                            Path.GetFileName(file), string.Join(" ", Constants.SubtitleFileExtensions))}");
                        break;
                    }
                    if (!File.Exists(file))
                    {
                        Console.WriteLine($"{L.T("error.prefix")}{L.T("sub.notfound", file)}");
                        break;
                    }
                    s.Files = [file];
                    s.Stream = null;   // the two picks are exclusive
                    Console.WriteLine(L.T("sub.files.set", Path.GetFileName(file)));
                    if (s.Mode == "off") Console.WriteLine(L.T("sub.hint.off"));
                    break;
                case "3":
                    if (c.InputPath is null || !c.InputPath.Exists)
                    {
                        Console.WriteLine(L.T("stream.selectfirst"));
                        break;
                    }
                    FFmpeg.SourceInfo source;
                    try
                    {
                        source = FFmpeg.Probe.Source(ffprobe, c.InputPath.FullName);
                    }
                    catch (InvalidOperationException exc)
                    {
                        Console.WriteLine(L.T("stream.probefailed", exc.Message));
                        break;
                    }
                    Console.WriteLine(L.T("stream.heading", c.InputPath.Name));
                    Console.WriteLine(LiveArchiveEncoder.Encoding.StreamSelection.Describe(source));
                    Console.Write(L.T("sub.prompt.stream",
                        s.Stream?.ToString(CultureInfo.InvariantCulture) ?? L.T("value.none")));
                    string rawIdx = (Console.ReadLine() ?? "").Trim();
                    if (rawIdx.Length == 0)
                    {
                        s.Stream = null;
                        Console.WriteLine(L.T("sub.stream.cleared"));
                        break;
                    }
                    if (!int.TryParse(rawIdx, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pick)
                        || !source.Streams.Any(st => st.Index == pick && st.Type == "subtitle"))
                    {
                        Console.WriteLine(L.T("sub.stream.invalid"));
                        break;
                    }
                    s.Stream = pick;
                    s.Files = [];   // the two picks are exclusive
                    Console.WriteLine(L.T("sub.stream.set", pick));
                    if (s.Mode == "off") Console.WriteLine(L.T("sub.hint.off"));
                    break;
                case "4":
                    if (s.Mode != "copy")
                    {
                        Console.WriteLine(L.T("sub.codec.copyonly"));
                        break;
                    }
                    string? preferred = c.Encoder is not null
                        && Constants.SubtitleCodecsByContainer.TryGetValue(
                            c.Encoder.FormatName, out string[]? list)
                        ? string.Join(", ", list)
                        : null;
                    if (preferred is not null)
                        Console.WriteLine(L.T("sub.codec.container", c.Encoder!.FormatName, preferred));
                    Console.Write(L.T("sub.prompt.codec", s.Codec ?? L.T("value.auto")));
                    string codecRaw = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
                    if (codecRaw == "-")
                    {
                        s.Codec = null;
                        Console.WriteLine(L.T("sub.codec.auto"));
                    }
                    else if (codecRaw.Length > 0)
                    {
                        s.Codec = codecRaw;
                        Console.WriteLine(L.T("sub.codec.set", codecRaw));
                    }
                    break;
                case "5":
                    EditSubtitleFonts(s);
                    break;
            }
        }
    }

    /// <summary>
    /// v7.9.5.1: the font half of the subtitle menu. An ASS file names its fonts
    /// in its style table; if the program's own directory does not have them,
    /// libass substitutes something and CJK text becomes boxes. This is where
    /// they get imported, and where a fallback family is chosen for SRT (which
    /// names none at all).
    /// </summary>
    private static void EditSubtitleFonts(SubtitleConfig s)
    {
        while (true)
        {
            Console.WriteLine($"\n== {L.T("sub.menu.fonts")} ==");
            Console.WriteLine($" 1. {L.T("sub.fonts.heading")}");
            Console.WriteLine($" 2. {L.Pad(L.T("sub.fonts.import"), 22)}");
            Console.WriteLine($" 3. {L.Pad(L.T("sub.fonts.fallback"), 22)} = {(s.FallbackFont is { Length: > 0 } f ? f : L.T("value.auto"))}");
            Console.WriteLine($" 0. {L.T("sub.menu.back")}");
            Console.Write(L.T("menu.select"));

            switch ((Console.ReadLine() ?? "").Trim())
            {
                case "0": return;
                case "1":
                    if (s.Files.Count == 0)
                    {
                        Console.WriteLine(L.T("sub.fonts.nofile"));
                        break;
                    }
                    foreach (FontLookup lookup in SubtitleFonts.Check(s.Files, s.FontDir))
                    {
                        Console.WriteLine(lookup.FoundAt is null
                            ? L.T("sub.fonts.missing", lookup.Required)
                            : L.T("sub.fonts.required", lookup.Required, lookup.FoundAt));
                    }
                    if (SubtitleFonts.DefaultFamily(s.FontDir) is { Length: > 0 } suggestion)
                        Console.WriteLine(L.T("sub.fonts.default", suggestion));
                    break;
                case "2":
                    Console.Write(L.T("sub.fonts.importprompt", FontResolver.FontDirectory));
                    string path = (Console.ReadLine() ?? "").Trim().Trim('"');
                    if (path.Length == 0) break;
                    (int copied, int skipped, string dir) = SubtitleFonts.Import([path]);
                    Console.WriteLine(L.T("sub.fonts.imported", copied, dir, skipped));
                    break;
                case "3":
                    Console.Write(L.T("sub.fonts.fallback.prompt", s.FallbackFont ?? ""));
                    string family = (Console.ReadLine() ?? "").Trim();
                    if (family.Length == 0) break;
                    if (family == "-")
                    {
                        s.FallbackFont = null;
                        Console.WriteLine(L.T("sub.fonts.fallback.none"));
                    }
                    else
                    {
                        s.FallbackFont = family;
                        Console.WriteLine(L.T("sub.fonts.fallback.set", family));
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Re-resolve everything that depends on which FFmpeg is in use. Called after
    /// the toolchain menu, because installing or selecting another build changes
    /// the encoder list, the filter list and the scaler list at the same time.
    /// </summary>
    private static void RefreshToolchain(
        Config c, ref string ffmpeg, ref string ffprobe, ref bool btbn,
        ref FFmpeg.Capabilities caps,
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
            // A different binary is a different profile; the cache is keyed by
            // the binary itself, so this is a warm read when it was seen before.
            caps = FFmpeg.BuildProfileStore.For(ffmpeg);
            encoders = caps.Encoders;
            audioAvailable = caps.AudioEncoders;
            availableFilters = caps.Filters;
            availableScaleAlgorithms = caps.ScaleAlgorithms;

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

            // Audio follows the container. v7.9.3.1: this used to null the
            // current codec when it no longer muxed and then unconditionally
            // overwrite it on the next line, so the "keep what still works"
            // intent was never reached. Say what is being replaced, then pick.
            if (c.AudioEncoder is { Length: > 0 } current
                && !AudioCatalog.SupportsContainer(current, effective.FormatName))
            {
                Console.WriteLine(L.T("audio.replaced", current, effective.FormatName));
            }
            c.AudioEncoder = EncoderMath.ChooseAudioEncoder(effective.FormatName, audioAvailable);
            (c.AudioRate, c.AudioChannels, c.AudioKbps) = AudioCatalog.Effective(
                c.AudioEncoder, c.AudioRate, c.AudioChannels, c.AudioKbps, effective.FormatName);

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
            // v7.9.5: re-read the build profile (cached: this costs nothing
            // after the first run) instead of launching `ffmpeg -encoders`.
            audioAvailable = FFmpeg.BuildProfileStore.For(ffmpeg).AudioEncoders;
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
            Core.AudioCodecInfo? info = AudioCatalog.Find(name);
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
        int kbpsBefore = c.AudioKbps;
        c.AudioEncoder = pick;
        (c.AudioRate, c.AudioChannels, c.AudioKbps) = AudioCatalog.Effective(
            pick, c.AudioRate, c.AudioChannels, c.AudioKbps, c.Encoder?.FormatName);
        if (c.AudioChannels != channelsBefore)
            Console.WriteLine(L.T("audio.channelsadjusted", channelsBefore, c.AudioChannels));
        if (c.AudioKbps != kbpsBefore)
            Console.WriteLine(L.T("audio.kbpsadjusted", kbpsBefore, c.AudioKbps));
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
                        // Bitrates are clamped like rates and channels: mp2 at
                        // 44.1 kHz starts at 32 kbps, wmav2 at 24, and libvorbis
                        // tops out at 224 -- values FFmpeg would refuse mid-run.
                        c.AudioKbps = ReadInt(L.T("param.prompt.audiokbps", c.AudioKbps), c.AudioKbps);
                        int kbpsAdjusted = AudioCatalog.NearestKbps(
                            c.AudioEncoder, c.AudioKbps, c.AudioRate);
                        if (kbpsAdjusted != c.AudioKbps)
                        {
                            Console.WriteLine(L.T("audio.kbpsadjusted", c.AudioKbps, kbpsAdjusted));
                            c.AudioKbps = kbpsAdjusted;
                        }
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
                        // The container is the stricter side for some codecs
                        // (FLV takes 11025/22050/44100/48000 for MP3), so it has
                        // to be part of the snap.
                        int? snapped = AudioCatalog.NearestRate(
                            c.AudioEncoder, requested, c.Encoder?.FormatName);
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
                        // The new codec may not accept the old numbers: the rate
                        // snaps, and the bitrate is clamped into the band the
                        // snapped rate allows.
                        (c.AudioRate, c.AudioChannels, c.AudioKbps) = AudioCatalog.Effective(
                            audioRaw, c.AudioRate, c.AudioChannels, c.AudioKbps, c.Encoder?.FormatName);
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

        // A different muxer means a different set of audio codecs, and a
        // different set of sample rates for the ones that stay: FLV only takes
        // 11025/22050/44100/48000 for MP3, so switching containers re-snaps
        // the rate (and the bitrate, whose band depends on the rate).
        if (c.AudioEncoder is { Length: > 0 } current
            && !AudioCatalog.SupportsContainer(current, resolved.FormatName))
        {
            Console.WriteLine(L.T("audio.replaced", current, resolved.FormatName));
            c.AudioEncoder = null;
        }
        (c.AudioRate, c.AudioChannels, c.AudioKbps) = AudioCatalog.Effective(
            c.AudioEncoder, c.AudioRate, c.AudioChannels, c.AudioKbps, resolved.FormatName);

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
        Config c, string ffmpeg, string ffprobe, bool btbn, FFmpeg.Capabilities caps,
        List<EncoderInfo> encoders,
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

        // v7.9.4: no container requested by the menu, so the output name decides
        // -- "out.avi" with mpeg1video is a request for AVI, not a mismatch.
        EncoderInfo byExtension = EncoderMath.ResolveByExtension(c.Encoder, c.OutputPath.Name);
        if (byExtension.FormatName != c.Encoder.FormatName)
        {
            Console.WriteLine(L.T("note.containerfromext",
                c.Encoder.Name, c.OutputPath.Extension, byExtension.FormatName));
            c.Encoder = byExtension;
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

        c.Encoder.Caps ??= caps.EncoderCaps(c.Encoder);

        // v7.9.4: the settings file can carry a rate this encoder never accepts
        // (mpeg1video + 15 fps), and the menu only re-checks when the encoder is
        // picked by hand. Re-check here, right before the encode, or the run
        // dies in preflight after every shard has already started.
        (double baseFps, bool fpsExact) = EncoderMath.ChooseBaseFps(c.Fps, c.Encoder.Caps);
        if (!fpsExact)
        {
            Console.WriteLine(L.T("encoder.fpsadjusted",
                c.Encoder.Name, $"{c.Fps:G6}", $"{baseFps:G6}"));
            c.Fps = baseFps;
            c.FpsLevels = EncoderMath.SelectLevels(c.Fps, c.FpsLevels);
            c.FpsLevelKbps = EncoderMath.PruneRungs(c.FpsLevelKbps, c.FpsLevels);
        }
        // Same fact the CLI reports: a bare .h261/.h263/.a64 bitstream keeps no
        // frame rate, so the menu does not pretend the number travels with it.
        if (Constants.RateLessFormats.Contains(c.Encoder.FormatName))
            Console.WriteLine(L.T("note.rateless", c.Encoder.FormatName, $"{baseFps:G6}"));

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
        // v7.9.3: show the equivalent command line right before the go-ahead, so
        // a run the user likes can become a script without retyping it.
        Report.PrintCommandLine(c);

        // v7.9.5.1: an ASS file names its fonts; say now which ones this tool
        // will not find on its own, rather than after the picture is burned.
        if (c.Subtitles.Mode == "burn" && c.Subtitles.Files.Count > 0)
        {
            foreach (FontLookup lookup in
                     SubtitleFonts.Check(c.Subtitles.Files, c.Subtitles.FontDir, includeSystem: false))
            {
                if (lookup.FoundAt is null)
                    Console.WriteLine(L.T("note.fontmissing", lookup.Required, FontResolver.FontDirectory));
            }
        }

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
                // v7.9.1: the subtitle request the menu built, in the same
                // shape the CLI half hands the encoder.
                Subtitles = new Encoding.SubtitleSettings(
                    [.. c.Subtitles.Files], c.Subtitles.Stream,
                    Encoding.SubtitleTrack.ParseMode(c.Subtitles.Mode), c.Subtitles.Codec)
                {
                    FontDir = c.Subtitles.FontDir,
                    FallbackFont = c.Subtitles.FallbackFont,
                },
                // Detected once here (and inherited by shards) instead of once
                // per shard: the copy path needs the build's own encoder list.
                AvailableSubtitleEncoders = c.Subtitles.Active
                    ? caps.SubtitleEncoders
                    : null,
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
                Parallel = c.Parallel,
            };

            // Keep the settings file in step even if the encode is interrupted.
            Persist(c);

            Encoding.EncodeResult result = Encoding.RealtimeEncoder.RunSplit(options, cancellationToken);
            Console.WriteLine();
            Report.PrintResult(c.OutputPath.FullName, result);

            // v7.6: the video-site recompress pass, after the live recording --
            // the site transcoded whatever it was given, so this is the only
            // stage left.
            string finalPath = c.OutputPath.FullName;
            if (PostProcess.PostPipeline.Needed(c))
            {
                PostProcess.PostProcessOutcome post =
                    PostProcess.PostPipeline.Run(ffmpeg, c.OutputPath.FullName, c);
                finalPath = post.FinalPath;

                if (post.RecompressRan && post.RecompressPath is { Length: > 0 } rc)
                    Console.WriteLine(L.T("post.recompress", rc));

                if (post.Failed)
                {
                    string stage = post.FailedStage!;
                    string detail = post.RecompressError ?? "";
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

    // ---- v7.6: era preset / recompress menus ----

    private static string DescribeEra(Config c) =>
        c.EraPreset is { Length: > 0 } id && PostProcess.EraPresets.Find(id) is not null
            ? id + (c.EraNative ? "" : " (modern)")
            : L.T("era.none");

    private static string DescribeRecompress(Config c) =>
        (c.Recompress.Enabled ? "on" : L.T("value.off"))
        + (c.Recompress.Args.Length > 0 ? $" | {c.Recompress.Args}" : "");

    /// <summary>
    /// The preset menu (v7.6, extended in v7.9.5.1).
    ///
    /// Applying a preset writes its live layer into the config, points the live
    /// encoder at the codec that site served (when this build has it), and
    /// pre-fills the second pass. It never arms the second pass: that stays a
    /// deliberate choice.
    ///
    /// The last four entries are the user's own presets: n/e/d maintain them and
    /// m flips native &lt;-&gt; modern rendering for the next apply.
    /// </summary>
    private static void PickEraPreset(
        Config c, List<EncoderInfo> encoders, HashSet<string> audioAvailable)
    {
        IReadOnlyList<PostProcess.EraPreset> list = PostProcess.EraPresets.All;
        Console.WriteLine($"\n== {L.T("era.menu")} ==");
        foreach (PostProcess.EraPreset preset in list)
        {
            string kind = preset.Kind == PostProcess.EraKind.Site
                ? L.T("era.kind.site") : L.T("era.kind.era");
            string mark = preset.BuiltIn ? " " : "*";
            Console.WriteLine($"  {preset.Id,-16} {preset.YearFrom}-{preset.YearTo} {kind}{mark} "
                + $"{preset.Summary()}");
            Console.WriteLine($"  {"",-16} {preset.Note}");
        }
        Console.WriteLine($"  n. {L.T("era.menu.new")}");
        Console.WriteLine($"  e. {L.T("era.menu.edit")}");
        Console.WriteLine($"  d. {L.T("era.menu.delete")}");
        Console.WriteLine($"  m. {L.T("era.menu.toggle")} = "
            + (c.EraNative ? L.T("era.native.on") : L.T("era.native.off")));

        Console.Write(L.T("era.prompt", c.EraPreset ?? ""));
        string pick = (Console.ReadLine() ?? "").Trim();
        if (pick.Length == 0) return;

        switch (pick.ToLowerInvariant())
        {
            case "n": EditCustomEra(c, encoders, null); return;
            case "e": EditCustomEra(c, encoders, AskPresetId()); return;
            case "d": DeleteCustomEra(c); return;
            case "m":
                c.EraNative = !c.EraNative;
                Console.WriteLine(c.EraNative ? L.T("era.native.on") : L.T("era.native.off"));
                return;
        }

        PostProcess.EraPreset? chosen = PostProcess.EraPresets.Find(pick);
        if (chosen is null)
        {
            Console.WriteLine($"{L.T("error.prefix")}{L.T("era.unknown", pick, PostProcess.EraPresets.Ids)}");
            return;
        }

        ApplyPreset(c, chosen, encoders, audioAvailable);
    }

    /// <summary>Apply a preset to a live config; shared by the menu and the wizard.</summary>
    private static void ApplyPreset(
        Config c, PostProcess.EraPreset preset, List<EncoderInfo> encoders, HashSet<string> audioAvailable)
    {
        bool live = preset.ApplyLive(c);
        // v7.9.5.1: the site's own codec becomes the live encoder when the build
        // has it, and the container follows that encoder.
        bool encoder = preset.ApplyEncoder(c, encoders);
        if (encoder && c.Encoder is not null)
        {
            List<string>? usable = AudioCatalog.Compatible(
                c.Encoder.FormatName, audioAvailable);
            if (usable is { Count: > 0 } && (c.AudioEncoder is null || !usable.Contains(c.AudioEncoder)))
                c.AudioEncoder = usable[0];
        }
        if (c.OutputIsAuto && c.InputPath is not null && c.Encoder is not null)
            c.OutputPath = DefaultOutput(c.InputPath, c.Encoder);

        c.Recompress.Args = preset.Fragment(c.EraNative);
        c.Recompress.Container = preset.ContainerFor(c.EraNative);
        c.EraPreset = preset.Id;

        if (live)
        {
            Console.WriteLine(L.T("era.applied", preset.Id,
                c.Width, c.Height, $"{c.Fps:G6}", $"{c.Limit:G6}"));
        }
        else
        {
            Console.WriteLine(L.T("rc.siteapplied", preset.Id, c.Recompress.Container));
        }
        if (encoder)
            Console.WriteLine(L.T("era.encoderapplied", preset.Encoder ?? "", preset.Id));
        else if (preset.HasEncoder)
            Console.WriteLine(L.T("note.eracodecmissing", preset.Id, preset.Encoder ?? ""));
        Console.WriteLine(L.T(c.EraNative ? "era.native.on" : "era.native.off"));
        Console.WriteLine($" {preset.Fragment(c.EraNative)}");
        Console.WriteLine(L.T("rc.hint"));
    }

    /// <summary>
    /// Create or edit a user preset (v7.9.5.1). <paramref name="seedId"/> null
    /// means "new"; an id that matches a built-in preset is allowed and produces
    /// an override, which is how a user changes history without a rebuild.
    /// </summary>
    private static void EditCustomEra(Config c, List<EncoderInfo> encoders, string? seedId)
    {
        PostProcess.EraPreset? existing = seedId is null
            ? null
            : PostProcess.EraPresets.Find(seedId);
        bool creating = existing is null;

        Console.WriteLine($"\n== {(creating ? L.T("era.custom.new") : existing!.Id)} ==");
        string id = ReadRaw(L.T("era.custom.id", existing?.Id ?? "my_2008")).Trim();
        string normalised = Configuration.EraConfigMap.NormaliseId(id);
        if (normalised.Length == 0)
        {
            Console.WriteLine($"{L.T("error.prefix")}{L.T("era.custom.badid")}");
            return;
        }
        if (creating && PostProcess.EraPresets.IsBuiltInId(normalised))
        {
            Console.WriteLine($"{L.T("error.prefix")}{L.T("era.custom.badid")}");
            return;
        }
        if (creating && PostProcess.EraPresets.Find(normalised) is not null)
        {
            Console.WriteLine($"{L.T("error.prefix")}{L.T("era.custom.badid")}");
            return;
        }

        bool era = existing?.Kind == PostProcess.EraKind.Era;
        string kind = ReadRaw(L.T("era.custom.kind", era ? "1" : "2")).Trim();
        if (kind is "1" or "2") era = kind == "1";

        string years = ReadRaw(L.T("era.custom.years", existing?.YearFrom ?? 2006, existing?.YearTo ?? 2009));
        (int from, int to) = ParseYears(years, existing?.YearFrom ?? 2006, existing?.YearTo ?? 2009);

        int width = existing?.RcWidth ?? 320, height = existing?.RcHeight ?? 240;
        Console.WriteLine(L.T("era.custom.size", width, height));
        string size = ReadRaw("> ").Trim();
        if (size.Contains('x') || size.Contains('X'))
        {
            string[] parts = size.ToLowerInvariant().Split('x');
            if (parts.Length == 2
                && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h)
                && w >= 16 && h >= 16)
            {
                width = w;
                height = h;
            }
        }

        double fps = ReadDouble(L.T("era.custom.fps", $"{existing?.RcFps ?? 15:G6}"), existing?.RcFps ?? 15);
        int vkbps = (int)ReadDouble(L.T("era.custom.vkbps", existing?.RcVideoKbps ?? 300),
            existing?.RcVideoKbps ?? 300);
        int akbps = (int)ReadDouble(L.T("era.custom.akbps", existing?.RcAudioKbps ?? 32),
            existing?.RcAudioKbps ?? 32);

        string videoCodec = ReadRaw(L.T("era.custom.vcodec",
            existing?.VideoCodec ?? "libx264", PostProcess.RecompressCodecs.VideoIdList)).Trim();
        if (videoCodec.Length == 0) videoCodec = existing?.VideoCodec ?? "libx264";
        if (PostProcess.RecompressCodecs.FindVideo(videoCodec) is null)
        {
            Console.WriteLine($"{L.T("error.prefix")}{L.T("rc.codecmissing", videoCodec)}");
            return;
        }
        videoCodec = PostProcess.RecompressCodecs.FindVideo(videoCodec)!.Name;

        string audioCodec = ReadRaw(L.T("era.custom.acodec",
            existing?.AudioCodec ?? PostProcess.RecompressCodecs.RecommendedAudio(videoCodec),
            PostProcess.RecompressCodecs.AudioIdList + ", copy")).Trim();
        if (audioCodec.Length == 0)
            audioCodec = PostProcess.RecompressCodecs.RecommendedAudio(videoCodec);
        if (!PostProcess.RecompressCodecs.IsCopyAudio(audioCodec)
            && PostProcess.RecompressCodecs.FindAudio(audioCodec) is null)
        {
            Console.WriteLine($"{L.T("error.prefix")}{L.T("rc.codecmissing", audioCodec)}");
            return;
        }

        List<string> containers = PostProcess.RecompressCodecs.ContainersFor(videoCodec, audioCodec);
        string container = ReadRaw(L.T("era.custom.container",
            existing?.RecompressContainer ?? containers.FirstOrDefault() ?? "mp4",
            string.Join(", ", containers))).Trim().TrimStart('.').ToLowerInvariant();
        if (container.Length == 0)
            container = existing?.RecompressContainer ?? containers.FirstOrDefault() ?? "mp4";
        string? mismatch = PostProcess.RecompressCodecs.ValidateContainer(videoCodec, audioCodec, container);
        if (mismatch is not null)
        {
            Console.WriteLine($"{L.T("error.prefix")}{L.T("rc.container.mismatch", mismatch)}");
            return;
        }

        string note = ReadRaw(L.T("era.custom.note", existing?.NoteText ?? "")).Trim();

        // ---- live layer, only for an "era" ----
        string? encoderName = null;
        int? liveWidth = null, liveHeight = null;
        double? liveFps = null, limit = null;
        if (era)
        {
            Console.WriteLine(L.T("era.custom.live"));
            encoderName = ReadRaw(L.T("era.custom.encoder", existing?.Encoder ?? "libx264")).Trim();
            if (encoderName.Length == 0) encoderName = existing?.Encoder ?? "libx264";

            int lw = existing?.Width ?? 320, lh = existing?.Height ?? 240;
            Console.WriteLine(L.T("era.custom.livesize", lw, lh));
            string liveSize = ReadRaw("> ").Trim();
            if (liveSize.Contains('x'))
            {
                string[] parts = liveSize.ToLowerInvariant().Split('x');
                if (parts.Length == 2 && int.TryParse(parts[0], out int w2) && int.TryParse(parts[1], out int h2)
                    && w2 >= 16 && h2 >= 16)
                {
                    lw = w2;
                    lh = h2;
                }
            }
            liveWidth = lw;
            liveHeight = lh;
            liveFps = ReadDouble(L.T("era.custom.livefps", $"{existing?.Fps ?? 15:G6}"), existing?.Fps ?? 15);
            limit = ReadDouble(L.T("era.custom.limit", $"{existing?.LimitKbps ?? 300:G6}"),
                existing?.LimitKbps ?? 300);
        }

        var preset = new PostProcess.EraPreset
        {
            Id = normalised,
            BuiltIn = false,
            Kind = era ? PostProcess.EraKind.Era : PostProcess.EraKind.Site,
            YearFrom = from,
            YearTo = to,
            Encoder = encoderName,
            Width = liveWidth,
            Height = liveHeight,
            Fps = liveFps,
            LimitKbps = limit,
            ScaleAlgo = era ? existing?.ScaleAlgo ?? "bicubic" : null,
            AudioEncoder = era ? existing?.AudioEncoder : null,
            AudioKbps = era ? existing?.AudioKbps : null,
            AudioChannels = era ? existing?.AudioChannels : null,
            AudioRate = era ? existing?.AudioRate : null,
            RcWidth = width,
            RcHeight = height,
            RcFps = fps > 0 ? fps : 15,
            RcVideoKbps = Math.Max(1, vkbps),
            RcCrf = existing?.RcCrf ?? (from < 2010 ? 28 : 24),
            RcProfile = existing?.RcProfile ?? (from < 2010 ? "baseline" : "main"),
            RcAudioKbps = Math.Max(0, akbps),
            RcAudioRate = existing?.RcAudioRate ?? (from < 2010 ? 22050 : 44100),
            RcAudioChannels = existing?.RcAudioChannels ?? (from < 2010 ? 1 : 2),
            VideoCodec = videoCodec,
            AudioCodec = audioCodec,
            RecompressContainer = container,
            NoteText = note.Length > 0 ? note : null,
        };

        c.CustomEras.RemoveAll(p => string.Equals(p.Id, preset.Id, StringComparison.OrdinalIgnoreCase));
        c.CustomEras.Add(preset);
        PostProcess.EraPresets.SetCustom(c.CustomEras);
        Console.WriteLine(L.T("era.custom.saved", preset.Id));
        Console.WriteLine($" {preset.Summary()} -- {preset.RecompressArgs}");
    }

    private static string AskPresetId()
    {
        Console.Write(L.T("era.custom.ask"));
        return (Console.ReadLine() ?? "").Trim();
    }

    private static void DeleteCustomEra(Config c)
    {
        if (c.CustomEras.Count == 0)
        {
            Console.WriteLine(L.T("era.custom.none"));
            return;
        }
        foreach (PostProcess.EraPreset preset in c.CustomEras)
            Console.WriteLine($"  {preset.Id,-16} {preset.Summary()}");

        string id = AskPresetId();
        int removed = c.CustomEras.RemoveAll(
            p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            Console.WriteLine($"{L.T("error.prefix")}{L.T("era.custom.notcustom", id)}");
            return;
        }
        PostProcess.EraPresets.SetCustom(c.CustomEras);
        Console.WriteLine(L.T("era.custom.deleted", id));
    }

    private static (int From, int To) ParseYears(string raw, int fallbackFrom, int fallbackTo)
    {
        if (raw.Length == 0) return (fallbackFrom, fallbackTo);
        string[] parts = raw.Replace('–', '-').Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2
            && int.TryParse(parts[0].Trim(), out int from)
            && int.TryParse(parts[1].Trim(), out int to))
        {
            return (Math.Clamp(from, 1990, 2100), Math.Clamp(Math.Max(from, to), 1990, 2100));
        }
        return (fallbackFrom, fallbackTo);
    }

    private static void EditRecompress(Config c, string ffmpeg, FFmpeg.Capabilities caps)
    {
        while (true)
        {
            // v7.9.5.1: the fragment is still the source of truth, so the codec
            // and quality shown here are read back out of it. Editing either one
            // rewrites the fragment instead of adding a second place to look.
            (List<string> tokens, _) = PostProcess.PostArgs.Parse(c.Recompress.Args);
            string video = PostProcess.RecompressCodecs.DetectVideoCodec(tokens) ?? "-";
            string audio = PostProcess.RecompressCodecs.DetectAudioCodec(tokens) ?? "-";
            string quality = QualityOf(tokens);

            Console.WriteLine($"\n== {L.T("rc.menu")} ==");
            Console.WriteLine($" 1. {L.Pad(L.T("rc.menu.enable"), 30)} = {(c.Recompress.Enabled ? "on" : L.T("value.off"))}");
            Console.WriteLine($" 2. {L.Pad(L.T("rc.menu.args"), 30)} = {(c.Recompress.Args.Length == 0 ? L.T("value.notselected") : c.Recompress.Args)}");
            Console.WriteLine($" 3. {L.Pad(L.T("rc.menu.container"), 30)} = {c.Recompress.Container}");
            // v7.9.5: the stage no longer needs a live recording in front of it.
            Console.WriteLine($" 4. {L.Pad(L.T("rc.menu.only"), 30)}");
            Console.WriteLine($" 5. {L.Pad(L.T("rc.menu.codec"), 30)} = {video}");
            Console.WriteLine($" 6. {L.Pad(L.T("rc.menu.quality"), 30)} = {quality}");
            Console.WriteLine($" 7. {L.Pad(L.T("rc.menu.audio"), 30)} = {audio}");
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
                    RecompressFile(c, ffmpeg);
                    break;
                case "5":
                    EditRecompressVideoCodec(c, caps);
                    break;
                case "6":
                    EditRecompressQuality(c);
                    break;
                case "7":
                    EditRecompressAudioCodec(c);
                    break;
            }
        }
    }

    /// <summary>Pick the second pass's video codec; the container follows it.</summary>
    private static void EditRecompressVideoCodec(Config c, FFmpeg.Capabilities caps)
    {
        Console.WriteLine(L.T("rc.available", PostProcess.RecompressCodecs.VideoIdList));
        Console.Write(L.T("rc.prompt.codec", PostProcess.RecompressCodecs.DetectVideoCodec(
            PostProcess.PostArgs.Tokenize(c.Recompress.Args)) ?? "libx264",
            PostProcess.RecompressCodecs.VideoIdList));
        string raw = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        if (raw.Length == 0) return;

        SecondPassVideoCodec? codec = PostProcess.RecompressCodecs.FindVideo(raw);
        if (codec is null)
        {
            Console.WriteLine($"{L.T("error.prefix")}{L.T("rc.codecmissing", raw)}");
            return;
        }
        if (!caps.VideoEncoders.Contains(codec.Name))
        {
            Console.WriteLine($"{L.T("error.prefix")}{L.T("rc.codecmissing", codec.Name)}");
            return;
        }

        (List<string> tokens, _) = PostProcess.PostArgs.Parse(c.Recompress.Args);
        string audio = PostProcess.RecompressCodecs.DetectAudioCodec(tokens)
            ?? PostProcess.RecompressCodecs.RecommendedAudio(codec.Name);
        int? quality = int.TryParse(QualityOf(tokens), out int q) ? q : null;

        c.Recompress.Args = ComposeSecondPass(tokens, codec, quality, audio, null);
        c.Recompress.Container = PostProcess.RecompressCodecs.DefaultContainer(codec.Name, audio)
            ?? c.Recompress.Container;
        Console.WriteLine(L.T("rc.codec.set", codec.Name));
        Console.WriteLine(L.T("rc.autoderived", codec.Name, audio, c.Recompress.Container));
    }

    private static void EditRecompressAudioCodec(Config c)
    {
        (List<string> tokens, _) = PostProcess.PostArgs.Parse(c.Recompress.Args);
        string video = PostProcess.RecompressCodecs.DetectVideoCodec(tokens) ?? "libx264";
        string current = PostProcess.RecompressCodecs.DetectAudioCodec(tokens)
            ?? PostProcess.RecompressCodecs.RecommendedAudio(video);

        Console.Write(L.T("rc.prompt.audio", current,
            PostProcess.RecompressCodecs.AudioIdList + ", copy"));
        string raw = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        if (raw.Length == 0) return;

        if (!PostProcess.RecompressCodecs.IsCopyAudio(raw)
            && PostProcess.RecompressCodecs.FindAudio(raw) is null)
        {
            Console.WriteLine($"{L.T("error.prefix")}{L.T("rc.codecmissing", raw)}");
            return;
        }
        string? mismatch = PostProcess.RecompressCodecs.ValidateContainer(video, raw, c.Recompress.Container);
        if (mismatch is not null)
        {
            Console.WriteLine($"{L.T("error.prefix")}{L.T("rc.container.mismatch", mismatch)}");
            return;
        }

        SecondPassVideoCodec? codec = PostProcess.RecompressCodecs.FindVideo(video);
        c.Recompress.Args = codec is null
            ? c.Recompress.Args
            : ComposeSecondPass(tokens, codec, int.TryParse(QualityOf(tokens), out int q) ? q : null, raw, null);
        Console.WriteLine(L.T("rc.audio.set", raw));
    }

    private static void EditRecompressQuality(Config c)
    {
        (List<string> tokens, _) = PostProcess.PostArgs.Parse(c.Recompress.Args);
        string video = PostProcess.RecompressCodecs.DetectVideoCodec(tokens) ?? "libx264";
        SecondPassVideoCodec? codec = PostProcess.RecompressCodecs.FindVideo(video);
        if (codec is null)
        {
            Console.WriteLine(L.T("rc.codecfirst"));
            return;
        }

        string current = QualityOf(tokens);
        Console.Write(L.T("rc.prompt.quality", current.Length > 0 ? current : "auto", codec.QualityFlag));
        string raw = (Console.ReadLine() ?? "").Trim();
        if (raw.Length == 0) return;
        if (!int.TryParse(raw, out int quality) || quality < 1 || quality > 63)
        {
            Console.WriteLine(L.T("param.badvalue", raw));
            return;
        }

        string audio = PostProcess.RecompressCodecs.DetectAudioCodec(tokens)
            ?? PostProcess.RecompressCodecs.RecommendedAudio(codec.Name);
        c.Recompress.Args = ComposeSecondPass(tokens, codec, quality, audio, null);
        Console.WriteLine(L.T("rc.quality.set", quality));
    }

    /// <summary>
    /// Rewrite a fragment around a codec choice: the filter chain the user wrote
    /// is kept, the codec/quality/audio arguments are replaced.
    /// </summary>
    private static string ComposeSecondPass(
        IReadOnlyList<string> tokens, SecondPassVideoCodec codec, int? quality,
        string audioCodec, int? audioKbps)
    {
        var parts = new List<string>();

        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] is "-vf" or "-filter:v" or "-filter" && i + 1 < tokens.Count)
            {
                parts.Add(tokens[i]);
                parts.Add(tokens[i + 1]);
                i++;
            }
        }

        parts.Add(PostProcess.RecompressCodecs.VideoArgs(codec, quality ?? codec.DefaultQuality));
        parts.Add(PostProcess.RecompressCodecs.AudioArgs(audioCodec, audioKbps, null, null));
        return string.Join(' ', parts);
    }

    /// <summary>Whatever follows <c>-crf</c> or <c>-q:v</c>, or "" when neither is there.</summary>
    private static string QualityOf(IReadOnlyList<string> tokens)
    {
        for (int i = 0; i < tokens.Count - 1; i++)
        {
            if (tokens[i] is "-crf" or "-q:v" or "-qscale:v") return tokens[i + 1];
        }
        return "";
    }

    /// <summary>
    /// v7.9.5: run the second pass on an existing file, with no live encode in
    /// front of it. Same fragment, same validator, same output naming -- the
    /// only difference is that the input is whatever the user names.
    /// </summary>
    private static void RecompressFile(Config c, string ffmpeg)
    {
        if (c.Recompress.Args.Length == 0)
        {
            Console.WriteLine(L.T("rc.args.empty"));
            Console.WriteLine(L.T("rc.hint"));
            return;
        }

        Console.Write(L.T("rc.only.fileprompt"));
        string raw = (Console.ReadLine() ?? "").Trim().Trim('"');
        if (raw.Length == 0) return;

        var src = new FileInfo(raw);
        if (!src.Exists)
        {
            Console.WriteLine($"{L.T("error.prefix")}{L.T("input.notfound")} {src.FullName}");
            return;
        }

        (_, string? error) = PostProcess.PostArgs.Parse(c.Recompress.Args);
        if (error is not null)
        {
            Console.WriteLine($"{L.T("error.prefix")}{L.T("rc.args.rejected", error)}");
            return;
        }

        string output = PostProcess.Recompress.DeriveOutputPath(
            src.FullName, c.Recompress.Container);
        if (string.Equals(Path.GetFullPath(output), src.FullName, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(L.T("output.sameasinput"));
            return;
        }

        var settings = new RecompressSettings
        {
            Enabled = true,
            Args = c.Recompress.Args,
            Container = c.Recompress.Container,
        };

        Console.WriteLine(L.T("rc.only.input", src.FullName));
        Console.WriteLine(L.T("rc.only.output", output));
        Console.Write(L.T("start.confirm"));
        string confirm = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        if (confirm is not ("" or "y" or "yes")) return;

        try
        {
            (bool ok, string? failure) =
                PostProcess.Recompress.Apply(ffmpeg, src.FullName, output, settings);
            if (!ok)
            {
                Console.WriteLine($"{L.T("error.prefix")}{L.T("post.failed", "recompress", failure ?? "")}");
                Console.WriteLine(L.T("post.kept", src.FullName));
                return;
            }
            Console.WriteLine(L.T("rc.only.done", output));
        }
        catch (Exception exc)
        {
            Console.WriteLine($"{L.T("error.prefix")}{exc.Message}");
        }
    }
}
