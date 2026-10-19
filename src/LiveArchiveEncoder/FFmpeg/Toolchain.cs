using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using LiveArchiveEncoder.Core;

namespace LiveArchiveEncoder.FFmpeg;

/// <summary>An FFmpeg build this tool has installed under its own directory.</summary>
public sealed class InstalledToolchain
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Home { get; init; }
    public required string FFmpeg { get; init; }
    public string? FFprobe { get; init; }
    public string? FFplay { get; init; }
    public string Version { get; init; } = "";
}

/// <summary>
/// Installs, selects and removes FFmpeg builds.
///
/// Layout:
/// <code>
///   %LOCALAPPDATA%\LiveArchiveEncoder\ffmpeg\&lt;source&gt;\bin\ffmpeg.exe
/// </code>
/// Every distribution lives side by side, so switching is instant and a bad
/// download never destroys a working build. Discovery prefers the selected one
/// and still falls back to PATH when nothing is installed.
///
/// Downloads go through aria2c when it is on PATH (16 connections, resumed
/// partial files, proxy-aware), and fall back to <c>HttpClient</c> otherwise --
/// the fallback honours the proxy too, just without multi-connection speed.
/// </summary>
public static class Toolchain
{
    /// <summary>Root of the managed installs, not the install of one source.</summary>
    public static string Root
    {
        get
        {
            string? appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = string.IsNullOrEmpty(appData)
                ? AppContext.BaseDirectory
                : Path.Combine(appData, "LiveArchiveEncoder");
            return Path.Combine(dir, "ffmpeg");
        }
    }

    public static string Home(string id) => Path.Combine(Root, id);

    public static string BinDir(string id) => Path.Combine(Home(id), "bin");

    /// <summary>Path of one tool (ffmpeg/ffprobe/ffplay) for an installed source.</summary>
    public static string? Exe(string id, string tool)
    {
        string name = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
        string path = Path.Combine(BinDir(id), name);
        return File.Exists(path) ? path : null;
    }

    public static bool IsInstalled(string id) => Exe(id, "ffmpeg") is not null;

    /// <summary>The ffmpeg executable a source would contribute, or null.</summary>
    public static string? FFmpegFor(string id) => Exe(id, "ffmpeg");

    public static List<InstalledToolchain> Installed()
    {
        var list = new List<InstalledToolchain>();
        // Available, not All: the catalogue holds one entry per platform per id,
        // and an installed build can only ever be this platform's.
        foreach (FfmpegDist dist in FfmpegCatalog.Available)
        {
            string? ffmpeg = FFmpegFor(dist.Id);
            if (ffmpeg is null) continue;
            list.Add(new InstalledToolchain
            {
                Id = dist.Id,
                Label = dist.Label,
                Home = Home(dist.Id),
                FFmpeg = ffmpeg,
                FFprobe = Exe(dist.Id, "ffprobe"),
                FFplay = Exe(dist.Id, "ffplay"),
                Version = ShortVersion(ffmpeg),
            });
        }
        return list;
    }

