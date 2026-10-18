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
    /// <summary>
    /// True for <c>\\server\share\...</c>, <c>//server/share/...</c> and the
    /// <c>smb://server/share/...</c> form Unix desktops use.
    /// </summary>
    public static bool IsUncPath(string? path)
    {
        if (IsSmbUrl(path)) return true;
        if (string.IsNullOrWhiteSpace(path)) return false;
        string p = path.Replace('/', '\\');
        return p.StartsWith(@"\\", StringComparison.Ordinal) && p.Length > 2;
    }

    /// <summary>True for <c>smb://server/share/...</c>.</summary>
    public static bool IsSmbUrl(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && path.StartsWith("smb://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the path needs the network-resilience treatment.
    ///
    /// Windows: UNC. Unix: a <c>smb://</c> URL or an ordinary path that happens
    /// to live under a CIFS mount -- on Linux a share is mounted into the tree
    /// (by <c>mount -t cifs</c> or a desktop shell), so from the application's
    /// point of view it is a perfectly normal directory that just happens to be
    /// slow and prone to stalling. Detecting the mount is what makes the
    /// timeouts apply there too.
    /// </summary>
    public static bool IsRemotePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (IsUncPath(path)) return true;
        return !OperatingSystem.IsWindows() && MountFor(path) is not null;
    }

    /// <summary>UNC paths always use backslashes; accept what users paste from Explorer or bash.</summary>
    public static string Normalize(string path)
    {
        if (IsSmbUrl(path)) return path.Trim();
        string p = path.Trim().Replace('/', '\\');
        if (p.StartsWith(@"\\", StringComparison.Ordinal)) return p;
        return p;
    }

    /// <summary>Split <c>\\server\share\rest</c> into its three parts.</summary>
    public static (string Server, string Share, string Remainder)? Split(string path)
    {
        if (IsSmbUrl(path)) return SplitUrl(path);
        if (!IsUncPath(path)) return null;
        string p = Normalize(path).TrimEnd('\\');
        string[] parts = p.TrimStart('\\').Split('\\');
        if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0) return null;
        return (parts[0], parts[1], parts.Length > 2 ? string.Join('\\', parts[2..]) : "");
    }

    private static (string Server, string Share, string Remainder)? SplitUrl(string path)
    {
        // smb://server/share/rest -- and stop at '?' or '#' like any other URL.
        string body = path["smb://".Length..];
        int cut = body.IndexOfAny(['?', '#']);
        if (cut >= 0) body = body[..cut];
        string[] parts = body.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1 || parts[0].Length == 0) return null;
        string server = parts[0];
        string share = parts.Length > 1 ? parts[1] : "";
        string rest = parts.Length > 2 ? string.Join('/', parts[2..]) : "";
        return (server, share, rest);
    }

    /// <summary>Root of the share, e.g. <c>\\server\share</c> or <c>smb://server/share</c>.</summary>
    public static string? ShareRoot(string path)
    {
        if (IsSmbUrl(path))
        {
            var u = SplitUrl(path);
            return u is null ? null
                : $"smb://{u.Value.Server}" + (u.Value.Share.Length > 0 ? "/" + u.Value.Share : "");
        }

        var parts = Split(path);
        return parts is null ? null : $@"\\{parts.Value.Server}\{parts.Value.Share}";
    }

    // ---------------------------------------------------------------- unix
    //
    // Linux has no UNC. A share is mounted into the filesystem
    // (`mount -t cifs //server/share /mnt/share`), after which the path looks
    // completely local. Reading the mount table is therefore the only way to
    // know that a plain /mnt/... path is actually a network read that needs
    // timeouts. It also gives us the negotiated dialect, which is the Linux
    // equivalent of Get-SmbConnection: the kernel reports it in the mount
    // options as vers=3.1.1 / vers=2.1 / vers=1.0.

    /// <summary>A CIFS/SMB3 mount that covers a path.</summary>
    public sealed record CifsMount(string MountPoint, string Source, string FsType, string Options);

    /// <summary>
    /// The CIFS mount a path lives under, or null. Longest mount point wins so a
    /// nested mount is reported instead of its parent. Never throws: an
    /// unreadable mount table means "assume local".
    /// </summary>
    public static CifsMount? MountFor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string full = Path.GetFullPath(path);
        CifsMount? best = null;

        foreach (CifsMount mount in EnumerateMounts())
        {
            if (!Covers(mount.MountPoint, full)) continue;
            if (best is null || mount.MountPoint.Length > best.MountPoint.Length) best = mount;
        }
        return best;
    }

    private static bool Covers(string mountPoint, string full)
    {
        string mp = mountPoint.TrimEnd('/');
        if (mp.Length == 0) mp = "/";
        return full.Equals(mp, StringComparison.Ordinal)
            || full.StartsWith(mp == "/" ? "/" : mp + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Every cifs/smb3 entry in /proc/self/mountinfo, falling back to
    /// /proc/mounts. mountinfo is preferred because it does not lie about paths
    /// containing spaces (it escapes them as \040).
    /// </summary>
    private static IEnumerable<CifsMount> EnumerateMounts()
    {
        foreach (string file in new[] { "/proc/self/mountinfo", "/proc/mounts" })
        {
            List<CifsMount>? mounts = ReadMountFile(file);
            if (mounts is null) continue;
            if (mounts.Count > 0) return mounts;
        }
        return [];
    }

    private static List<CifsMount>? ReadMountFile(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var mounts = new List<CifsMount>();

            foreach (string raw in File.ReadLines(file))
            {
                // mountinfo: id parent major:minor root mountpoint opts - fstype source superopts
                // mounts:    source mountpoint fstype opts dump pass
                int sep = raw.IndexOf(" - ", StringComparison.Ordinal);
                string fstype, source, mountPoint, options;

                if (sep > 0)
                {
                    string[] left = raw[..sep].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    string[] right = raw[(sep + 3)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (left.Length < 5 || right.Length < 2) continue;
                    fstype = right[0];
                    source = right[1];
                    mountPoint = Unescape(left[4]);
                    options = right.Length > 2 ? right[2] : "";
                }
                else
                {
                    string[] f = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length < 4) continue;
                    source = f[0];
                    mountPoint = Unescape(f[1]);
                    fstype = f[2];
                    options = f[3];
                }

                if (!fstype.Equals("cifs", StringComparison.OrdinalIgnoreCase)
                    && !fstype.Equals("smb3", StringComparison.OrdinalIgnoreCase)
                    && !fstype.Equals("smbfs", StringComparison.OrdinalIgnoreCase)) continue;

                mounts.Add(new CifsMount(mountPoint, source, fstype, options));
            }
            return mounts;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Unescape(string value) => value
        .Replace(@"\040", " ").Replace(@"\011", "\t").Replace(@"\012", "\n").Replace(@"\134", "\\");

    /// <summary>
    /// Dialect a mounted share negotiated, read from its <c>vers=</c> option.
    /// This is the Linux half of <see cref="NegotiatedDialect"/>.
    /// </summary>
    public static SmbDialect DialectFromOptions(string options)
    {
        foreach (string opt in options.Split(','))
        {
            if (!opt.StartsWith("vers=", StringComparison.OrdinalIgnoreCase)) continue;
            string v = opt["vers=".Length..];
            return v.StartsWith("3", StringComparison.Ordinal) ? SmbDialect.Smb3
                : v.StartsWith("2", StringComparison.Ordinal) ? SmbDialect.Smb2
                : v.StartsWith("1", StringComparison.Ordinal) ? SmbDialect.Smb1
                : SmbDialect.Unknown;
        }
        return SmbDialect.Unknown;
    }

    /// <summary>
    /// FFmpeg has no SMB protocol on either platform, so a share has to be
    /// reachable as a real path. This returns null when it is, and otherwise the
    /// command that would make it reachable -- much better than letting FFmpeg
    /// fail twenty seconds in with "No such file or directory".
    /// </summary>
    public static string? UnreachableHint(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        if (IsSmbUrl(path))
        {
            var u = SplitUrl(path);
            if (u is null) return null;

            if (OperatingSystem.IsWindows())
            {
                string rest = u.Value.Remainder.Length > 0
                    ? @"\" + u.Value.Remainder.Replace('/', '\\')
                    : "";
                return "smb:// is not a path Windows can open; use the UNC form "
                    + $@"\\{u.Value.Server}\{u.Value.Share}{rest}";
            }

            string share = u.Value.Share.Length > 0 ? "/" + u.Value.Share : "";
            return $"mount it first: sudo mount -t cifs //{u.Value.Server}{share} /mnt/{u.Value.Share} "
                + "-o username=<user>,vers=3.1.1  (or open it in the file manager, which mounts it "
                + "under /run/user/$UID/gvfs)";
        }

        // A backslash UNC typed on Linux: Windows syntax, no meaning here.
        if (!OperatingSystem.IsWindows() && path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            string forward = "//" + path.TrimStart('\\').Replace('\\', '/');
            return "UNC paths are Windows-only; mount the share and use its mount point: "
                + $"sudo mount -t cifs {forward} /mnt/<name> -o username=<user>,vers=3.1.1";
        }
        return null;
    }

    /// <summary>
    /// Best-effort look at the dialect Windows actually negotiated for a share.
    /// Returns null when the query is unavailable (no PowerShell, no permission,
    /// share not currently connected) -- never throws, because a missing report
    /// must not block an encode that would otherwise succeed.
    /// </summary>
    public static (SmbDialect Dialect, string? Detail)? NegotiatedDialect(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            CifsMount? mount = MountFor(path);
            if (mount is null) return null;
            string vers = mount.Options.Split(',')
                .FirstOrDefault(o => o.StartsWith("vers=", StringComparison.OrdinalIgnoreCase)) ?? "";
            return (DialectFromOptions(mount.Options),
                vers.Length > 0 ? vers : mount.FsType);
        }

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
        bool url = IsSmbUrl(path);
        bool unc = !url && IsUncPath(path);
        CifsMount? mount = OperatingSystem.IsWindows() ? null : MountFor(path);
        if (!url && !unc && mount is null) return null;

        // On Linux the "share root" is the mount: what the user needs to see is
        // which remote source landed on which local directory.
        string root = mount is not null
            ? $"{mount.Source} -> {mount.MountPoint}"
            : ShareRoot(path) ?? path;

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
        return $"{label,-17}: {root} -- {verdict}";
    }

    /// <summary>
    /// Same idea for ffprobe, which is the first process to touch the file and
    /// therefore the one that hangs first on a stalled share. ffprobe only knows
    /// <c>-rw_timeout</c> (verified with <c>ffprobe -h</c>), so the protocol-level
    /// reconnect options are left out here.
    /// </summary>
    public static List<string> RemoteProbeArgs(string path)
    {
        if (!IsRemotePath(path)) return [];
        return ["-rw_timeout", "30000000"];
    }

    /// <summary>
    /// FFmpeg options that make a network read survivable instead of hanging
    /// forever. Only applied to remote inputs: on a local file they would just
    /// add overhead.
    ///
    /// <c>-rw_timeout</c> is the only option here on purpose. Both attempts to
    /// add protocol-level friends turned into hard failures:
    ///
    ///   * <c>-stimeout</c> is an <em>ffplay</em> option. FFmpeg rejects it with
    ///     "Unrecognized option 'stimeout'. Error splitting the argument list",
    ///     which made every UNC source fail before a single frame was read.
    ///   * the <c>-reconnect*</c> family belongs to the HTTP/FTP protocols, not
    ///     to <c>file</c>. It is listed in <c>ffmpeg -h full</c> as <c>D</c>
    ///     (decoding only) and older builds ignored it on a UNC path, but a
    ///     current master build answers
    ///     "Option reconnect not found. Error opening input file ...".
    ///     Measured 2026-10-18 on a BtbN linux64 build: a CIFS-mounted source
    ///     decoded to **zero** frames with it and to 26 frames without it.
    ///     A share needs no reconnect logic anyway -- the OS (UNC) or the kernel
    ///     (CIFS mount) already owns that.
    /// </summary>
    public static List<string> RemoteReadArgs(string path)
    {
        if (!IsRemotePath(path)) return [];
        // Give up on a stalled read instead of blocking the whole pipeline.
        return ["-rw_timeout", "30000000"];
    }

    /// <summary>
    /// Options for a remote <em>destination</em> (UNC share or CIFS mount point).
    ///
    /// <c>-rw_timeout</c> is the one AVFormatContext option that covers writes,
    /// and it is what stops a vanished share from hanging the muxer forever.
    /// The reconnect family is not used anywhere: see
    /// <see cref="RemoteReadArgs"/> for what happened when it was.
    /// </summary>
    public static List<string> RemoteWriteArgs(string path)
    {
        if (!IsRemotePath(path)) return [];
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
        // A URL is not something we can create a marker in; the caller is
        // supposed to have rejected it via UnreachableHint long before here.
        if (IsSmbUrl(directory)) return false;
        if (!IsRemotePath(directory)) return true;

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
