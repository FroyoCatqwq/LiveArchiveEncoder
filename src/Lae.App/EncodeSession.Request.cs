using Lae.Core;
using Lae.Core.Presets;
using Lae.Core.Settings;

namespace Lae.App;

/// <summary>
/// The first half of <see cref="EncodeSession"/>: what the command line asked
/// for, turned into a request that never touches the disk.
/// </summary>
public static partial class EncodeSession
{
    /// <summary>
    /// Lays the command line over the stored defaults.
    ///
    /// <para>Only the fields this command actually spells are touched, and the
    /// result is a <c>with</c> expression, so a field the command does not know
    /// about survives untouched. Two things are deliberately <em>not</em> taken
    /// from the settings: the recompress chain and the subtitle block. A CLI run
    /// re-encodes its own output or burns subtitles only when it says so on the
    /// command line -- that rule is older than the settings file and it is what
    /// keeps a scripted run predictable.</para>
    /// </summary>
    public static (EncodeRequest? Request, string? Error) BuildRequest(
        string input, EncodeOverrides overrides, SettingsDocument settings)
    {
        // Order: stored defaults, then the preset, then the command line. Each
        // layer may only overwrite what it actually spells, which is what makes
        // "my preset, but at a smaller size" a one-word change.
        EncodeDefaults encode = settings.Defaults;

        if (overrides.Preset is { Length: > 0 } presetId)
        {
            if (PresetCatalog.Find(settings, presetId) is not { } preset)
                return (null, $"there is no preset named '{presetId}'; run 'lae preset list'");

            encode = PresetCatalog.Apply(preset, encode);
        }

        encode = encode with
        {
            Recompress = new RecompressChain(),
            Subtitles = new SubtitleDefaults(),
        };

        // ...and the same rule for subtitles: the file the last interactive
        // session had open is not this run's business. Only the command line can
        // turn burning on, which also means a scripted run cannot start drawing
        // captions because of something in a settings file.
        //
        // Three ways of naming subtitles, and mixing them is not a preference --
        // it is two runs the user asked for at once. Said here rather than left to
        // the later layer, because "carry" would otherwise quietly win over
        // "burn" and the burned track would simply not be there.
        if (overrides.SubtitleTrack is not null && overrides.SubtitleCopyTracks is { Count: > 0 })
        {
            return (null, "--subtitle-track burns one track and --subtitle-copy carries others; "
                         + "they are different jobs, pick one");
        }

        if (overrides.SubtitleFiles is { Count: > 0 }
            || overrides.SubtitleTrack is not null
            || overrides.SubtitleCopyTracks is { Count: > 0 })
        {
            // Three ways of naming subtitles, one mode each. A carried track is not
            // a burn: it stays a stream, which is the only thing that works for a
            // bitmap track and the only thing that keeps the original cue timing.
            bool carrying = overrides.SubtitleCopyTracks is { Count: > 0 };

            encode = encode with
            {
                Subtitles = new SubtitleDefaults
                {
                    Mode = carrying ? "copy" : "burn",
                    Files = [.. (overrides.SubtitleFiles ?? []).Where(f => !string.IsNullOrWhiteSpace(f))],
                    Stream = carrying ? null : overrides.SubtitleTrack,
                    CopyTracks = carrying ? [.. overrides.SubtitleCopyTracks!.Distinct()] : [],
                    BoxAspect = overrides.SubtitleBox ?? "source",
                    FallbackFont = overrides.SubtitleFont,
                    FontDir = overrides.FontsDir,
                },
            };
        }

        // The chain is armed by naming a hop and by nothing else. Each
        // --recompress is one more transcode, in the order written;
        // --recompress-container is paired by position, and a hop without one
        // derives its container from the codec it names.
        (RecompressChain? chain, string? chainError) = BuildChain(overrides);
        if (chainError is not null) return (null, chainError);
        if (chain is not null) encode = encode with { Recompress = chain };

        // The encoder moves first, and the quality number moves with it. A stored
        // 8 means a 2004 stream's -q:v 8; handed to libx264 unchanged it becomes
        // -crf 8, which is a different picture by a factor of two and a half in
        // bitrate. That is v7.9.8.2's bug, and switching the encoder from the
        // command line is the same door it came through -- the preset path
        // converts, so this one has to as well.
        //
        // Only when the user did not restate the number: --quality 20 alongside
        // --encoder libx264 is already a CRF, and converting it would turn 20
        // into 25.
        if (overrides.Encoder is { Length: > 0 } named)
        {
            EncodeDefaults previous = encode;
            encode = encode with { Encoder = named };

            if (overrides.Quality is null)
            {
                encode = encode with
                {
                    Quality = EncoderMath.ConvertQuality(previous.Encoder, named, previous.Quality),
                };
            }
        }
        else if (!encode.Encoder.HasText)
        {
            encode = encode with { Encoder = "mpeg4" };
        }

        if (overrides.Container is { } container) encode = encode with { Container = container };
        if (overrides.Quality is { } quality) encode = encode with { Quality = quality };
        if (overrides.LimitKbps is { } limit) encode = encode with { LimitKbps = limit };
        if (overrides.RecoverKbps is { } recover) encode = encode with { RecoverKbps = recover };

        // The ladder is replaced whole, never merged: rungs and their thresholds
        // are one list keyed by frame rate, and taking the new rungs while
        // keeping the old thresholds would put a user's 150 kbps drop point on
        // whichever rung happened to survive.
        if (overrides.FpsLevels is { Count: > 0 } levels)
        {
            encode = encode with
            {
                FpsLevels = [.. levels],
                FpsThresholds = overrides.FpsThresholds is { } thresholds ? [.. thresholds] : [],
            };
        }

        if (overrides.Size is { } size)
        {
            if (!TryParseSize(size, out int width, out int height))
                return (null, $"--size wants WIDTHxHEIGHT, got '{size}'");

            encode = encode with { Width = width, Height = height };
        }

        // No --out means "beside the source", but the extension is the plan's to
        // decide: it is the container the chosen encoder actually writes, which
        // is not known until the encoder has been looked up in this build. The
        // CLI used to guess "avi" here, and `--encoder libx264` then wrote an
        // MP4 into clip.avi.
        string? destination = string.IsNullOrWhiteSpace(overrides.Output) ? null : overrides.Output;

        var request = new EncodeRequest
        {
            SourcePath = input,
            DestinationPath = destination,
            Encode = encode,
            StartSeconds = overrides.From ?? 0,
            EndSeconds = overrides.To,
        };

        return (request, null);
    }

