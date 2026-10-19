using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.FFmpeg;
using LiveArchiveEncoder.Core;

namespace LiveArchiveEncoder.PostProcess;

/// <summary>
/// The recompress stage: what a video site did to an upload.
///
/// The user writes the middle of an FFmpeg command and the program owns both
/// ends:
/// <c>ffmpeg -y -i &lt;live product&gt; &lt;user args&gt; &lt;output&gt;</c>.
/// Handing over the ends too would let a stray path overwrite the source, so
/// <see cref="PostArgs"/> tokenises and vets the fragment first.
///
/// Off by default. Turning it on is a deliberate choice: it re-encodes an
/// already lossy file, which is exactly the point (that is what the sites did)
/// but should never happen to someone who did not ask.
/// </summary>
public static class Recompress
{
    private const int TimeoutSeconds = 3600;

    /// <summary>
    /// Build the full argument vector. Separate from running it so the shape can
    /// be asserted on without invoking FFmpeg.
    /// </summary>
    public static (List<string> Argv, string? Error) BuildArgv(
        string ffmpeg, string input, string output, RecompressSettings settings)
    {
        (List<string> tokens, string? error) = PostArgs.Parse(settings.Args);
        if (error is not null) return ([], error);

        var argv = new List<string>
        {
            ffmpeg, "-y", "-hide_banner", "-loglevel", "error",
            "-i", input,
        };
        argv.AddRange(tokens);
        argv.Add(output);
        return (argv, null);
    }

    public static (bool Ok, string? Error) Apply(
        string ffmpeg, string input, string output, RecompressSettings settings)
    {
        if (!settings.Enabled) return (true, null);

        (List<string> argv, string? error) = BuildArgv(ffmpeg, input, output, settings);
        if (error is not null) return (false, error);

        string? dir = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        CaptureResult result;
        try
        {
            result = ProcessRunner.Run(argv, TimeoutSeconds);
        }
        // A bad --ffmpeg path or an unreadable input surfaces here as an OS
        // exception, not as an exit code. Report it the same way as any other
        // failure: the live product must survive this stage going wrong.
        catch (Exception exc) when (exc is InvalidOperationException
                                       or System.ComponentModel.Win32Exception
                                       or TimeoutException
                                       or IOException)
        {
            return (false, exc.Message);
        }

        if (result.ExitCode != 0)
            return (false, $"recompress failed (exit {result.ExitCode}): {Tail(result.Stderr)}");

        if (!File.Exists(output) || new FileInfo(output).Length == 0)
            return (false, "the recompressed file was not produced");

        return (true, null);
    }

    /// <summary>
    /// <c>clip_live.rm</c> and <c>clip_live_wm.mp4</c> both become
    /// <c>clip_live_web.&lt;container&gt;</c>: "_wm" marks an intermediate, "_web"
    /// marks the file a site would have served, and the two never stack up into
    /// something unreadable.
    /// </summary>
    public static string DeriveOutputPath(string inputPath, string container)
    {
        string dir = Path.GetDirectoryName(inputPath) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(inputPath);
        if (stem.EndsWith("_wm", StringComparison.Ordinal))
            stem = stem[..^"_wm".Length];

        string ext = container.Or("mp4").TrimStart('.').ToLowerInvariant();
        return Path.Combine(dir, stem + "_web." + ext);
    }

    private static string Tail(string text)
    {
        string flat = text.Trim().Replace("\r", " ").Replace("\n", " ");
        return flat.Length <= 300 ? flat : flat[^300..];
    }
}
