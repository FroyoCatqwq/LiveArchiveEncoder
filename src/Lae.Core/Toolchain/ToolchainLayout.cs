using Lae.Core.Settings;

namespace Lae.Core.Toolchain;

/// <summary>
/// Where a managed install lives, as pure path arithmetic.
///
/// <para>The base is the folder the settings file is in, not the program's own
/// folder: v8 publishes as a self-contained single file, which unpacks itself
/// into a temporary location at startup, so a toolchain installed next to the
/// executable would be gone by the next run. Putting it beside the settings also
/// means <c>LAE_CONFIG_DIR</c> moves both together, which is what a portable
/// install means.</para>
///
/// <para>Every distribution gets its own folder and every folder is replaced
/// whole, so switching between two installs is instant and a bad download can
/// never damage a working one.</para>
/// </summary>
public static class ToolchainLayout
{
    /// <summary>The folder under the settings root that holds every install.</summary>
    public const string FolderName = "ffmpeg";

    /// <summary>Root of all managed installs.</summary>
    public static string Root(string configRoot) => Path.Combine(configRoot, FolderName);

    /// <summary>One distribution's install folder.</summary>
    public static string Home(string configRoot, string id) => Path.Combine(Root(configRoot), id);

    /// <summary>Where the executables of one distribution live.</summary>
    public static string Bin(string configRoot, string id) => Path.Combine(Home(configRoot, id), "bin");

    /// <summary>
    /// Where a download is unpacked before it is published. Under the same root as
    /// the installs so the final step is a rename on one filesystem, and named so
    /// a leftover from a killed process is obviously ours.
    /// </summary>
    public static string Staging(string configRoot, string id)
        => Path.Combine(Root(configRoot), ".staging", id);

    /// <summary>The name a tool has on this platform.</summary>
    public static string ToolName(string tool)
        => OperatingSystem.IsWindows() ? tool + ".exe" : tool;

    /// <summary>Full path a tool would have in an install, whether or not it is there.</summary>
    public static string Tool(string configRoot, string id, string tool)
        => Path.Combine(Bin(configRoot, id), ToolName(tool));
}

/// <summary>
/// Turns the two settings that can name an FFmpeg into the one folder an encode
/// should search.
///
/// <para>One place, because there are three callers (<c>encode</c>,
/// <c>--recompress-only</c> and <c>setup</c>) and they must agree: a <c>setup</c>
/// report that looked somewhere else than the encode would be worse than no
/// report.</para>
/// </summary>
public static class ToolchainSource
{
    /// <summary>
    /// The folder to search, or null for "whatever is on PATH".
    ///
    /// <para>An explicit <see cref="ToolchainSettings.FfmpegPath"/> wins, because
    /// the user has already decided. Otherwise a distribution id means its managed
    /// install -- but only if it is really there: reporting a path that does not
    /// exist would hide the very problem <c>setup</c> exists to name.</para>
    ///
    /// <param name="present">
    /// "Does this path exist" -- a predicate rather than a call, because Core does
    /// not touch a disk. It has to answer yes for both a file and a folder, since
    /// FfmpegPath may name either.
    /// </param>
    /// </summary>
    public static string? Folder(ToolchainSettings toolchain, string? configRoot, Func<string, bool>? present = null)
    {
        string? explicitPath = toolchain.FfmpegPath;
        if (explicitPath.HasText)
        {
            bool exists = present?.Invoke(explicitPath) ?? true;
            if (!exists) return null;

            // Either the executable itself or the folder holding it; both are things
            // a person types. Both spellings are accepted on both platforms: a
            // settings file written on one machine may name the other platform's
            // executable, and deciding by *this* machine's convention would then read
            // a file path as a folder.
            return LooksLikeTool(explicitPath) ? Path.GetDirectoryName(explicitPath) : explicitPath;
        }

        if (configRoot.HasText && FfmpegCatalog.Find(toolchain.FfmpegSource) is { } dist)
        {
            string bin = ToolchainLayout.Bin(configRoot, dist.Id);
            string probe = Path.Combine(bin, ToolchainLayout.ToolName("ffmpeg"));
            return (present?.Invoke(probe) ?? true) ? bin : null;
        }

        return null;
    }

    private static bool LooksLikeTool(string path)
        => path.EndsWith("ffmpeg", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("ffmpeg.exe", StringComparison.OrdinalIgnoreCase);
}
