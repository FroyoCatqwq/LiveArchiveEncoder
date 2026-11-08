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
        string ffmpeg, string input, string output, RecompressSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!settings.Enabled) return (true, null);
        if (settings.Stages().FirstOrDefault() is not { } first) return (true, null);
        (bool ok, string? error, _) = ApplyWithPath(
            ffmpeg, input, output, first, cancellationToken);
        return (ok, error);
    }

    /// <summary>
    /// Compatibility shim for the single-pass callers that do not need the
    /// placed path. The pipeline uses <see cref="ApplyWithPath"/>.
    /// </summary>
    public static (bool Ok, string? Error) Apply(
        string ffmpeg, string input, string output, RecompressPass pass,
        CancellationToken cancellationToken = default)
    {
        (bool ok, string? error, _) = ApplyWithPath(
            ffmpeg, input, output, pass, cancellationToken);
        return (ok, error);
    }

    /// <summary>
    /// Run one pass, writing to <c>output.part.ext</c> until it succeeds.
    /// v7.9.9.2: returns where the file REALLY landed, because Promote may save
    /// it as <c>name (2).ext</c> when the target is held by a player. The old
    /// two-tuple made PostPipeline derive a `_web` name that did not exist.
    /// </summary>
    public static (bool Ok, string? Error, string? PlacedPath) ApplyWithPath(
        string ffmpeg, string input, string output, RecompressPass pass,
        CancellationToken cancellationToken = default)
    {
        // v7.9.8.1: the same ".part" rule the live pass follows. This stage was
        // the one write path v7.9.8 missed -- `ffmpeg -y` truncates the
        // destination as it opens it, so a second pass that failed halfway used
        // to destroy the "_web" file it was replacing, leaving a short file that
        // looks finished. The live product survives a bad second pass; its
        // predecessor did not.
        var dst = new FileInfo(output);
        (bool available, string? availabilityError) = OutputFile.EnsureTargetAvailable(dst);
        if (!available) return (false, availabilityError, null);

        FileInfo part = OutputFile.PartPath(dst);

        (List<string> argv, string? error) = BuildArgv(ffmpeg, input, part.FullName, pass);
        if (error is not null) return (false, error, null);

        string? dir = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        CaptureResult result;
        try
        {
            result = ProcessRunner.Run(argv, TimeoutSeconds,
                cancellationToken: cancellationToken);
        }
        // A bad --ffmpeg path or an unreadable input surfaces here as an OS
        // exception, not as an exit code. Report it the same way as any other
        // failure: the live product must survive this stage going wrong.
        // v7.9.9.3: cancellation must also land here -- otherwise the exception
        // escaped PostPipeline and the finished .part was never discarded.
        catch (Exception exc) when (exc is InvalidOperationException
                                       or System.ComponentModel.Win32Exception
                                       or TimeoutException
                                       or IOException
                                       or OperationCanceledException)
        {
            OutputFile.Discard(part);
            return (false, exc.Message, null);
        }

        if (result.ExitCode != 0)
        {
            OutputFile.Discard(part);
            return (false, $"recompress failed (exit {result.ExitCode}): {Tail(result.Stderr)}", null);
        }

        part.Refresh();
        if (!part.Exists || part.Length == 0)
        {
            OutputFile.Discard(part);
            return (false, "the recompressed file was not produced", null);
        }

        try
        {
            FileInfo placed = OutputFile.Promote(part, dst);
            return (true, null, placed.FullName);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException
                                       or InvalidOperationException)
        {
            // v7.9.8.10: the second pass already succeeded. Discard() used to
            // delete the only good copy here; keep it and say where instead.
            OutputFile.Keep(part, dst, exc.Message);
            return (false, $"could not put the recompressed file in place: {exc.Message}; "
                + $"the finished file is kept at {part.FullName}", null);
        }
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

        // v7.9.9.1: the container name becomes a file extension, so it must not
        // carry a separator or a parent reference -- a typo used to be able to
        // write outside the input's directory.
        string ext = new([.. container.Or("mp4").TrimStart('.').ToLowerInvariant()
            .Where(char.IsAsciiLetterOrDigit)]);
        if (ext.Length == 0) ext = "mp4";
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
