using System.CommandLine;
using Lae.App;
using Lae.App.Ui;
using Lae.Core;
using Lae.Core.Settings;

namespace Lae.Tests.App;

/// <summary>
/// The ladder as the user spells it: <c>--limit</c>, <c>--recover</c> and
/// <c>--fps-levels</c>.
///
/// <para>These three only exist on the command line -- a dry run cannot show them
/// because they steer what goes down the pipe rather than what FFmpeg is told -- so
/// the thing worth testing is that what is typed arrives in the request, and that
/// a ladder that cannot work is refused before anything starts.</para>
/// </summary>
public sealed class LadderOptionTests
{
    private static SettingsDocument Stored() => new()
    {
        Defaults = new EncodeDefaults { Encoder = "mpeg4", LimitKbps = 200, RecoverKbps = 150 },
    };

    [Test]
    public async Task TheCeilingAndRecoveryReachTheRequest()
    {
        (EncodeRequest? request, string? error) = EncodeSession.BuildRequest(
            "/media/in.mkv",
            new EncodeOverrides { LimitKbps = 60, RecoverKbps = 40 },
            Stored());

        await Assert.That(error).IsNull();
        await Assert.That(request!.Encode.LimitKbps).IsEqualTo(60.0);
        await Assert.That(request.Encode.RecoverKbps).IsEqualTo(40.0);
    }

    /// <summary>
    /// Rungs replace the stored ladder whole. Keeping the old per-rung thresholds
    /// would leave a 150 kbps drop point attached to whichever frame rate
    /// survived, which is not what anyone asked for.
    /// </summary>
    [Test]
    public async Task NamedRungsReplaceTheStoredLadderAndItsThresholds()
    {
        (EncodeRequest? request, _) = EncodeSession.BuildRequest(
            "/media/in.mkv",
            new EncodeOverrides
            {
                FpsLevels = [13, 8],
                FpsThresholds = [new FpsThreshold(13, null, null), new FpsThreshold(8, 120, 90)],
            },
            Stored() with
            {
                Defaults = Stored().Defaults with
                {
                    FpsLevels = [13, 8, 5, 3],
                    FpsThresholds = [new FpsThreshold(5, 999, 888)],
                },
            });

        await Assert.That(string.Join(',', request!.Encode.FpsLevels)).IsEqualTo("13,8");
        await Assert.That(request.Encode.FpsThresholds.Count).IsEqualTo(2);
        await Assert.That(request.Encode.FpsThresholds[1].DropAboveKbps).IsEqualTo(120.0);
    }

    [Test]
    public async Task BothOptionsExistOnTheCommand()
    {
        var host = AppHost.InMemory(Stored(), new BufferedTerminal());

        ParseResult parsed = CommandTree.Build(host).Parse(
            ["encode", "in.mkv", "--limit", "60", "--recover", "40", "--fps-levels", "13,8:120:90", "--dry-run"]);

        await Assert.That(parsed.Errors.Count).IsEqualTo(0);
    }

    /// <summary>
    /// A ladder whose rise threshold is above its drop threshold can only flap.
    /// The option is refused before the toolchain is even probed, which is why
    /// this test needs no FFmpeg: exit 2 comes back without one.
    /// </summary>
    [Test]
    public async Task ALadderThatWouldFlapIsRefusedWithUsage()
    {
        var terminal = new BufferedTerminal();
        var host = AppHost.InMemory(Stored(), terminal);

        int code = CommandTree.Build(host)
            .Parse(["encode", "in.mkv", "--fps-levels", "13:150:200"])
            .Invoke();

        await Assert.That(code).IsEqualTo(ExitCodes.Usage);
        await Assert.That(terminal.Errors).Contains("--fps-levels");
    }
}
