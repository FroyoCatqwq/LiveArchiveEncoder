using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.UI;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.9.2 regressions: the era-standard default ladder, path memory,
/// generic encoder arguments, VBV validation and the output lock pre-flight.
/// </summary>
public sealed class V7992Tests
{
    [Test]
    public async Task TheDefaultLadderUsesTheEraStandardRungs()
    {
        await Assert.That(Constants.DefaultFpsLevels)
            .IsEquivalentTo(new[] { 13.0, 12.0, 10.0, 8.0, 5.0 });
        await Assert.That(Constants.DefaultFpsLevelsSpec).IsEqualTo("13,12,10,8,5");
        // SelectLevels appends the base rate; the first rung must already be it.
        await Assert.That(Constants.DefaultFpsLevels[0]).IsEqualTo(Constants.DefaultFps);

        List<double> ladder = EncoderMath.SelectLevels(Constants.DefaultFps, null);
        await Assert.That(ladder.Count).IsEqualTo(5);
        await Assert.That(ladder[0]).IsEqualTo(Constants.DefaultFps);
        await Assert.That(ladder[1]).IsEqualTo(12.0);
        // The Python-baseline 13/8/5/3 is kept ONLY for migrating old configs.
        await Assert.That(Constants.LegacyFpsLevels)
            .IsEquivalentTo(new[] { 13.0, 8.0, 5.0, 3.0 });
    }

