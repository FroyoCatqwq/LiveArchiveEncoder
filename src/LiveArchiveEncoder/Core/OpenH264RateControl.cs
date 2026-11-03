namespace LiveArchiveEncoder.Core;

/// <summary>
/// The rate-control modes OpenH264 can actually be driven in, and the two it
/// cannot.
///
/// <para>v7.9.8.8. Everything here was read off <c>libavcodec/libopenh264enc.c</c>
/// (the option table and <c>svc_encode_init</c>) and then measured against the
/// build this project ships with. The manual's libopenh264 section documents
/// <c>-b</c>, <c>-maxrate</c>, <c>-allow_skip_frames</c> and nothing else, so
/// the switch this whole feature hangs on is invisible to anyone reading the
/// docs -- which is why the probe exists and why this class exists.</para>
///
/// <para>The short version. <b>Bitrate is the only dial that moves the
/// picture.</b> There is no <c>-qp</c>: the wrapper never reads
/// <c>global_quality</c>, and <c>ffmpeg</c> says so out loud ("AVOption qp ...
/// has not been used for any stream"). <c>-qmin/-qmax</c> are copied into
/// <c>param.iMinQp/iMaxQp</c> but change nothing at <c>RC_OFF_MODE</c>, which
/// uses the library's own <c>iDLayerQp</c> of 26: measured, qmin=qmax of 10 and
/// of 51 produce byte-identical files.</para>
/// </summary>
public static class OpenH264RateControl
{
    /// <summary>
    /// Average bitrate is the target; the encoder spends it where the picture
    /// needs it. Measured linear: 100k -> 344 kbps, 1000k -> 1001, 3000k -> 2968.
    /// </summary>
    public const string Bitrate = "bitrate";

    /// <summary>
    /// Quality first: the encoder picks QP on its own and treats <c>-b:v</c> as
    /// a target rather than a budget. Measured 361 kbps at <c>-b:v 300k</c>,
    /// i.e. it spends about 20% over -- which is the point of the mode.
    /// </summary>
    public const string Quality = "quality";

    /// <summary>
    /// Bitrate distributed by presentation timestamps; the mode a live encoder
    /// wants when frame intervals wander. Measured 358 kbps at <c>-b:v 300k</c>.
    /// </summary>
    public const string Timestamp = "timestamp";

    /// <summary>The modes this program will emit, in the order it lists them.</summary>
    public static readonly IReadOnlyList<string> Usable = [Bitrate, Quality, Timestamp];

    public static string UsableList => string.Join(", ", Usable);

    /// <summary>
    /// The mode to emit, defaulting to <see cref="Bitrate"/>.
    ///
    /// <para>The default is not OpenH264's own (<c>quality</c>) on purpose.
    /// This program has always pinned a bitrate -- the ceiling is the promise
    /// the adaptive ladder is built on -- and <c>quality</c> spends up to 20%
    /// over it. Anyone who wants that behaviour asks for it.</para>
    /// </summary>
    public static string Normalize(string? mode)
    {
        string value = (mode ?? "").Trim().ToLowerInvariant();
        return value.Length == 0 ? Bitrate : value;
    }

    /// <summary>
    /// Null when <paramref name="mode"/> is one this program can honour,
    /// otherwise the sentence explaining why not.
    ///
    /// <para><c>buffer</c> and <c>off</c> are legal values of the FFmpeg option
    /// and both are useless here. <c>buffer</c> is documented as "no bitrate
    /// control" and behaves that way: asked for 300 kbps it produced 3.1 Mbps,
    /// a 10x miss that would blow through any ladder. <c>off</c> locks QP to the
    /// library's internal 26 with no exposed way to change it, so it is neither
    /// a quality knob nor a bitrate knob -- a switch that looks adjustable and
    /// is not. Refusing them with the measurement beats offering them and
    /// lying.</para>
    /// </summary>
    public static string? Problem(string? mode)
    {
        string value = Normalize(mode);
        if (Usable.Contains(value)) return null;

        return value switch
        {
            "buffer" => "rate control 'buffer' ignores the bitrate (measured: 300 kbps asked, "
                        + "3.1 Mbps produced), so it cannot be used to hold a ceiling; "
                        + $"choose one of: {UsableList}",
            "off" => "rate control 'off' pins OpenH264 to its internal QP 26 and exposes no way to "
                     + "change it (-qp does not exist; -qmin/-qmax are ignored in this mode), so it "
                     + $"is neither quality nor bitrate control; choose one of: {UsableList}",
            _ => $"unknown rate-control mode '{mode}'; choose one of: {UsableList}",
        };
    }
}
