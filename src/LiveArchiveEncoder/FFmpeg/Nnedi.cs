using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveArchiveEncoder.Core;

namespace LiveArchiveEncoder.FFmpeg;

/// <summary>What an installed weights set is, as recorded beside it.</summary>
public sealed class NnediRecord
{
    /// <summary>Release tag it came from, e.g. <c>r3.3</c>, or <c>master</c>.</summary>
    public string? Release { get; set; }

    /// <summary>Exact URL that was fetched, front prefix included.</summary>
    public string? Source { get; set; }

    public long Bytes { get; set; }
    public string? Sha256 { get; set; }
    public string? InstalledAt { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NnediRecord))]
internal sealed partial class NnediJsonContext : JsonSerializerContext;

/// <summary>
/// The NNEDI3 weights FFmpeg's <c>nnedi</c> filter needs, taken from the
/// reference implementation.
///
/// <para>Why this exists rather than shipping the file: it is 13,574,928 bytes
/// (measured 2026-10-27), it is not ours to redistribute in a 4 MB release, and
/// upstream revises it with the plugin. <c>znedi3</c> is sekrit-twc's
/// VapourSynth port of nnedi3 and the canonical home of
/// <c>nnedi3_weights.bin</c>; FFmpeg's own documentation points at it. The
/// filter reads the file by path (<c>nnedi=weights=...</c>) and refuses to
/// initialise without it, so the file has to exist before an encode, not during
/// one.</para>
///
/// <para>Layout mirrors the FFmpeg toolchain: one directory under
/// <c>%LOCALAPPDATA%\LiveArchiveEncoder\nnedi</c>, the weights under their own
/// name, and a small JSON record of which release they came from -- so
/// "update" can tell "already latest" from "newer available" without a
/// hash of a 13 MB file every time.</para>
/// </summary>
public static class Nnedi
{
    public const string Repo = "sekrit-twc/znedi3";
    public const string WeightsName = "nnedi3_weights.bin";

    /// <summary>Smallest plausible weights file. A 403 page and a redirected HTML interstitial are both orders of magnitude smaller.</summary>
    private const long MinimumBytes = 1L << 20;

    private const string ApiLatest = "https://api.github.com/repos/" + Repo + "/releases/latest";
    private const string RawRoot = "https://raw.githubusercontent.com/" + Repo + "/";
    private const string ContentsApi = "https://api.github.com/repos/" + Repo + "/contents/" + WeightsName;

    public static string Root
    {
        get
        {
            string? appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = string.IsNullOrEmpty(appData) ? AppContext.BaseDirectory
                : Path.Combine(appData, "LiveArchiveEncoder");
            return Path.Combine(dir, "nnedi");
        }
    }

    public static string WeightsPath => Path.Combine(Root, WeightsName);

    private static string RecordPath => Path.Combine(Root, "install.json");

    /// <summary>True when a weights file of a plausible size is installed here.</summary>
    public static bool Installed => FileSize(WeightsPath) >= MinimumBytes;

    /// <summary>
    /// The weights an encode should use: an explicit path when one was given,
    /// otherwise the managed install. Null means "nothing usable" -- the caller
    /// turns that into a message naming <c>--nnedi-update</c>.
    ///
    /// <para>A directory is accepted and searched for the file by name: the
    /// obvious way to get znedi3 is to unzip the release, and pointing
    /// <c>--nnedi-weights</c> at that folder should work.</para>
    /// </summary>
    public static string? Resolve(string? explicitPath)
    {
        if (!explicitPath.HasText) return Installed ? WeightsPath : null;

        string path = explicitPath.Trim().Trim('"');
        if (Directory.Exists(path)) path = Path.Combine(path, WeightsName);
        return FileSize(path) >= MinimumBytes ? path : null;
    }

    /// <summary>What is installed, or null when nothing is.</summary>
    public static NnediRecord? Read()
    {
        long bytes = FileSize(WeightsPath);
        if (bytes < MinimumBytes) return null;

        NnediRecord? record = null;
        try
        {
            if (File.Exists(RecordPath))
            {
                record = JsonSerializer.Deserialize(
                    File.ReadAllText(RecordPath), NnediJsonContext.Default.NnediRecord);
            }
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException
                                        or JsonException)
        {
            // A damaged record is not a missing install: the weights are what
            // matters, and the record only answers "which release".
            record = null;
        }

        return new NnediRecord
        {
            Release = record?.Release,
            Source = record?.Source,
            Bytes = bytes,
            Sha256 = record?.Sha256,
            InstalledAt = record?.InstalledAt,
        };
    }

