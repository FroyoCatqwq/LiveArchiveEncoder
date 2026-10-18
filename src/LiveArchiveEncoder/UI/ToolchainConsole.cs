using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.FFmpeg;
using LiveArchiveEncoder.Localization;

namespace LiveArchiveEncoder.UI;

/// <summary>
/// The interactive front of the FFmpeg toolchain manager.
///
/// Kept separate from <see cref="Cui"/> because it owns a nested menu with its
/// own prompt loop, and because it has to survive a discovery refresh: switching
/// or installing a build changes which ffmpeg the rest of the program uses.
/// </summary>
public static class ToolchainConsole
{
    /// <summary>One line per known distribution: installed or not, and where.</summary>
    public static void PrintInstalled(string? activeId = null)
    {
        Console.WriteLine($"\n{L.T("tc.heading")}");
        Console.WriteLine($"{L.Pad(L.T("tc.root"), 15)}: {Toolchain.Root}");
        // Available, not All: the catalogue holds one entry per id per
        // platform, and listing all of them would show "btbn (win64)" next to
        // "btbn (linux64)" on a machine that can only ever install one.
        foreach (FfmpegDist dist in FfmpegCatalog.Available)
        {
            string? exe = Toolchain.FFmpegFor(dist.Id);
            bool active = string.Equals(dist.Id, activeId, StringComparison.OrdinalIgnoreCase);
            string state = exe is null
                ? L.T("tc.notinstalled")
                : active ? L.T("tc.active") : L.T("tc.installed");
            Console.WriteLine($"  {L.Pad(dist.Id, 8)} {L.Pad(state, 14)} {dist.Label}");
            if (exe is not null) Console.WriteLine($"           {exe}");
        }
        Console.WriteLine($"\n{L.T("tc.hint")}");
        Console.WriteLine(L.T("tc.hint.manual", FfmpegCatalog.AutomaticIds));
    }

    /// <summary>
    /// Nested menu. Returns the source and proxy in effect afterwards so the
    /// caller can re-run discovery when they changed.
    /// </summary>
    public static (string Source, string? Proxy) Menu(string sourceId, string? proxy)
    {
        while (true)
        {
            Console.WriteLine($"\n{L.T("tc.menu.heading")}");
            Console.WriteLine($" 1. {L.T("tc.menu.list")}");
            Console.WriteLine($" 2. {L.Pad(L.T("tc.menu.switch"), 26)} = {sourceId}");
            Console.WriteLine($" 3. {L.T("tc.menu.download")}");
            Console.WriteLine($" 4. {L.T("tc.menu.delete")}");
            Console.WriteLine($" 5. {L.Pad(L.T("tc.menu.proxy"), 26)} = {proxy ?? L.T("value.none")}");
            Console.WriteLine($" 0. {L.T("tc.menu.back")}");
            Console.Write(L.T("menu.select"));

            switch ((Console.ReadLine() ?? "").Trim())
            {
                case "0": return (sourceId, proxy);
                case "1":
                    PrintInstalled(sourceId);
                    break;
                case "2":
                    sourceId = Switch(sourceId);
                    break;
                case "3":
                    sourceId = Download(sourceId, proxy);
                    break;
                case "4":
                    Delete();
                    break;
                case "5":
                    proxy = EditProxy(proxy);
                    break;
                default:
                    Console.WriteLine(L.T("menu.invalid"));
                    break;
            }
        }
    }

    private static string AskSource()
    {
        Console.WriteLine(L.T("tc.choose", FfmpegCatalog.Ids));
        Console.Write(L.T("tc.prompt.source"));
        return (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
    }

    private static string Switch(string sourceId)
    {
        string picked = AskSource();
        if (picked.Length == 0) return sourceId;

        FfmpegDist? dist = FfmpegCatalog.Find(picked);
        if (dist is null)
        {
            Console.WriteLine(L.T("tc.unknown", picked));
            return sourceId;
        }
        if (!Toolchain.IsInstalled(dist.Id))
        {
            Console.WriteLine(L.T("tc.notinstalled.hint", dist.Id));
            return sourceId;
        }
        Console.WriteLine(L.T("tc.switched", dist.Id, Toolchain.FFmpegFor(dist.Id)));
        return dist.Id;
    }

    private static string Download(string sourceId, string? proxy)
    {
        string picked = AskSource();
        if (picked.Length == 0) picked = sourceId;

        FfmpegDist? dist = FfmpegCatalog.Find(picked);
        if (dist is null)
        {
            Console.WriteLine(L.T("tc.unknown", picked));
            return sourceId;
        }

        // Sources whose host blocks automated downloads are installed from a
        // file the user already fetched.
        string? localArchive = null;
        if (!dist.AutoDownload)
        {
            Console.WriteLine(L.T("tc.manual.howto", dist.Homepage));
            Console.WriteLine(L.T("tc.manual.step1", dist.Homepage));
            Console.WriteLine(L.T("tc.manual.step2"));
            Console.Write(L.T("tc.manual.prompt"));
            localArchive = (Console.ReadLine() ?? "").Trim().Trim('"');
            if (localArchive.Length == 0)
            {
                Console.WriteLine(L.T("tc.cancelled"));
                return sourceId;
            }
        }
        else
        {
            string? effectiveProxy = proxy;
            if (effectiveProxy is null)
            {
                Console.Write(L.T("tc.prompt.proxy"));
                string entered = (Console.ReadLine() ?? "").Trim();
                if (entered.Length > 0) effectiveProxy = entered;
            }
            else
            {
                Console.WriteLine(L.T("tc.proxyinuse", effectiveProxy));
            }
            proxy = effectiveProxy;
        }

        Console.WriteLine(L.T("tc.installing", dist.Label));
        Console.WriteLine(dist.Homepage);
        (bool ok, string? error) = Toolchain.Install(dist, proxy, localArchive, Console.WriteLine);
        if (!ok)
        {
            Console.WriteLine($"{L.T("error.prefix")}{L.T("tc.installfail", error)}");
            return sourceId;
        }

        // A build without --enable-gpl drops a chunk of the legacy encoder list
        // (libx264, libxvid, libtheora...). Say so rather than let the user
        // wonder why the encoder menu got shorter.
        string? exe = Toolchain.FFmpegFor(dist.Id);
        if (exe is not null)
        {
            (string version, _, bool gpl) = FFmpegDiscovery.BuildInfo(exe);
            Console.WriteLine(version);
            if (!gpl) Console.WriteLine(L.T("tc.nogpl"));
        }

        Console.WriteLine(L.T("tc.installok", dist.Id));
        return dist.Id;
    }

    private static void Delete()
    {
        string picked = AskSource();
        if (picked.Length == 0) return;

        FfmpegDist? dist = FfmpegCatalog.Find(picked);
        if (dist is null)
        {
            Console.WriteLine(L.T("tc.unknown", picked));
            return;
        }

        (bool ok, string? error) = Toolchain.Delete(dist.Id);
        Console.WriteLine(ok ? L.T("tc.deleted", dist.Id) : $"{L.T("error.prefix")}{L.T("tc.deletefail", error)}");
    }

    private static string? EditProxy(string? proxy)
    {
        Console.Write(L.T("tc.prompt.proxy.edit", proxy ?? L.T("value.none")));
        string entered = (Console.ReadLine() ?? "").Trim();
        if (entered.Length == 0) return proxy;
        // "-" clears it: an empty answer means "leave it alone".
        if (entered == "-") return null;
        Console.WriteLine(L.T("tc.proxyset", entered));
        return entered;
    }
}
