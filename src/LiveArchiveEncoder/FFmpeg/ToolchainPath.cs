using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using LiveArchiveEncoder.Configuration;

namespace LiveArchiveEncoder.FFmpeg;

/// <summary>
/// Puts a managed FFmpeg install on the shell's <c>PATH</c>.
///
/// <para>v7.9.8.11: installing a build used to make it visible only to this
/// program's own discovery (<see cref="Toolchain.FFmpegFor"/>). A new cmd,
/// PowerShell or bash window still had no <c>ffmpeg</c> / <c>ffprobe</c>, which
/// is exactly what "I installed FFmpeg with the tool" is expected to mean.</para>
///
/// <para>A child process cannot change its parent shell's environment, so
/// publishing has two halves: persist the directory where the <em>next</em>
/// shell reads it (the user PATH on Windows, shell startup files plus
/// <c>~/.local/bin</c> symlinks on Linux), and print the one line that fixes
/// the <em>current</em> shell (<see cref="ActivateLine"/>). Both are idempotent,
/// so switching or reinstalling a distribution does not stack entries.</para>
/// </summary>
public static class ToolchainPath
{
    public const string BeginMarker = "# >>> LiveArchiveEncoder FFmpeg >>>";
    public const string EndMarker = "# <<< LiveArchiveEncoder FFmpeg <<<";

    private const string PathVariable = "PATH";

    private static readonly string[] Shells =
        ["auto", "powershell", "pwsh", "ps", "cmd", "bash", "zsh", "sh", "fish"];

    public static char Separator => OperatingSystem.IsWindows() ? ';' : ':';

    public static bool IsShell(string? value) =>
        value is not null && Shells.Contains(value.Trim().ToLowerInvariant());

    /// <summary>Append a directory once, without disturbing existing entries.</summary>
    public static string AddToPath(string? existing, string dir, char separator)
    {
        string text = existing ?? "";
        if (text.Split(separator, StringSplitOptions.RemoveEmptyEntries).Any(p => SamePath(p, dir, separator)))
            return text;
        string trimmed = text.TrimEnd(separator);
        return trimmed.Length == 0 ? dir : trimmed + separator + dir;
    }

    /// <summary>Drop a directory, keeping every other entry in its original order.</summary>
    public static string RemoveFromPath(string? existing, string dir, char separator) =>
        string.Join(separator, (existing ?? "")
            .Split(separator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !SamePath(p, dir, separator)));

