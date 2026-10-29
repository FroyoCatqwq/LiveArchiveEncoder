using System.Globalization;
using System.Text.Json;

namespace Lae.Core;

/// <summary>
/// Turns <c>ffprobe -show_streams -show_format -of json</c> output into
/// <see cref="SourceInfo"/>.
///
/// <para>Pure, and deliberately so. In v7 this parsing lived inside the same
/// method as the process launch, which meant every edge case -- a stream with no
/// duration, a container with no bitrate, a source with three video tracks --
/// could only be exercised by having a real file with that shape. Here a string
/// is enough, so the awkward cases get covered with literals.</para>
/// </summary>
public static class FfprobeJson
{
    /// <summary>
    /// Reads the probe. Returns null and a reason instead of throwing: a CLI has
    /// to report "this file has no video stream" as a sentence with an exit code,
    /// not as an exception with a stack trace.
    /// </summary>
    public static SourceInfo? TryParse(string json, out string? error)
    {
        error = null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            error = "ffprobe did not return valid JSON";
            return null;
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            JsonElement? video = null;
            bool hasAudio = false;
            int audioRate = 0;
            int audioChannels = 0;
            var streams = new List<SourceStream>();

            if (root.TryGetProperty("streams", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement stream in list.EnumerateArray())
                {
                    string type = Text(stream, "codec_type") ?? "?";

                    if (type == "video" && video is null) video = stream;

                    if (type == "audio" && !hasAudio)
                    {
                        hasAudio = true;
                        audioRate = Number(stream, "sample_rate");
                        audioChannels = Number(stream, "channels");
                    }

                    int index = Number(stream, "index");
                    if (index < 0) continue;

                    streams.Add(new SourceStream(
                        index,
                        type,
                        Text(stream, "codec_name"),
                        Text(Tags(stream), "language"),
                        Text(Tags(stream), "title"),
                        type == "video" ? Video(stream) : null));
                }
            }

            if (video is not { } chosen)
            {
                error = "the source has no video stream";
                return null;
            }

            // The video stream's own duration wins; a container that stores none
            // falls back to the format's. A VOB on a share is the case that made
            // this explicit: its per-stream duration is absent.
            double duration = Positive(chosen, "duration") ?? Positive(root, "format", "duration") ?? 0;

            return new SourceInfo
            {
                DurationSeconds = duration,
                Width = Number(chosen, "width"),
                Height = Number(chosen, "height"),
                PixelFormat = Text(chosen, "pix_fmt") ?? "yuv420p",
                ColorRange = Text(chosen, "color_range"),
                ColorSpace = Text(chosen, "color_space"),
                ColorPrimaries = Text(chosen, "color_primaries"),
                ColorTransfer = Text(chosen, "color_transfer"),
                HasAudio = hasAudio,
                AudioRate = audioRate,
                AudioChannels = audioChannels,
                Streams = streams,
            };
        }
    }

    private static StreamInfo Video(JsonElement stream) => new()
    {
        Width = Number(stream, "width"),
        Height = Number(stream, "height"),
        PixFmt = Text(stream, "pix_fmt") ?? "yuv420p",
        ColorRange = Text(stream, "color_range"),
        ColorSpace = Text(stream, "color_space"),
        ColorPrimaries = Text(stream, "color_primaries"),
        ColorTransfer = Text(stream, "color_transfer"),
    };

    private static JsonElement Tags(JsonElement stream)
        => stream.TryGetProperty("tags", out JsonElement tags) ? tags : default;

    private static string? Text(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out JsonElement value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null,
        };
    }

    /// <summary>
    /// ffprobe writes most numbers as strings ("48000", "1.500000"). Reading
    /// through the text form accepts both shapes without caring which one a
    /// given FFmpeg build uses.
    /// </summary>
    private static int Number(JsonElement element, string name)
        => int.TryParse(Text(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : 0;

    private static double? Positive(JsonElement element, string name)
        => Positive(element, name, name) is { } value ? value : null;

    private static double? Positive(JsonElement element, string parent, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (parent != name)
        {
            if (!element.TryGetProperty(parent, out JsonElement child)) return null;
            return Positive(child, name);
        }

        if (!double.TryParse(Text(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            return null;
        return double.IsFinite(value) && value > 0 ? value : null;
    }
}
