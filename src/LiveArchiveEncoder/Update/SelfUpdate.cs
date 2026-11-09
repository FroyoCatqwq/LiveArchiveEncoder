using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.FFmpeg;

namespace LiveArchiveEncoder.Update;

/// <summary>One downloadable file on a release.</summary>
public sealed class ReleaseAsset
{
    public required string Name { get; init; }
    public long Size { get; init; }

    /// <summary>
    /// Lowercase hex SHA-256 as GitHub records it at upload time
    /// (<c>digest: "sha256:..."</c>), or null for a release uploaded before
    /// GitHub started filling that field in.
    /// </summary>
    public string? Sha256 { get; init; }

    public required string Url { get; init; }
}

/// <summary>A release, reduced to what an updater needs.</summary>
public sealed class Release
{
    public required string Tag { get; init; }
    public required VersionTag Version { get; init; }
    public string? Title { get; init; }
    public string? Notes { get; init; }
    public string? PageUrl { get; init; }
    public required List<ReleaseAsset> Assets { get; init; }

    /// <summary>
    /// v7.9.9.5: VersionTag deliberately drops a `-rc1` / `+build` suffix for
    /// comparison, so without this flag a pre-release would compare as newer
    /// than the stable build and be installed over it.
    /// </summary>
    public bool Prerelease { get; init; }
}

public enum Outcome
{
    /// <summary>The running build is the newest one the feed knows about.</summary>
    UpToDate,

    /// <summary>New files were put in place; the caller must exit so they can be swapped in.</summary>
    Staged,

    Failed,
}

/// <summary>What an update attempt did, for the caller to phrase.</summary>
public sealed class UpdateReport
{
    public Outcome Kind { get; init; }
    public string? Error { get; init; }

    /// <summary>The tag that was installed, when one was.</summary>
    public string? Tag { get; init; }

    /// <summary>The asset that was fetched, when one was.</summary>
    public string? Asset { get; init; }

    /// <summary>True when the replacement will relaunch the program afterwards.</summary>
    public bool Restarting { get; init; }

    public Release? Release { get; init; }
}

/// <summary>
/// Updating the program from its own GitHub releases.
///
/// <para>Two halves. <see cref="Install"/> runs inside the program that is about
/// to be replaced: it checks the feed, downloads, verifies and unpacks into a
/// staging directory beside the executable, then starts the <em>new</em> build
/// with <c>--self-update-apply</c>. <see cref="ApplySelf"/> is that new build,
/// running from staging: it waits for its parent to exit (Windows cannot replace
/// a running image), copies itself into place, and optionally relaunches.</para>
///
/// <para>Why a second process at all: a file that is mapped as an executable
/// image is locked on Windows, so the process that replaces it cannot be the one
/// being replaced. Doing the swap from staging also gives the update a natural
/// no-op rollback -- until the move succeeds, the installed build is untouched,
/// and the staging directory stays behind if the move fails.</para>
///
/// <para>What this deliberately does not do: keep a backup of the previous
/// binary. A release asset is 4-10 MB and re-downloading an older tag is a
/// one-command operation, so an <c>.old</c> file next to the executable would be
/// permanent clutter bought with disk space that nobody asked for.</para>
/// </summary>
public static class SelfUpdate
{
    /// <summary>
    /// The repository this build updates from.
    ///
    /// <para>v7.9.8.7: this was <c>fr0yo0v0/LiveArchiveEncoder</c>, the account the
    /// repository used to live under. That account was suspended and the API
    /// answers 404 for everything on it, so every update check failed with "the
    /// release feed did not return a JSON object" and there was nothing the user
    /// could do about it. The live repository is <c>FroyoCatqwq/LiveArchiveEncoder</c>.</para>
    /// </summary>
    public const string Repo = "FroyoCatqwq/LiveArchiveEncoder";

    /// <summary>The release feed, GitHub's own shape.</summary>
    public const string PublicFeed = "https://api.github.com/repos/" + Repo + "/releases/latest";

    /// <summary>How long a successful check is trusted before a start asks again.</summary>
    public static readonly TimeSpan CheckTtl = TimeSpan.FromHours(24);

    /// <summary>Prefix of the staging directories; see <see cref="CleanupStaging"/>.</summary>
    private const string StagingPrefix = ".update-";

    /// <summary>The version this process is.</summary>
    public static string Current => Constants.Version;

    public static VersionTag CurrentTag => VersionTag.Parse(Current) ?? VersionTag.Zero;

    /// <summary>The directory the running executable lives in.</summary>
    public static string InstallDirectory()
    {
        string? exe = Environment.ProcessPath;
        if (exe.HasText)
        {
            string? dir = Path.GetDirectoryName(exe);
            if (dir.HasText) return dir!;
        }
        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    }