    /// <summary>
    /// The recompress chain this command line asked for, or null when it asked for
    /// none.
    ///
    /// <para>Shared by the two entry points on purpose: <c>--recompress-only</c>
    /// runs the same chain against a file that already exists, and a second copy of
    /// the pairing rules is how the two would drift apart.</para>
    /// </summary>
    public static (RecompressChain? Chain, string? Error) BuildChain(EncodeOverrides overrides)
    {
        if (overrides.RecompressArgs is not { Count: > 0 } passes) return (null, null);

        string[] containers = overrides.RecompressContainers is { Count: > 0 } given ? [.. given] : [];

        if (containers.Length > passes.Count)
        {
            return (null, $"{containers.Length} containers were given for {passes.Count} passes; "
                        + "there is nothing for the extra one to belong to");
        }

        return (new RecompressChain
        {
            Enabled = true,
            Passes =
            [
                .. passes.Select((args, index) =>
                    new RecompressPass(args, index < containers.Length ? containers[index] : "")),
            ],
        }, null);
    }

    /// <summary>
    /// WIDTHxHEIGHT, with either case of the separator and nothing else. The
    /// parse is strict because the alternative -- FFmpeg's own guess at a
    /// half-understood size -- produces a file at a size nobody asked for.
    /// </summary>
    public static bool TryParseSize(string text, out int width, out int height)
    {
        width = 0;
        height = 0;

        string[] parts = text.Split('x', 'X');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out width) || !int.TryParse(parts[1], out height)) return false;

        return width > 0 && height > 0;
    }
}
