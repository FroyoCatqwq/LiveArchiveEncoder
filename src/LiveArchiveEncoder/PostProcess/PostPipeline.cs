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

    /// <summary>The last hop that was written, i.e. the delivered file.</summary>
    public string? RecompressPath { get; set; }

    /// <summary>
    /// v7.9.8.3: every hop that was written, in order. A single-pass run has one
    /// entry, which is also <see cref="RecompressPath"/>.
    /// </summary>
    public List<string> Passes { get; } = [];

    /// <summary>How many passes the chain has; 0 when it is not armed.</summary>
    public int PassCount { get; set; }

    /// <summary>1-based number of the pass that failed, or 0.</summary>
    public int FailedPass { get; set; }

    public string? RecompressError { get; set; }

    /// <summary>Name of the stage that failed, for a localised message.</summary>
    public string? FailedStage { get; set; }

    public bool Failed => FailedStage is not null;

    /// <summary>
    /// The stage name to report: "recompress" for a lone pass, and
    /// "recompress 2/3" once there is a chain -- a bare "recompress" would not
    /// say which hop failed.
    /// </summary>
    public string StageName => PassCount > 1 && FailedPass > 0
        ? $"recompress {FailedPass}/{PassCount}"
        : FailedStage ?? "recompress";
}

/// <summary>
/// Runs the stages that follow the live encode: the video-site recompress chain.
///
/// v7.9.1 removed the watermark stage that used to run first. The ordering it
/// existed for -- a site burned its logo in and then transcoded, so the mark
/// suffered the same compression the picture did -- is recorded in
/// <c>archive/retired/RetiredWatermark.cs</c>, together with the code, should
/// the feature come back as its own project.
///
/// v7.9.8.3: the recompress step is a chain rather than a single pass. Each hop
/// reads the previous hop's product, so N hops are N real transcodes -- which is
/// what "uploaded to a 2007 portal, then re-uploaded to a 2011 one" does to a
/// file. Every intermediate file is kept: they are each a real artefact of the
/// process, and only the last one is the delivery.
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
        if (!config.Recompress.Enabled) return outcome;

        List<RecompressPass> stages = config.Recompress.Stages();
        outcome.PassCount = stages.Count;
        if (stages.Count == 0) return outcome;

        string input = livePath;
        for (int i = 0; i < stages.Count; i++)
        {
            int pass = i + 1;
            // Named from the live product, not from the previous hop: see
            // Recompress.DeriveOutputPath.
            string output = Recompress.DeriveOutputPath(livePath, stages[i].Container, pass);
            (bool ok, string? error) = Recompress.Apply(ffmpeg, input, output, stages[i]);

            if (!ok)
            {
                outcome.FailedStage = "recompress";
                outcome.FailedPass = pass;
                outcome.RecompressError = error;
                return outcome;
            }

            outcome.RecompressRan = true;
            outcome.Passes.Add(output);
            outcome.RecompressPath = output;
            outcome.FinalPath = output;
            input = output;
        }

        return outcome;
    }

    /// <summary>True when the stage is armed, so callers can skip the call entirely.</summary>
    public static bool Needed(Configuration.Config config) =>
        config.Recompress.Enabled && config.Recompress.Stages().Count > 0;
}
