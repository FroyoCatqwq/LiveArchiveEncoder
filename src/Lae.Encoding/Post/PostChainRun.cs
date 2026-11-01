using Lae.Media;

namespace Lae.Encoding;

/// <summary>
/// Why a chain stopped.
///
/// <para>Declared here rather than as an exit code because this assembly has no
/// business knowing what number a program returns. The chain says what kind of
/// thing went wrong; the command decides that a plugin error is 4 and an unusable
/// product is 5.</para>
/// </summary>
public enum ChainFailure
{
    /// <summary>Nothing failed.</summary>
    None,

    /// <summary>An FFmpeg hop did not produce its output. Exit 1.</summary>
    Hop,

    /// <summary>A plugin could not be run, would not describe itself, or exited non-zero. Exit 4.</summary>
    Plugin,

    /// <summary>A plugin returned success and its product does not hold up. Exit 5.</summary>
    Verification,
}

/// <summary>
/// What one hop produced: nothing wrong, or a reason and the kind of thing it was.
///
/// <para>Convertible from <c>string?</c> so an FFmpeg hop can keep saying
/// <c>return null</c> / <c>return "鈥?</c>. That is not a shortcut: a bare string
/// from a hop can only mean "this hop failed", and the only hop that can fail in a
/// way worth distinguishing is a plugin, which does not use the conversion.</para>
/// </summary>
public sealed record HopResult
{
    /// <summary>The hop succeeded; its part product is ready to promote.</summary>
    public static readonly HopResult Ok = new();

    public ChainFailure Failure { get; init; } = ChainFailure.Hop;

    /// <summary>The sentence, already worded by whoever knows what went wrong.</summary>
    public string? Error { get; init; }

    public bool Failed => Error is not null;

    public static implicit operator HopResult(string? error)
        => error is null ? Ok : new HopResult { Failure = ChainFailure.Hop, Error = error };
}

/// <summary>
/// One link of the post-encode chain: a program that turns the product it is
/// given into another file.
///
/// <para>A delegate rather than a base class, because the two producers have
/// nothing in common except the shape of the call. An FFmpeg hop runs a command
/// line it was planned with; a plugin hop starts someone else's executable and
/// has to check its output; a future one might not start a process at all. What
/// they must agree on is only this: "write <c>part</c> from <c>input</c>, and
/// tell me why you could not".</para>
///
/// <para><see cref="Produce"/> writes to the <em>part</em> path, never to
/// <see cref="OutputPath"/>: the runner promotes it only after
/// <see cref="Produce"/> has returned success, so a hop that dies halfway leaves
/// a discardable temporary rather than a truncated file wearing the real name.
/// This is the same rule the live pass follows, and the runner is where it is
/// enforced once for every kind of hop.</para>
/// </summary>
public sealed record PostHop
{
    /// <summary>1-based position in the chain; what a failure reports.</summary>
    public required int Number { get; init; }

    /// <summary>
    /// What this hop is, for a human: <c>pass 2 (webm)</c> or
    /// <c>plugin lae-damage (damage)</c>. It goes into the failure sentence, so it
    /// has to read as the subject of one.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>Where the finished product belongs once the hop succeeds.</summary>
    public required string OutputPath { get; init; }

    /// <summary>
    /// Writes <c>part</c> from <c>input</c>. Returns success, or the reason this hop
    /// cannot be kept -- already a sentence, because every producer words its own
    /// failure differently (an exit code from FFmpeg, a broken handshake from a
    /// plugin).
    /// </summary>
    public required Func<string, string, CancellationToken, HopResult?> Produce { get; init; }
}

/// <summary>What a chain run produced.</summary>
public sealed record PostChainOutcome
{
    /// <summary>The file to hand on: the last hop that was written, or the input.</summary>
    public required string FinalPath { get; init; }

    /// <summary>Every hop that was written, in order.</summary>
    public required IReadOnlyList<string> Written { get; init; }

    /// <summary>1-based number of the hop that failed, or 0.</summary>
    public int FailedStep { get; init; }

    /// <summary>What kind of failure stopped it; <see cref="ChainFailure.None"/> when it finished.</summary>
    public ChainFailure FailedKind { get; init; }

    /// <summary>Why it stopped, or null when the chain finished.</summary>
    public string? Error { get; init; }

    public bool Failed => Error is not null;
}

/// <summary>
/// Runs a list of <see cref="PostHop"/>: one child per hop, each reading the
/// hop before.
///
/// <para>This type is what the chain's failure policy is written on, and it is
/// deliberately ignorant of FFmpeg. Everything that matters here is the same
/// whether the hop is a transcode or a plugin: <b>the input is never touched</b>,
/// a failed hop removes its own part product and stops the chain, whatever
/// already finished stays on disk, and an interrupt is not a failure. Two copies
/// of that policy -- one for FFmpeg hops, one for plugin hops -- is how the
/// second copy ends up promoting a file the first would have discarded.</para>
///
/// <para>The FFmpeg-specific part reduced to a delegate is
/// <see cref="RecompressRun"/>, which is now an adapter over this.</para>
/// </summary>
public static class PostChainRun
{
    /// <param name="hops">In order. Empty is legal: nothing ran, nothing was written.</param>
    /// <param name="startPath">What the first hop reads, and what is handed back if none ran.</param>
    /// <param name="warn">Where a leftover part product's sentence goes, or null.</param>
    public static PostChainOutcome Run(
        IReadOnlyList<PostHop> hops,
        string startPath,
        Action<string>? warn,
        CancellationToken token)
    {
        var written = new List<string>();
        string current = startPath;

        foreach (PostHop hop in hops)
        {
            token.ThrowIfCancellationRequested();

            string part = PartFile.PathFor(hop.OutputPath);
            string? folder = Path.GetDirectoryName(hop.OutputPath);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            // A part product left by a run that was killed from outside is ours
            // and is disposable. FFmpeg also gets -y, so a leftover can never
            // turn into "already exists" -- but a plugin might not, and this is
            // the one place that knows to clear the way.
            if (PartFile.Discard(part) is { } leftover) warn?.Invoke(leftover);

            HopResult result;
            try
            {
                // Null and Ok mean the same thing here. A hop written as
                // `return null` -- the shape every FFmpeg hop had before plugin
                // hops existed -- hands back a null reference rather than going
                // through the conversion to HopResult, so accepting it is what
                // keeps those hops working.
                result = hop.Produce(current, part, token) ?? HopResult.Ok;
            }
            catch (OperationCanceledException)
            {
                // An interrupt is not a hop failure. Its part product goes, and
                // the cancel travels on so the program can answer with 130.
                PartFile.Discard(part);
                throw;
            }

            string? error = result.Error;

            if (error is not null)
            {
                PartFile.Discard(part);
                return new PostChainOutcome
                {
                    FinalPath = current,
                    Written = written,
                    FailedStep = hop.Number,
                    FailedKind = result.Failure,
                    Error = $"{hop.Label} failed: {error}",
                };
            }

            try
            {
                PartFile.Promote(part, hop.OutputPath);
            }
            catch (Exception exc) when (exc is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                PartFile.Discard(part);
                return new PostChainOutcome
                {
                    FinalPath = current,
                    Written = written,
                    FailedStep = hop.Number,
                    FailedKind = result.Failure,
                    Error = $"{hop.Label} left nothing to keep: {exc.Message}",
                };
            }

            written.Add(hop.OutputPath);
            current = hop.OutputPath;
        }

        return new PostChainOutcome { FinalPath = current, Written = written };
    }
}
