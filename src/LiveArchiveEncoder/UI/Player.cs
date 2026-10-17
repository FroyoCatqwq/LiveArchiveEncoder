using System.Diagnostics;
using LiveArchiveEncoder.Configuration;

namespace LiveArchiveEncoder.UI;

/// <summary>
/// Post-encode playback.
///
/// ffplay is preferred when it exists because it is the one player that reliably
/// opens the exotic legacy containers this tool writes (rm, roq, amv, a64...);
/// the external player is for people who want the result in something like MPC-HC
/// or VLC. With no external player configured the file is handed to the shell,
/// i.e. whatever the user's file association says.
/// </summary>
public static class Player
{
    /// <summary>Parses "off" / "ffplay" / "external"; anything else is "off".</summary>
    public static PlayMode ParseMode(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "ffplay" => PlayMode.Ffplay,
        "external" or "ext" or "player" => PlayMode.External,
        _ => PlayMode.Off,
    };

    public static string ModeName(PlayMode mode) => mode switch
    {
        PlayMode.Ffplay => "ffplay",
        PlayMode.External => "external",
        _ => "off",
    };

    public static string Describe(PlayMode mode, string? externalPlayer, string? ffplayPath) => mode switch
    {
        PlayMode.Ffplay => ffplayPath is null ? "ffplay (not found)" : ffplayPath,
        PlayMode.External => string.IsNullOrWhiteSpace(externalPlayer)
            ? "external (system default player)"
            : externalPlayer,
        _ => "off",
    };

    /// <summary>
    /// Start playback and return immediately. Failure is reported, never thrown:
    /// a broken player must not make a finished encode look like a failure.
    /// </summary>
    public static (bool Ok, string? Error) Launch(
        PlayMode mode, string file, string ffmpegPath, string? externalPlayer)
    {
        if (mode == PlayMode.Off) return (false, null);
        if (!File.Exists(file)) return (false, $"output file is gone: {file}");

        try
        {
            if (mode == PlayMode.Ffplay)
            {
                string? ffplay = FFmpeg.FFmpegDiscovery.FindFFplay(ffmpegPath);
                if (ffplay is null)
                    return (false, "ffplay was not found next to FFmpeg or on PATH; "
                        + "choose an external player instead");
                Start(ffplay, ["-autoexit", "-window_title", Path.GetFileName(file), file]);
                return (true, null);
            }

            if (string.IsNullOrWhiteSpace(externalPlayer))
            {
                // Shell association: opens with whatever the user has registered.
                Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
                return (true, null);
            }

            if (!File.Exists(externalPlayer))
                return (false, $"external player not found: {externalPlayer}");
            Start(externalPlayer, [file]);
            return (true, null);
        }
        catch (Exception exc) when (exc is System.ComponentModel.Win32Exception
                                        or InvalidOperationException or IOException)
        {
            return (false, exc.Message);
        }
    }

    private static void Start(string exe, IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo { FileName = exe, UseShellExecute = false };
        foreach (string a in args) info.ArgumentList.Add(a);
        Process.Start(info);
    }
}
