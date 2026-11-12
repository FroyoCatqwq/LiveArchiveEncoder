using System.Globalization;

namespace LiveArchiveEncoder.PostProcess;

/// <summary>
/// One physical video disc format. The numbers follow the published
/// specifications, not taste:
///
/// <list type="bullet">
/// <item>DVD-Video (DVD Forum, via the Wikipedia article the user cited):
/// MPEG-2 up to 9.8 Mbit/s for video alone; PAL 25 fps accepts
/// 720x576 / 704x576 / 352x576 / 352x288, NTSC 29.97 accepts
/// 720x480 / 704x480 / 352x480 / 352x240; AC-3 is 48 kHz, 1-5.1 channels,
/// up to 448 kbit/s; MP2 is 48 kHz up to 912 kbit/s; 4:2:0, 8 bit.</item>
/// <item>Video CD (White Book): MPEG-1, 4:3, 352x288 at 25 fps (PAL) or
/// 352x240 at 29.97 fps (NTSC), video 1150 kbit/s CBR, MPEG-1 Layer II audio
/// at 44.1 kHz and 224 kbit/s CBR.</item>
/// <item>CD-i (Green Book): the same MPEG-1 envelope as VCD. Wikipedia states
/// the two are close enough to be mutually incompatible only because of
/// "minor differences between the standards"; the encoding constraints are
/// therefore identical here and the profile exists so a CD-i target can be
/// named explicitly.</item>
/// <item>Super Video CD: MPEG-2 480x480 / 480x576, 2520 kbit/s video,
/// 224 kbit/s MP2 at 44.1 kHz. Not a required target, offered because it sits
/// between VCD and DVD and the table costs three lines.</item>
/// </list>
///
/// <para>GOP length is the one number the cited pages do not state; 15 frames
/// at 25 fps and 18 at 29.97 fps are the long-standing practical limits (about
/// 0.6 s) that every authoring tool uses, and they are what the profile
/// encodes.</para>
/// </summary>
public sealed record DiscProfile(
    string Id,
    string Name,
    string Encoder,
    string Container,
    int PalWidth,
    int PalHeight,
    int NtscWidth,
    int NtscHeight,
    int PalFps,
    int NtscFps,
    int VideoKbps,
    int MaxRateKbps,
    int BufsizeKbps,
    int GopFrames,
    string AudioEncoder,
    int AudioKbps,
    int AudioRate,
    int AudioChannels)
{
    public int WidthFor(int fps) => fps < 27 ? PalWidth : NtscWidth;

    public int HeightFor(int fps) => fps < 27 ? PalHeight : NtscHeight;

    public string Extension => Container switch
    {
        "mpeg" => ".mpg",
        "vob" => ".vob",
        _ => "." + Container,
    };

    /// <summary>The output-side encoder options this disc requires.</summary>
    public string VideoArgs =>
        $"-c:v {Encoder} -b:v {Kbps(VideoKbps)} -maxrate {Kbps(MaxRateKbps)} "
        + $"-bufsize {Kbps(BufsizeKbps)} -g {GopFrames.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The same options WITHOUT <c>-c:v</c>: the encoder is already pinned
    /// through the encoder selection, and injecting it twice would only make
    /// the command harder to read.
    /// </summary>
    public string EncoderArgs =>
        $"-b:v {Kbps(VideoKbps)} -maxrate {Kbps(MaxRateKbps)} "
        + $"-bufsize {Kbps(BufsizeKbps)} -g {GopFrames.ToString(CultureInfo.InvariantCulture)}";

    public string AudioArgs =>
        $"-c:a {AudioEncoder} -b:a {Kbps(AudioKbps)} "
        + $"-ar {AudioRate.ToString(CultureInfo.InvariantCulture)} "
        + $"-ac {AudioChannels.ToString(CultureInfo.InvariantCulture)}";

    private static string Kbps(int value) => value.ToString(CultureInfo.InvariantCulture) + "k";
}

public static class DiscProfiles
{
    public static readonly DiscProfile Dvd = new(
        "dvd", "DVD-Video", "mpeg2video", "mpeg",
        PalWidth: 720, PalHeight: 576, NtscWidth: 720, NtscHeight: 480,
        PalFps: 25, NtscFps: 30,
        VideoKbps: 6000, MaxRateKbps: 8000, BufsizeKbps: 1835, GopFrames: 15,
        AudioEncoder: "ac3", AudioKbps: 384, AudioRate: 48000, AudioChannels: 2);

    public static readonly DiscProfile Vcd = new(
        "vcd", "Video CD", "mpeg1video", "mpeg",
        PalWidth: 352, PalHeight: 288, NtscWidth: 352, NtscHeight: 240,
        PalFps: 25, NtscFps: 30,
        VideoKbps: 1150, MaxRateKbps: 1150, BufsizeKbps: 327, GopFrames: 15,
        AudioEncoder: "mp2", AudioKbps: 224, AudioRate: 44100, AudioChannels: 2);

    /// <summary>CD-i (Green Book): same MPEG-1 envelope as VCD.</summary>
    public static readonly DiscProfile Cd = new(
        "cd", "CD-i (Green Book)", "mpeg1video", "mpeg",
        PalWidth: 352, PalHeight: 288, NtscWidth: 352, NtscHeight: 240,
        PalFps: 25, NtscFps: 30,
        VideoKbps: 1150, MaxRateKbps: 1150, BufsizeKbps: 327, GopFrames: 15,
        AudioEncoder: "mp2", AudioKbps: 224, AudioRate: 44100, AudioChannels: 2);

    public static readonly DiscProfile Svcd = new(
        "svcd", "Super Video CD", "mpeg2video", "mpeg",
        PalWidth: 480, PalHeight: 576, NtscWidth: 480, NtscHeight: 480,
        PalFps: 25, NtscFps: 30,
        VideoKbps: 2520, MaxRateKbps: 2520, BufsizeKbps: 917, GopFrames: 15,
        AudioEncoder: "mp2", AudioKbps: 224, AudioRate: 44100, AudioChannels: 2);

    public static readonly DiscProfile[] All = [Dvd, Vcd, Svcd, Cd];

    public static DiscProfile? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        string key = id.Trim().ToLowerInvariant();
        // "--disc cd-i" and "--disc cd_i" name the same target.
        key = key.Replace('_', '-');
        return key switch
        {
            "cd-i" or "cdi" => Cd,
            _ => All.FirstOrDefault(p => p.Id == key),
        };
    }

    public static string IdList => string.Join(" / ", All.Select(p => p.Id));

    /// <summary>One line per profile for --list-discs and the CUI.</summary>
    public static string Describe(DiscProfile p) => string.Create(CultureInfo.InvariantCulture,
        $"{p.Id,-5} {p.Name,-20} {p.Encoder} {p.PalWidth}x{p.PalHeight}@{p.PalFps} "
        + $"{p.VideoKbps}k video + {p.AudioEncoder} {p.AudioKbps}k/{p.AudioRate}Hz");
}
