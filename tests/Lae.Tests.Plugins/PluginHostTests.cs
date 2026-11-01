using System.Runtime.CompilerServices;
using Lae.Core;
using Lae.Core.Localization;
using Lae.Core.Plugins;
using Lae.Core.Settings;
using Lae.Media.Localization;
using Lae.Plugins;

namespace Lae.Tests.Plugins;

/// <summary>
/// The host, against a plugin that really runs.
///
/// <para><c>tools/fixtures/fake-plugin.py</c> is a real program that fails in
/// specific ways, so these are the only tests in the suite that spawn something.
/// Everything else about a plugin -- discovery, the handshake's shape, the three
/// product checks -- is tested without one, which is why there are only a handful
/// here.</para>
///
/// <para>Each test gets its own copy of the fixture, because how it should misbehave
/// is a file beside it. A shared copy with a shared mode would make these tests
/// order-dependent, and an order-dependent test is a test that fails on a busy
/// machine.</para>
/// </summary>
public sealed class PluginHostTests
{
    private const string Id = "lae-fake";
    private static readonly IMessages En = I18NextMessages.Create("en");

    /// <summary>The interpreter, or null on a machine with no Python at all.</summary>
    private static string? Python()
    {
        string root = RepoRoot();

        string venv = OperatingSystem.IsWindows()
            ? Path.Combine(root, ".venv", "Scripts", "python.exe")
            : Path.Combine(root, ".venv", "bin", "python");

        if (File.Exists(venv)) return venv;

        foreach (string name in OperatingSystem.IsWindows()
                     ? new[] { "python.exe", "python3.exe" }
                     : ["python3", "python"])
        {
            foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "")
                         .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(folder, name);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>Skips quietly when the machine has no interpreter to run the fixture with.</summary>
    private static string? Interpreter()
    {
        string? python = Python();
        if (python is null) return null;

        return File.Exists(Path.Combine(RepoRoot(), "tools", "fixtures", "fake-plugin.py"))
            ? python
            : null;
    }

    /// <summary>A product that reads as ten seconds of video, whatever the file is.</summary>
    private static (SourceInfo? Info, string? Error) Probe(string _, string __, CancellationToken ___)
        => (new SourceInfo
        {
            DurationSeconds = 10,
            Width = 320,
            Height = 240,
            Streams = [new SourceStream(0, "video", "mpeg4", "yuv420p", null)],
        }, null);

    [Test]
    public async Task AWorkingPluginProducesItsProduct()
    {
        if (Interpreter() is not { } python) return;

        using var box = new Box(python);
        string input = box.Input(256);
        string part = Path.Combine(box.PluginFolder, "out.part.mp4");

        PluginStepOutcome outcome = box.Host().Run(Box.Call(input, part), default);

        await Assert.That(outcome.Failed).IsFalse().Because(outcome.Error ?? "the host gave no reason");
        await Assert.That(File.Exists(part)).IsTrue();
        await Assert.That(new FileInfo(part).Length).IsEqualTo(256);
    }

    /// <summary>
    /// A plugin's own commentary is forwarded, not swallowed and not treated as an
    /// error. FFmpeg taught this program the same lesson.
    /// </summary>
    [Test]
    public async Task WhatThePluginWritesToStandardErrorIsForwarded()
    {
        if (Interpreter() is not { } python) return;

        using var box = new Box(python);
        List<string> said = [];

        PluginStepOutcome outcome = box.Host(warn: said.Add)
            .Run(Box.Call(box.Input(64), Path.Combine(box.PluginFolder, "out.part.mp4")), default);

        await Assert.That(outcome.Failed).IsFalse().Because(outcome.Error ?? "the host gave no reason");
        await Assert.That(string.Join(" | ", said)).Contains("plugin lae-fake:");
        await Assert.That(string.Join(" | ", said)).Contains("copy");
    }

    [Test]
    public async Task AHandshakeFromTheFutureIsRefused()
    {
        if (Interpreter() is not { } python) return;

        using var box = new Box(python, mode: "schema");

        PluginStepOutcome outcome = box.Host().Run(Box.Call(box.Input(64), box.Part()), default);

        await Assert.That(outcome.Failure).IsEqualTo(PluginFailure.Rejected);
        await Assert.That(outcome.Error).Contains("schema 99");
    }

    [Test]
    public async Task AHandshakeThatNamesADifferentPluginIsRefused()
    {
        if (Interpreter() is not { } python) return;

        using var box = new Box(python, mode: "wrongid");

        PluginStepOutcome outcome = box.Host().Run(Box.Call(box.Input(64), box.Part()), default);

        await Assert.That(outcome.Failure).IsEqualTo(PluginFailure.Rejected);
        await Assert.That(outcome.Error).Contains("someone-else");
    }

    /// <summary>
    /// A plugin that never answers must not hold the run. The timeout is shortened
    /// here so the test takes a second rather than ten.
    /// </summary>
    [Test]
    public async Task AHandshakeThatNeverAnswersGivesUp()
    {
        if (Interpreter() is not { } python) return;

        using var box = new Box(python, mode: "hang");

        PluginStepOutcome outcome = box.Host(handshakeTimeout: TimeSpan.FromMilliseconds(600))
            .Run(Box.Call(box.Input(64), box.Part()), default);

        await Assert.That(outcome.Failure).IsEqualTo(PluginFailure.Rejected);
        await Assert.That(outcome.Error).Contains("did not describe itself");
    }

    [Test]
    public async Task APartnerThatFailsIsReportedWithItsOwnCode()
    {
        if (Interpreter() is not { } python) return;

        using var box = new Box(python);

        PluginStepOutcome outcome = box.Host()
            .Run(Box.Call(box.Input(64), box.Part(), ("mode", "fail")), default);

        await Assert.That(outcome.Failure).IsEqualTo(PluginFailure.Rejected);
        await Assert.That(outcome.Error).Contains("code 3");
        await Assert.That(outcome.Error).Contains("refusing on purpose");
    }

    /// <summary>
    /// Exit 2 is the one code that is about this program rather than the plugin: it
    /// means the command line was wrong. Reporting it as a plugin failure starts the
    /// search in the wrong codebase.
    /// </summary>
    [Test]
    public async Task ExitTwoIsBlamedOnTheHostNotThePlugin()
    {
        if (Interpreter() is not { } python) return;

        using var box = new Box(python);

        PluginStepOutcome outcome = box.Host()
            .Run(Box.Call(box.Input(64), box.Part(), ("mode", "usage")), default);

        await Assert.That(outcome.Failure).IsEqualTo(PluginFailure.Rejected);
        await Assert.That(outcome.Error).Contains("not in the plugin");
    }

    /// <summary>
    /// The failure the whole verification layer exists for: exit zero, product
    /// unusable.
    /// </summary>
    [Test]
    public async Task ASuccessfulExitWithNoProductFailsVerification()
    {
        if (Interpreter() is not { } python) return;

        using var box = new Box(python);

        PluginStepOutcome outcome = box.Host()
            .Run(Box.Call(box.Input(64), box.Part(), ("mode", "empty")), default);

        await Assert.That(outcome.Failure).IsEqualTo(PluginFailure.Verification);
        await Assert.That(outcome.Error).Contains("empty");
    }

    [Test]
    public async Task APluginThatIsNotInstalledSaysSo()
    {
        if (Interpreter() is not { } python) return;

        using var box = new Box(python);

        PluginStepOutcome outcome = box.Host()
            .Run(new PluginCall
            {
                Plugin = "lae-nowhere",
                Stage = "copy",
                InputPath = box.Input(64),
                PartPath = box.Part(),
                Budget = TimeSpan.FromSeconds(30),
            }, default);

        await Assert.That(outcome.Failure).IsEqualTo(PluginFailure.Rejected);
        await Assert.That(outcome.Error).Contains("no plugin named");
    }

    /// <summary>A plugin that will not finish is killed rather than waited for.</summary>
    [Test]
    public async Task APluginThatTakesTooLongIsStopped()
    {
        if (Interpreter() is not { } python) return;

        using var box = new Box(python);
        PluginCall call = Box.Call(box.Input(64), box.Part(), ("mode", "slow")) with
        {
            Budget = TimeSpan.FromSeconds(1),
        };

        DateTime started = DateTime.UtcNow;
        PluginStepOutcome outcome = box.Host().Run(call, default);

        await Assert.That(outcome.Failure).IsEqualTo(PluginFailure.Rejected);
        await Assert.That(outcome.Error).Contains("did not finish");
        await Assert.That(DateTime.UtcNow - started).IsLessThan(TimeSpan.FromSeconds(20))
            .Because("the child sleeps for thirty seconds; only a kill gets us here sooner");
    }

    /// <summary>One throwaway install of the fixture, plus a host pointed at it.</summary>
    private sealed class Box : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "lae-host-" + Guid.NewGuid().ToString("N"));

