using System.IO.Compression;
using System.Net;
using System.Net.Http;
using Lae.Core;
using Lae.Core.Localization;
using Lae.Core.Toolchain;

namespace Lae.Media.Toolchain;

/// <summary>What an install attempt ended in.</summary>
public sealed record InstallOutcome
{
    public required bool Ok { get; init; }

    /// <summary>Why it failed, as a sentence for the user, or null.</summary>
    public string? Error { get; init; }

    /// <summary>The first line <c>ffmpeg -version</c> printed, when it could be asked.</summary>
    public string? Version { get; init; }
}

/// <summary>One distribution that is installed right now.</summary>
public sealed record InstalledToolchain
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public required string Ffmpeg { get; init; }

    public string? Ffprobe { get; init; }

    public string? Version { get; init; }
}

/// <summary>
/// Fetches, unpacks and removes FFmpeg builds.
///
/// <para>Everything is staged first and published second: a download is written
/// into <c>ffmpeg/.staging/&lt;id&gt;</c>, unpacked there, checked, and only then
/// copied into <c>ffmpeg/&lt;id&gt;/bin</c>. A failed download therefore leaves the
/// previous build untouched, which matters because "re-download today's snapshot"
/// is a normal thing to do and losing a working toolchain to a dropped connection
/// would be a poor trade.</para>
///
/// <para>No <c>aria2c</c>. v7 shelled out to it for sixteen connections, then
/// fell back to one anyway whenever it was absent, blocked, or pointed at a host
/// that disliked the connection count -- which is most of the time on the machines
/// this tool runs on. One implementation that always works beats two where the
/// faster one is occasionally the reason a download fails.</para>
///
/// <para>Every failure comes back as a sentence in the caller's language. The keys
/// are <c>toolchain.error.*</c>; nothing here concatenates a sentence together,
/// because word order is not portable.</para>
/// </summary>
public sealed class ToolchainInstaller(string configRoot, IMessages messages)
{
    /// <summary>The raw file name inside the staging folder. The extension must survive: it is how the unpacker is chosen.</summary>
    private const string DownloadName = "download";

    private readonly string _root = configRoot;
    private readonly IMessages _messages = messages;

    /// <summary>Every distribution whose binaries are present, with the version it reports.</summary>
    public IReadOnlyList<InstalledToolchain> Installed()
    {
        var found = new List<InstalledToolchain>();

        foreach (FfmpegDist dist in FfmpegCatalog.Available)
        {
            string ffmpeg = ToolchainLayout.Tool(_root, dist.Id, "ffmpeg");
            if (!File.Exists(ffmpeg)) continue;

            string probe = ToolchainLayout.Tool(_root, dist.Id, "ffprobe");
            found.Add(new InstalledToolchain
            {
                Id = dist.Id,
                Label = dist.Label,
                Ffmpeg = ffmpeg,
                Ffprobe = File.Exists(probe) ? probe : null,
                Version = VersionOf(ffmpeg),
            });
        }

        return found;
    }

