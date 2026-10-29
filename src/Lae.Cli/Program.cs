using System.CommandLine;
using Lae.App;

// The whole entry point. Parsing, dispatch and rendering live in Lae.App so that
// this file stays small enough to read in one screen — v7's counterpart was 542
// lines of linear branches with 48 bare returns.

ParseResult parsed = CommandTree.Build().Parse(args);

// System.CommandLine answers a parse error with 1; this program's contract says
// a usage error is 2, so the mapping is explicit rather than inherited.
if (parsed.Errors.Count > 0)
{
    foreach (var error in parsed.Errors)
        Console.Error.WriteLine($"ERROR: {error.Message}");

    return ExitCodes.Usage;
}

return await parsed.InvokeAsync();
