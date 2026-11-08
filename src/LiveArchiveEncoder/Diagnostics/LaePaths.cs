namespace LiveArchiveEncoder.Diagnostics;

/// <summary>
/// v7.9.9: where diagnostics live. Logs are deliberately kept out of
/// <c>ConfigStore.RootDirectory</c> -- that directory holds config.json, output
/// shards, capability caches and update state, and is redirected wholesale by
/// <c>LAE_CONFIG_DIR</c>. Mixing rolling logs into it would pollute the user's
/// settings directory and break the e2e isolation story.
/// </summary>
public static class LaePaths
{
    public const string LogDirVariable = "LAE_LOG_DIR";
    public const string LogLevelVariable = "LAE_LOG_LEVEL";
    public const string LogMirrorVariable = "LAE_LOG_MIRROR";

    /// <summary>Same application data root as <c>FFmpeg.Toolchain.Root</c>'s parent.</summary>
    public static string DataRoot { get; } = ResolveDataRoot();

    /// <summary>
    /// <c>LAE_LOG_DIR</c> wins; otherwise &lt;DataRoot&gt;/logs. Read on every
    /// call so a test (or a wrapper script) can redirect it before Init().
    /// </summary>
    public static string LogDirectory
    {
        get
        {
            string? overrideDir = Environment.GetEnvironmentVariable(LogDirVariable);
            return string.IsNullOrWhiteSpace(overrideDir)
                ? Path.Combine(DataRoot, "logs")
                : Path.GetFullPath(overrideDir.Trim().Trim('"'));
        }
    }

    private static string ResolveDataRoot()
    {
        string? appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(appData)
            ? AppContext.BaseDirectory
            : Path.Combine(appData, "LiveArchiveEncoder");
    }
}
