using System.Diagnostics;
using System.Text;
using LiveArchiveEncoder.Localization;

namespace LiveArchiveEncoder.Remote;

/// <summary>Dialect negotiated for an SMB share.</summary>
public enum SmbDialect
{
    Unknown = 0,
    /// <summary>SMB 1.0/CIFS. Obsolete, insecure and disabled by default on modern Windows.</summary>
    Smb1 = 1,
    Smb2 = 2,
    Smb3 = 3,
}

/// <summary>
/// SMB/UNC helpers for reading sources straight off a network share.
///
/// Important: on Windows the client and server negotiate the dialect between
/// themselves -- an application cannot pick SMB3 directly. What this class does
/// instead is (a) recognise UNC inputs, (b) report which dialect actually got
/// negotiated so a silent downgrade to SMB1 is visible, and (c) hand FFmpeg the
/// resilience options a network read needs.
///
/// The policy is "SMB3 when available, otherwise fall back to SMB2, never SMB1":
/// SMB1 is flagged as a warning rather than silently accepted.
/// </summary>
public static class Smb
{
    /// <summary>True for <c>\\server\share\...</c> and <c>//server/share/...</c>.</summary>
    public static bool IsUncPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string p = path.Replace('/', '\\');
        return p.StartsWith(@"\\", StringComparison.Ordinal) && p.Length > 2;
    }

    /// <summary>UNC paths always use backslashes; accept what users paste from Explorer or bash.</summary>
    public static string Normalize(string path)
    {
        string p = path.Trim().Replace('/', '\\');
        if (p.StartsWith(@"\\", StringComparison.Ordinal)) return p;
        return p;
    }

    /// <summary>Split <c>\\server\share\rest</c> into its three parts.</summary>
    public static (string Server, string Share, string Remainder)? Split(string path)
    {
        if (!IsUncPath(path)) return null;
        string p = Normalize(path).TrimEnd('\\');
        string[] parts = p.TrimStart('\\').Split('\\');
        if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0) return null;
        return (parts[0], parts[1], parts.Length > 2 ? string.Join('\\', parts[2..]) : "");
    }

    /// <summary>Root of the share, e.g. <c>\\server\share</c>.</summary>
    public static string? ShareRoot(string path)
    {
        var parts = Split(path);
        return parts is null ? null : $@"\\{parts.Value.Server}\{parts.Value.Share}";
    }

    /// <summary>
    /// Best-effort look at the dialect Windows actually negotiated for a share.
    /// Returns null when the query is unavailable (no PowerShell, no permission,
    /// share not currently connected) -- never throws, because a missing report
    /// must not block an encode that would otherwise succeed.
    /// </summary>
    public static (SmbDialect Dialect, string? Detail)? NegotiatedDialect(string path)
    {
        var parts = Split(path);
        if (parts is null) return null;

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "powershell",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add(
                $"$c = Get-SmbConnection -ServerName '{parts.Value.Server}' -ErrorAction SilentlyContinue "
                + $"| Where-Object {{ $_.ShareName -eq '{parts.Value.Share}' }}; "
                + "if ($c) { $c.Dialect } else { '' }");

            using Process? proc = Process.Start(start);
            if (proc is null) return null;
            if (!proc.WaitForExit(15000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return null;
            }

            string text = proc.StandardOutput.ReadToEnd().Trim();
            if (text.Length == 0) return null;

            SmbDialect dialect = text.StartsWith("3", StringComparison.Ordinal) ? SmbDialect.Smb3
                : text.StartsWith("2", StringComparison.Ordinal) ? SmbDialect.Smb2
                : text.StartsWith("1", StringComparison.Ordinal) ? SmbDialect.Smb1
                : SmbDialect.Unknown;
            return (dialect, text);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Human-readable line for the banner. Always reports something for a UNC
    /// input, because knowing that the resilience options are in play matters
    /// even when Windows has not connected the share yet and the dialect is
    /// therefore still unknown.
    /// </summary>
    public static string? Describe(string path) => Describe(path, L.T("smb.share"));

    /// <summary>Same report for a UNC <em>destination</em>.</summary>
    public static string? DescribeOutput(string path) => Describe(path, L.T("smb.shareout"));

    private static string? Describe(string path, string label)
    {
        if (!IsUncPath(path)) return null;

        var report = NegotiatedDialect(path);
        string verdict = report is null
            ? L.T("smb.notconnected")
            : report.Value.Dialect switch
            {
                SmbDialect.Smb3 => L.T("smb.smb3"),
                SmbDialect.Smb2 => L.T("smb.smb2"),
                SmbDialect.Smb1 => L.T("smb.smb1"),
                _ => L.T("smb.unknown", report.Value.Detail ?? "?"),
            };
        return $"{label,-17}: {ShareRoot(path)} -- {verdict}";
    }

    /// <summary>
    /// Same idea for ffprobe, which is the first process to touch the file and
    /// therefore the one that hangs first on a stalled share. ffprobe only knows
    /// <c>-rw_timeout</c> (verified with <c>ffprobe -h</c>), so the protocol-level
    /// reconnect options are left out here.
    /// </summary>
    public static List<string> RemoteProbeArgs(string path)
    {
        if (!IsUncPath(path)) return [];
        return ["-rw_timeout", "30000000"];
    }

    /// <summary>
    /// FFmpeg options that make a network read survivable instead of hanging
    /// forever or dying on the first transient disconnect. Only applied to UNC
    /// inputs: on a local file they would just add overhead.
    ///
    /// Every entry here was checked against <c>ffmpeg -h full</c> on this build
    /// (2026-10-18):
    ///
    ///   * <c>-rw_timeout</c> is an AVFormatContext option flagged <c>ED</c>, so
    ///     it covers both reading and writing;
    ///   * the <c>-reconnect*</c> family is flagged <c>D</c> -- decoding only --
    ///     which is exactly how we use it, and it is silently accepted even by
    ///     the plain file protocol;
    ///   * <c>-stimeout</c> was in the original list but is an <em>ffplay</em>
    ///     option. FFmpeg rejects it outright with
    ///     "Unrecognized option 'stimeout'. Error splitting the argument list",
    ///     which made every UNC source fail before a single frame was read.
    ///     It is gone for that reason.
    /// </summary>
    public static List<string> RemoteReadArgs(string path)
    {
        if (!IsUncPath(path)) return [];
        return
        [
            // Give up on a stalled read instead of blocking the whole pipeline.
            "-rw_timeout", "30000000",
            "-reconnect", "1",
            "-reconnect_streamed", "1",
            "-reconnect_delay_max", "5",
        ];
    }

    /// <summary>
    /// Options for a UNC <em>destination</em>.
    ///
    /// The reconnect family cannot be used here: <c>ffmpeg -h full</c> flags it
    /// <c>D</c> (decoding only), so on the output side it would be rejected.
    /// <c>-rw_timeout</c> is the one AVFormatContext option that covers writes,
    /// and it is what stops a vanished share from hanging the muxer forever.
    /// </summary>
    public static List<string> RemoteWriteArgs(string path)
    {
        if (!IsUncPath(path)) return [];
        return ["-rw_timeout", "30000000"];
    }

    /// <summary>
    /// Cheap write probe for a UNC destination directory.
    ///
    /// An encode is deliberately slow (real-time pacing), so discovering after
    /// twenty seconds that the share is read-only or the credentials only grant
    /// read access is worse than discovering it now. Creates and immediately
    /// deletes a zero-byte marker next to the output.
    /// </summary>
    public static bool CanWriteDirectory(string directory)
    {
        if (!IsUncPath(directory)) return true;

        string marker = Path.Combine(directory, ".lae-write-probe");
        try
        {
            Directory.CreateDirectory(directory);
            using (File.Create(marker, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException
                                        or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
        finally
        {
            try { if (File.Exists(marker)) File.Delete(marker); } catch { /* best effort */ }
        }
    }
}
