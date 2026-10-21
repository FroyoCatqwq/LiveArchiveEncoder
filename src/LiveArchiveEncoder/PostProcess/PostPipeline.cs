using LiveArchiveEncoder.Configuration;

namespace LiveArchiveEncoder.PostProcess;

/// <summary>What the post-processing chain produced.</summary>
public sealed class PostProcessOutcome
{
    /// <summary>The live recording; always present, always kept.</summary>
    public required string LivePath { get; init; }

    /// <summary>The file to play or hand on; equals <see cref="LivePath"/> when the stage did not run.</summary>
    public string FinalPath { get; set; } = "";

    public bool RecompressRan { get; set; }
    public string? RecompressPath { get; set; }
    public string? RecompressError { get; set; }

    /// <summary>Name of the stage that failed, for a localised message.</summary>
    public string? FailedStage { get; set; }

    public bool Failed => FailedStage is not null;
}

/// <summary>
/// Runs the stage that follows the live encode: the video-site recompress.
///
/// v7.9.1 removed the watermark stage that used to run first. The ordering it
/// existed for -- a site burned its logo in and then transcoded, so the mark
/// suffered the same compression the picture did -- is recorded in
/// <c>archive/retired/RetiredWatermark.cs</c>, together with the code, should
/// the feature come back as its own project.
///
/// Failure policy: the live product is never touched. A failed stage is reported
/// with the FFmpeg error and the chain stops there, leaving whatever was already
/// written in place. A recompress that cannot run is not a reason to throw away
/// a twenty-minute encode.
/// </summary>
public static class PostPipeline
{
    public static PostProcessOutcome Run(
        string ffmpeg, string livePath, Config config)
    {
        var outcome = new PostProcessOutcome { LivePath = livePath, FinalPath = livePath };

        // ---- recompress ----
        if (config.Recompress.Enabled)
        {
            string rcOut = Recompress.DeriveOutputPath(livePath, config.Recompress.Container);
            (bool ok, string? error) = Recompress.Apply(ffmpeg, livePath, rcOut, config.Recompress);

            if (!ok)
            {
                outcome.FailedStage = "recompress";
                outcome.RecompressError = error;
                return outcome;
            }

            outcome.RecompressRan = true;
            outcome.RecompressPath = rcOut;
            outcome.FinalPath = rcOut;
        }

        return outcome;
    }

    /// <summary>True when the stage is armed, so callers can skip the call entirely.</summary>
    public static bool Needed(Configuration.Config config) =>
        config.Recompress.Enabled;
}
