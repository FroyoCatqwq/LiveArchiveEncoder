using System.CommandLine;
using Lae.App;
using Lae.App.Ui;
using Lae.Core;
using Lae.Core.Localization;
using Lae.Core.Settings;
using Lae.Media.Localization;

namespace Lae.Tests.App;

/// <summary>
/// <c>lae preset</c> and <c>lae encode --preset</c>, through the real command
/// tree.
///
/// <para>Driving the tree rather than the helpers is deliberate: the bug this
/// guards against is not "the catalog is wrong" (that is tested in Core) but
/// "the option was added to the parser and not to the command", which is a whole
/// class of v7 failure. A parse that goes nowhere looks exactly like a feature
/// that works until someone tries it.</para>
/// </summary>
public sealed class PresetCommandTests
{
    /// <summary>The sentences a user actually sees; the assertions below quote them.</summary>
    private static readonly IMessages En = I18NextMessages.Create("en");

    private static (AppHost Host, BufferedTerminal Terminal) Build(SettingsDocument? stored = null)
    {
        var terminal = new BufferedTerminal();
        return (AppHost.InMemory(stored, terminal), terminal);
    }

    private static int Run(AppHost host, params string[] args)
        => CommandTree.Build(host).Parse(args).Invoke();

    private static SettingsDocument WithDefaults() => new()
    {
        Defaults = new EncodeDefaults { Encoder = "mpeg4", Width = 320, Height = 240, LimitKbps = 200 },
    };

    [Test]
    public async Task AddStoresThePresetAndSaysHowToUseIt()
    {
        var (host, terminal) = Build(WithDefaults());

        int code = Run(host, "preset", "add", "Portal 2006", "--encoder", "mpeg4", "--size", "320x240", "--limit", "150");

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(host.Settings.Load().CustomPresets.Count).IsEqualTo(1);
        await Assert.That(host.Settings.Load().CustomPresets[0].Id).IsEqualTo("portal-2006");
        await Assert.That(terminal.Output).Contains("--preset portal-2006");
    }

    [Test]
    public async Task AddRefusesASizeItCannotParse()
    {
        var (host, _) = Build(WithDefaults());

        int code = Run(host, "preset", "add", "bad", "--size", "320by240");

        await Assert.That(code).IsEqualTo(ExitCodes.Usage);
        await Assert.That(host.Settings.Load().CustomPresets.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ListSaysWhatToDoWhenThereIsNothingToShow()
    {
        var (host, terminal) = Build(WithDefaults());

        int code = Run(host, "preset", "list");

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(terminal.Output).Contains("lae preset add");
    }

    [Test]
    public async Task ListAndRemoveRoundTrip()
    {
        var (host, terminal) = Build(WithDefaults());
        Run(host, "preset", "add", "one", "--encoder", "mpeg4");

        await Assert.That(Run(host, "preset", "list")).IsEqualTo(0);
        await Assert.That(terminal.Output).Contains("one");

        await Assert.That(Run(host, "preset", "remove", "one")).IsEqualTo(0);
        await Assert.That(host.Settings.Load().CustomPresets.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RemovingSomethingAbsentFailsWithUsage()
    {
        var (host, _) = Build(WithDefaults());

        await Assert.That(Run(host, "preset", "remove", "ghost")).IsEqualTo(ExitCodes.Usage);
    }

    [Test]
    public async Task ShowPrintsTheEffectiveSettingsItWouldProduce()
    {
        var (host, terminal) = Build(WithDefaults());
        Run(host, "preset", "add", "small", "--size", "160x120", "--quality", "4");

        await Assert.That(Run(host, "preset", "show", "small")).IsEqualTo(0);
        await Assert.That(terminal.Output).Contains("160x120");
        await Assert.That(terminal.Output).Contains("applies");
    }

    /// <summary>
    /// A preset is the middle layer: stored defaults, then the preset, then
    /// whatever the command line says. Typing both must not leave the preset
    /// winning.
    /// </summary>
    [Test]
    public async Task TheCommandLineStillWinsOverThePreset()
    {
        SettingsDocument stored = WithDefaults();
        stored = stored with
        {
            CustomPresets = [new CustomPreset { Id = "small", Width = 160, Height = 120 }],
        };

        (EncodeRequest? request, string? error) = EncodeSession.BuildRequest(
            "/media/in.mkv",
            new EncodeOverrides { Preset = "small" },
            stored, En);

        await Assert.That(error).IsNull();
        await Assert.That(request!.Encode.Width).IsEqualTo(160);

        (EncodeRequest? overridden, _) = EncodeSession.BuildRequest(
            "/media/in.mkv",
            new EncodeOverrides { Preset = "small", Size = "640x480" },
            stored, En);

        await Assert.That(overridden!.Encode.Width).IsEqualTo(640);
    }

    [Test]
    public async Task AnUnknownPresetIsAUsageErrorNotAFallback()
    {
        (EncodeRequest? request, string? error) = EncodeSession.BuildRequest(
            "/media/in.mkv", new EncodeOverrides { Preset = "typo" }, WithDefaults(), En);

        // Silently encoding with the defaults would produce a file the user did
        // not ask for and could not explain.
        await Assert.That(request).IsNull();
        await Assert.That(error!.Contains("typo", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task TheOptionExistsOnTheEncodeCommand()
    {
        var (host, _) = Build(WithDefaults());

        // Parsing is where an option that was added to the handler but not to the
        // command shows up as "unrecognized".
        ParseResult parsed = CommandTree.Build(host).Parse(
            ["encode", "in.mkv", "--preset", "small", "--dry-run"]);

        await Assert.That(parsed.Errors.Count).IsEqualTo(0);
    }
}
