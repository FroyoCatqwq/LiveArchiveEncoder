using System.CommandLine;
using System.Globalization;
using Lae.App.Ui;
using Lae.Core;
using Lae.Core.History;
using Lae.Core.Localization;

namespace Lae.App.Commands;

/// <summary>
/// <c>lae history</c> — what previous runs did.
///
/// <para>The program printed one line per encode and forgot it, so the questions
/// worth asking ("what did I use for episode 3", "did that one actually come out
/// at 200 kbps") had no answer. This is the smallest command that gives them
/// one: list, and where the file is.</para>
///
/// <para>No <c>show</c> and no <c>prune</c> yet. A row is already one line and a
/// range is already a <c>--limit</c>; deleting history is the kind of command that
/// should exist after someone has asked for it, not before.</para>
///
/// <para>This is the first command translated end to end, and it is the pattern for
/// the rest: every sentence a user reads comes from <see cref="IMessages"/>, the
/// numbers and paths stay where they are, and nothing constructs a sentence out of
/// fragments (word order is not portable, and Chinese does not put the count in the
/// same place English does).</para>
/// </summary>
public static class HistoryCommand
{
    public static Command Build(IRunHistory history, ITerminal terminal, IMessages messages)
    {
        var command = new Command("history", "Show what previous runs did.");
        command.Subcommands.Add(ListCommand(history, terminal, messages));
        command.Subcommands.Add(PathCommand(history, terminal));
        return command;
    }

    private static Command PathCommand(IRunHistory history, ITerminal terminal)
    {
        var command = new Command("path", "Print where the history is kept.");
        command.SetAction(_ =>
        {
            terminal.Line(history.Location);
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command ListCommand(IRunHistory history, ITerminal terminal, IMessages messages)
    {
        var command = new Command("list", "Print the most recent runs, newest first.");

        var limitOption = new Option<int>("--limit")
        {
            Description = "How many rows to print.",
            DefaultValueFactory = _ => 20,
        };
        command.Options.Add(limitOption);

        command.SetAction((ParseResult parseResult) =>
        {
            int limit = parseResult.GetValue(limitOption);
            if (limit < 1)
            {
                terminal.ErrorLine($"ERROR: {messages["history.limit"]}");
                return ExitCodes.Usage;
            }

            IReadOnlyList<RunRecord> runs = history.Recent(limit);
            if (runs.Count == 0)
            {
                terminal.Line(messages["history.empty"]);
                return ExitCodes.Ok;
            }

            terminal.Line(messages["history.header"]);
            foreach (RunRecord run in runs) terminal.Line(Row(run));

            // The total goes last: it answers "how many in all" without a second
            // command, and it is the number that grows while --limit stays fixed.
            terminal.Line();
            terminal.Line(messages.Get("history.total", new { runs = history.Count(), path = history.Location }));
            return ExitCodes.Ok;
        });

        return command;
    }

    /// <summary>
    /// One row, fixed width so the columns line up in a terminal and in a log.
    ///
    /// <para>Times are shown in local time: a person comparing a row against their
    /// own sent-mail folder does not want to do arithmetic. It is stored in UTC.</para>
    ///
    /// <para>The last column is "in → out", not one of the two: "which preset did I
    /// use for episode 3" needs the source, and "where did that file go" needs the
    /// product. Showing only one of them made two runs of the same source print the
    /// same row, which is exactly the case this list exists for.</para>
    /// </summary>
    private static string Row(RunRecord run)
    {
        string encoder = run.Encoder is { Length: > 0 } name
            ? run.Container is { Length: > 0 } container ? $"{name}/{container}" : name
            : "-";

        return string.Create(CultureInfo.InvariantCulture,
            $"{run.Id,5}  {run.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm}  {run.Result,-10} "
            + $"{encoder,-18} {Kbps(run.AverageKbps),4}  {Paths(run.SourcePath, run.OutputPath)}");
    }

    /// <summary>
    /// <c>tape.mkv → tape_live.avi</c>, with each half cut in the middle when it is
    /// long. A full path would push the interesting part off the screen; a bare file
    /// name keeps the row readable and the folder is one <c>history show</c> away.
    /// </summary>
    private static string Paths(string source, string? output)
    {
        string from = Shorten(Name(source), 30);
        if (output is null) return $"{from} → -";

        string to = Shorten(Name(output), 30);
        return string.Equals(from, to, StringComparison.Ordinal) ? from : $"{from} → {to}";
    }

    private static string Shorten(string text, int width)
        => text.Length <= width ? text : text[..(width / 2 - 1)] + "…" + text[^(width / 2)..];

    private static string Kbps(double? value)
        => value is { } kbps ? kbps.ToString("F0", CultureInfo.InvariantCulture) : "-";

    private static string Name(string path)
    {
        try
        {
            return Path.GetFileName(path) is { Length: > 0 } name ? name : path;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }
}