    /// <summary>The executable an install would provide, or null when it is not there.</summary>
    public string? FfmpegFor(string id)
    {
        string path = ToolchainLayout.Tool(_root, id, "ffmpeg");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Install one distribution, from the network or from an archive the user
    /// already fetched.
    ///
    /// <paramref name="localArchive"/> exists for a machine that cannot reach
    /// GitHub at all: download it elsewhere, hand over the path, done. The
    /// catalogue is not consulted for the archive kind in that case -- a file name
    /// is the only evidence available, and refusing a correct .zip because the
    /// table says .tar.xz would be silly.
    /// </summary>
    public InstallOutcome Install(
        FfmpegDist dist,
        string? localArchive,
        string? proxy,
        string? front,
        Action<string>? log,
        CancellationToken token)
    {
        ArchiveKind kind = dist.Archive;
        if (localArchive.HasText)
        {
            if (!File.Exists(localArchive)) return Fail("toolchain.error.archiveMissing", new { path = localArchive });

            if (FfmpegCatalog.KindOfArchive(localArchive) is not { } detected)
                return Fail("toolchain.error.notAnArchiveName", new { path = localArchive });

            kind = detected;
        }

        string staging = ToolchainLayout.Staging(_root, dist.Id);
        string bin = ToolchainLayout.Bin(_root, dist.Id);

        try
        {
            ResetStaging(staging);
            string unpacked = Path.Combine(staging, "unpacked");
            Directory.CreateDirectory(unpacked);

            string archive = Path.Combine(staging, DownloadName + ExtensionOf(kind));

            if (localArchive.HasText)
            {
                log?.Invoke(_messages.Format("toolchain.log.archive", new { path = localArchive }));
                File.Copy(localArchive, archive, overwrite: true);
            }
            else
            {
                string url = GitHubFront.Apply(dist.Url, front);
                log?.Invoke(_messages.Format("toolchain.log.url", new { url }));

                if (Download(url, archive, proxy, log, token) is { } downloadError) return downloadError;
            }

            // A downloader will happily save a 403 page and call it done. Check the
            // first bytes before the unpacker produces a far more confusing error.
            if (Verify(archive, kind) is { } archiveError) return archiveError;

            log?.Invoke(_messages["toolchain.log.unpacking"]);
            if (Unpack(archive, unpacked) is { } unpackError) return unpackError;

            if (FindBinaries(unpacked) is not { } source)
                return Fail("toolchain.error.noFfmpegInside");

            Directory.CreateDirectory(bin);
            Publish(source, bin);

            string ffmpeg = ToolchainLayout.Tool(_root, dist.Id, "ffmpeg");
            if (!File.Exists(ffmpeg))
                return Fail("toolchain.error.finishedWithoutFfmpeg", new { path = bin });

            return new InstallOutcome { Ok = true, Version = VersionOf(ffmpeg) };
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException
                                        or NotSupportedException or ArgumentException)
        {
            // One place for the "the machine would not let us" family, so an
            // install never ends in a stack trace. The exception's own type and
            // sentence stay: they are what says whether it was permissions, a full
            // disk or a name the filesystem would not take.
            return Fail("toolchain.error.machineRefused", new
            {
                type = exc.GetType().Name,
                detail = exc.Message,
            });
        }
        finally
        {
            ResetStaging(staging);
        }
    }

    /// <summary>
    /// Remove an install. Reports rather than ignores a busy folder: on Windows
    /// that means a process is still running from it, and saying so is the whole
    /// value of the command.
    /// </summary>
    public string? Remove(string id)
    {
        string home = ToolchainLayout.Home(_root, id);
        if (!Directory.Exists(home)) return _messages.Format("toolchain.error.notInstalled", new { id });

        try
        {
            Directory.Delete(home, recursive: true);
            return null;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return _messages.Format("toolchain.error.removeFailed", new { path = home, detail = exc.Message });
        }
    }

    /// <summary>
    /// The first line of <c>ffmpeg -version</c>, or null. Local, short, and never
    /// fatal: this runs after a good install and during a listing, and neither is
    /// worth failing over a version string.
    /// </summary>
    public static string? VersionOf(string ffmpeg)
    {
        try
        {
            using ChildProcess child = ChildProcess.Start(
                [ffmpeg, "-hide_banner", "-version"],
                new ChildSpec { ReadOutput = true, DiagnosticLines = 8 });

            using var text = new MemoryStream();
            child.Output!.CopyTo(text);
            child.WaitOrCancel(TimeSpan.FromSeconds(30), "ffmpeg -version did not answer", CancellationToken.None);

            string output = System.Text.Encoding.UTF8.GetString(text.ToArray());
            return output.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0);
        }
        catch (Exception exc) when (exc is IOException or InvalidOperationException
                                        or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ExtensionOf(ArchiveKind kind)
        => kind == ArchiveKind.Zip ? ".zip" : ".tar.xz";

    private InstallOutcome Fail(string key, object? values = null)
        => new() { Ok = false, Error = values is null ? _messages[key] : _messages.Format(key, values) };

    private static void ResetStaging(string staging)
    {
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            // Leftovers are only ever clutter: the publish step overwrites the
            // files it cares about, and a staging folder that cannot be cleared
            // will fail loudly at the next create instead.
        }
    }

    /// <summary>Fetch into place, with a progress line and an idle deadline.</summary>
    private InstallOutcome? Download(
        string url, string destination, string? proxy, Action<string>? log, CancellationToken token)
    {
        // The transfer lives in HttpDownload, which the NNEDI weights fetch also
        // uses: two copies of an idle deadline is two copies of the same
        // off-by-one-window bug, and only one of them gets fixed.
        string? error = HttpDownload.Fetch(url, destination, proxy, _messages, log, token);
        return error is null ? null : new InstallOutcome { Ok = false, Error = error };
    }
    /// <summary>
    /// Confirm the downloaded bytes really are the archive asked for, by their
    /// magic number. Returns null when they are, otherwise a sentence naming the
    /// likely cause -- which is what turns "InvalidDataException: The archive
    /// entry ..." into "the server answered with a web page".
    /// </summary>
    private InstallOutcome? Verify(string path, ArchiveKind kind)
    {
        byte[] head = new byte[8];
        int read;

        try
        {
            using FileStream stream = File.OpenRead(path);
            read = stream.Read(head, 0, head.Length);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return new InstallOutcome { Ok = false, Error = exc.Message };
        }

        bool ok = kind switch
        {
            ArchiveKind.Zip => read >= 2 && head[0] == 'P' && head[1] == 'K',

            // The xz container starts FD 37 7A 58 5A 00.
            _ => read >= 6 && head[0] == 0xFD && head[1] == 0x37 && head[2] == 0x7A
                      && head[3] == 0x58 && head[4] == 0x5A && head[5] == 0x00,
        };

        if (ok) return null;

        bool looksLikeHtml = read >= 1 && (head[0] == '<' || head[0] == 0xEF);
        return looksLikeHtml
            ? Fail("toolchain.error.htmlInsteadOfArchive")
            : Fail("toolchain.error.notAnArchive", new { kind = kind.ToString() });
    }

    private InstallOutcome? Unpack(string archive, string destination)
    {
        if (archive.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase)
            || archive.EndsWith(".txz", StringComparison.OrdinalIgnoreCase))
        {
            string? tar = FindOnPath("tar");
            if (tar is null) return Fail("toolchain.error.noTar");

            using ChildProcess child = ChildProcess.Start(
                [tar, "-xJf", archive, "-C", destination],
                new ChildSpec { DiagnosticLines = 32 });

            child.WaitOrCancel(
                TimeSpan.FromMinutes(10), _messages["toolchain.error.tarTimeout"], CancellationToken.None);

            return child.ExitCode == 0
                ? null
                : Fail("toolchain.error.tarFailed", new
                {
                    code = child.ExitCode,
                    detail = child.Diagnostics(4),
                });
        }

        try
        {
            ZipFile.ExtractToDirectory(archive, destination, overwriteFiles: true);
            return null;
        }
        catch (Exception exc) when (exc is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Fail("toolchain.error.zipFailed", new { detail = exc.Message });
        }
    }

