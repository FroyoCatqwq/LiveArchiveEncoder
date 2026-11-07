using LiveArchiveEncoder.Localization;
using LiveArchiveEncoder.PostProcess;

namespace LiveArchiveEncoder.UI;

/// <summary>
/// v7.9.8.13: the two steps that used to be entirely manual -- finding the
/// subtitle that belongs to the video, and unpacking the font pack that belongs
/// to the subtitle. Both are per-run choices: they deliberately never become
/// settings, so a run cannot quietly rewrite the user's config.json.
/// </summary>
public static class SubtitleSetup
{
    /// <summary>
    /// Fill in <see cref="CliOptions.SubtitleFiles"/> from the source's name and,
    /// when a pack was named, import its fonts. Returns an early exit code only
    /// for a request that cannot be honoured (a bad path the user typed).
    /// </summary>
    public static (CliOptions Options, int? ExitCode) Prepare(CliOptions cli)
    {
        if (!cli.AutoSubtitle && cli.FontPack is not { Length: > 0 }) return (cli, null);
        if (string.IsNullOrWhiteSpace(cli.Input))
        {
            Console.Error.WriteLine($"ERROR: {L.T("fontpack.nosource")}");
            return (cli, 2);
        }

        List<string> subs = [.. cli.SubtitleFiles];
        if (subs.Count == 0)
        {
            IReadOnlyList<string> found = SubtitleDiscovery.Find(cli.Input);
            IReadOnlyList<string> chosen = ChooseSubtitles(cli.Input, found, Interactive());
            subs.AddRange(chosen);
            if (chosen.Count == 0)
                Console.WriteLine(L.T("fontpack.skipsub"));
            else
                Console.WriteLine(L.T("fontpack.found", Path.GetFileName(chosen[0])));

            // A path the user typed by hand must exist; the discovery itself
            // only returns files that do.
            foreach (string sub in chosen)
            {
                if (File.Exists(sub)) continue;
                Console.Error.WriteLine($"ERROR: {L.T("fontpack.badsub", sub)}");
                return (cli, 2);
            }
        }

        if (cli.FontPack is { Length: > 0 } pack)
        {
            if (!FontPack.IsArchive(pack))
            {
                Console.Error.WriteLine($"ERROR: {L.T("fontpack.badpack", pack)}");
                return (cli, 2);
            }
            (bool ok, string? error) = ImportPack(pack, subs, cli.GhProxy, Interactive());
            if (!ok)
            {
                Console.Error.WriteLine($"ERROR: {error}");
                return (cli, 2);
            }
        }

        cli.ApplyDiscoveredSubtitles(subs);
        return (cli, null);
    }

    /// <summary>
    /// Shared by the CLI and the CUI. No match means the user is asked for a
    /// path; several matches mean a numbered pick. A non-interactive caller
    /// gets the first (best-ranked) candidate and never blocks on stdin.
    /// </summary>
    public static IReadOnlyList<string> ChooseSubtitles(
        string inputPath, IReadOnlyList<string> found, bool interactive)
    {
        if (found.Count == 0)
        {
            if (!interactive) return [];
            Console.WriteLine(L.T("fontpack.nosub", Path.GetFileName(inputPath)));
            Console.Write(L.T("fontpack.manual"));
            string? typed = Console.ReadLine();
            return string.IsNullOrWhiteSpace(typed) ? [] : [typed.Trim().Trim('"')];
        }

        if (found.Count == 1 || !interactive) return [found[0]];

        Console.WriteLine(L.T("fontpack.picksub"));
        for (int i = 0; i < found.Count; i++)
            Console.WriteLine($" {i + 1}. {Path.GetFileName(found[i])}");
        Console.Write(L.T("fontpack.pick"));
        string? answer = Console.ReadLine();
        int chosen = int.TryParse(answer?.Trim(), out int n) && n >= 1 && n <= found.Count ? n : 1;
        return [found[chosen - 1]];
    }

    /// <summary>
    /// Unpack a pack, then copy either the fonts the ASS styles ask for or the
    /// ones the user picked. "By personal preference" is the numbered / "*"
    /// answer; blank takes the ASS suggestion.
    /// </summary>
    public static (bool Ok, string? Error) ImportPack(
        string pack, IReadOnlyList<string> subtitles, string? ghProxy = null,
        bool interactive = false)
    {
        using FontPack.Unpacked unpacked = FontPack.Unpack(pack, ghProxy, Console.WriteLine);
        if (unpacked.Error is not null) return (false, unpacked.Error);
        Console.WriteLine(L.T("fontpack.unpacked", unpacked.Fonts.Count));
        if (unpacked.Fonts.Count == 0) return (true, null);

        List<string> suggested = FontPack.Recommended(
            unpacked.Fonts, SubtitleFonts.RequiredFamilies(subtitles));
        if (suggested.Count == 0) suggested = [.. unpacked.Fonts];

        List<string> selection = suggested;
        if (interactive)
        {
            Console.WriteLine(L.T("fontpack.fonts"));
            for (int i = 0; i < unpacked.Fonts.Count; i++)
                Console.WriteLine($" {i + 1}. {Path.GetFileName(unpacked.Fonts[i])}");
            Console.Write(L.T("fontpack.pickfonts"));
            selection = ParseSelection(Console.ReadLine(), unpacked.Fonts, suggested);
        }

        (int copied, int skipped, string dir) = FontPack.Install(selection);
        Console.WriteLine(L.T("fontpack.imported", copied, dir, skipped));
        return (true, null);
    }

    /// <summary>Blank = ASS suggestion, "*" = everything, "1,3" = those files.</summary>
    internal static List<string> ParseSelection(
        string? answer, IReadOnlyList<string> fonts, IReadOnlyList<string> suggested)
    {
        string text = answer?.Trim() ?? "";
        if (text.Length == 0) return [.. suggested];
        if (text is "*" or "all") return [.. fonts];

        var picked = new List<string>();
        foreach (string part in text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, out int n) && n >= 1 && n <= fonts.Count
                && !picked.Contains(fonts[n - 1])) picked.Add(fonts[n - 1]);
        }
        return picked.Count > 0 ? picked : [.. suggested];
    }

    // v7.9.8.13.2: a redirected stdout means a script is capturing us; asking
    // on a terminal that is still attached to stdin would block it forever.
    private static bool Interactive() =>
        !Console.IsInputRedirected && !Console.IsOutputRedirected;
}
