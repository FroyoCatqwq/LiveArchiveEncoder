using System.CommandLine;
using Lae.App;

// The whole entry point. Parsing, dispatch and rendering live in Lae.App so that
// this file stays small enough to read in one screen — v7's counterpart was 542
// lines of linear branches with 48 bare returns.

// Ctrl+C must mean "stop this run cleanly" for every command, not just encode:
// installing the handler here is what turns it into a cancelled token instead of
// the runtime killing the process with children still attached.
Interrupt.Install();

ParseResult parsed = CommandTree.Build(AppHost.CreateDefault()).Parse(args);

// System.CommandLine answers a parse error with 1; this program's contract says
// a usage error is 2, so the mapping is explicit rather than inherited.
if (parsed.Errors.Count > 0)
{
    foreach (var error in parsed.Errors)
        Console.Error.WriteLine($"ERROR: {error.Message}");

    return ExitCodes.Usage;
}

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
