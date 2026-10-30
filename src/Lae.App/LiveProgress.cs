using System.Diagnostics;

namespace Lae.App;

/// <summary>
/// The one line a long encode rewrites in place.
///
/// <para>Written to standard error, so a caller can pipe the report to a file and
/// still see progress on the terminal. When standard error is redirected the line
/// is emitted every few seconds instead of being rewritten -- a <c>\r</c> in a log
/// file is a hundred useless lines.</para>
/// </summary>
internal sealed class LiveProgress : IDisposable
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly bool _inline = !Console.IsErrorRedirected;
    private double _lastReport;
    private bool _closed;

    /// <summary>Say where the run is. Cheap enough to call every few frames.</summary>
    public void Report(long done, long? total)
    {
        if (_closed) return;
        double elapsed = _clock.Elapsed.TotalSeconds;
        if (elapsed - _lastReport < (_inline ? 0.25 : 5.0)) return;
        _lastReport = elapsed;

        string text = total is { } goal && goal > 0
            ? $"[{elapsed,7:F1}s] {done,7}/{goal} frames ({100.0 * done / goal,5:F1}%)"
            : $"[{elapsed,7:F1}s] {done,7} frames";

        if (_inline) Console.Error.Write($"\r{text}");
        else Console.Error.WriteLine(text);
    }

    /// <summary>Erase the progress line and print the outcome instead.</summary>
    public void Finish(string summary)
    {
        Erase();
        _closed = true;
        Console.Error.WriteLine(summary);
    }

    private void Erase()
    {
        if (_inline && _lastReport > 0) Console.Error.Write("\r" + new string(' ', 60) + "\r");
    }

    public void Dispose()
    {
        if (_closed) return;
        Erase();
        _closed = true;
    }
}
