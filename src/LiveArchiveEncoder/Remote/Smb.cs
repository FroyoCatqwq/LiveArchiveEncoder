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
    public static string? Describe(string path)
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
        return $"{L.T("smb.share"),-17}: {ShareRoot(path)} -- {verdict}";
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
    /// </summary>
    public static List<string> RemoteReadArgs(string path)
    {
        if (!IsUncPath(path)) return [];
        return
        [
            // Give up on a stalled read instead of blocking the whole pipeline.
            "-rw_timeout", "30000000",
            "-stimeout", "30000000",
            "-reconnect", "1",
            "-reconnect_streamed", "1",
            "-reconnect_delay_max", "5",
        ];
    }
}
