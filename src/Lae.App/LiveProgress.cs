using System.Diagnostics;
using System.Globalization;
using Lae.App.Ui;
using Lae.Core.Localization;

namespace Lae.App;

/// <summary>
/// The one line a long encode rewrites in place.
///
/// <para>Written to standard error, so a caller can pipe the report to a file and
/// still see progress on the terminal. When standard error is redirected the line
/// is emitted every few seconds instead of being rewritten -- a <c>\r</c> in a log
/// file is a hundred useless lines.</para>
///
/// <para>The numbers arrive already padded, because the padding is a property of
/// the column and not of the sentence: <c>{{elapsed}}</c> is right-aligned to the
/// width the clock needs, and a translation that reorders the line around it does
/// not have to reproduce that.</para>
/// </summary>
internal sealed class LiveProgress : IDisposable
{
    private readonly ITerminal _terminal;
    private readonly IMessages _messages;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly bool _inline;
    private double _lastReport;
    private bool _closed;

    internal LiveProgress(ITerminal terminal, IMessages messages)
    {
        _terminal = terminal;
        _messages = messages;
        _inline = terminal.IsErrorInteractive;
    }

    /// <summary>Say where the run is. Cheap enough to call every few frames.</summary>
    public void Report(long done, long? total)
    {
        if (_closed) return;

        double elapsed = _clock.Elapsed.TotalSeconds;
        if (elapsed - _lastReport < (_inline ? 0.25 : 5.0)) return;
        _lastReport = elapsed;

        string seconds = elapsed.ToString("F1", CultureInfo.InvariantCulture).PadLeft(7);

        string text = total is { } goal && goal > 0
            ? _messages.Format("encode.progress.of", new
            {
                elapsed = seconds,
                done = done.ToString(CultureInfo.InvariantCulture).PadLeft(7),
                total = goal.ToString(CultureInfo.InvariantCulture),
                percent = (100.0 * done / goal).ToString("F1", CultureInfo.InvariantCulture).PadLeft(5),
            })
            : _messages.Format("encode.progress.only", new
            {
                elapsed = seconds,
                done = done.ToString(CultureInfo.InvariantCulture).PadLeft(7),
            });

        if (_inline) _terminal.Inline($"\r{text}");
        else _terminal.ErrorLine(text);
    }

    /// <summary>Erase the progress line and print the outcome instead.</summary>
    public void Finish(string summary)
    {
        Erase();
        _closed = true;
        _terminal.ErrorLine(summary);
    }

    private void Erase()
    {
        if (_inline && _lastReport > 0) _terminal.Inline("\r" + new string(' ', 60) + "\r");
    }

    public void Dispose()
    {
        if (_closed) return;
        Erase();
        _closed = true;
    }
}
