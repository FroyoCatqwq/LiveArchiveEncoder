using System.CommandLine;
using PrettyPrompt;
using PrettyPrompt.Completion;
using PrettyPrompt.Documents;

namespace Lae.App;

/// <summary>
/// The <c>lae repl</c> loop: read a line with highlighting and completion, hand
/// it to the same <see cref="CommandTree"/> the CLI uses, run it.
///
/// <para>Completion candidates come from <c>RootCommand.Parse(text).GetCompletions(caret)</c>,
/// which means the REPL menu and the generated shell scripts are derived from
/// one tree. Adding an option makes it suggestable everywhere in one edit.</para>
/// </summary>
internal static class Repl
{
    public static async Task<int> RunAsync(RootCommand root, CancellationToken cancellationToken)
    {
        var prompt = new Prompt(
            callbacks: new ReplCallbacks(root),
            configuration: new PromptConfiguration(prompt: "lae> "));

        while (!cancellationToken.IsCancellationRequested)
        {
            PromptResult response = await prompt.ReadLineAsync();
            if (!response.IsSuccess) break;   // Ctrl-C or end of input.

            string line = response.Text.Trim();
            if (line.Length == 0) continue;
            if (line is "exit" or "quit") break;

            // Nesting one REPL inside another is never what the typist meant.
            if (line.StartsWith("repl", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("ERROR: already in the prompt; type 'exit' to leave.");
                continue;
            }

            ParseResult parsed = root.Parse(line);
            if (parsed.Errors.Count > 0)
            {
                foreach (var error in parsed.Errors)
                    Console.Error.WriteLine($"ERROR: {error.Message}");
                continue;
            }

            await parsed.InvokeAsync();
        }

        return ExitCodes.Ok;
    }

    private sealed class ReplCallbacks(RootCommand root) : PromptCallbacks
    {
        protected override Task<IReadOnlyList<CompletionItem>> GetCompletionItemsAsync(
            string text, int caret, TextSpan spanToBeReplaced, CancellationToken cancellationToken)
        {
            IReadOnlyList<CompletionItem> items = [.. root.Parse(text)
                .GetCompletions(caret)
                .Select(c => new CompletionItem(
                    replacementText: c.Label,
                    displayText: c.Label))];

            return Task.FromResult(items);
        }
    }
}