    private static string ShortVersion(string ffmpeg)
    {
        try
        {
            CaptureResult p = ProcessRunner.Run(ffmpeg, "-hide_banner", "-version");
            return (p.Stdout + "\n" + p.Stderr).Split('\n')
                .Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0) ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// Remove an installed source. Fails (rather than silently doing nothing)
    /// when the directory is busy, which on Windows usually means an FFmpeg
    /// process from that directory is still running.
    /// </summary>
    public static (bool Ok, string? Error) Delete(string id)
    {
        FfmpegDist? dist = FfmpegCatalog.Find(id);
        if (dist is null) return (false, $"unknown source '{id}'");

        string home = Home(dist.Id);
        if (!Directory.Exists(home)) return (false, $"'{dist.Id}' is not installed");

        try
        {
            Directory.Delete(home, recursive: true);
            return (true, null);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return (false, exc.Message);
        }
    }

    /// <summary>
    /// Install a distribution, either by downloading it or from an archive the
    /// user already fetched.
    ///
    /// <paramref name="localArchive"/> exists for sources that cannot be
    /// downloaded automatically (SourceForge hides the mirror behind a
    /// Cloudflare interstitial): you download the file in a browser and hand the
    /// path over here. Everything is staged first, so a failed download or a
    /// missing extractor leaves the previous build intact.
    /// </summary>
    public static (bool Ok, string? Error) Install(
        FfmpegDist dist, string? proxy, string? localArchive = null, Action<string>? log = null)
    {
        void Say(string line) => log?.Invoke(line);

        ArchiveKind kind = dist.Archive;
        if (localArchive is { Length: > 0 })
        {
            if (!File.Exists(localArchive)) return (false, $"archive not found: {localArchive}");
            if (FfmpegCatalog.KindOfFile(localArchive) is not { } detected)
                return (false, $"'{localArchive}' is not a .zip, .7z or .tar.xz archive");
            kind = detected;
        }
        else if (!dist.AutoDownload)
        {
            return (false, $"'{dist.Id}' cannot be downloaded automatically; fetch the archive "
                + $"from {dist.Homepage} and install it with --ffmpeg-archive <path>");
        }

        string home = Home(dist.Id);
        string bin = BinDir(dist.Id);
        string staging = Path.Combine(Root, ".staging", dist.Id);
        string extractDir = Path.Combine(staging, "extract");

        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(extractDir);
            Directory.CreateDirectory(bin);

            string archive = Path.Combine(staging, "download" + dist.ArchiveExtension);
            if (kind != dist.Archive)
            {
                // A manual --ffmpeg-archive may disagree with the catalogue.
                archive = Path.Combine(staging, "download"
                    + (kind == ArchiveKind.Zip ? ".zip"
                       : kind == ArchiveKind.SevenZip ? ".7z" : ".tar.xz"));
            }
            if (localArchive is { Length: > 0 })
            {
                Say($"Archive: {localArchive}");
                File.Copy(localArchive, archive, overwrite: true);
            }
            else
            {
                string url = dist.Url!;
                Say($"URL    : {url}");
                Say($"Target : {bin}");

                (bool downloaded, string? downloadError) = Download(url, archive, proxy, Say);
                if (!downloaded) return (false, downloadError);

                // A downloader will happily save a 403 page or an interstitial
                // and call it a day. Check the magic bytes before the extractor
                // produces a far more confusing error, and retry once over
                // HttpClient, whose redirect handling differs from aria2c's.
                if (VerifyArchive(archive, kind) is { Length: > 0 } archiveError)
                {
                    Say(archiveError);
                    Say("Retrying with a single-connection download...");
                    try { File.Delete(archive); } catch { /* best effort */ }

                    (bool retried, string? retryError) = DownloadHttp(url, archive, proxy, Say);
                    if (!retried) return (false, retryError);
                    if (VerifyArchive(archive, kind) is { Length: > 0 } stillBad)
                        return (false, stillBad);
                }
            }

            if (VerifyArchive(archive, kind) is { Length: > 0 } badArchive)
                return (false, badArchive);

            Say("Extracting...");
            (bool extracted, string? extractError) = Extract(archive, extractDir);
            if (!extracted) return (false, extractError);

            (string? found, string? layoutError) = FindFfmpegDir(extractDir);
            if (found is null) return (false, layoutError);

            CopyInto(bin, found);
            if (Exe(dist.Id, "ffmpeg") is null)
            {
                string want = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
                return (false, $"extraction finished but no {want} landed in {bin}");
            }

            Say($"Installed: {Exe(dist.Id, "ffmpeg")}");
            Say(ShortVersion(Exe(dist.Id, "ffmpeg")!));
            return (true, null);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException
                                        or NotSupportedException or ArgumentException)
        {
            return (false, exc.Message);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// User-Agent sent by both downloaders.
    ///
    /// Two measurements behind this string (2026-10-18):
    ///
    ///   * a full "Chrome/124 ... Safari/537.36" UA is <b>rejected with 403</b> by
    ///     SourceForge, while the short Mozilla form is accepted -- their WAF
    ///     evidently blacklists that exact spoof;
    ///   * aria2c's own default UA is rejected by SourceForge too, and GitHub is
    ///     happy with any of them.
    ///
    /// The short form is the only one verified against all four hosts.
    /// </summary>
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";

    /// <summary>aria2c when available, HttpClient otherwise.</summary>
    private static (bool Ok, string? Error) Download(
        string url, string destination, string? proxy, Action<string> log)
    {
        string? aria2c = FindOnPath("aria2c");
        if (aria2c is not null)
        {
            log($"Downloading with aria2c ({aria2c}), 16 connections...");
            var args = new List<string>
            {
                "-c",                       // resume a partial download
                "--file-allocation=none",   // no preallocation on a slow share
                "--auto-file-renaming=false",
                "--allow-overwrite=true",
                "--timeout=60", "--connect-timeout=30",
                "--max-tries=5", "--retry-wait=5",
                "--summary-interval=0",
                "--console-log-level=notice",
                // SourceForge (and some mirrors) answer 403 to aria2's default
                // User-Agent while accepting a browser one -- measured
                // 2026-10-18, same URL and proxy.
                "--user-agent", BrowserUserAgent,
                "--dir", Path.GetDirectoryName(destination)!,
                "-o", Path.GetFileName(destination),
            };
            if (proxy.HasText)
            {
                args.Add("--all-proxy");
                args.Add(proxy);
            }
            args.Add(url);

            (int code, string output) = RunLong(aria2c, args, log);
            if (code == 0 && File.Exists(destination)) return (true, null);

            // aria2c failing is not the end: a blocked User-Agent, an odd proxy
            // or a host that dislikes the connection count all land here, and
            // the single-connection downloader still has a real chance.
            log($"aria2c exited with {code}: {Tail(output)}");
        }

        log(aria2c is null
            ? "aria2c not found; downloading with a single connection."
            : "Falling back to a single-connection download.");
        return DownloadHttp(url, destination, proxy, log);
    }

    private static (bool Ok, string? Error) DownloadHttp(
        string url, string destination, string? proxy, Action<string> log)
    {
        try
        {
            using var handler = new HttpClientHandler();
            if (proxy.HasText)
            {
                handler.Proxy = new WebProxy(proxy);
                handler.UseProxy = true;
            }

            using var client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
            using HttpResponseMessage response =
                client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                // The body of a 403 usually says who blocked it (CDN, WAF, proxy)
                // and why; without it the message is unactionable.
                string body = "";
                try { body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult(); }
                catch { /* body is a nicety, never a failure */ }
                string detail = body.Length == 0 ? "" : " -- " + Head(body.Trim(), 200).Replace('\n', ' ');
                return (false, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}{detail}");
            }

            long? total = response.Content.Headers.ContentLength;
            using FileStream file = File.Create(destination);
            using Stream stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();

            var buffer = new byte[1024 * 1024];
            long written = 0;
            long nextReport = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                file.Write(buffer, 0, read);
                written += read;
                if (written >= nextReport)
                {
                    nextReport = written + 20L * 1024 * 1024;
                    log(total is null
                        ? $"  {written / (1024 * 1024)} MB"
                        : $"  {written / (1024 * 1024)} / {total.Value / (1024 * 1024)} MB"
                          + $" ({100.0 * written / total.Value:F0}%)");
                }
            }
            return (true, null);
        }
        catch (Exception exc) when (exc is HttpRequestException or IOException
                                        or TaskCanceledException or System.Net.Sockets.SocketException)
        {
            return (false, exc.Message);
        }
    }