    [Test]
    public async Task NoneStillRemovesTheWholeLadder()
    {
        (List<double> fps, List<FpsRungSetting> rungs, string? error) =
            EncoderMath.ParseLevelSpec("none");
        await Assert.That(error).IsNull();
        await Assert.That(fps.Count).IsEqualTo(0);
        await Assert.That(rungs.Count).IsEqualTo(0);
        await Assert.That(EncoderMath.SelectLevels(Constants.DefaultFps, fps).Count)
            .IsEqualTo(1);

        (fps, _, error) = EncoderMath.ParseLevelSpec("Off");
        await Assert.That(error).IsNull();
        await Assert.That(fps.Count).IsEqualTo(0);

        (_, _, error) = EncoderMath.ParseLevelSpec("abc");
        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task PathMemoryKeepsOnlyExistingAbsoluteUniqueDirectories()
    {
        string dir = NewDir();
        try
        {
            List<string> kept = PathMemory.Normalise(
                [dir, dir.ToUpperInvariant(), "relative/path", "", "   ", dir]);
            await Assert.That(kept.Count).IsEqualTo(1);
            await Assert.That(kept[0]).IsEqualTo(Path.GetFullPath(dir));

            var mru = new List<string>();
            PathMemory.Remember(mru, dir, "no/such/dir");
            await Assert.That(mru.Count).IsEqualTo(1);

            string second = NewDir();
            try
            {
                PathMemory.Remember(mru, second, dir);
                await Assert.That(mru.Count).IsEqualTo(2);
                await Assert.That(mru[0]).IsEqualTo(Path.GetFullPath(second));
                await Assert.That(mru[1]).IsEqualTo(Path.GetFullPath(dir));
            }
            finally { Directory.Delete(second, recursive: true); }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task ANamedDirectoryIsCompletedIntoAnOutputFile()
    {
        string dir = NewDir();
        try
        {
            string? completed = PathMemory.CompleteDirectory(dir, "clip", ".avi");
            await Assert.That(completed).IsEqualTo(Path.Combine(dir, "clip_live.avi"));
            await Assert.That(PathMemory.CompleteDirectory(
                Path.Combine(dir, "not-a-dir"), "clip", "avi")).IsNull();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task GenericEncoderArgumentsAreParsedAndRoundTripped()
    {
        (CliOptions? opts, string? error) = CliParser.Parse(
            ["clip.mkv", "--recompress-video-kbps", "250",
             "--recompress-maxrate", "300", "--recompress-minrate", "100",
             "--recompress-bufsize", "600", "--recompress-extra", "-preset slow -ac 1",
             "--extra-encoder-args", "-maxrate 300k -bufsize 600k"]);
        await Assert.That(error).IsNull();
        await Assert.That(opts!.RecompressMaxrate).IsEqualTo(300);
        await Assert.That(opts.RecompressMinrate).IsEqualTo(100);
        await Assert.That(opts.RecompressBufsize).IsEqualTo(600);
        await Assert.That(opts.RecompressExtra).IsEqualTo("-preset slow -ac 1");
        await Assert.That(opts.ExtraEncoderArgs).IsEqualTo("-maxrate 300k -bufsize 600k");

        // A bad number is rejected at parse time, not silently dropped.
        (_, error) = CliParser.Parse(["clip.mkv", "--recompress-maxrate", "abc"]);
        await Assert.That(error).IsNotNull();

        var stored = new UserConfig { ExtraEncoderArgs = "-profile:v high" };
        await Assert.That(CliArgs.FromUserConfig(stored).Contains("--extra-encoder-args"))
            .IsTrue();
    }

    [Test]
    public async Task VbvNeedsATargetBitrateAndSaneBounds()
    {
        (CliOptions? noBitrate, _) = CliParser.Parse(
            ["clip.mkv", "--recompress-maxrate", "300"]);
        (_, string? error) = Cli.ResolveRecompress(noBitrate!, null, true);
        await Assert.That(error).IsNotNull();
        await Assert.That(error!).Contains("need a target bitrate");

        (CliOptions? inverted, _) = CliParser.Parse(
            ["clip.mkv", "--recompress-codec", "libx264",
             "--recompress-video-kbps", "250",
             "--recompress-maxrate", "100", "--recompress-minrate", "300"]);
        (_, error) = Cli.ResolveRecompress(inverted!, null, true);
        await Assert.That(error).IsNotNull();
        await Assert.That(error!).Contains("must not exceed");

        // With a bitrate present the trio lands in the generated fragment.
        (CliOptions? valid, _) = CliParser.Parse(
            ["clip.mkv", "--recompress-codec", "libx264",
             "--recompress-video-kbps", "250",
             "--recompress-maxrate", "300", "--recompress-minrate", "100",
             "--recompress-bufsize", "600"]);
        (Configuration.RecompressSettings settings, error) =
            Cli.ResolveRecompress(valid!, null, true);
        await Assert.That(error).IsNull();
        await Assert.That(settings.Args).Contains("-b:v 250k");
        await Assert.That(settings.Args).Contains("-maxrate 300k");
        await Assert.That(settings.Args).Contains("-minrate 100k");
        await Assert.That(settings.Args).Contains("-bufsize 600k");
    }

    [Test]
    public async Task PreflightRemovesStalePartFilesAndAcceptsAFreeTarget()
    {
        string dir = NewDir();
        try
        {
            string stale = Path.Combine(dir, "clip.deadbeef.part.avi");
            File.WriteAllText(stale, "stale");
            string target = Path.Combine(dir, "clip.avi");

            (bool ok, string? error) = OutputFile.EnsureTargetAvailable(new FileInfo(target));
            await Assert.That(ok).IsTrue();
            await Assert.That(error).IsNull();
            await Assert.That(File.Exists(stale)).IsFalse();

            // A directory in the way is left to Promote's "keep the part" path.
            string blocked = Path.Combine(dir, "blocked.avi");
            Directory.CreateDirectory(blocked);
            (ok, error) = OutputFile.EnsureTargetAvailable(new FileInfo(blocked));
            await Assert.That(ok).IsTrue();
            await Assert.That(error).IsNull();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task KillingAnEmptyPlayerListIsHarmless()
    {
        // v7.9.9.3: `>= 0` was true by construction. With no matching holder
        // the contract is exactly zero, both for a null and an unknown path.
        await Assert.That(ChildProcesses.KillAll(null)).IsEqualTo(0);
        await Assert.That(ChildProcesses.KillAll(
            Path.Combine(Path.GetTempPath(), "lae-not-tracked.avi"))).IsEqualTo(0);
    }

    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lae-v7992-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
