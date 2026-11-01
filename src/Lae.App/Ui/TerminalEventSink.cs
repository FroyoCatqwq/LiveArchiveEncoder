using Lae.Core.Localization;

namespace Lae.App.Ui;

/// <summary>
/// Renders the event stream the way v8's console output has always looked:
/// a progress line rewritten in place, <c>note:</c> on stderr, and one summary
/// line where the progress line was.
///
/// <para>Nothing new appears on screen because of this type -- that is the
/// requirement, not an accident. The e2e suite quotes these lines, and a
/// refactor that rearranges a user's terminal is a behaviour change wearing a
/// structure's clothes.</para>
///
/// <para><see cref="Stage"/> deliberately prints nothing: the phases are already
/// in the report, and a second "// stage: capture" line before every run is noise
/// for the only reader who has the report anyway. The event is still emitted, so
/// the L2 renderer and the tests can see it.</para>
/// </summary>
internal sealed class TerminalEventSink : IEventSink, IDisposable
{
    private readonly ITerminal _terminal;
    private readonly IMessages _messages;

    /// <summary>Created on first use, so a run that never reports progress never builds a line.</summary>
    private LiveProgress? _progress;

    internal TerminalEventSink(ITerminal terminal, IMessages messages)
    {
        _terminal = terminal;
        _messages = messages;
    }

    public void Stage(string name)
    {
        // See the type's remarks: emitted for other consumers, silent here.
    }

    public void Progress(long done, long? total) => Line().Report(done, total);

    public void Warn(string message)
        => _terminal.ErrorLine(_messages.Format("encode.note.prefixed", new { text = message }));

    public void Done(string summary, IReadOnlyList<string> artifacts, TimeSpan elapsed)
        => Line().Finish(summary);

    public void Dispose() => _progress?.Dispose();

    private LiveProgress Line() => _progress ??= new LiveProgress(_terminal, _messages);
}