    private static (bool Ok, string? Error) Extract(string archive, string destination)
    {
        // Linux builds ship as .tar.xz; tar is the only tool guaranteed to be
        // there (Bsdtar on Windows never sees this path).
        if (archive.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase)
            || archive.EndsWith(".txz", StringComparison.OrdinalIgnoreCase))
        {
            string? tar = FindOnPath("tar");
            if (tar is null)
                return (false, "a .tar.xz archive needs tar; install tar and xz-utils");

            (int tarCode, string tarOutput) = RunLong(tar, ["-xJf", archive, "-C", destination], null);
            return tarCode == 0
                ? (true, null)
                : (false, $"tar exited with {tarCode}: {Tail(tarOutput)} -- is xz-utils installed?");
        }

        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                ZipFile.ExtractToDirectory(archive, destination, overwriteFiles: true);
                return (true, null);
            }
            catch (Exception exc) when (exc is IOException or InvalidDataException
                                            or UnauthorizedAccessException)
            {
                return (false, $"zip extraction failed: {exc.Message}");
            }
        }

        string? sevenZip = FindSevenZip();
        if (sevenZip is null)
            return (false, OperatingSystem.IsWindows()
                ? "a .7z archive needs an extractor; install 7-Zip (7z.exe) or put 7z on PATH, "
                  + "or pick the BtbN / nonfree source which ships a .zip"
                : "a .7z archive needs an extractor; install p7zip (7z / 7za / 7zz on PATH), "
                  + "or pick the BtbN / nonfree source");

        (int code, string output) = RunLong(sevenZip,
            ["x", "-y", "-bso0", "-bsp0", $"-o{destination}", archive], null);
        return code == 0 ? (true, null) : (false, $"7z exited with {code}: {Tail(output)}");
    }

    private static string? FindSevenZip()
    {
        string[] names = ["7z", "7za", "7zz"];
        foreach (string name in names)
        {
            string? found = FindOnPath(name);
            if (found is not null) return found;
        }

        if (!OperatingSystem.IsWindows()) return null;

        // Only meaningful on Windows; GetFolderPath gives "" elsewhere and the
        // join would produce a path that never exists anyway.
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe"),
        ];
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Archives differ: BtbN puts the binaries in &lt;name&gt;/bin, gyan at the
    /// root, avbuild nests a couple of levels. Take the shallowest directory
    /// that actually holds ffmpeg.exe.
    /// </summary>
    private static (string? Dir, string? Error) FindFfmpegDir(string root)
    {
        string exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var matches = new List<string>();
        try
        {
            foreach (string path in Directory.EnumerateFiles(root, exe, SearchOption.AllDirectories))
            {
                matches.Add(Path.GetDirectoryName(path)!);
            }
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return (null, $"could not scan the extracted archive: {exc.Message}");
        }

        if (matches.Count == 0) return (null, "no ffmpeg binary inside the downloaded archive");

        return (matches
            .OrderBy(d => d.Count(c => c == Path.DirectorySeparatorChar))
            .ThenBy(d => d.Length)
            .First(), null);
    }

    /// <summary>
    /// Confirm the downloaded file really is the archive we asked for. Returns
    /// null when it is, otherwise a message naming the likely cause.
    /// </summary>
    private static string? VerifyArchive(string file, ArchiveKind kind)
    {
        byte[] head = new byte[8];
        int read;
        try
        {
            using FileStream stream = File.OpenRead(file);
            read = stream.Read(head, 0, head.Length);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return $"could not read the downloaded file: {exc.Message}";
        }

        bool ok = kind switch
        {
            ArchiveKind.Zip => read >= 2 && head[0] == 'P' && head[1] == 'K',
            // .tar.xz: the xz container starts FD 37 7A 58 5A 00.
            ArchiveKind.TarXz => read >= 6 && head[0] == 0xFD && head[1] == 0x37
                                 && head[2] == 0x7A && head[3] == 0x58 && head[4] == 0x5A
                                 && head[5] == 0x00,
            _ => read >= 6 && head[0] == 0x37 && head[1] == 0x7A && head[2] == 0xBC
                  && head[3] == 0xAF && head[4] == 0x27 && head[5] == 0x1C,
        };
        if (ok) return null;

        bool looksHtml = read >= 1 && (head[0] == '<' || head[0] == 0xEF);
        return looksHtml
            ? "the server returned an HTML page instead of the archive -- usually an "
              + "interstitial or a blocked request; try again, or use --proxy"
            : $"the downloaded file is not a valid {kind} archive";
    }

    /// <summary>
    /// Publish the extracted directory into <c>&lt;source&gt;/bin</c>. Existing
    /// files are overwritten, which is what "re-download the daily build" means.
    /// </summary>
    private static void CopyInto(string bin, string sourceDir)
    {
        foreach (string file in Directory.EnumerateFiles(sourceDir))
        {
            string name = Path.GetFileName(file);
            if (!IsToolchainFile(name)) continue;
            string target = Path.Combine(bin, name);
            File.Copy(file, target, overwrite: true);
            // File.Copy does not carry the mode across. On Unix a freshly
            // copied ffmpeg would land as 0644 and then fail to exec, so the
            // owner execute bit has to be restored explicitly.
            MakeExecutable(target, name);
        }
    }

    private static void MakeExecutable(string path, string name)
    {
        if (OperatingSystem.IsWindows()) return;
        if (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return;
        if (name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase)) return;
        if (name.StartsWith("COPYING", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            File.SetUnixFileMode(path,
                File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute
                                           | UnixFileMode.OtherExecute);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException
                                        or PlatformNotSupportedException)
        {
            // Non-executable toolchain is a hard failure, but it surfaces
            // clearly at launch; do not abort the whole install here.
        }
    }

    /// <summary>
    /// Executables and their licences, nothing else -- no docs, no headers.
    /// On Unix the tools have no extension at all (ffmpeg, ffprobe, ffplay), so
    /// extension-less files count as binaries there.
    /// </summary>
    private static bool IsToolchainFile(string name)
    {
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("LICENSE.txt", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("COPYING", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return true;

        return !OperatingSystem.IsWindows()
               && !name.Contains('.')
               && name is "ffmpeg" or "ffprobe" or "ffplay";
    }

    /// <summary>
    /// Long-running child with streamed output. No wall-clock cap: a multi-GB
    /// download must not be killed by a fixed timeout, and Ctrl+C still tears
    /// the child down with the console.
    /// </summary>
    private static (int Code, string Output) RunLong(
        string exe, IReadOnlyList<string> args, Action<string>? log)
    {
        var info = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in args) info.ArgumentList.Add(a);

        using Process? proc = Process.Start(info);
        if (proc is null) return (-1, $"could not start {exe}");

        var tail = new System.Text.StringBuilder();
        var tailGate = new Lock();
        void Pipe(StreamReader reader)
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                log?.Invoke(line);
                lock (tailGate)
                {
                    if (tail.Length < 4000)
                    {
                        tail.Append(line).Append('\n');
                        if (tail.Length > 4000) tail.Remove(0, tail.Length - 4000);
                    }
                }
            }
        }

        Task outTask = Task.Run(() => Pipe(proc.StandardOutput));
        Task errTask = Task.Run(() => Pipe(proc.StandardError));
        Task.WaitAll(outTask, errTask);

        proc.WaitForExit();
        return (proc.ExitCode, tail.ToString());
    }

    private static string? FindOnPath(string name)
    {
        string exeName = OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name + ".exe"
            : name;

        string? envPath = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(envPath)) return null;

        foreach (string dir in envPath.Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            try
            {
                string full = Path.Combine(dir.Trim('"'), exeName);
                if (File.Exists(full)) return full;
            }
            catch { /* invalid PATH entry */ }
        }
        return null;
    }

    private static string Head(string text, int max) =>
        text.Length <= max ? text : text[..max];

    private static string Tail(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[^300..];
    }
}
