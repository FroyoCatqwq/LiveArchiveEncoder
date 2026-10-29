using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Lae.Tests.Architecture;

/// <summary>
/// Keeps the layering described in the v8 blueprint honest.
///
/// <para>Two kinds of check, both deliberately static — no reflection over built
/// assemblies, because a test that loads Lae.Core.dll proves what the compiler
/// produced, while these prove what the source says. The source is what a human
/// edits, so a red test here points at a file and a line.</para>
///
/// <para>Every assertion has a message naming the fix: move the type to the
/// assembly that is allowed to do the thing.</para>
/// </summary>
public sealed partial class DependencyDirectionTests
{
    /// <summary>Repository root, resolved through the compiler rather than the working directory.</summary>
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string ProjectFile(string assembly)
        => Path.Combine(RepoRoot(), "src", assembly, $"{assembly}.csproj");

    private static string[] SourceFiles(string assembly)
    {
        string root = Path.Combine(RepoRoot(), "src", assembly);
        if (!Directory.Exists(root)) return [];

        return [.. Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];
    }

    private static string[] ReferencedProjects(string assembly)
    {
        string file = ProjectFile(assembly);
        if (!File.Exists(file)) return [];

        return [.. ProjectReferencePattern().Matches(File.ReadAllText(file))
            .Select(m => Path.GetFileNameWithoutExtension(m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar)))
            .Order(StringComparer.Ordinal)];
    }

    [GeneratedRegex("ProjectReference\\s+Include\\s*=\\s*\"([^\"]+)\"")]
    private static partial Regex ProjectReferencePattern();

    [GeneratedRegex(@"\b(Console|File|Directory|FileStream|StreamWriter|StreamReader|Process|HttpClient|Environment\.Exit)\b")]
    private static partial Regex ForbiddenInCore();

    /// <summary>
    /// The domain assembly must not depend on any other project. If it ever needs
    /// one, the type being added is not domain logic.
    /// </summary>
    [Test]
    public async Task CoreDependsOnNothing()
    {
        string[] refs = ReferencedProjects("Lae.Core");
        await Assert.That(refs).IsEmpty()
            .Because("Lae.Core must stay dependency-free; move the type to Lae.Media or Lae.App");
    }

    /// <summary>
    /// The domain assembly must not reach for the console, the filesystem or a
    /// process. Those are host concerns; keeping them out is what makes every
    /// rule in Core testable without a fixture.
    ///
    /// <para><c>Path</c> is intentionally allowed: it is pure string arithmetic,
    /// and forbidding it would push path composition into every caller.</para>
    /// </summary>
    [Test]
    public async Task CoreStaysPure()
    {
        List<string> offenders = [];

        foreach (string file in SourceFiles("Lae.Core"))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (ForbiddenInCore().IsMatch(lines[i]))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        await Assert.That(offenders).IsEmpty()
            .Because("Lae.Core must not touch Console/File/Process/HttpClient; "
                   + "move the type to Lae.Media (I/O) or Lae.App (host)");
    }

    /// <summary>
    /// The four lower assemblies must never point back up at Lae.App. This is the
    /// arrow direction from the blueprint: App depends on them, not the reverse.
    /// </summary>
    [Test]
    [Arguments("Lae.Core")]
    [Arguments("Lae.Media")]
    [Arguments("Lae.Encoding")]
    [Arguments("Lae.Plugins")]
    public async Task LowerLayersDoNotReferenceTheApp(string assembly)
    {
        string[] refs = ReferencedProjects(assembly);
        await Assert.That(refs).DoesNotContain("Lae.App")
            .Because($"{assembly} must not depend on the composition root");
    }

    /// <summary>
    /// Media and Plugins are siblings: neither may reach into the other, and
    /// neither may reach into Encoding. Encoding is the only one allowed to sit
    /// on top of Media.
    /// </summary>
    [Test]
    public async Task MediaAndPluginsAreSiblings()
    {
        await Assert.That(ReferencedProjects("Lae.Media")).DoesNotContain("Lae.Encoding")
            .Because("Lae.Encoding sits above Lae.Media, not the other way round");

        await Assert.That(ReferencedProjects("Lae.Plugins")).DoesNotContain("Lae.Encoding")
            .Because("plugins are orchestrated by the host, not by the pipeline");

        await Assert.That(ReferencedProjects("Lae.Media")).DoesNotContain("Lae.Plugins")
            .Because("media handling must not know that plugins exist");
    }

    /// <summary>
    /// Only the composition root is allowed to pull in the presentation
    /// libraries. A parse or rendering type leaking into Encoding would make the
    /// pipeline untestable again.
    /// </summary>
    [Test]
    public async Task PresentationLibrariesStayInTheApp()
    {
        string[] presentation = ["System.CommandLine", "Spectre.Console", "PrettyPrompt"];

        foreach (string assembly in new[] { "Lae.Core", "Lae.Media", "Lae.Encoding", "Lae.Plugins" })
        {
            string file = ProjectFile(assembly);
            if (!File.Exists(file)) continue;
            string text = File.ReadAllText(file);

            foreach (string package in presentation)
            {
                await Assert.That(text.Contains(package, StringComparison.OrdinalIgnoreCase)).IsFalse()
                    .Because($"{assembly} must not reference {package}; keep presentation in Lae.App");
            }
        }
    }
}