    /// <summary>Explicit override, then <c>LAE_UPDATE_FEED</c>, then GitHub.</summary>
    public static string FeedOr(string? over)
    {
        if (over.HasText) return over.Trim();
        string? env = Environment.GetEnvironmentVariable("LAE_UPDATE_FEED");
        return env.HasText ? env.Trim() : PublicFeed;
    }

    /// <summary>
    /// True when the feed is not GitHub's own. A front rewrites GitHub URLs into
    /// its own, so prefixing a mirror's URL would only break it -- and probing a
    /// mirror for "is GitHub reachable" answers the wrong question.
    /// </summary>
    public static bool IsCustomFeed(string? over) =>
        !string.Equals(FeedOr(over), PublicFeed, StringComparison.Ordinal);

    /// <summary>
    /// True when this process carries its own .NET runtime in its own directory.
    /// A framework-dependent publish contains only the app dll, deps.json and
    /// runtimeconfig.json; a self-contained one also has coreclr.dll
    /// (libcoreclr.so on Linux) and System.Private.CoreLib.dll. NativeAOT is a
    /// separate case and is decided before this is consulted.
    /// </summary>
    public static bool IsSelfContained(string? directory = null)
    {
        string dir = directory ?? AppContext.BaseDirectory;
        return File.Exists(Path.Combine(dir, "System.Private.CoreLib.dll"))
            || File.Exists(Path.Combine(dir, "coreclr.dll"))
            || File.Exists(Path.Combine(dir, "libcoreclr.so"));
    }

    /// <summary>
    /// Which asset this build needs: <c>-win-x64.zip</c>,
    /// <c>-win-x64-self-contained.zip</c>, <c>-win-x64-aot.zip</c> and friends.
    ///
    /// <para>v7.9.8.12: a self-contained JIT build still supports dynamic code,
    /// so <c>IsDynamicCodeSupported</c> alone used to select the
    /// framework-dependent archive and leave the install without a runtime.
    /// The suffix now names all three flavours explicitly.</para>
    /// </summary>
    public static string AssetSuffix()
    {
        string os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsMacOS() ? "osx"
            : "unknown";

        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => "unknown",
        };

