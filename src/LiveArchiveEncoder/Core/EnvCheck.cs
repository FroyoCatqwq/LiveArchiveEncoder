namespace LiveArchiveEncoder.Core;

using System.Globalization;
using System.Runtime.InteropServices;

/// <summary>
/// The documented minimums, checked on the machine that is actually running
/// (v7.9.5.1).
///
/// This is a warning, never a refusal: the numbers in
/// <c>docs/requirements.md</c> are what the project is tested against, and a
/// slow machine still makes a correct file -- it just takes longer and may not
/// be able to shard. Saying so once, at the top of an interactive session, is
/// cheaper than a user wondering why an eight-shard run is swapping.
/// </summary>
public static class EnvCheck
{
    /// <summary>A shard needs a core; below two, sharding is pointless.</summary>
    public const int MinCores = 2;

    /// <summary>Enough for one 1080p decode + encode pipeline.</summary>
    public const double MinMemoryGb = 2.0;

    /// <summary>
    /// Free space at the destination. A sharded run writes every shard before
    /// it joins them, so the peak is roughly the output size again.
    /// </summary>
    public const double MinFreeDiskGb = 2.0;

    /// <summary>
    /// Oldest Windows build this project is tested on: Windows Server 2019 /
    /// Windows 10 1809 (10.0.17763). v7.9.6.3 lowered the documented floor from
    /// "Windows 11" to what .NET 10 itself supports, and this is the oldest
    /// machine in the test fleet.
    /// </summary>
    public const int MinWindowsBuild = 17763;

    /// <summary>
    /// Oldest glibc the Linux builds are expected to run against. .NET 10
    /// supports RHEL 8 / AlmaLinux 8, which ship glibc 2.28; the NativeAOT
    /// artefacts inherit whatever the build host had, so this is also the
    /// number to compare a distribution against before taking the AOT archive.
    /// </summary>
    public const double MinGlibc = 2.28;

    /// <summary>Localised one-line warnings; empty when the machine is fine.</summary>
    public static List<string> Warnings(string? outputPath)
    {
        var warnings = new List<string>();

        if (Environment.ProcessorCount < MinCores)
            warnings.Add(Localization.L.T("env.cores", Environment.ProcessorCount, MinCores));

        double memoryGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024);
        if (memoryGb > 0 && memoryGb < MinMemoryGb)
            warnings.Add(Localization.L.T("env.memory", $"{memoryGb:F1}", $"{MinMemoryGb:F1}"));

        double? freeGb = FreeSpaceGb(outputPath);
        if (freeGb is { } free && free < MinFreeDiskGb)
            warnings.Add(Localization.L.T("env.disk", $"{free:F1}", $"{MinFreeDiskGb:F1}"));

        // v7.9.6.3: the operating system floor, which used to be prose in
        // docs/requirements.md only. The JIT build rolls forward across .NET
        // versions, so an older machine does not fail to start -- it fails
        // somewhere stranger, and this line is what saves the bug report.
        if (OperatingSystem.IsWindows() && Environment.OSVersion.Version.Build < MinWindowsBuild)
        {
            warnings.Add(Localization.L.T("env.os.windows",
                Environment.OSVersion.Version.ToString(), MinWindowsBuild));
        }
        if (OperatingSystem.IsLinux() && GlibcVersion() is { } glibc && glibc < MinGlibc)
            warnings.Add(Localization.L.T("env.os.glibc", $"{glibc:F2}", $"{MinGlibc:F2}"));

        return warnings;
    }

    /// <summary>
    /// The running glibc's version, or null when it cannot be determined
    /// (musl, a static runtime, an exotic loader).
    ///
    /// <para><c>gnu_get_libc_version</c> is the only interface glibc exposes for
    /// this; it returns a pointer to a NUL-terminated "2.28" string. Everything
    /// here is resolved at run time rather than declared as a DllImport, so the
    /// NativeAOT build links against nothing extra and a non-glibc system simply
    /// answers null.</para>
    /// </summary>
    public static double? GlibcVersion()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            if (!NativeLibrary.TryLoad("libc.so.6", out IntPtr libc)) return null;
            try
            {
                if (!NativeLibrary.TryGetExport(libc, "gnu_get_libc_version", out IntPtr fn)) return null;
                var get = Marshal.GetDelegateForFunctionPointer<GnuGetLibcVersion>(fn);
                IntPtr text = get();
                if (text == IntPtr.Zero) return null;
                string? version = Marshal.PtrToStringUTF8(text);
                if (version is null) return null;
                // "2.28" -> 2.28; a longer "2.28-251" keeps the numeric head.
                int dot = version.IndexOf('.');
                if (dot < 0) return null;
                int end = dot + 1;
                while (end < version.Length && (char.IsDigit(version[end]) || version[end] == '.')) end++;
                return double.TryParse(version[..end], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double parsed) ? parsed : null;
            }
            finally
            {
                NativeLibrary.Free(libc);
            }
        }
        catch (Exception exc) when (exc is DllNotFoundException or EntryPointNotFoundException
                                        or BadImageFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>glibc's own version accessor: <c>const char *gnu_get_libc_version(void)</c>.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GnuGetLibcVersion();

    /// <summary>
    /// Free space where the product will land. Null when the drive cannot be
    /// read -- a network share, a path that does not exist yet -- because a
    /// check that cannot answer must not warn about a guess.
    /// </summary>
    public static double? FreeSpaceGb(string? outputPath)
    {
        try
        {
            string full = Path.GetFullPath(outputPath.HasText ? outputPath : ".");
            string? root = Path.GetPathRoot(full);
            if (!root.HasText) return null;
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return null;
            return drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
        }
        catch (Exception exc) when (exc is ArgumentException or IOException
                                        or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}
