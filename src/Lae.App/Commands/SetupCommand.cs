using System.CommandLine;
using Lae.Core.Settings;
using Lae.Media.Toolchain;

namespace Lae.App.Commands;

/// <summary>
/// <c>lae setup</c> — say whether this machine can encode, and what to do if not.
///
/// <para>This exists because v8 dropped the interactive shell. v7's first run
/// noticed there was no FFmpeg and offered to fetch it; without that, a new
/// user's first encode dies with "cannot find ffmpeg" and no next step. v7.9.6.3
/// had already called that dead end out and fixed it, so losing it again would
/// be a regression dressed up as a simplification.</para>
///
/// <para>It reports and does not act. Downloading a build is
/// <c>lae toolchain install</c>, which is explicit; a command named "setup"
/// silently pulling 80 MB over someone's metered link is not a good default, and
/// a diagnostic that can fail halfway is not a diagnostic.</para>
/// </summary>
public static class SetupCommand
{
    public static Command Build(ISettingsStore store)
    {
        var command = new Command(
            "setup",
            "Check that FFmpeg and ffprobe are available. Exits 1 when they are not.");

        command.SetAction(_ =>
        {
            SettingsDocument settings = store.Load();
            Console.WriteLine($"Settings   {store.Location}");

            // The configured source wins over PATH, because that is what an
            // encode will use; reporting a PATH copy the run will ignore would be
            // worse than saying nothing.
            string? directory = ToolchainDirectory(settings);
            ToolchainProbeResult probe = ToolchainProbe.Probe(directory);

            Console.WriteLine($"Source     {settings.Toolchain.FfmpegSource}"
                + (directory is null ? "" : $" ({directory})"));
            Console.WriteLine($"ffmpeg     {probe.Ffmpeg ?? "(not found)"}");
            Console.WriteLine($"ffprobe    {probe.Ffprobe ?? "(not found)"}");

            if (probe.Complete)
            {
                Console.WriteLine();
                Console.WriteLine("Ready. Try: lae encode <input> --dry-run");
                return ExitCodes.Ok;
            }

            Console.WriteLine();
            Console.WriteLine("Missing:");
            foreach (string line in probe.Missing()) Console.WriteLine($"  - {line}");

            Console.WriteLine();
            Console.WriteLine("To fix it, either:");
            Console.WriteLine("  1. put ffmpeg and ffprobe on PATH, then run 'lae setup' again; or");
            Console.WriteLine("  2. run 'lae toolchain install' to fetch a build into the program's own folder.");
            Console.WriteLine();
            Console.WriteLine("This build has no download command yet; use option 1 for now.");

            return ExitCodes.Failed;
        });

        return command;
    }

    /// <summary>
    /// The folder an encode would look in, or null for "whatever is on PATH".
    /// Only an explicit path can name a folder: a distribution id such as
    /// <c>btbn</c> points at a managed install, which does not exist until
    /// <c>lae toolchain install</c> has run.
    /// </summary>
    private static string? ToolchainDirectory(SettingsDocument settings)
    {
        string? explicitPath = settings.Toolchain.FfmpegPath;
        if (string.IsNullOrWhiteSpace(explicitPath)) return null;

        // A path may name the executable rather than its folder; accept both,
        // because both are what a user would naturally type.
        if (File.Exists(explicitPath)) return Path.GetDirectoryName(explicitPath);
        return Directory.Exists(explicitPath) ? explicitPath : null;
    }
}
