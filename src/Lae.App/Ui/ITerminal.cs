using System.Text;

namespace Lae.App.Ui;

/// <summary>
/// Everything a command is allowed to say, and the only way it says it.
///
/// <para>v7 wrote to <c>Console</c> from 56 static classes, which is why none of
/// its behaviour could be asserted: a function that prints has no return value to
/// check, and capturing the process's own standard output in a test is a
/// subprocess test in disguise. Commands here take this instead, so a test can
/// read back every line a command produced and compare it with what a user would
/// have seen.</para>
///
/// <para>Two streams, not one, and the split is deliberate: stdout is the answer
/// to the question ("here is the plan", "here is the path"), stderr is the
/// commentary ("note:", "ERROR:", progress). Anything that pipes the output keeps
/// working, and a progress line rewritten in place can never end up inside a
/// JSON document.</para>
/// </summary>
public interface ITerminal
{
    /// <summary>One line on stdout. No argument means a blank line.</summary>
    void Line(string text = "");

    /// <summary>One line on stderr, for warnings and errors.</summary>
    void ErrorLine(string text);

    /// <summary>
    /// Write on stderr without ending the line, so the caller can rewrite it --
    /// the progress readout does exactly that.
    /// </summary>
    void Inline(string text);

    /// <summary>
    /// Whether stderr is a terminal. A rewritten progress line is right for a
    /// human and a hundred useless lines in a log, so the caller has to be able
    /// to ask.
    /// </summary>
    bool IsErrorInteractive { get; }
}

/// <summary>The real terminal: the process's own streams.</summary>
public sealed class SystemTerminal : ITerminal
{
    public void Line(string text = "") => Console.Out.WriteLine(text);

    public void ErrorLine(string text) => Console.Error.WriteLine(text);

    public void Inline(string text) => Console.Error.Write(text);

    public bool IsErrorInteractive => !Console.IsErrorRedirected;
}

/// <summary>
/// A terminal that keeps what was written instead of showing it.
///
/// <para>For tests, and for the one case where keeping it is the point: a caller
/// that wants to collect a command's output and then decide what to do with it.
/// It reports itself as non-interactive, because nothing is watching.</para>
/// </summary>
public sealed class BufferedTerminal : ITerminal
{
    private readonly StringBuilder _out = new();
    private readonly StringBuilder _err = new();

    /// <summary>Everything written to stdout, in order.</summary>
    public string Output => _out.ToString();

    /// <summary>Everything written to stderr, in order.</summary>
    public string Errors => _err.ToString();

    /// <summary>stdout split into lines, which is what assertions usually want.</summary>
    public IReadOnlyList<string> OutputLines => Lines(Output);

    /// <summary>stderr split into lines.</summary>
    public IReadOnlyList<string> ErrorLines => Lines(Errors);

    public void Line(string text = "") => _out.Append(text).Append('\n');

    public void ErrorLine(string text) => _err.Append(text).Append('\n');

    public void Inline(string text) => _err.Append(text);

    public bool IsErrorInteractive => false;

    private static string[] Lines(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
               .Select(line => line.TrimEnd('\r'))
               .ToArray();
}
