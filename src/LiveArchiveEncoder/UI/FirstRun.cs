using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.FFmpeg;
using LiveArchiveEncoder.Localization;

namespace LiveArchiveEncoder.UI;

/// <summary>
/// First-run bootstrap: when no FFmpeg can be found, ask which distribution to
/// download instead of dying with "Cannot find ffmpeg".
///
/// <para>v7.9.6.3: the tool has been able to install FFmpeg for itself since
/// v7.7, but only through a menu that a first-time user never reaches -- a run
/// with no FFmpeg on PATH failed at discovery, before any UI existed. The
/// catalogue is therefore offered right at the failure, and a successful
/// install is remembered in the settings file so the next run starts straight
/// into the encoder.</para>
///
/// <para>The prompt is strictly interactive: with stdin or stdout redirected
/// (a script, a pipeline, CI) guessing is worse than failing, so the caller
/// keeps its original error and the manual flags
/// (<c>--ffmpeg-download</c>, <c>--ffmpeg</c>) stay the scripted route.</para>
/// </summary>
public static class FirstRun
{
    /// <summary>True when there is a human on the other end to answer a question.</summary>
    public static bool CanAsk() => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    /// <summary>
    /// Offer the downloadable distributions and install the chosen one.
    /// Returns true when an FFmpeg is now installed and selected.
    /// </summary>
    public static bool OfferInstall()
    {
        // Only the sources this build can fetch without a browser: asking a
        // first-time user to go find a .7z by hand is not a first run, it is a
        // scavenger hunt.
        List<FfmpegDist> choices = [.. FfmpegCatalog.Available.Where(d => d.AutoDownload)];

        Console.WriteLine();
        Console.WriteLine(L.T("firstrun.heading"));
        Console.WriteLine(L.T("firstrun.body"));
        Console.WriteLine();
        for (int i = 0; i < choices.Count; i++)
        {
            Console.WriteLine($" {i + 1}. {L.Pad(choices[i].Id, 8)} {choices[i].Label}");
        }
        Console.WriteLine($" 0. {L.T("firstrun.quit")}");
        Console.Write(L.T("firstrun.select"));

        string answer = (Console.ReadLine() ?? "").Trim();
        if (answer.Length == 0 || answer == "0") return false;
        if (!int.TryParse(answer, out int picked) || picked < 1 || picked > choices.Count)
        {
            Console.WriteLine(L.T("menu.invalid"));
            return false;
        }

        FfmpegDist dist = choices[picked - 1];
        // The proxy is the one already configured: a user behind one set it on
        // an earlier run, and asking again here would bury the actual question.
        UserConfig stored = ConfigStore.Load();
        Console.WriteLine(L.T("tc.installing", dist.Label));
        Console.WriteLine(dist.Homepage);

        (bool ok, string? error) = Toolchain.Install(dist, stored.FfmpegProxy, null, Console.WriteLine);
        if (!ok)
        {
            Console.Error.WriteLine($"{L.T("error.prefix")}{L.T("tc.installfail", error)}");
            return false;
        }

        // Remembered as the active source, exactly like --ffmpeg-download does,
        // so the next start finds it without a prompt.
        stored.FfmpegSource = dist.Id;
        ConfigStore.Save(stored);

        string? exe = Toolchain.FFmpegFor(dist.Id);
        if (exe is not null)
        {
            (string version, _, bool gpl) = FFmpegDiscovery.BuildInfo(exe);
            Console.WriteLine(version);
            if (!gpl) Console.WriteLine(L.T("tc.nogpl"));
        }
        Console.WriteLine(L.T("tc.installok", dist.Id));
        return true;
    }
}
