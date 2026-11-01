namespace Lae.Core.Plugins;

/// <summary>
/// The places a plugin may be inserted, and the only three.
///
/// <para>One list, because two lists drift. The blueprint's handshake example and
/// <c>PluginRegistration</c> had already drifted before a single line of the
/// plugin host existed: the settings type offered <c>pre-encode</c>, which this
/// program can never honour. A live capture's source is a stream, not a file --
/// there is no moment before the encode at which a plugin could be handed a
/// complete product, so a stage by that name would either be rejected at run
/// time or, worse, silently skipped. Neither is a thing a user can act on.</para>
///
/// <para>The three that remain are all "there is a finished file, here it is":</para>
/// <list type="bullet">
/// <item><c>post-encode</c> -- after the recording is on disk, before the chain.</item>
/// <item><c>between-passes</c> -- between two hops of the recompress chain.</item>
/// <item><c>after-chain</c> -- after the last hop, on the final product.</item>
/// </list>
///
/// <para>Names are the wire format: they appear in the handshake, in
/// <c>settings.toml</c>, and in the JSON event stream. Renaming one is a protocol
/// change, not a refactor.</para>
/// </summary>
public static class PluginStages
{
    /// <summary>After the recording, before any recompress hop.</summary>
    public const string PostEncode = "post-encode";

    /// <summary>Between two hops of the recompress chain.</summary>
    public const string BetweenPasses = "between-passes";

    /// <summary>After the last hop, on the product that would be delivered.</summary>
    public const string AfterChain = "after-chain";

    /// <summary>Every stage, in the order a run reaches them.</summary>
    public static IReadOnlyList<string> All { get; } = [PostEncode, BetweenPasses, AfterChain];

    private static readonly HashSet<string> Known = new(All, StringComparer.Ordinal);

    /// <summary>
    /// Whether the host can run a plugin at this stage.
    ///
    /// <para>Forgiving about spelling (<c>Post-Encode</c>, <c>" between-passes "</c>)
    /// because those come from a hand-written file, and strict about the word
    /// itself because a stage the host cannot run is a configuration mistake that
    /// has to be reported, not rounded off to something nearby.</para>
    /// </summary>
    public static bool IsKnown(string? stage)
        => !string.IsNullOrWhiteSpace(stage) && Known.Contains(Normalised(stage));

    /// <summary>The canonical spelling: trimmed and lower-cased.</summary>
    public static string Normalised(string stage) => stage.Trim().ToLowerInvariant();

    /// <summary>
    /// Canonicalise a declared list: trimmed, lower-cased, blanks dropped and
    /// duplicates removed, order kept.
    ///
    /// <para>Unknown names survive on purpose. Dropping them here would turn "you
    /// asked for a stage this build does not have" into "this plugin does
    /// nothing", which is the failure <c>PluginRegistration</c> exists to
    /// prevent.</para>
    /// </summary>
    public static IReadOnlyList<string> Normalise(IEnumerable<string?>? stages)
    {
        if (stages is null) return [];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        foreach (string? stage in stages)
        {
            if (string.IsNullOrWhiteSpace(stage)) continue;

            string canonical = Normalised(stage);
            if (seen.Add(canonical)) result.Add(canonical);
        }

        return result;
    }
}
