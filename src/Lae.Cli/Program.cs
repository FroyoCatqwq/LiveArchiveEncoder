using System.CommandLine;
using Lae.App;

// The whole entry point. Parsing, dispatch and rendering live in Lae.App so that
// this file stays small enough to read in one screen — v7's counterpart was 542
// lines of linear branches with 48 bare returns.

// Before anything can write: redirected output is UTF-8 on every platform, so a
// pipeline never has to guess this machine's code page.
OutputEncoding.EnsureUtf8WhenRedirected();

// Ctrl+C must mean "stop this run cleanly" for every command, not just encode:
// installing the handler here is what turns it into a cancelled token instead of
// the runtime killing the process with children still attached.
Interrupt.Install();

AppHost host = AppHost.CreateDefault();
ParseResult parsed = CommandTree.Build(host).Parse(args);

// System.CommandLine installs its own catch around the action and answers with
// exit 1. That would make this program's two documented outcomes unreachable:
// Ctrl+C is 130, and an unexpected failure is 1 with a one-line error rather
// than whatever the parser decided to print. Both live below instead.
parsed.InvocationConfiguration.EnableDefaultExceptionHandler = false;

// System.CommandLine answers a parse error with 1; this program's contract says
// a usage error is 2, so the mapping is explicit rather than inherited.
if (parsed.Errors.Count > 0)
{
    foreach (var error in parsed.Errors)
        Console.Error.WriteLine($"ERROR: {error.Message}");

    return ExitCodes.Usage;
}

// A settings file that could not be read, or one that was migrated a moment ago.
// Said once, here, because the command's own read cannot reproduce it: the read
// that migrates also writes. Running an encode on defaults without ever saying so
// is how a user concludes the program ignored their settings.
host.ReportStartupNotes();

try
{
    return await parsed.InvokeAsync();
}
catch (OperationCanceledException)
{
    // Ctrl+C is not a failure, and it is not a stack trace either.
    Console.Error.WriteLine("Interrupted.");
    return ExitCodes.Interrupted;
}
catch (Exception exc)
{
    // The backstop. Every command is supposed to report its own problems as a
    // sentence with an exit code -- EncodePlan.Create is written to return a
    // verdict rather than throw, for exactly this reason -- but a program that
    // meets an unexpected exception should still say one line and exit, not
    // pour a stack trace over the terminal and leave the user to guess whether
    // their file is half-written. This is the same lesson v7 learned from a
    // menu that died on a mistyped number.
    Console.Error.WriteLine($"ERROR: {exc.Message}");
    return ExitCodes.Failed;
}
