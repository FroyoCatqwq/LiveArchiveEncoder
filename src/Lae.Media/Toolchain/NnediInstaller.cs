using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lae.Core.Localization;
using Lae.Core.Toolchain;
using Lae.Media.Ffmpeg;

namespace Lae.Media.Toolchain;

/// <summary>What the weights fetch ended in.</summary>
public sealed record NnediOutcome
{
    public required bool Ok { get; init; }

    /// <summary>Why it failed, as a sentence for the user, or null.</summary>
    public string? Error { get; init; }

    /// <summary>True when the file already on disk was the current one.</summary>
    public bool AlreadyCurrent { get; init; }

    /// <summary>Where the weights are (or would be).</summary>
    public required string Weights { get; init; }

    public long Bytes { get; init; }
}

/// <summary>What an installed weights set is, as recorded beside it.</summary>
public sealed class NnediRecord
{
    public string? Source { get; set; }

    public long Bytes { get; set; }

    public string? Sha256 { get; set; }

    public string? InstalledAt { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NnediRecord))]
internal sealed partial class NnediJsonContext : JsonSerializerContext;

/// <summary>
/// Fetches the NNEDI3 weights that FFmpeg's <c>nnedi</c> filter loads by path.
///
/// <para>Why a download at all: <c>nnedi</c> is the only deinterlacer in this
/// program that needs a file, and without it the filter refuses to initialise.
/// v7 shipped <c>--nnedi-update</c> for exactly this and v8 dropped it during the
/// rewrite, which left <c>deinterlace = "nnedi"</c> reachable in the settings and
/// impossible in practice -- a setting that only ever produced a refusal.</para>
///
/// <para>Staged first, published second, same as the FFmpeg installs: a download
/// that dies at 11 MB leaves the previous weights where they were, and the part
/// file is named so a leftover from a killed run is obviously ours.</para>
/// </summary>
public sealed class NnediInstaller(string configRoot, IMessages messages)
{
    private readonly string _root = configRoot;
    private readonly IMessages _messages = messages;

    /// <summary>Where the weights are, whether or not they are there.</summary>
    public string Weights => Nnedi.Weights(_root);

    public long Bytes => Size(Weights);

    /// <summary>True when a file of a plausible size is installed.</summary>
    public bool Installed => Bytes >= Nnedi.MinimumBytes;

    /// <summary>
    /// Fetch the weights, or report that what is installed already is current.
    ///
    /// <param name="ffmpeg">
    /// Used to prove the file is really the weights -- the filter reads fixed
    /// offsets, so size alone cannot tell it from a 13 MB HTML page. Null skips
    /// that check, for a machine that has no FFmpeg yet.
    /// </param>
    ///
    /// <param name="force">
    /// Download even when the remote size matches the local one. Needed for "the
    /// file is there but FFmpeg will not load it", which the size check cannot
    /// see.
    /// </param>
    /// </summary>
    public NnediOutcome Install(
        string? ffmpeg, string? proxy, string? front, bool force,
        Action<string>? log, CancellationToken token)
    {
        string weights = Weights;
        string partial = Nnedi.Partial(_root);
        string url = GitHubFront.Apply(Nnedi.Source, front);

        try
        {
            Directory.CreateDirectory(Nnedi.Root(_root));
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException
                                        or NotSupportedException or ArgumentException)
        {
            return Fail(_messages.Format("toolchain.nnedi.error.folder", new { path = _root, detail = exc.Message }), weights);
        }

        long local = Size(weights);
        if (!force && local >= Nnedi.MinimumBytes)
        {
            // Same size on both ends is the cheap answer. It is not proof -- a
            // rebuild upstream keeps the size only by coincidence -- but the
            // alternative is moving 13 MB on every check.
            if (HttpDownload.Length(url, proxy, token) is { } remote && remote == local)
            {
                return new NnediOutcome
                {
                    Ok = true,
                    AlreadyCurrent = true,
                    Weights = weights,
                    Bytes = local,
                };
            }
        }

        log?.Invoke(_messages.Format("toolchain.log.url", new { url }));
        Discard(partial);

        if (HttpDownload.Fetch(url, partial, proxy, _messages, log, token) is { } error)
        {
            return Fail(error, weights);
        }

        long bytes = Size(partial);
        if (bytes < Nnedi.MinimumBytes)
        {
            // Almost always a block page or an interstitial saved with a 200.
            // Saying which is the difference between "retry" and "read the source".
            bool html = LooksLikeHtml(partial);
            Discard(partial);
            return Fail(_messages.Format(
                html ? "toolchain.nnedi.error.html" : "toolchain.nnedi.error.tooSmall",
                new { bytes }), weights);
        }

        if (ffmpeg is { Length: > 0 } binary)
        {
            if (FfmpegTools.ProbeNnedi(binary, partial, token) is { } loadError)
            {
                Discard(partial);
                return Fail(_messages.Format("toolchain.nnedi.error.notLoadable", new { detail = loadError }), weights);
            }
        }

        string sha = Sha256(partial);

        try
        {
            File.Move(partial, weights, overwrite: true);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            // Leave the finished part file: it is the product, and the next run
            // can move it rather than transfer 13 MB again.
            return Fail(_messages.Format("toolchain.nnedi.error.publish", new { path = weights, detail = exc.Message }), weights);
        }

        WriteRecord(new NnediRecord
        {
            Source = url,
            Bytes = bytes,
            Sha256 = sha,
            InstalledAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
        });

        return new NnediOutcome { Ok = true, Weights = weights, Bytes = bytes };
    }

    /// <summary>
    /// Delete the installed weights. The folder stays: it is where the next
    /// install goes, and it holds nothing else worth sweeping.
    /// </summary>
    public string? Remove()
    {
        string weights = Weights;

        try
        {
            if (File.Exists(weights)) File.Delete(weights);
            Discard(Nnedi.Record(_root));
            return null;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return _messages.Format("toolchain.error.removeFailed", new { path = weights, detail = exc.Message });
        }
    }

    private static NnediOutcome Fail(string error, string weights)
        => new() { Ok = false, Error = error, Weights = weights };

    private static void Discard(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            // Litter, not a verdict.
        }
    }

    private static long Size(string path)
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

    private static bool LooksLikeHtml(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[16];
            int read = stream.Read(head);
            if (read <= 0) return false;

            string text = System.Text.Encoding.UTF8.GetString(head[..read]).TrimStart();
            return text.StartsWith('<');
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private void WriteRecord(NnediRecord record)
    {
        try
        {
            File.WriteAllText(
                Nnedi.Record(_root),
                JsonSerializer.Serialize(record, NnediJsonContext.Default.NnediRecord));
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            // Bookkeeping; the weights are the product.
        }
    }
}

