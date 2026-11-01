namespace Lae.Core.Plugins;

/// <summary>
/// One plugin invocation, as the host should perform it.
///
/// <para>Paths are absolute and already resolved: the caller decided which
/// product this hop reads and where its part product goes. Nothing here is
/// optional except the parameters, and that is checked before the call, not
/// during it.</para>
/// </summary>
public sealed record PluginCall
{
    /// <summary>The plugin's id, which is also what discovery looks for.</summary>
    public required string Plugin { get; init; }

    /// <summary>The plugin's own subcommand, passed as <c>run --stage</c>.</summary>
    public required string Stage { get; init; }

    /// <summary>Parameters to pass as <c>--param k=v</c>, already validated.</summary>
    public IReadOnlyDictionary<string, string>? Parameters { get; init; }

    /// <summary>What the plugin reads. Absolute; never the live product if a hop precedes it.</summary>
    public required string InputPath { get; init; }

    /// <summary>
    /// Where the plugin writes. Always a <c>.part</c>: a plugin returning zero is
    /// not the same as a product worth keeping, and the runner promotes only after
    /// both have been established.
    /// </summary>
    public required string PartPath { get; init; }

    /// <summary>
    /// How long this hop may take.
    ///
    /// <para>Derived by the caller from the recording's length, with the same
    /// formula an FFmpeg hop gets -- a plugin doing a full transcode is not faster
    /// than FFmpeg doing one, and a fixed budget would either kill slow machines or
    /// wait forever on a wedged plugin.</para>
    /// </summary>
    public required TimeSpan Budget { get; init; }
}

/// <summary>Why a plugin hop did not produce something worth keeping.</summary>
public enum PluginFailure
{
    /// <summary>It worked.</summary>
    None,

    /// <summary>
    /// The plugin could not be run, would not describe itself, said something this
    /// build cannot honour, or exited non-zero. One answer to the user: exit 4.
    /// </summary>
    Rejected,

    /// <summary>
    /// It exited zero and the product does not hold up: missing, empty, not a
    /// video, or a fraction of the length it was given. Exit 5, and the part stays
    /// on disk for diagnosis.
    ///
    /// <para>Separate from <see cref="Rejected"/> because the two mean opposite
    /// things about where to look. A rejected plugin never ran properly; a plugin
    /// that fails verification ran, printed success, and delivered something
    /// broken -- which is the failure mode this whole layer exists for.</para>
    /// </summary>
    Verification,
}

/// <summary>What a plugin invocation produced.</summary>
public sealed record PluginStepOutcome
{
    public PluginFailure Failure { get; init; }

    /// <summary>One sentence, already worded, naming the plugin. Null on success.</summary>
    public string? Error { get; init; }

    /// <summary>Things worth saying that do not stop anything (a non-reproducible step).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public bool Failed => Failure != PluginFailure.None;

    public static PluginStepOutcome Ok(params string[] notes)
        => new() { Notes = notes };

    public static PluginStepOutcome Reject(string error, params string[] notes)
        => new() { Failure = PluginFailure.Rejected, Error = error, Notes = notes };

    public static PluginStepOutcome Unverified(string error)
        => new() { Failure = PluginFailure.Verification, Error = error };
}

/// <summary>
/// Runs one plugin hop. Implemented by the host in <c>Lae.Plugins</c>, consumed by
/// the chain runner in <c>Lae.Encoding</c>, and declared here so neither has to
/// know the other.
///
/// <para>The interface exists for one reason: the pipeline must not reference the
/// plugin host, because a plugin reaching into the pipeline is the direction that
/// would let third-party code drive the encode. Both sides reference the contract
/// instead, which is also the only way the runner stays testable without a plugin
/// on disk.</para>
///
/// <para>Implementations must not throw for a plugin's misbehaviour. A missing
/// executable, a plugin that hangs, one that exits 2 because the host built a bad
/// command line -- all of it is an outcome, and the chain turns an outcome into a
/// message. An exception here would end a nineteen-minute recording with a stack
/// trace.</para>
/// </summary>
public interface IPluginRunner
{
    PluginStepOutcome Run(PluginCall request, CancellationToken token);
}