        bool aot = !RuntimeFeature.IsDynamicCodeSupported;
        string flavour = aot ? "-aot" : IsSelfContained() ? "-self-contained" : "";
        string extension = OperatingSystem.IsWindows() ? "zip" : "tar.gz";
        return $"-{os}-{arch}{flavour}.{extension}";
    }

    /// <summary>A human name for the flavour, for messages.</summary>
    public static string Flavour() =>
        (!RuntimeFeature.IsDynamicCodeSupported ? "AOT"
            : IsSelfContained() ? "JIT / self-contained" : "JIT / framework-dependent")
        + " / " + (OperatingSystem.IsWindows() ? "Windows"
            : OperatingSystem.IsLinux() ? "Linux"
            : OperatingSystem.IsMacOS() ? "macOS" : "unknown")
        + " / " + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    // ---- reading the feed ----

    /// <summary>
    /// Query the feed and record what it said. One function so the command line
    /// and the shell cannot disagree about the cache.
    /// </summary>
    public static (Release? Release, string? Error) Check(
        string? feed, string? proxy, string? front,
        int timeoutSeconds = 12, Action<string>? log = null)
    {
        (Release? release, string? error) = Latest(feed, proxy, front, timeoutSeconds, log);

        UpdateState state = UpdateState.Load();
        state.CheckedAt = DateTimeOffset.Now;
        state.Result = error is null ? "ok" : "offline";
        if (release is not null) state.Seen = release.Tag;
        UpdateState.Save(state);

        return (release, error);
    }

    public static (Release? Release, string? Error) Latest(
        string? feed, string? proxy, string? front,
        int timeoutSeconds = 12, Action<string>? log = null)
    {
        // A front re-issues the request from its own side, so the Authorization
        // header never arrives -- measured 2026-10-28: the same feed answers 404
        // through gh-proxy.com with a token attached and 200 without the front.
        // A token therefore wins over a front rather than silently 404ing.
        bool token = HasToken;
        if (token && front.HasText)
        {
            log?.Invoke("A token is set, so the GitHub front is bypassed "
                + "(a front cannot forward credentials).");
            front = null;
        }

        string url = GitHubProxy.Apply(FeedOr(feed), front);
        log?.Invoke($"Feed: {url}");

        (string? body, string? error) = Fetch(url, proxy, timeoutSeconds);
        return body is null ? (null, error) : Parse(body);
    }

    /// <summary>True when <c>LAE_UPDATE_TOKEN</c> is set; never stored, never logged.</summary>
    public static bool HasToken => Environment.GetEnvironmentVariable("LAE_UPDATE_TOKEN").HasText;

    /// <summary>The token itself, trimmed, or null. Read at every use rather than cached.</summary>
    private static string? Token => Environment.GetEnvironmentVariable("LAE_UPDATE_TOKEN")?.Trim();

    /// <summary>
    /// Parse a GitHub release document. Separated from the request so the whole
    /// decision path can be tested against a string.
    /// </summary>
    public static (Release? Release, string? Error) Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exc)
        {
            return (null, $"the release feed is not valid JSON: {exc.Message}");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, "the release feed did not return a JSON object");

            string? tag = Text(root, "tag_name");
            if (VersionTag.Parse(tag) is not { } version)
                return (null, $"the release feed has no usable tag_name ('{tag ?? "none"}')");

            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out JsonElement list)
                && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    string? name = Text(item, "name");
                    string? url = Text(item, "browser_download_url");
                    if (!name.HasText || !url.HasText) continue;
                    // A name that carries a path separator would be used as a file
                    // name later; refuse it here rather than sanitising it into a
                    // different asset.
                    // v7.9.9.5: a feed is untrusted JSON. Drive-relative names,
                    // ADS colons, dot segments and absurd lengths must not
                    // become local paths either (they are cleaned again below).
                    if (name.Contains('/') || name.Contains('\\') || name.Contains(':')
                        || name is "." or ".." || name.Length > 256
                        || name.Trim().Length == 0) continue;

                    assets.Add(new ReleaseAsset
                    {
                        Name = name,
                        Url = url!,
                        Size = Number(item, "size"),
                        Sha256 = Digest(item),
                    });
                }
            }

            return (new Release
            {
                Tag = tag!.Trim(),
                Version = version,
                Title = Text(root, "name"),
                Notes = Text(root, "body"),
                PageUrl = Text(root, "html_url"),
                Assets = assets,
                Prerelease = tag!.Trim().TrimStart('v', 'V').AsSpan()
                    .IndexOfAny(LiveArchiveEncoder.Core.CharSets.VersionSuffix) >= 0,
            }, null);
        }
    }

    /// <summary>The asset this build would use, or null when the release has none for it.</summary>
    public static ReleaseAsset? Pick(Release release, string? suffix = null)
    {
        string want = suffix ?? AssetSuffix();
        return release.Assets.FirstOrDefault(
            a => a.Name.EndsWith(want, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Download, verify, unpack, verify again, then hand over to the new build.
    ///
    /// <para>The order is the point: nothing in the install directory is touched
    /// until the archive has the hash the release claims <em>and</em> the binary
    /// inside it has answered <c>--version</c> with something at least as new as
    /// what is running. A corrupt download, a truncated archive, a wrong-arch
    /// binary and a stale tag all fail there, with the working program still in
    /// place.</para>
    /// </summary>
    public static UpdateReport Install(
        string? feed, string? proxy, string? front, bool restart,
        Action<string>? log = null)
    {
        void Say(string line) => log?.Invoke(line);

        string installDir = InstallDirectory();
        (Release? release, string? error) = Check(feed, proxy, front, 12, log);
        if (release is null)
            return new UpdateReport { Kind = Outcome.Failed, Error = error };

        if (!release.Version.IsNewerThan(CurrentTag))
        {
            Say($"{release.Tag} is not newer than {Current}.");
            return new UpdateReport { Kind = Outcome.UpToDate, Tag = release.Tag, Release = release };
        }

        // v7.9.9.5: VersionTag.Parse drops a -rc1/+build suffix, so an RC
        // tag with the same numeric part would otherwise replace a stable
        // installation as if it were a later release.
        if (release.Prerelease)
        {
            return new UpdateReport
            {
                Kind = Outcome.Failed, Tag = release.Tag, Release = release,
                Error = $"'{release.Tag}' is a pre-release tag; it will not replace a stable install",
            };
        }

        if (Pick(release) is not { } asset)
        {
            string have = release.Assets.Count == 0
                ? "it has no assets"
                : "it has " + string.Join(", ", release.Assets.Select(a => a.Name));
            return new UpdateReport
            {
                Kind = Outcome.Failed,
                Tag = release.Tag,
                Release = release,
                Error = $"release {release.Tag} has no asset for this build "
                    + $"('{AssetSuffix()}'); {have}",
            };
        }

        // A feed is somebody else's JSON. A relative or garbled download URL used
        // to reach HttpClient as-is and surface as an unhandled
        // InvalidOperationException, which is a crash where an error belongs.
        // v7.9.8.10: the asset URL comes from the same untrusted JSON as the
        // rest of the release. Only https is accepted for a real download;
        // loopback http is kept for the local-feed e2e harness. Plain http to a
        // remote host would let anything on the wire replace the binary that
        // this process is about to execute.
        if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out Uri? assetUri)
            || (assetUri.Scheme != Uri.UriSchemeHttp && assetUri.Scheme != Uri.UriSchemeHttps)
            || (assetUri.Scheme == Uri.UriSchemeHttp && !assetUri.IsLoopback))
        {
            return Fail(release, asset,
                $"the feed gives '{asset.Url}' as the download URL for {asset.Name}; "
                + "only https (or a loopback http URL for local tests) is accepted");
        }

        string staging = Path.Combine(installDir, StagingPrefix + SafeName(release.Tag));
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return new UpdateReport
            {
                Kind = Outcome.Failed, Tag = release.Tag, Release = release,
                Error = $"cannot write next to the program ({installDir}): {exc.Message}",
            };
        }

        // Downloaded under a fixed .part name so an interrupted update resumes,
        // then renamed once it is proven good: the staging directory is worth
        // being able to read by hand when something goes wrong, and
        // "LiveArchiveEncoder-v9.9.9-win-x64.zip.part" says what it is.
        // v7.9.9.5: never use the feed's name verbatim as a local file name.
        string archive = Path.Combine(staging, SafeAssetName(asset.Name));
        string partial = archive + ".part";
        Say($"Downloading {asset.Name} ({asset.Size:N0} bytes)...");
        (bool downloaded, string? downloadError) =
            Toolchain.DownloadFile(asset.Url, partial, proxy, log, Token);
        if (!downloaded)
            return Fail(release, asset, $"download failed: {downloadError}");

        if (asset.Sha256.HasText)
        {
            string actual = Sha256(partial);
            if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(partial);
                return Fail(release, asset,
                    $"the download does not match the release digest "
                    + $"(expected {asset.Sha256![..16]}..., got {actual[..16]}...)");
            }
            Say($"SHA-256 ok: {actual}");
        }
        else
        {
            // v7.9.8.10: size is just another field from the same feed that
            // supplied the URL. "Only size-checked" used to still unpack the
            // archive, run its --version and hand the program over to it, while
            // Program returned exit 0 so scripts thought the update succeeded.
            // An unverified binary is never executed.
            TryDelete(partial);
            return Fail(release, asset,
                "the release carries no SHA-256 digest; refusing to run an unverified binary. "
                + "Download this build by hand, or point --update-feed at a release "
                + "that publishes a digest.");
        }

        try
        {
            File.Move(partial, archive, overwrite: true);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return Fail(release, asset, $"could not finish the download: {exc.Message}");
        }

        string payload = Path.Combine(staging, "payload");
        (bool unpacked, string? unpackError) = Extract(archive, payload);
        if (!unpacked)
            return Fail(release, asset, $"could not unpack the archive: {unpackError}");

        if (FindExecutable(payload) is not { } stagedExe)
            return Fail(release, asset, "the archive contains no LiveArchiveEncoder executable");

        (bool runnable, string? runError) = VerifyStaged(stagedExe);
        if (!runnable)
            return Fail(release, asset, $"the downloaded build does not run: {runError}");

        string payloadDir = Path.GetDirectoryName(stagedExe)!;

        // Pre-flight only: can this directory be written at all? Nothing is
        // replaced from here -- the running image is what would be in the way.
        if (!Writable(installDir, out string? unwritable))
            return Fail(release, asset, unwritable);

        // The process that does the swap is the DOWNLOADED one, not this one and
        // not a second copy of this one: anything running from the install
        // directory has the very files that must be replaced mapped into it.
        (bool started, string? startError) = StartApplier(stagedExe, payloadDir, installDir, restart);
        if (!started)
            return Fail(release, asset, $"the new build is downloaded but could not be started: {startError}");

        Say(restart
            ? "Handing over to the new build; it will replace this one and restart."
            : "Handing over; the files are replaced as this program exits.");
        return new UpdateReport
        {
            Kind = Outcome.Staged,
            Tag = release.Tag,
            Asset = asset.Name,
            Release = release,
            Restarting = restart,
        };
    }

    private static UpdateReport Fail(Release release, ReleaseAsset? asset, string? error) => new()
    {
        Kind = Outcome.Failed,
        Error = error,
        Tag = release.Tag,
        Asset = asset?.Name,
        Release = release,
    };

    // ---- the half that runs from staging ----

    /// <summary>
    /// <c>--self-update-apply &lt;pid&gt; &lt;installDir&gt; &lt;payloadDir&gt; [--restart]</c>.
    ///
    /// <para>Runs in the freshly downloaded build, from the staging directory.
    /// Nothing here can use the old build's memory, which is the point: the file
    /// being replaced is unmapped the moment its process exits.</para>
    /// </summary>
    public static int Apply(string[] argv)
    {
        if (argv.Length < 4
            || !int.TryParse(argv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parentPid))
        {
            Console.Error.WriteLine("ERROR: --self-update-apply needs a pid, an install directory "
                + "and a payload directory");
            return 2;
        }

        string installDir = argv[2];
        string payloadDir = argv[3];
        bool restart = argv.Contains("--restart", StringComparer.Ordinal);

        // The parent is the build being replaced. Waiting for it is the whole
        // reason this is a separate process; a gone pid is the normal case when
        // the user closed the program first.
        try
        {
            using Process parent = Process.GetProcessById(parentPid);
            if (!parent.WaitForExit(120_000))
            {
                Console.Error.WriteLine($"ERROR: the running program (pid {parentPid}) did not exit");
                return 1;
            }
        }
        catch (Exception exc) when (exc is ArgumentException or InvalidOperationException)
        {
            // Already gone.
        }

        if (!TryReplace(payloadDir, installDir, out string? error))
        {
            Console.Error.WriteLine($"ERROR: could not put the new build in place: {error}");
            return 1;
        }

        // The staging directory is finished with. Marked rather than deleted:
        // this process is still executing from it, so on Windows the delete
        // fails anyway -- the guard in CleanupStaging lets the next start remove
        // it immediately instead of waiting out the ten-minute safety window.
        MarkDone(payloadDir);

        if (!restart) return 0;

        if (FindExecutable(installDir) is not { } exe)
        {
            Console.Error.WriteLine("ERROR: the update was applied but no executable was found "
                + $"in {installDir}");
            return 1;
        }

        try
        {
            // Inline, not shell-execute: a console program relaunched through the
            // shell gets a window of its own instead of this terminal.
            var info = new ProcessStartInfo(exe)
            {
                WorkingDirectory = installDir,
                UseShellExecute = false,
            };
            Process.Start(info);
            return 0;
        }
        catch (Exception exc) when (exc is InvalidOperationException
                                        or System.ComponentModel.Win32Exception
                                        or IOException)
        {
            Console.Error.WriteLine($"ERROR: could not restart {exe}: {exc.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Remove staging directories left by earlier updates.
    ///
    /// <para>Two cases. A directory with no marker may belong to an update that
    /// is still downloading or copying, and deleting it would turn a working
    /// update into a broken install -- so it waits ten minutes. A marked one has
    /// already been applied; it is removed straight away, retrying for a few
    /// seconds because the process that marked it is usually still exiting when
    /// the rewritten program starts. Failures are ignored: a read-only install
    /// directory must not stop the program from starting.</para>
    /// </summary>
    public static void CleanupStaging(string? installDir = null)
    {
        string dir = installDir ?? InstallDirectory();
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (string staging in Directory.EnumerateDirectories(dir, StagingPrefix + "*"))
            {
                bool finished = File.Exists(Path.Combine(staging, DoneMarker));
                if (!finished
                    && DateTime.UtcNow - Directory.GetCreationTimeUtc(staging) < TimeSpan.FromMinutes(10))
                {
                    continue;
                }

                // 15 × 200 ms: long enough for the process that wrote the marker
                // to finish exiting, short enough that a start never feels slow.
                for (int attempt = 0; attempt < 15; attempt++)
                {
                    try
                    {
                        Directory.Delete(staging, recursive: true);
                        break;
                    }
                    catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
                    {
                        if (!finished || attempt == 14) break;
                        Thread.Sleep(200);
                    }
                }
            }
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException
                                        or DirectoryNotFoundException)
        {
            // Same reasoning: housekeeping.
        }
    }

    /// <summary>Written into a staging directory once its files are in place.</summary>
    private const string DoneMarker = ".done";

    private static void MarkDone(string payloadDir)
    {
        try
        {
            string? staging = Path.GetDirectoryName(payloadDir);
            if (staging.HasText) File.WriteAllText(Path.Combine(staging!, DoneMarker), "");
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            // The ten-minute rule still gets it eventually.
        }
    }

    // ---- files ----

    /// <summary>
    /// Can this process write into the install directory?
    ///
    /// <para>A probe file, written and deleted: an install under Program Files or
    /// in another user's home should say so before a four-megabyte download, not
    /// after it. The running executable itself is not touched -- that is the one
    /// file this process cannot write, and the whole reason the swap happens in
    /// a second process.</para>
    /// </summary>
    private static bool Writable(string dir, out string? error)
    {
        error = null;
        string probe = Path.Combine(dir, ".update-probe");
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException
                                        or NotSupportedException)
        {
            error = $"the program's directory ({dir}) cannot be written to: {exc.Message}. "
                + "An update replaces files there; move the program somewhere writable, "
                + "or run this with the privileges that own that directory.";
            return false;
        }
    }

    /// <summary>
    /// Copy the payload over the install directory.
    ///
    /// <para>Each file lands as <c>name.new</c> first and is then moved onto the
    /// target. v7.9.8.10: the old claim that "a failure halfway leaves the old
    /// build complete" was false -- files 1..N-1 had already been overwritten and
    /// there was no backup to restore them from. Every target is now backed up
    /// before the swap and restored on any failure. Retries cover the Windows
    /// case where the just-exited process's image, an antivirus scanner or the
    /// shell's icon cache still holds the old file for a few hundred ms.</para>
    /// </summary>
    private static bool TryReplace(string payloadDir, string installDir, out string? error)
    {
        error = null;
        string[] names;
        try
        {
            // v7.9.8.10: copy every file in the payload, not just those whose
            // names start with the product. v7.9.9.3: recurse -- self-contained
            // / AOT payloads contain runtimes/, cs/, de/ ... subdirectories and
            // the old top-level-only walk silently dropped native libraries and
            // satellite resources, leaving an install that could not start.
            names = Directory
                .EnumerateFiles(payloadDir, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(payloadDir, p))
                .Where(n => n.HasText && !n.StartsWith("..", StringComparison.Ordinal))
                .ToArray();
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            error = exc.Message;
            return false;
        }

        if (names.Length == 0)
        {
            error = $"no files in {payloadDir}";
            return false;
        }

        // The rollback copies live next to the payload (NOT inside it), so a
        // later FindExecutable scanning the payload cannot mistake a saved old
        // exe for the downloaded one. The caller keeps the staging area on
        // failure, which is exactly where these copies are useful.
        string backup = Path.GetFullPath(Path.Combine(payloadDir, "..", ".rollback"));
        var replaced = new List<string>();
        var created = new List<string>();
        try { Directory.CreateDirectory(backup); }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            error = $"cannot create the rollback directory: {exc.Message}";
            return false;
        }

        foreach (string name in names)
        {
            string source = Path.Combine(payloadDir, name);
            string target = Path.Combine(installDir, name);
            string staged = target + ".new";

            try
            {
                // Relative paths may contain subdirectories now: build both
                // the install and the rollback tree before copying.
                string backupTarget = Path.Combine(backup, name);
                foreach (string? directory in new[]
                         { Path.GetDirectoryName(target), Path.GetDirectoryName(backupTarget) })
                {
                    if (directory is { Length: > 0 }) Directory.CreateDirectory(directory);
                }

                // Save the old file before the first overwrite. Without this a
                // failure on a later file left a mixed install that could not
                // start and had nothing to restore from.
                if (File.Exists(target))
                    File.Copy(target, backupTarget, overwrite: true);
                else
                    created.Add(name);   // v7.9.8.13.2: rollback removes it
                File.Copy(source, staged, overwrite: true);
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
            {
                error = $"cannot write {staged}: {exc.Message}";
                TryDelete(staged);
                Rollback(backup, installDir, replaced, created, ref error);
                return false;
            }

            bool moved = false;
            for (int attempt = 0; attempt < 6 && !moved; attempt++)
            {
                try
                {
                    File.Move(staged, target, overwrite: true);
                    moved = true;
                }
                catch (Exception exc) when (attempt < 5
                                            && exc is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(200 * (attempt + 1));
                }
                catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
                {
                    error = $"cannot replace {target}: {exc.Message}";
                    TryDelete(staged);
                    Rollback(backup, installDir, replaced, created, ref error);
                    return false;
                }
            }
            replaced.Add(name);

            if (!OperatingSystem.IsWindows() && !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.SetUnixFileMode(target,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                catch (Exception exc) when (exc is IOException or UnauthorizedAccessException
                                                or PlatformNotSupportedException)
                {
                    error = $"cannot make {target} executable: {exc.Message}";
                    Rollback(backup, installDir, replaced, created, ref error);
                    return false;
                }
            }
        }

        TryDeleteDirectory(backup);
        return true;
    }

    /// <summary>Put the old build back after a swap that failed halfway.</summary>
    private static void Rollback(string backup, string installDir,
        List<string> replaced, List<string> created, ref string? error)
    {
        // v7.9.8.13.2: a file the old install did not have must disappear on
        // rollback, otherwise a failed update leaves a mixed, unstartable
        // directory (new deps next to old binaries).
        foreach (string name in created)
        {
            try { File.Delete(Path.Combine(installDir, name)); }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
            {
                error += $" [rollback removal of {name} failed: {exc.Message}]";
            }
        }

        foreach (string name in replaced)
        {
            string saved = Path.Combine(backup, name);
            if (!File.Exists(saved)) continue;
            string target = Path.Combine(installDir, name);
            try
            {
                File.Copy(saved, target, overwrite: true);
                if (!OperatingSystem.IsWindows()
                    && !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    File.SetUnixFileMode(target,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException
                                            or PlatformNotSupportedException)
            {
                error += $" [rollback of {name} failed: {exc.Message}]";
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    /// <summary>Unpack a <c>.zip</c> or <c>.tar.gz</c>, refusing anything that would escape <paramref name="destination"/>.</summary>
    public static (bool Ok, string? Error) Extract(string archive, string destination)
    {
        try
        {
            Directory.CreateDirectory(destination);
            if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ExtractZip(archive, destination);
            else if (archive.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                     || archive.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
                ExtractTarGz(archive, destination);
            else
                return (false, $"unknown archive type: {Path.GetFileName(archive)}");
            return (true, null);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException
                                        or InvalidDataException or FormatException
                                        or NotSupportedException)
        {
            return (false, exc.Message);
        }
    }

    private static void ExtractZip(string archive, string destination)
    {
        using ZipArchive zip = ZipFile.OpenRead(archive);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            if (SafeTarget(destination, entry.FullName) is not { } target) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static void ExtractTarGz(string archive, string destination)
    {
        using FileStream file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        while (reader.GetNextEntry() is { } entry)
        {
            // Directories and regular files only. A symlink in an archive is the
            // classic way to write outside the extraction root, and this program
            // has no use for one.
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.Directory))
                continue;
            if (entry.Name.Length == 0) continue;
            if (SafeTarget(destination, entry.Name) is not { } target) continue;

            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>
    /// Resolve an archive entry against the extraction root, or null when it
    /// points outside it (absolute paths, <c>..</c>, drive letters).
    /// </summary>
    private static string? SafeTarget(string root, string name)
    {
        string relative = name.Replace('\\', '/').Trim();
        while (relative.StartsWith("./", StringComparison.Ordinal)) relative = relative[2..];
        relative = relative.TrimStart('/');
        if (relative.Length == 0) return null;
        if (relative.Contains(':')) return null;
        if (relative.Split('/').Any(part => part == "..")) return null;

        string full = Path.GetFullPath(Path.Combine(root, relative));
        string prefix = Path.GetFullPath(root);
        if (!prefix.EndsWith(Path.DirectorySeparatorChar)) prefix += Path.DirectorySeparatorChar;

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.StartsWith(prefix, comparison) ? full : null;
    }

    private static string? FindExecutable(string dir)
    {
        string name = OperatingSystem.IsWindows() ? "LiveArchiveEncoder.exe" : "LiveArchiveEncoder";
        string direct = Path.Combine(dir, name);
        if (File.Exists(direct)) return direct;

        try
        {
            return Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Ask the downloaded build what version it is.
    ///
    /// <para>This is the last gate before the swap, and it catches what a hash
    /// cannot: a binary for the wrong architecture, a JIT build on a machine with
    /// no runtime, an archive whose payload was never executable. The check is
    /// "runs, and is not older than what is running" rather than "equals the tag",
    /// so a feed that reports a tag it did not actually build cannot install a
    /// downgrade.</para>
    /// </summary>
    private static (bool Ok, string? Error) VerifyStaged(string exe)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(exe,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            var info = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            };
            info.ArgumentList.Add("--version");

            using Process process = Process.Start(info)
                ?? throw new InvalidOperationException("the process could not be started");

            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return (false, "it did not answer --version within 30 s");
            }

            string output = stdout.GetAwaiter().GetResult().Trim();
            string errors = stderr.GetAwaiter().GetResult().Trim();

            if (process.ExitCode != 0)
                return (false, $"--version exited with {process.ExitCode}: {FirstLine(errors)}");

            if (VersionTag.Parse(output) is not { } version)
                return (false, $"--version answered '{FirstLine(output)}', which is not a version");

            if (version.CompareTo(CurrentTag) < 0)
                return (false, $"it reports {version}, older than the running {Current}");

            return (true, null);
        }
        catch (Exception exc) when (exc is InvalidOperationException
                                        or System.ComponentModel.Win32Exception
                                        or IOException or UnauthorizedAccessException
                                        or PlatformNotSupportedException)
        {
            return (false, exc.Message);
        }
    }

    private static (bool Ok, string? Error) StartApplier(
        string exe, string payloadDir, string installDir, bool restart)
    {
        try
        {
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = payloadDir,
            };
            info.ArgumentList.Add("--self-update-apply");
            info.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add(installDir);
            info.ArgumentList.Add(payloadDir);
            if (restart) info.ArgumentList.Add("--restart");

            return Process.Start(info) is null
                ? (false, "the process could not be started")
                : (true, null);
        }
        catch (Exception exc) when (exc is InvalidOperationException
                                        or System.ComponentModel.Win32Exception
                                        or IOException)
        {
            return (false, exc.Message);
        }
    }

    // ---- plumbing ----

    /// <summary>A tag is used as a directory name, so anything but a plain name is folded out of it.</summary>
    private static string SafeName(string tag)
    {
        // v7.9.9.5: the tag comes from an untrusted release feed. Sizing a
        // stackalloc by its length let a multi-megabyte tag overflow the stack
        // -- a StackOverflowException cannot be caught, even by Program.Main's
        // catch-all, and the process died outside the exit-code contract.
        int length = Math.Min(tag.Length, 192);
        string cleaned = string.Create(length, tag, static (span, source) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                char ch = source[i];
                span[i] = char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_';
            }
        });
        return cleaned.Length == 0 ? "new" : cleaned;
    }

    /// <summary>A feed-supplied asset name reduced to a plain local filename.</summary>
    private static string SafeAssetName(string name)
    {
        string cleaned = SafeName(name);
        // "." and ".." survive SafeName's alphabet but are directory aliases.
        return cleaned is "." or ".." || cleaned.Trim('.').Length == 0 ? "payload" : cleaned;
    }

    private static (string? Body, string? Error) Fetch(string url, string? proxy, int timeoutSeconds)
    {
        try
        {
            using var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) };
            if (proxy.HasText)
            {
                if (!Uri.TryCreate(proxy, UriKind.Absolute, out Uri? proxyUri))
                    return (null, $"invalid proxy address '{proxy}'");
                handler.Proxy = new WebProxy(proxyUri);
                handler.UseProxy = true;
            }
            using var client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 3, 120)),
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LiveArchiveEncoder/" + Current);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            // Read from the environment and never stored: a token in config.json
            // would be a credential in a file people copy to each other, and the
            // whole reason to have one here is a repository that cannot be read
            // anonymously -- which is exactly the case where it is a secret.
            // v7.9.8.10: the feed URL can be overridden (--update-feed /
            // LAE_UPDATE_FEED), and a token sent to whatever host it names is a
            // leaked GitHub PAT. Only GitHub's own hosts ever see credentials.
            string? token = Environment.GetEnvironmentVariable("LAE_UPDATE_TOKEN");
            if (token.HasText && GitHubProxy.CanWrap(url))
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token!.Trim());

            using HttpResponseMessage response = client.GetAsync(url).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                int code = (int)response.StatusCode;
                // Measured 2026-10-28: a repository whose anonymous access is
                // restricted answers 404 to every unauthenticated request while
                // the API still reports it as public, and the same request with
                // a token answers 200. Without the hint this reads as "the
                // updater is broken"; with it, the fix is one environment
                // variable.
                string hint = code switch
                {
                    401 or 403 => " (the feed wants credentials: set LAE_UPDATE_TOKEN)",
                    404 => " (no such release feed -- private or restricted repository, "
                        + "or a URL that does not exist; LAE_UPDATE_TOKEN lifts an "
                        + "authentication wall, and it bypasses the GitHub front, which "
                        + "cannot forward credentials)",
                    _ => "",
                };
                return (null, $"HTTP {code} {response.ReasonPhrase}{hint}");
            }

            return (response.Content.ReadAsStringAsync().GetAwaiter().GetResult(), null);
        }
        catch (Exception exc) when (exc is HttpRequestException or IOException
                                        or TaskCanceledException or OperationCanceledException
                                        or System.Net.Sockets.SocketException
                                        or InvalidOperationException)
        {
            return (null, exc.Message);
        }
    }

    private static string? Digest(JsonElement item)
    {
        string? digest = Text(item, "digest");
        if (!digest.HasText) return null;
        string value = digest!.Trim();
        if (value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            value = value[7..];
        return value.Length == 64 ? value.ToLowerInvariant() : null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out long number)
            ? number
            : 0;

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string FirstLine(string text)
    {
        string flat = text.Trim();
        int cut = flat.AsSpan().IndexOfAny(LiveArchiveEncoder.Core.CharSets.NewLine);
        if (cut >= 0) flat = flat[..cut].Trim();
        return flat.Length <= 200 ? flat : flat[..200];
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            // A leftover .part or .new is harmless; the next attempt overwrites it.
        }
    }
}