    /// <summary>
    /// Archives differ in where they put the binaries: BtbN nests them under
    /// <c>&lt;name&gt;/bin</c>, a hand-made one may have them at the root. Take the
    /// shallowest folder that actually holds an ffmpeg.
    /// </summary>
    private static string? FindBinaries(string root)
    {
        string name = ToolchainLayout.ToolName("ffmpeg");

        try
        {
            return Directory
                .EnumerateFiles(root, name, SearchOption.AllDirectories)
                .Select(path => Path.GetDirectoryName(path))
                .OfType<string>()
                .OrderBy(folder => folder.Count(c => c == Path.DirectorySeparatorChar))
                .ThenBy(folder => folder.Length)
                .FirstOrDefault();
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Copy the executables and the licence out of the unpacked tree. Docs and
    /// headers are left behind: they are a few megabytes of nothing.
    /// </summary>
    private static void Publish(string from, string bin)
    {
        foreach (string path in Directory.EnumerateFiles(from))
        {
            string name = Path.GetFileName(path);
            if (!IsToolchainFile(name)) continue;

            string target = Path.Combine(bin, name);
            File.Copy(path, target, overwrite: true);

            // Copying does not carry the mode across, and on Unix a freshly copied
            // ffmpeg lands as 0644 and then fails to execute. Restoring the execute
            // bit is what makes the difference between an install and a mystery.
            MakeExecutable(target, name);
        }
    }

    private static bool IsToolchainFile(string name)
    {
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("LICENSE.txt", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("COPYING", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return true;

        return !OperatingSystem.IsWindows() && name is "ffmpeg" or "ffprobe" or "ffplay";
    }

    private static void MakeExecutable(string path, string name)
    {
        if (OperatingSystem.IsWindows()) return;
        if (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return;
        if (name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase)) return;
        if (name.StartsWith("COPYING", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            File.SetUnixFileMode(
                path,
                File.GetUnixFileMode(path)
                | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException
                                        or PlatformNotSupportedException)
        {
            // The consequence shows up as "cannot execute", which is a clear
            // enough failure; aborting the install here would be worse.
        }
    }

    /// <summary>
    /// Scan <c>PATH</c> by hand. <c>tar</c> is the only thing this needs, and it
    /// is not optional on the platform that ships <c>.tar.xz</c>.
    /// </summary>
    private static string? FindOnPath(string tool)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is null) return null;

        foreach (string entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string folder;
            try
            {
                folder = entry.Trim().Trim('"');
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (folder.Length == 0) continue;

            try
            {
                string candidate = Path.Combine(folder, tool);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                continue;
            }
        }

        return null;
    }
}