        private readonly string _python;

        internal Box(string python, string? mode = null)
        {
            _python = python;

            Directory.CreateDirectory(PluginFolder);

            File.Copy(
                Path.Combine(RepoRoot(), "tools", "fixtures", "fake-plugin.py"),
                Path.Combine(PluginFolder, "fake-plugin.py"),
                overwrite: true);

            // The manifest names the interpreter and a relative script, which is
            // the shape a real plugin in a scripting language would use.
            File.WriteAllText(
                Path.Combine(PluginFolder, "plugin.json"),
                "{\"id\":\"" + Id + "\",\"command\":[\"" + python.Replace("\\", "\\\\")
                + "\",\"fake-plugin.py\"]}");

            if (mode is not null) File.WriteAllText(Path.Combine(PluginFolder, "mode.txt"), mode);
        }

        internal string PluginFolder => Path.Combine(_root, "plugins", Id);

        internal PluginHost Host(
            Action<string>? warn = null, TimeSpan? handshakeTimeout = null)
        {
            var search = new PluginSearch
            {
                ConfigRoot = _root,
                BaseDirectory = Path.Combine(_root, "app"),
                ExtraFolders = [],
                OnPath = _ => null,
            };

            return new PluginHost(
                new PluginFinder(search),
                [],
                new PluginVerifier("ffprobe", Probe, En),
                En,
                warn,
                handshakeTimeout);
        }

        internal static PluginCall Call(string input, string part, params (string Key, string Value)[] parameters)
            => new()
            {
                Plugin = Id,
                Stage = "copy",
                Parameters = parameters.ToDictionary(p => p.Key, p => p.Value),
                InputPath = input,
                PartPath = part,
                Budget = TimeSpan.FromSeconds(30),
            };

        internal string Part() => Path.Combine(PluginFolder, $"out-{Guid.NewGuid():N}.part.mp4");

        internal string Input(int bytes)
        {
            string path = Path.Combine(_root, $"in-{Guid.NewGuid():N}.mp4");
            File.WriteAllBytes(path, new byte[bytes]);
            return path;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
            {
                // A leftover temporary folder is not a test failure.
            }
        }
    }
}
