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
    /// Build the full argument vector for one pass. Separate from running it so
    /// the shape can be asserted on without invoking FFmpeg.
    /// </summary>
    public static (List<string> Argv, string? Error) BuildArgv(
        string ffmpeg, string input, string output, RecompressPass pass)
    {
        (List<string> tokens, string? error) = PostArgs.Parse(pass.Args);
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

    /// <summary>The first pass of a whole chain -- the pre-v7.9.8.3 shape.</summary>
    public static (List<string> Argv, string? Error) BuildArgv(
        string ffmpeg, string input, string output, RecompressSettings settings) =>
        BuildArgv(ffmpeg, input, output,
            new RecompressPass { Args = settings.Args, Container = settings.Container });

    /// <summary>
    /// Run a whole chain's first pass. Kept because the single-pass call sites
    /// and tests read better this way; <see cref="Apply(string, string, string, RecompressPass)"/>
    /// is what the pipeline actually loops over.
    /// </summary>
    public static (bool Ok, string? Error) Apply(
        string ffmpeg, string input, string output, RecompressSettings settings) =>
        !settings.Enabled ? (true, null)
        : settings.Stages().FirstOrDefault() is { } first
            ? Apply(ffmpeg, input, output, first)
            : (true, null);

    /// <summary>Run one pass, writing to <c>output.part.ext</c> until it succeeds.</summary>
    public static (bool Ok, string? Error) Apply(
        string ffmpeg, string input, string output, RecompressPass pass)
    {
        // v7.9.8.1: the same ".part" rule the live pass follows. This stage was
        // the one write path v7.9.8 missed -- `ffmpeg -y` truncates the
        // destination as it opens it, so a second pass that failed halfway used
        // to destroy the "_web" file it was replacing, leaving a short file that
        // looks finished. The live product survives a bad second pass; its
        // predecessor did not.
        var dst = new FileInfo(output);
        FileInfo part = OutputFile.PartPath(dst);

        (List<string> argv, string? error) = BuildArgv(ffmpeg, input, part.FullName, pass);
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
            OutputFile.Discard(part);
            return (false, exc.Message);
        }

        if (result.ExitCode != 0)
        {
            OutputFile.Discard(part);
            return (false, $"recompress failed (exit {result.ExitCode}): {Tail(result.Stderr)}");
        }

        part.Refresh();
        if (!part.Exists || part.Length == 0)
        {
            OutputFile.Discard(part);
            return (false, "the recompressed file was not produced");
        }

        try
        {
            OutputFile.Promote(part, dst);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException
                                       or InvalidOperationException)
        {
            OutputFile.Discard(part);
            return (false, $"could not put the recompressed file in place: {exc.Message}");
        }

        return (true, null);
    }

    /// <summary>
    /// <c>clip_live.rm</c> becomes <c>clip_live_web.&lt;container&gt;</c>: "_web"
    /// marks the file a site would have served, and it never stacks onto the
    /// live product's name.
    ///
    /// <para>v7.9.8.3: a chain numbers its later hops <c>_web2</c>, <c>_web3</c>.
    /// The number comes from the pass, and the name is always derived from the
    /// <em>live</em> product -- deriving pass 2 from pass 1's name would give
    /// <c>clip_live_web_web2</c>, which says nothing about the order it joined
    /// the chain in.</para>
    /// </summary>
    public static string DeriveOutputPath(string inputPath, string container, int pass = 1)
    {
        string dir = Path.GetDirectoryName(inputPath) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(inputPath);

        string ext = container.Or("mp4").TrimStart('.').ToLowerInvariant();
        return Path.Combine(dir, stem + Suffix(pass) + "." + ext);
    }

    /// <summary>The "_web" / "_web2" part of a hop's name.</summary>
    public static string Suffix(int pass) => pass <= 1 ? "_web" : $"_web{pass}";

    private static string Tail(string text)
    {
        string flat = text.Trim().Replace("\r", " ").Replace("\n", " ");
        return flat.Length <= 300 ? flat : flat[^300..];
    }
}