    private static bool SamePath(string left, string right, char separator)
    {
        string Normalize(string value) => value.Trim().Trim('"')
            .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar, separator);
        return string.Equals(Normalize(left), Normalize(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>The idempotent block written into a POSIX shell startup file.</summary>
    public static string ProfileBlock(string dir) =>
        $"{BeginMarker}\nexport PATH=\"{dir}:$PATH\"\n{EndMarker}\n";

    public static string RemoveProfileBlock(string content)
    {
        var kept = new List<string>();
        bool inside = false;
        foreach (string line in content.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Trim() == BeginMarker) { inside = true; continue; }
            if (inside)
            {
                if (line.Trim() == EndMarker) inside = false;
                continue;
            }
            kept.Add(line);
        }
        string body = string.Join("\n", kept).TrimEnd('\n');
        return body.Length == 0 ? "" : body + "\n";
    }

    /// <summary>Replace our block, so switching distributions rewrites it in place.</summary>
    public static string InsertProfileBlock(string content, string dir) =>
        RemoveProfileBlock(content) + ProfileBlock(dir);

    /// <summary>The one line a user can paste (or eval) into an already-open shell.</summary>
    public static string ActivateLine(string? shell, string dir)
    {
        string name = (shell ?? "").Trim().ToLowerInvariant();
        if (name is "cmd" or "command" or "bat") return $"set \"PATH={dir};%PATH%\"";
        if (name is "powershell" or "pwsh" or "ps") return $"$env:PATH = \"{dir};$env:PATH\"";
        if (name == "fish") return $"set -gx PATH \"{dir}\" $PATH";
        return $"export PATH=\"{dir}:$PATH\"";
    }

    /// <summary>Best guess for the shell that launched this process.</summary>
    public static string DefaultShell()
    {
        if (OperatingSystem.IsWindows()) return "powershell";
        string? shell = Environment.GetEnvironmentVariable("SHELL");
        string name = string.IsNullOrEmpty(shell)
            ? "bash" : System.IO.Path.GetFileName(shell).ToLowerInvariant();
        return name is "fish" or "zsh" or "bash" or "sh" ? name : "bash";
    }

    /// <summary>The managed bin directory to advertise, or null when nothing is installed.</summary>
    public static string? ActiveBinDir()
    {
        List<InstalledToolchain> installed = Toolchain.Installed();
        if (installed.Count == 0) return null;

        string? chosen = null;
        try { chosen = ConfigStore.Load().FfmpegSource; } catch { /* damaged settings: pick any */ }
        InstalledToolchain pick = installed.FirstOrDefault(
            x => string.Equals(x.Id, chosen, StringComparison.OrdinalIgnoreCase)) ?? installed[0];
        return System.IO.Path.GetDirectoryName(pick.FFmpeg);
    }

    /// <summary>
    /// Make <paramref name="binDir"/> visible to new shells and to this process.
    /// A failure here never fails the install: the build is already on disk and
    /// the caller says what the user can do instead.
    /// </summary>
    public static (bool Ok, string? Error) Publish(string binDir, Action<string>? log = null)
    {
        try
        {
            if (OperatingSystem.IsWindows()) PublishWindows(binDir);
            else PublishUnix(binDir);

            Environment.SetEnvironmentVariable(PathVariable,
                AddToPath(Environment.GetEnvironmentVariable(PathVariable), binDir, Separator),
                EnvironmentVariableTarget.Process);

            log?.Invoke($"PATH    : {binDir}");
            log?.Invoke("New terminals can run ffmpeg / ffprobe directly. For this one:");
            log?.Invoke("  " + ActivateLine(DefaultShell(), binDir));
            log?.Invoke("  (or: --ffmpeg-env powershell|cmd|bash|zsh|fish)");
            return (true, null);
        }
        catch (Exception exc) when (exc is UnauthorizedAccessException or SecurityException
                                    or IOException or ArgumentException or NotSupportedException)
        {
            return (false, exc.Message);
        }
    }

    /// <summary>Reverse <see cref="Publish"/> when a distribution is deleted.</summary>
    public static void Unpublish(string binDir)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                string user = Environment.GetEnvironmentVariable(PathVariable, EnvironmentVariableTarget.User) ?? "";
                Environment.SetEnvironmentVariable(PathVariable,
                    RemoveFromPath(user, binDir, ';'), EnvironmentVariableTarget.User);
                try { BroadcastEnvironmentChange(); } catch { /* best effort */ }
            }
            else
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home))
                {
                    foreach (string name in new[] { ".profile", ".bashrc", ".zshrc" })
                    {
                        string path = System.IO.Path.Combine(home, name);
                        if (!File.Exists(path)) continue;
                        string content = File.ReadAllText(path);
                        if (!content.Contains(binDir, StringComparison.Ordinal)) continue;
                        File.WriteAllText(path, RemoveProfileBlock(content), new UTF8Encoding(false));
                    }
                    DropSymlinks(home, binDir);
                }
            }
            Environment.SetEnvironmentVariable(PathVariable,
                RemoveFromPath(Environment.GetEnvironmentVariable(PathVariable), binDir, Separator),
                EnvironmentVariableTarget.Process);
        }
        catch { /* best effort: the build itself is already gone */ }
    }

    private static void PublishWindows(string dir)
    {
        string user = Environment.GetEnvironmentVariable(PathVariable, EnvironmentVariableTarget.User) ?? "";
        Environment.SetEnvironmentVariable(PathVariable, AddToPath(user, dir, ';'), EnvironmentVariableTarget.User);
        // Tell already-running Explorer windows to re-read the user environment,
        // so a terminal opened from one of them sees the new PATH immediately.
        try { BroadcastEnvironmentChange(); } catch { /* best effort */ }
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    private static void BroadcastEnvironmentChange()
    {
        const int HwndBroadcast = 0xffff;
        const uint WmSettingChange = 0x001A;
        const uint SmtoAbortIfHung = 0x0002;
        _ = SendMessageTimeout((IntPtr)HwndBroadcast, WmSettingChange, IntPtr.Zero,
            "Environment", SmtoAbortIfHung, 2000, out _);
    }

    private static void PublishUnix(string dir)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return;

        // .profile is always ours to write (POSIX login shells read it); the
        // interactive-only files are updated only when the user already has
        // them, so installing FFmpeg never creates an unused dotfile.
        foreach (string name in new[] { ".profile", ".bashrc", ".zshrc" })
        {
            string path = System.IO.Path.Combine(home, name);
            if (!File.Exists(path) && name != ".profile") continue;
            string old = File.Exists(path) ? File.ReadAllText(path) : "";
            string updated = InsertProfileBlock(old, dir);
            if (updated != old) File.WriteAllText(path, updated, new UTF8Encoding(false));
        }

        // ~/.local/bin is on PATH on Debian/Ubuntu login shells. Symlinks make
        // the tools available even where no startup file is sourced.
        string localBin = System.IO.Path.Combine(home, ".local", "bin");
        Directory.CreateDirectory(localBin);
        foreach (string tool in new[] { "ffmpeg", "ffprobe", "ffplay" })
        {
            string source = System.IO.Path.Combine(dir, tool);
            string link = System.IO.Path.Combine(localBin, tool);
            if (!File.Exists(source)) continue;
            try
            {
                var current = new FileInfo(link);
                if (current.Exists && current.LinkTarget is null) continue; // a real file: not ours
                if (current.Exists || current.LinkTarget is not null) current.Delete();
                File.CreateSymbolicLink(link, source);
            }
            catch { /* best effort: profile block already covers it */ }
        }
    }

    private static void DropSymlinks(string home, string dir)
    {
        string localBin = System.IO.Path.Combine(home, ".local", "bin");
        foreach (string tool in new[] { "ffmpeg", "ffprobe", "ffplay" })
        {
            string link = System.IO.Path.Combine(localBin, tool);
            try
            {
                var info = new FileInfo(link);
                if (info.LinkTarget is not null
                    && info.LinkTarget.Contains(dir, StringComparison.Ordinal))
                {
                    info.Delete();
                }
            }
            catch { /* best effort */ }
        }
    }
}