    /// <summary>
    /// The newest release tag, its size and hash, or an error. Uses the GitHub
    /// API, which the front proxies too (measured: <c>gh-proxy.com</c> answers
    /// 200 for <c>api.github.com</c>).
    /// </summary>
    public static (string? Tag, long? Bytes, string? Sha, string? Error) Latest(
        string? proxy, string? ghPrefix)
    {
        (JsonDocument? doc, string? error) = GetJson(GitHubProxy.Apply(ApiLatest, ghPrefix), proxy);
        if (doc is null) return (null, null, null, error);

        using (doc)
        {
            string? tag = doc.RootElement.TryGetProperty("tag_name", out JsonElement t)
                ? t.GetString() : null;
            if (!tag.HasText) return (null, null, null, "the release listing has no tag_name");

            // The contents endpoint is where the size and blob hash live; the
            // release itself only carries the plugin archive.
            (JsonDocument? info, _) = GetJson(
                GitHubProxy.Apply($"{ContentsApi}?ref={Uri.EscapeDataString(tag)}", ghPrefix), proxy);
            if (info is null) return (tag, null, null, null);
            using (info)
            {
                long? size = info.RootElement.TryGetProperty("size", out JsonElement s)
                        && s.TryGetInt64(out long value) ? value : null;
                string? sha = info.RootElement.TryGetProperty("sha", out JsonElement h)
                    ? h.GetString() : null;
                return (tag, size, sha, null);
            }
        }
    }

    /// <summary>What an update did, so the caller can word it correctly.</summary>
    public enum Outcome
    {
        Failed,
        AlreadyCurrent,
        Installed,
    }

    /// <summary>
    /// Fetch the newest weights, or report that the installed set already is the
    /// newest.
    ///
    /// <para><paramref name="ffmpeg"/> is only used to prove the downloaded file
    /// is loadable by the <c>nnedi</c> filter: the filter reads fixed offsets out
    /// of the blob, so "13 MB of HTML" and "13 MB of the right file" are
    /// indistinguishable by size alone. Null skips that check (a machine with no
    /// FFmpeg yet can still fetch the file).</para>
    /// </summary>
    public static (Outcome Result, string? Error, string? Release) Update(
        string? ffmpeg, string? proxy, string? ghPrefix, bool autoFront = true,
        Action<string>? log = null)
    {
        void Say(string line) => log?.Invoke(line);

        // Decide the route before spending a request on it. An explicit front or
        // a proxy answers the question already; otherwise five seconds of probing
        // is cheaper than the stall timeout a blocked host costs.
        string? front = ghPrefix ?? (autoFront
            ? GitHubProxy.Detect($"{RawRoot}master/{WeightsName}", proxy, Say)
            : null);

        (string? tag, long? remoteBytes, _, string? apiError) = Latest(proxy, front);
        NnediRecord? current = Read();

        if (tag is null && apiError is not null)
        {
            // No API (rate limit, offline, an unfriendly host) is not a dead end:
            // the master ref is always fetchable, and the hash comparison below
            // then answers "did anything change" on its own.
            Say($"GitHub API unavailable ({apiError}); falling back to the master branch.");
        }

        string reference = tag ?? "master";
        if (current is not null
            && string.Equals(current.Release, reference, StringComparison.OrdinalIgnoreCase)
            && (remoteBytes is null || remoteBytes == current.Bytes))
        {
            Say($"{WeightsName} is already the latest ({reference}, "
                + $"{current.Bytes:N0} bytes).");
            return (Outcome.AlreadyCurrent, null, reference);
        }

        string url = $"{RawRoot}{reference}/{WeightsName}";
        string actual = GitHubProxy.Apply(url, front);
        Say($"Release: {reference}");
        Say($"URL    : {actual}");

        Directory.CreateDirectory(Root);
        Sweep();
        string staging = PartialPath;
        if (remoteBytes is > 0 && FileSize(staging) > remoteBytes)
        {
            // Larger than the remote file: a leftover from a different release,
            // and resuming onto it would produce a corrupt result.
            ResetPartial();
        }

        (bool ok, string? downloadError) = Toolchain.DownloadFile(actual, staging, proxy, log);
        if (!ok && front is null && autoFront)
        {
            // The probe said direct, then the transfer died anyway -- a blocked
            // route often looks alive for the first kilobyte. One retry through
            // the front, from scratch, is worth 13 MB of "try again with a flag
            // you had to read the source to know about".
            Say($"{downloadError}; retrying through {GitHubProxy.DefaultPrefix} from the start.");
            ResetPartial();
            actual = GitHubProxy.Apply(url, GitHubProxy.DefaultPrefix);
            Say($"URL    : {actual}");
            (ok, downloadError) = Toolchain.DownloadFile(actual, staging, proxy, log);
        }
        if (!ok)
        {
            // Keep a partial download: aria2c resumes it on the next run, which
            // is the difference between "try again" and "wait 70 seconds again".
            long kept = FileSize(staging);
            string note = kept >= MinimumBytes
                ? $" ({kept:N0} bytes kept; run --nnedi-update again to resume)"
                : "";
            if (kept < MinimumBytes) ResetPartial();
            return (Outcome.Failed, downloadError + note, reference);
        }

        long bytes = FileSize(staging);
        if (bytes < MinimumBytes)
        {
            // Almost always an HTML page saved by a downloader that accepted a
            // 200. Say which, rather than "invalid file".
            string head = Head(staging, 200);
            ResetPartial();
            bool html = head.TrimStart().StartsWith('<')
                        || head.Contains("<html", StringComparison.OrdinalIgnoreCase);
            return (Outcome.Failed, html
                ? $"the server returned an HTML page instead of the weights ({bytes} bytes) -- "
                  + "usually a blocked request; try again with --gh-proxy or --proxy"
                : $"the downloaded file is only {bytes} bytes, which cannot be {WeightsName}",
                reference);
        }

        if (ffmpeg.HasText
            && !Loadable(ffmpeg, staging, out string? loadError))
        {
            ResetPartial();
            return (Outcome.Failed, $"the file downloaded but the nnedi filter cannot read it: {loadError}",
                reference);
        }

        string sha = Sha256(staging);
        if (current is not null && string.Equals(current.Sha256, sha, StringComparison.OrdinalIgnoreCase))
        {
            // Same bytes under a new tag name: keep the file, refresh the record.
            ResetPartial();
            WriteRecord(new NnediRecord
            {
                Release = reference, Source = actual, Bytes = bytes, Sha256 = sha,
                InstalledAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            });
            Say($"Unchanged (same content as before, {bytes:N0} bytes).");
            return (Outcome.AlreadyCurrent, null, reference);
        }

        try
        {
            File.Move(staging, WeightsPath, overwrite: true);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            // Leave the finished staging file: it is the product, and the next
            // run can move it rather than transfer 13 MB again.
            return (Outcome.Failed, $"could not put the weights in place: {exc.Message}", reference);
        }

        WriteRecord(new NnediRecord
        {
            Release = reference, Source = actual, Bytes = bytes, Sha256 = sha,
            InstalledAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
        });
        // The caller prints the final line; two "installed" messages read like
        // two installs.
        return (Outcome.Installed, null, reference);
    }

