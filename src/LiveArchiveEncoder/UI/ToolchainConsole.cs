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
    /// Nested menu. Returns the source, proxy and GitHub front in effect
    /// afterwards so the caller can re-run discovery when they changed.
    ///
    /// <para>v7.9.8.3 adds the two settings that belong to downloading rather
    /// than to encoding: the front GitHub traffic goes through, and the NNEDI3
    /// weights the <c>nnedi</c> filter cannot start without. Both were
    /// CLI-only until now, which left the interactive shell unable to reach
    /// them at all.</para>
    /// </summary>
    public static (string Source, string? Proxy, string? GhProxy) Menu(
        string sourceId, string? proxy, string? ghProxy = null)
    {
        while (true)
        {
            Console.WriteLine($"\n{L.T("tc.menu.heading")}");
            Console.WriteLine($" 1. {L.T("tc.menu.list")}");
            Console.WriteLine($" 2. {L.Pad(L.T("tc.menu.switch"), 26)} = {sourceId}");
            Console.WriteLine($" 3. {L.T("tc.menu.download")}");
            Console.WriteLine($" 4. {L.T("tc.menu.delete")}");
            Console.WriteLine($" 5. {L.Pad(L.T("tc.menu.proxy"), 26)} = {proxy ?? L.T("value.none")}");
            Console.WriteLine($" 6. {L.Pad(L.T("tc.menu.ghproxy"), 26)} = {DescribeFront(ghProxy)}");
            Console.WriteLine($" 7. {L.Pad(L.T("tc.menu.nnedi"), 26)} = {DescribeWeights()}");
            Console.WriteLine($" 0. {L.T("tc.menu.back")}");
            Console.Write(L.T("menu.select"));

            switch ((Console.ReadLine() ?? "").Trim())
            {
                case "0": return (sourceId, proxy, ghProxy);
                case "1":
                    PrintInstalled(sourceId);
                    break;
                case "2":
                    sourceId = Switch(sourceId);
                    break;
                case "3":
                    sourceId = Download(sourceId, proxy, ghProxy);
                    break;
                case "4":
                    Delete();
                    break;
                case "5":
                    proxy = EditProxy(proxy);
                    break;
                case "6":
                    ghProxy = EditGhProxy(ghProxy);
                    break;
                case "7":
                    NnediMenu(proxy, ghProxy);
                    break;
                default:
                    Console.WriteLine(L.T("menu.invalid"));
                    break;
            }
        }
    }

    /// <summary>
    /// What the front setting currently says. Three states, not two: unset means
    /// a download probes the route and picks one, which is not the same as
    /// "off" (the user said no front, and a probe must not override that).
    /// </summary>
    private static string DescribeFront(string? front) =>
        front is null ? L.T("tc.ghproxyauto")
        : front.Length == 0 || string.Equals(front, GitHubProxy.Off, StringComparison.OrdinalIgnoreCase)
            ? L.T("value.off")
            : front;

    private static string DescribeWeights() =>
        Nnedi.Read() is { } record
            ? $"{record.Release ?? "?"} ({record.Bytes:N0} B)"
            : L.T("tc.notinstalled");

    private static string? EditGhProxy(string? front)
    {
        Console.Write(L.T("tc.prompt.ghproxy"));
        string entered = (Console.ReadLine() ?? "").Trim();
        if (entered.Length == 0) return front;

        if (entered is "-" || entered.Equals(GitHubProxy.Off, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(L.T("tc.ghproxyoff"));
            return GitHubProxy.Off;
        }

        (string? prefix, string? error) = GitHubProxy.Normalise(entered);
        if (error is not null)
        {
            Console.WriteLine(L.T("tc.ghproxybad", entered, error));
            return front;
        }
        Console.WriteLine(L.T("tc.ghproxyset", prefix));
        return prefix;
    }

    /// <summary>
    /// Show the weights, offer to fetch them. The FFmpeg verifier is not passed
    /// here: the menu runs before a build is necessarily installed, and a
    /// download that has the right size and hash is worth having either way.
    /// </summary>
    private static void NnediMenu(string? proxy, string? front)
    {
        Console.WriteLine($"\n{L.T("nnedi.heading", Nnedi.WeightsPath)}");
        if (Nnedi.Read() is { } record)
        {
            Console.WriteLine(L.T("nnedi.release", record.Release ?? "?"));
            Console.WriteLine(L.T("nnedi.size", record.Bytes));
            if (record.Sha256 is { Length: > 0 } sha) Console.WriteLine(L.T("nnedi.sha", sha[..16]));
            if (record.InstalledAt is { Length: > 0 } when) Console.WriteLine(L.T("nnedi.when", when));
        }
        else
        {
            Console.WriteLine(L.T("nnedi.none"));
        }

        Console.Write(L.T("tc.prompt.nnedi"));
        string entered = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        if (entered != "u") return;

        (Nnedi.Outcome outcome, string? error, string? release) =
            Nnedi.Update(null, proxy, front, front is null, Console.WriteLine);
        Console.WriteLine(outcome switch
        {
            Nnedi.Outcome.Installed => L.T("nnedi.updok", release ?? "?", Nnedi.WeightsPath),
            Nnedi.Outcome.AlreadyCurrent => L.T("nnedi.updcurrent", release ?? "?"),
            _ => $"{L.T("error.prefix")}{L.T("nnedi.updfail", error ?? "")}",
        });
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

    private static string Download(string sourceId, string? proxy, string? front)
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
        // v7.9.8.3: same auto-route rule as the CLI -- when the shell has not
        // been told which front to use, ask the network instead of assuming.
        if (front is null && localArchive is null && dist.Url is { Length: > 0 } githubUrl)
            front = GitHubProxy.Detect(githubUrl, proxy, Console.WriteLine);

        (bool ok, string? error) = Toolchain.Install(dist, proxy, localArchive, Console.WriteLine, front);
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
