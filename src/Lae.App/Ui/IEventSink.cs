namespace Lae.App.Ui;

/// <summary>
/// What a command says while it runs, as events rather than as sentences.
///
/// <para>The distinction is the point. Until now the encode session printed its
/// own progress line, its own <c>note:</c> prefix and its own closing summary
/// directly through <see cref="ITerminal"/>, so the only consumer that could
/// ever exist was a human at a terminal. <c>--json-events</c> (L2) needs the same
/// facts a second time, in JSON Lines, and the honest way to add it is a second
/// renderer -- not a second set of print calls next to the first, which is how
/// the report and the log end up disagreeing about how many frames were
/// written.</para>
///
/// <para>One sink per command invocation. It is not thread-safe on its own; the
/// callers that emit from the pump's thread already funnel through the app's
/// single reporting path.</para>
/// </summary>
public interface IEventSink
{
    /// <summary>
    /// Where the run is: <c>preflight</c>, <c>capture</c>, and the plugin
    /// placements from <see cref="Lae.Core.Plugins.PluginStages"/> for the
    /// moments that are literally the same phase (<c>post-encode</c>,
    /// <c>between-passes</c>, <c>after-chain</c>).
    ///
    /// <para>Names are the wire format for L2. A renderer that has nothing to say
    /// about a stage may ignore it -- the terminal does, because its phases are
    /// already visible as the report's own lines and a second "stage" line would
    /// be noise.</para>
    /// </summary>
    void Stage(string name);

    /// <summary>How far the capture has got. Called often; throttling is the renderer's job.</summary>
    void Progress(long done, long? total);

    /// <summary>
    /// Something the user should know but that is not a failure -- the text that
    /// used to be printed behind a <c>note:</c> prefix. The renderer adds the
    /// prefix, so a translation can put it where its own language wants it.
    /// </summary>
    void Warn(string message);

    /// <summary>
    /// The run finished. <paramref name="summary"/> is the one line a human reads
    /// in place of the progress line; the rest is what a machine needs.
    /// </summary>
    void Done(string summary, IReadOnlyList<string> artifacts, TimeSpan elapsed);
}
