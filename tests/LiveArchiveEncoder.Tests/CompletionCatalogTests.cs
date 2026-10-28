using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LiveArchiveEncoder.UI;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.8.6: the completion scripts were generated from a hand-kept list, and it
/// had fallen 43 options behind the parser -- everything added after v7.9.8.2
/// completed nothing in any shell. These cases keep the one table in
/// <see cref="OptionCatalog"/> honest against the parser source itself, so the two
/// cannot drift again without a red test.
/// </summary>
public sealed partial class CompletionCatalogTests
{
    /// <summary>
    /// The parser, located through the compiler rather than the working directory:
    /// tests run from bin/, the source lives two levels up.
    /// </summary>
    private static string ParserSource([CallerFilePath] string thisFile = "")
    {
        string dir = Path.GetDirectoryName(thisFile)!;
        return Path.Combine(dir, "..", "..", "src", "LiveArchiveEncoder", "UI", "CliOptions.cs");
    }

    [GeneratedRegex("""
        "(--?[a-z0-9][a-z0-9-]*)"
        """)]
    private static partial Regex OptionLiteral();

    private static HashSet<string> ParserOptions()
    {
        string path = Path.GetFullPath(ParserSource());
        if (!File.Exists(path))
            throw new InvalidOperationException($"the parser source is not readable at {path}");
        return [.. OptionLiteral().Matches(File.ReadAllText(path)).Select(m => m.Groups[1].Value)];
    }

    [Test]
    public async Task EveryOptionTheParserAcceptsIsCompletable()
    {
        HashSet<string> catalogued = [.. OptionCatalog.All.Select(o => o.Name)];
        string[] missing = [.. ParserOptions().Where(name => !catalogued.Contains(name)).Order(StringComparer.Ordinal)];

        await Assert.That(missing).IsEmpty()
            .Because("an option the parser takes but the catalog omits completes nothing in every shell: "
                + string.Join(", ", missing));
    }

    [Test]
    public async Task TheCatalogOffersNothingTheParserWouldReject()
    {
        HashSet<string> parsed = ParserOptions();
        string[] invented = [.. OptionCatalog.All.Select(o => o.Name)
            .Where(name => !parsed.Contains(name)).Order(StringComparer.Ordinal)];

        await Assert.That(invented).IsEmpty()
            .Because("completing an option that does not exist sends the user straight into "
                + "'unrecognized argument': " + string.Join(", ", invented));
    }

    [Test]
    public async Task NoOptionIsListedTwice()
    {
        string[] duplicates = [.. OptionCatalog.All
            .GroupBy(o => o.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)];

        await Assert.That(duplicates).IsEmpty();
    }

    /// <summary>
    /// The options that v7.9.8.5 shipped with no completion at all. Each one is
    /// named in a case arm or a value table of the generated script.
    /// </summary>
    [Test]
    [Arguments("--era-aspect")]
    [Arguments("--h264")]
    [Arguments("--pix-fmt")]
    [Arguments("--color-range")]
    [Arguments("--dither")]
    [Arguments("--nnedi-weights")]
    [Arguments("--update")]
    [Arguments("--update-feed")]
    [Arguments("--subtitle-aspect")]
    [Arguments("--recompress-quality")]
    [Arguments("--net-test")]
    public async Task PreviouslyMissingOptionsNowTakePartInCompletion(string option)
    {
        foreach (string shell in new[] { "bash", "zsh", "fish", "powershell" })
        {
            string script = Completion.Generate(shell, ["mpeg4"]);
            await Assert.That(script).Contains(option)
                .Because($"{shell} should mention {option}");
        }
    }

    [Test]
    public async Task ChoicesComeFromTheCatalogNotFromASecondList()
    {
        // --era-aspect's values are the labels EraAspects prints; if this ever
        // stops matching, the catalog is holding a stale copy again.
        string script = Completion.Generate("zsh", []);
        foreach (string label in LiveArchiveEncoder.PostProcess.EraAspects.Labels)
        {
            await Assert.That(script).Contains(label);
        }
    }

    [Test]
    public async Task FreeTextOptionsEmitNoChoiceList()
    {
        // --threads takes a number: bash must not produce an empty -W list for it,
        // which is what makes the shell stop offering anything at all.
        string bash = Completion.Generate("bash", []);
        await Assert.That(bash).DoesNotContain("        --threads)");
        await Assert.That(bash).DoesNotContain("        --qv)");
    }

    [Test]
    public async Task FileOptionsAskForFiles()
    {
        string bash = Completion.Generate("bash", []);
        await Assert.That(bash).Contains("        --subtitle)");
        await Assert.That(bash).Contains("compgen -f");
    }

    [Test]
    public async Task EncoderNamesAreFilledInAtGenerationTime()
    {
        string bash = Completion.Generate("bash", ["mpeg4", "rv20"]);
        await Assert.That(bash).Contains("compgen -W \"mpeg4 rv20\"");
        await Assert.That(bash).DoesNotContain(OptionCatalog.EncodersMarker);
    }

    [Test]
    public async Task EveryShellAcceptsTheSameOptionSet()
    {
        string[] names = [.. OptionCatalog.All.Select(o => o.Name)];
        foreach (string shell in new[] { "bash", "zsh", "fish", "powershell" })
        {
            string script = Completion.Generate(shell, []);
            string[] absent = [.. names.Where(n => !script.Contains(n, StringComparison.Ordinal))];
            await Assert.That(absent).IsEmpty().Because($"{shell} is missing {string.Join(", ", absent)}");
        }
    }
}
