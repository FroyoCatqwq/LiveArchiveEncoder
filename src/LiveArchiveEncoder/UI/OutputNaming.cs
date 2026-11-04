using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.FFmpeg;

namespace LiveArchiveEncoder.UI;

/// <summary>
/// v7.9.8.9: where an automatic output goes, and what it is called.
///
/// <para>Beside the source has always been the rule, and for a local file it is
/// still the right one. A source on a network share is different: the capture
/// then writes tens of gigabytes back over the same link it is reading from, and
/// the archive stays on the machine that was only supposed to serve it. For
/// those the default moves under the program's own directory, and the menu says
/// where. A path the user typed is never touched.</para>
/// </summary>
internal static class OutputNaming
{
    /// <summary>The suffix an automatic name carries, so it cannot collide with the source.</summary>
    public const string Suffix = "_live";

    /// <summary>
    /// The default output for a source file, and whether it had to leave the
    /// network to get there.
    /// </summary>
    public static (FileInfo Path, bool MovedOffNetwork) ForSource(FileInfo source, string extension)
    {
        string name = Path.GetFileNameWithoutExtension(source.Name) + Suffix + "." + extension;
        string? dir = source.DirectoryName;

        // No directory at all (a bare name) means the working directory, which
        // is local. A network folder -- UNC, mapped letter or a Linux mount --
        // is the case this exists for.
        if (dir is not { Length: > 0 } || !Remote.Smb.IsRemotePath(dir))
            return (new FileInfo(Path.Combine(dir ?? ".", name)), false);

        string fallback = ConfigStore.CaptureDirectory;
        try
        {
            Directory.CreateDirectory(fallback);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            // A read-only profile is still better served by a file next to the
            // source than by no file at all; the write itself will report the
            // path it could not use.
            return (new FileInfo(Path.Combine(dir, name)), false);
        }
        return (new FileInfo(Path.Combine(fallback, name)), true);
    }

    /// <summary>The default output for a URL: there is no source folder to inherit.</summary>
    public static FileInfo ForUrl(string stem, string extension) =>
        new(Path.Combine(".", $"{stem}{Suffix}.{extension}"));

    /// <summary>
    /// The name this encoder can actually write.
    ///
    /// <para>v7.9.8.9: an extension no container of the chosen encoder carries
    /// (<c>out.rm</c> with libx264) used to abort the run with a muxer message.
    /// The user asked for a file, so the extension is corrected instead -- and
    /// the corrected name never lands on a file nobody named, nor on the source
    /// itself: a capture must not overwrite something the program was not told
    /// to write.</para>
    ///
    /// <para><c>From</c> and <c>To</c> are null when the name was already
    /// writable; otherwise both are bare extensions, for the caller to put in a
    /// sentence it can localise.</para>
    /// </summary>
    public static (FileInfo Path, string? From, string? To) CorrectExtension(
        FileInfo path, EncoderInfo encoder, string? avoidFullPath = null)
    {
        string extension = path.Extension.TrimStart('.');
        if (extension.Length > 0 && CanWrite(encoder, extension)) return (path, null, null);

        string wanted = "." + encoder.Extension;
        string dir = path.DirectoryName ?? ".";
        string stem = Path.GetFileNameWithoutExtension(path.Name);
        var candidate = new FileInfo(Path.Combine(dir, stem + wanted));

        for (int n = 2; n < 1000; n++)
        {
            bool taken = candidate.Exists
                || (avoidFullPath is not null
                    && string.Equals(candidate.FullName, avoidFullPath, StringComparison.OrdinalIgnoreCase));
            if (!taken) break;
            candidate = new FileInfo(Path.Combine(dir, $"{stem} ({n}){wanted}"));
        }
        return (candidate, extension.Length == 0 ? "(none)" : extension, encoder.Extension);
    }

    /// <summary>
    /// True when this encoder has a container that calls itself
    /// <paramref name="extension"/> -- either the short name the menu shows or
    /// one of the muxer's own spellings (mkv / matroska).
    /// </summary>
    public static bool CanWrite(EncoderInfo encoder, string extension)
    {
        foreach ((string ext, string format) in EncoderMath.AllowedContainers(encoder))
        {
            if (string.Equals(ext, extension, StringComparison.OrdinalIgnoreCase)) return true;
            if (Constants.FormatExtensions.TryGetValue(format, out IReadOnlySet<string>? aliases)
                && aliases.Contains("." + extension, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
