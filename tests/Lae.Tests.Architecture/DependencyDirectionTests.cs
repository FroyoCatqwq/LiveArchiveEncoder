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
    /// Concurrency and the clock are runtime mechanics, not domain rules.
    ///
    /// <para>These were not on the list when the pump lived in Core, so the claim
    /// "every rule in Core is testable without a fixture" was true of the planners
    /// and false of the one type that started a thread. The pump's producer, the
    /// frame handoff and the 1 ms timer now live in <c>Lae.Encoding</c>, where the
    /// pipeline is allowed to own them -- and this list is what keeps them from
    /// drifting back.</para>
    ///
    /// <para><c>Stopwatch</c> and <c>Timer</c> are here for the same reason: a
    /// domain rule that reads the wall clock cannot be tested without waiting.
    /// Time a rule needs arrives as a parameter or as <c>TimeProvider</c>.</para>
    /// </summary>
    [GeneratedRegex(@"\b(Thread|Stopwatch|Timer|CancellationTokenSource|SemaphoreSlim)\b")]
    private static partial Regex ForbiddenInCoreConcurrency();

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
    /// The domain assembly must not reach for the console, the filesystem, a
    /// process, a thread or the clock. Those are host concerns; keeping them out
    /// is what makes every rule in Core testable without a fixture.
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
                if (ForbiddenInCore().IsMatch(lines[i]) || ForbiddenInCoreConcurrency().IsMatch(lines[i]))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        await Assert.That(offenders).IsEmpty()
            .Because("Lae.Core must not touch Console/File/Process/HttpClient, nor Thread/Stopwatch/Timer/"
                   + "CancellationTokenSource; move the type to Lae.Media (I/O), Lae.Encoding (pipeline "
                   + "mechanics) or Lae.App (host)");
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
    /// <c>Lae.Encoding</c> is the top of the pipeline: nothing below or beside it
    /// may reference it.
    ///
    /// <para>The direction is the whole point. The pipeline runs hops it is given;
    /// it does not go looking for who produced them. A plugin reaching into
    /// <c>Lae.Encoding</c> would let an extension drive the encode, and Media
    /// reaching in would make the I/O layer depend on the thing built on top of
    /// it.</para>
    ///
    /// <para>This test used to be called <c>MediaAndPluginsAreSiblings</c>, which
    /// was accurate when both were leaves. It stopped being accurate the moment
    /// the plugin host needed <c>ChildProcess</c>: Plugins now sits on Media. A
    /// test whose name lies is worse than no test, because the next person
    /// decides what to do from the name.</para>
    /// </summary>
    [Test]
    [Arguments("Lae.Media")]
    [Arguments("Lae.Plugins")]
    public async Task ThePipelineIsNeverReferencedFromBelow(string assembly)
    {
        await Assert.That(ReferencedProjects(assembly)).DoesNotContain("Lae.Encoding")
            .Because("Lae.Encoding sits above Lae.Media and beside nothing; "
                   + "the pipeline executes hops, it does not know who made them");
    }

    /// <summary>
    /// The media layer must not know that plugins exist. One-way only: Plugins
    /// references Media for <c>ChildProcess</c>, and that is the direction the
    /// layering allows.
    /// </summary>
    [Test]
    public async Task MediaKnowsNothingOfPlugins()
    {
        await Assert.That(ReferencedProjects("Lae.Media")).DoesNotContain("Lae.Plugins")
            .Because("media handling must not know that plugins exist; "
                   + "plugins may use Media, never the reverse");
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

    [GeneratedRegex(@"\b(Process\.Start|new\s+Process\s*\()")]
    private static partial Regex ProcessSpawn();

    [GeneratedRegex(@"\bnew\s+Thread\s*\(")]
    private static partial Regex ThreadSpawn();

    /// <summary>
    /// Every child process goes through <c>Lae.Media/Processes/ChildProcess</c>.
    ///
    /// <para>Not tidiness: that type is the only one that drains stderr and kills
    /// the whole tree, and a second spawn site is a second thing that can hang an
    /// encode on a full pipe or leave two FFmpeg children behind after a Ctrl+C.
    /// v7 had four. The rule is worth a test because the next <c>lae toolchain
    /// install</c> will want to run a program, and the obvious way to write it is
    /// the wrong one.</para>
    /// </summary>
    [Test]
    public async Task ProcessesAreOnlyStartedThroughChildProcess()
    {
        List<string> offenders = [];

        foreach (string assembly in new[] { "Lae.Core", "Lae.App", "Lae.Cli", "Lae.Plugins", "Lae.Encoding" })
        {
            foreach (string file in SourceFiles(assembly))
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (ProcessSpawn().IsMatch(lines[i]))
                        offenders.Add($"{assembly}/{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        await Assert.That(offenders).IsEmpty()
            .Because("spawn through Lae.Media/Processes/ChildProcess; it owns stderr draining and tree kill");
    }

    /// <summary>
    /// The pacing thread belongs to the pipeline's pump and to nothing else above
    /// it.
    ///
    /// <para>A thread started by a command is a thread nobody joins, and the
    /// encoding pipeline already has exactly one place where a second thread is
    /// justified -- the producer that reads the decoder pipe. Everything else
    /// above that line is meant to be a pure function.</para>
    ///
    /// <para>The pump moved from <c>Lae.Core</c> to <c>Lae.Encoding</c> (it is
    /// runtime mechanics, not a domain rule), so the scan follows it: the pipeline
    /// is now the assembly that owns the thread, and its neighbours there are
    /// checked too. <c>FramePump.cs</c> is the one file allowed to say
    /// <c>new Thread</c>.</para>
    /// </summary>
    [Test]
    public async Task OnlyThePipelineStartsThreads()
    {
        List<string> offenders = [];

        foreach (string assembly in new[] { "Lae.App", "Lae.Cli", "Lae.Encoding" })
        {
            foreach (string file in SourceFiles(assembly))
            {
                // The single producer thread, and the only one in the program.
                if (Path.GetFileName(file) == "FramePump.cs") continue;

                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (ThreadSpawn().IsMatch(lines[i]))
                        offenders.Add($"{assembly}/{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        await Assert.That(offenders).IsEmpty()
            .Because("the pipeline owns the only thread, in FramePump; the host and the chain do not start one");
    }
}
