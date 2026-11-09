using LiveArchiveEncoder.Core;

namespace LiveArchiveEncoder.FFmpeg;

/// <summary>
/// v7.9.8.13.1: one official 7-Zip for both platforms, always from
/// <c>https://github.com/ip7z/7zip/releases</c>. Windows uses the x64 installer
/// (<c>7z2605-x64.exe</c>, silent <c>/S</c> + <c>/D=dir</c> per the 7-Zip FAQ);
/// Linux uses the official CLI tarball (<c>7z2605-linux-x64.tar.xz</c>), whose
/// <c>7zz</c> is a full 7-Zip and understands .zip / .7z / .rar alike.
///
/// <para>An installation already on the machine always wins. This module only
/// downloads when neither the system nor our own private copy has 7-Zip, so a
/// normal run never touches the network just to read a font pack.</para>
/// </summary>
public static class SevenZip
{
    public const string ReleaseTag = "26.05";
    public const string ReleasePage = "https://github.com/ip7z/7zip/releases";

    private const string DownloadBase =
        "https://github.com/ip7z/7zip/releases/download/26.05/";

    public static string AssetFor(DistPlatform platform) =>
        platform == DistPlatform.Windows ? "7z2605-x64.exe" : "7z2605-linux-x64.tar.xz";

    public static string UrlFor(DistPlatform platform) => DownloadBase + AssetFor(platform);

    /// <summary>User-writable home of the copy we install ourselves.</summary>
    public static string DirectoryPath => Path.Combine(Toolchain.Root, "7zip");

    public static string ExeNameFor(DistPlatform platform) =>
        platform == DistPlatform.Windows ? "7z.exe" : "7zz";

    private static string OwnExe() =>
        Path.Combine(DirectoryPath, ExeNameFor(FfmpegCatalog.Current));

    /// <summary>System 7-Zip first (PATH / Program Files), then our private copy.</summary>
    public static string? Find()
    {
        string? system = Toolchain.FindSevenZip();
        if (system is not null) return system;
        return File.Exists(OwnExe()) ? OwnExe() : null;
    }

    /// <summary>
    /// Get a usable 7-Zip executable, downloading and unpacking the official
    /// build when necessary. Returns the path plus an error message on failure.
    /// </summary>
    public static (string? Exe, string? Error) Ensure(
        string? ghProxy = null, string? proxy = null, Action<string>? log = null)
    {
        string? existing = Find();
        if (existing is not null) return (existing, null);

        DistPlatform platform = FfmpegCatalog.Current;
        string dir = DirectoryPath;
        string exe = OwnExe();
        string asset = Path.Combine(dir, AssetFor(platform));
        try { Directory.CreateDirectory(dir); }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        { return (null, exc.Message); }

        // v7.9.8.13.2: when the caller has no proxy settings (the font-pack
        // path), still try the auto-detected GitHub front rather than a direct
        // connection that is known to hang on some networks.
        string? front = ghProxy;
        if (string.IsNullOrEmpty(front) && string.IsNullOrEmpty(proxy))
            front = GitHubProxy.Detect(UrlFor(platform), null, log);

        string url = GitHubProxy.Apply(UrlFor(platform), front);
        log?.Invoke($"7-Zip not found; downloading the official {ReleaseTag} build: {url}");
        (bool ok, string? dlError) = Toolchain.DownloadFile(url, asset, proxy, log);
        if (!ok) return (null, $"cannot download 7-Zip: {dlError}");

        CaptureResult run;
        if (platform == DistPlatform.Windows)
        {
            // 7-Zip FAQ: "/S" = silent, /D="dir" = destination; case-sensitive,
            // and quoted because %LOCALAPPDATA% often contains spaces.
            run = ProcessRunner.Run([asset, "/S", $"/D=\"{dir}\""], 180);
        }
        else
        {
            run = ProcessRunner.Run(["tar", "-xJf", asset, "-C", dir], 180);
            if (run.ExitCode == 0)
            {
                CaptureResult chmod = ProcessRunner.Run(["chmod", "+x", exe], 30);
                if (chmod.ExitCode != 0)
                    return (null, $"could not make {exe} executable: {chmod.Stderr.Trim()}");
            }
        }
        if (run.ExitCode != 0)
        {
            TryDelete(asset);
            return (null, $"could not unpack 7-Zip (exit {run.ExitCode}): "
                          + $"{(run.Stderr + run.Stdout).Trim()}");
        }
        if (!File.Exists(exe))
        {
            TryDelete(asset);
            return (null, $"7-Zip asset unpacked but {exe} is missing; "
                          + $"install it manually from {ReleasePage}");
        }

        // v7.9.9.5: the installer/archive is several MB and was left in the
        // private 7-Zip directory on every successful install.
        TryDelete(asset);
        log?.Invoke($"7-Zip ready at {exe}");
        return (exe, null);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception) { /* best effort: do not mask the real failure */ }
    }
}
