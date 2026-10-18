using LiveArchiveEncoder.Configuration;

namespace LiveArchiveEncoder.PostProcess;

/// <summary>What the post-processing chain produced.</summary>
public sealed class PostProcessOutcome
{
    /// <summary>The live recording; always present, always kept.</summary>
    public required string LivePath { get; init; }

    /// <summary>The file to play or hand on; equals <see cref="LivePath"/> when neither stage ran.</summary>
    public string FinalPath { get; set; } = "";

    public bool WatermarkRan { get; set; }
    public string? WatermarkPath { get; set; }
    public string? WatermarkError { get; set; }

    /// <summary>True when the watermarked intermediate was deleted after use.</summary>
    public bool IntermediateRemoved { get; set; }

    public bool RecompressRan { get; set; }
    public string? RecompressPath { get; set; }
    public string? RecompressError { get; set; }

    /// <summary>Name of the stage that failed, for a localised message.</summary>
    public string? FailedStage { get; set; }

    public bool Failed => FailedStage is not null;
}

/// <summary>
/// Runs the stages that follow the live encode, in the order the user asked for:
/// watermark first, then recompress.
///
/// The ordering is the whole point -- a site burned its logo in and then
/// transcoded, so the watermark has to suffer the same compression the picture
/// does. Doing it the other way round leaves a crisp modern-looking mark sitting
/// on top of a wrecked picture.
///
/// Failure policy: the live product is never touched. A failed stage is reported
/// with the FFmpeg error and the chain stops there, leaving whatever was already
/// written in place. A watermark that cannot be applied is not a reason to throw
/// away a twenty-minute encode.
/// </summary>
public static class PostPipeline
{
    public static PostProcessOutcome Run(
        string ffmpeg, string livePath, Config config)
    {
        var outcome = new PostProcessOutcome { LivePath = livePath, FinalPath = livePath };

        string current = livePath;

        // ---- watermark ----
        if (config.Watermark.Enabled)
        {
            string wmOut = Watermark.DeriveOutputPath(livePath);
            (bool ok, string? error) = Watermark.Apply(
                ffmpeg, current, wmOut, config.Watermark, config.Width, config.Height);

            if (!ok)
            {
                outcome.FailedStage = "watermark";
                outcome.WatermarkError = error;
                return outcome;
            }

            outcome.WatermarkRan = true;
            outcome.WatermarkPath = wmOut;
            current = wmOut;
            outcome.FinalPath = wmOut;
        }

        // ---- recompress ----
        if (config.Recompress.Enabled)
        {
            string rcOut = Recompress.DeriveOutputPath(current, config.Recompress.Container);
            (bool ok, string? error) = Recompress.Apply(ffmpeg, current, rcOut, config.Recompress);

            if (!ok)
            {
                outcome.FailedStage = "recompress";
                outcome.RecompressError = error;
                return outcome;
            }

            outcome.RecompressRan = true;
            outcome.RecompressPath = rcOut;
            outcome.FinalPath = rcOut;

            // The intermediate only existed to feed this stage, so drop it
            // unless the user asked to keep it. Never drop it on failure -- it
            // is then the newest good file.
            if (!config.Recompress.KeepIntermediate
                && outcome.WatermarkPath is { Length: > 0 } wm
                && !string.Equals(Path.GetFullPath(wm), Path.GetFullPath(livePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(wm);
                outcome.IntermediateRemoved = true;
            }
        }

        return outcome;
    }

    /// <summary>True when either stage is armed, so callers can skip the call entirely.</summary>
    public static bool Needed(Configuration.Config config) =>
        config.Watermark.Enabled || config.Recompress.Enabled;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            // Leftovers are harmless; a crash here is not.
        }
    }
}