    /// <summary>
    /// Prove the file is really the weights: let the filter initialise on it.
    /// The filter reads fixed offsets, so this is the only honest check short of
    /// comparing hashes with upstream.
    /// </summary>
    public static bool Loadable(string ffmpeg, string weights, out string? error)
    {
        error = null;
        var argv = new List<string>
        {
            ffmpeg, "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "color=c=black:s=64x64:r=2:d=1",
            "-vf", $"nnedi=weights={FilterArgs.EscapePath(weights)}",
            "-frames:v", "1", "-f", "null", "-",
        };
        try
        {
            CaptureResult run = ProcessRunner.Run(argv, 30);
            if (run.ExitCode == 0) return true;
            string text = (run.Stderr + "\n" + run.Stdout).Trim();
            error = text.Length == 0 ? $"nnedi exited with {run.ExitCode}" : FirstLine(text);
            return false;
        }
        catch (Exception exc) when (exc is InvalidOperationException
                                        or System.ComponentModel.Win32Exception
                                        or TimeoutException or IOException)
        {
            error = exc.Message;
            return false;
        }
    }

    // ---- plumbing ----

    /// <summary>
    /// Where a download accumulates before it is known to be good. Fixed rather
    /// than random so aria2c's <c>-c</c> can resume it: a random name per run
    /// silently turned every interrupted attempt into 13 MB of waste.
    /// </summary>
    private static string PartialPath => Path.Combine(Root, "." + WeightsName + ".part");

    /// <summary>Drop the partial file and aria2c's control file with it.</summary>
    private static void ResetPartial()
    {
        TryDelete(PartialPath);
        TryDelete(PartialPath + ".aria2");
    }

    /// <summary>
    /// Remove the randomly named staging files older builds left in this
    /// directory (measured: two of them, 7.6 MB, from interrupted attempts).
    /// </summary>
    private static void Sweep()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(Root, ".download.*"))
                TryDelete(file);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            // Housekeeping only.
        }
    }

    private static (JsonDocument? Doc, string? Error) GetJson(string url, string? proxy)
    {
        try
        {
            using var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(20) };
            if (proxy.HasText)
            {
                if (!Uri.TryCreate(proxy, UriKind.Absolute, out Uri? proxyUri))
                    return (null, $"invalid proxy address '{proxy}'");
                handler.Proxy = new WebProxy(proxyUri);
                handler.UseProxy = true;
            }
            using var client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LiveArchiveEncoder");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using HttpResponseMessage response = client.GetAsync(url).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                return (null, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return (JsonDocument.Parse(body), null);
        }
        catch (Exception exc) when (exc is HttpRequestException or IOException
                                        or TaskCanceledException or JsonException
                                        or System.Net.Sockets.SocketException
                                        or OperationCanceledException)
        {
            return (null, exc.Message);
        }
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void WriteRecord(NnediRecord record)
    {
        try
        {
            File.WriteAllText(RecordPath,
                JsonSerializer.Serialize(record, NnediJsonContext.Default.NnediRecord));
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            // The record is bookkeeping; the weights are the product.
        }
    }

    private static long FileSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string Head(string path, int max)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            byte[] buffer = new byte[Math.Min(max, (int)Math.Min(stream.Length, max))];
            int read = stream.Read(buffer, 0, buffer.Length);
            return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static string FirstLine(string text) =>
        text.Split('\n')[0].Trim();
}
