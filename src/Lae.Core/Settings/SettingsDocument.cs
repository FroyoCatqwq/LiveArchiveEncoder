using System.Text.Json.Serialization;

namespace Lae.Core.Settings;

/// <summary>
/// The persisted settings, and the only persisted shape there is.
///
/// <para>v7 kept two models: a 76-property mutable <c>Config</c> for the running
/// program and a 98-property mutable <c>UserConfig</c> for the file, joined by a
/// hand-written 76-line <c>Capture</c> plus an 80-line <c>Apply</c>. Adding one
/// field gave three chances to get it wrong: forget the file side and the value
/// silently resets on the next save; forget the live side and it never takes
/// effect. Both happened (the NNEDI3 weights and the per-rung bitrate
/// thresholds were each dropped this way).</para>
///
/// <para>Here a field is declared once. Everything is <c>init</c>-only and a
/// changed document is produced with <c>with</c>, so a save cannot race an edit
/// and a forgotten field cannot silently vanish -- it either exists or it does
/// not compile.</para>
///
/// <para>What deliberately is <em>not</em> here: anything specific to one run
/// (input path, trim range, stream picks). Those live in <c>EncodeRequest</c>,
/// which never reaches the disk. v7 stored them, which is why a scripted run
/// used to inherit the previous session's trim range.</para>
/// </summary>
public sealed record SettingsDocument
{
    /// <summary>
    /// Bumped when the stored shape changes in a way a reader has to know about.
    /// A file with an older number is migrated on read; a newer one is refused
    /// rather than half-understood.
    /// </summary>
    public const int CurrentSchemaVersion = 11;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public ToolchainSettings Toolchain { get; init; } = new();

    public NetworkSettings Network { get; init; } = new();

    public UiSettings Ui { get; init; } = new();

    public EncodeDefaults Defaults { get; init; } = new();

    /// <summary>
    /// The presets there are. All of them.
    ///
    /// <para>v8 ships no catalogue. v7 carried 32 built-in rows -- five generic
    /// eras plus 27 site-and-year combinations -- and they were the part of the
    /// program that aged worst: a number that was right for one portal in 2006
    /// went wrong the moment anyone looked at the file closely, and nothing in
    /// the program could tell which row a user actually trusted. So the table is
    /// gone, and a preset is something the user wrote down, named and can edit.
    /// </para>
    /// </summary>
    public IReadOnlyList<CustomPreset> CustomPresets { get; init; } = [];

    public IReadOnlyList<PluginRegistration> Plugins { get; init; } = [];

    /// <summary>
    /// Repair whatever a hand-edited or older file can spell as <c>null</c>.
    ///
    /// <para>System.Text.Json takes <c>"fpsLevels": null</c> literally and writes
    /// a null into a property whose type forbids it. v7 learned this the hard way
    /// -- <c>--emit-args</c> died with a NullReferenceException inside the ladder
    /// parser, because only one of the read paths defended itself. One repair at
    /// the read boundary means every consumer sees the shape the type
    /// promises.</para>
    /// <para>Recursive, and that word is the whole point. The first version of
    /// this only repaired the six top-level members, so
    /// <c>"defaults": { "fpsLevels": null }</c> came out of the file with a null
    /// ladder and failed later, inside whichever consumer happened to walk it --
    /// which is precisely the shape of the v7 bug this method exists to prevent.
    /// A nested record gets its own <c>Normalised</c> and the parent calls it, so
    /// a new collection anywhere in the tree has one obvious place to be
    /// defended.</para>
    /// </summary>
    public SettingsDocument Normalised() => this with
    {
        Toolchain = Toolchain ?? new(),
        Network = Network ?? new(),
        Ui = Ui ?? new(),
        Defaults = Defaults is { } defaults ? defaults.Normalised() : new(),
        CustomPresets = CustomPresets is { } presets ? [.. presets.Where(p => p is not null)] : [],
        Plugins = Plugins is { } plugins ? [.. plugins.Where(p => p is not null)] : [],
    };
}

/// <summary>Which FFmpeg build to use and where the NNEDI3 weights come from.</summary>
public sealed record ToolchainSettings
{
    /// <summary>
    /// A distribution id from the toolchain catalogue, or <c>"path"</c> for
    /// "whatever is on PATH".
    /// </summary>
    public string FfmpegSource { get; init; } = "path";

    /// <summary>Explicit FFmpeg executable; overrides <see cref="FfmpegSource"/>.</summary>
    public string? FfmpegPath { get; init; }

    /// <summary>Explicit NNEDI3 weights file; null uses the managed install.</summary>
    public string? NnediWeights { get; init; }
}

/// <summary>
/// How downloads reach the network.
///
/// <para><see cref="GithubFront"/> is a prefix such as
/// <c>https://gh-proxy.com/</c>. The literal <c>"off"</c> is not a URL: it
/// records a decision ("do not route through a front"), which is different from
/// null ("never asked"). That distinction is load-bearing -- the weights
/// installer probes and picks a route only while the answer is null.</para>
/// </summary>
public sealed record NetworkSettings
{
    /// <summary>Proxy for toolchain downloads, e.g. <c>http://127.0.0.1:10808</c>.</summary>
    public string? Proxy { get; init; }

    public string? GithubFront { get; init; }
}

/// <summary>What the program prints and what it opens when the work is done.</summary>
public sealed record UiSettings
{
    /// <summary>Language tag, e.g. <c>en</c> or <c>zh</c>.</summary>
    public string Language { get; init; } = "en";

    /// <summary>"off", "ffplay" or "external".</summary>
    public string PlayMode { get; init; } = "off";

    /// <summary>External player executable; empty means the shell association.</summary>
    public string? PlayerPath { get; init; }

    /// <summary>Ask the release feed for a newer build when a session starts.</summary>
    public bool CheckForUpdates { get; init; } = true;
}

/// <summary>
/// The settings an encode starts from when the command line does not say.
///
/// <para>Every value here is a plain one: the encoder is a name, not a resolved
/// <c>EncoderInfo</c>, because which encoders exist depends on the FFmpeg build
/// that happens to be installed. A name that cannot be resolved is dropped on
/// read instead of trusted.</para>
/// </summary>
public sealed record EncodeDefaults
{
    public string? Encoder { get; init; }

    /// <summary>Container override; null means the encoder's own default.</summary>
    public string? Container { get; init; }

    public string? AudioEncoder { get; init; } = "libmp3lame";

    public double Fps { get; init; } = Constants.DefaultFps;

    /// <summary>Adaptive ladder, highest rung first.</summary>
    public IReadOnlyList<double> FpsLevels { get; init; } = [.. Constants.DefaultFpsLevels];

    /// <summary>
    /// Per-rung bitrate thresholds, keyed by FPS rather than by position so
    /// editing the ladder cannot shift a threshold onto another rung. Empty
    /// means "derive from Limit/Recover".
    /// </summary>
    public IReadOnlyList<FpsThreshold> FpsThresholds { get; init; } = [];

    /// <summary>Bitrate ceiling; the engine descends a rung when it is reached.</summary>
    public double LimitKbps { get; init; } = 200.0;

    /// <summary>Bitrate the engine climbs back from.</summary>
    public double RecoverKbps { get; init; } = 150.0;

    /// <summary>Window the bitrate is averaged over.</summary>
    public double WindowSeconds { get; init; } = 2.0;

    /// <summary>
    /// Quality, in the scale of whichever encoder is chosen: 2..31 for the mpeg
    /// family, 0..51 for x264 and friends. The number moves with the encoder
    /// through <c>EncoderMath.ConvertQuality</c>, and
    /// <c>ArgsBuilder.VideoQualityArgs</c> is the only place that turns it into
    /// flags. Reading it as one fixed scale was v7.9.8.2's bug: <c>-q:v 9</c>
    /// handed to libx264 as a CRF came out at 2.5x the intended bitrate.
    /// </summary>
    public int Quality { get; init; } = 8;

    public int Threads { get; init; } = 16;

    public int BufferFrames { get; init; } = 6;

    public int AudioKbps { get; init; } = 32;

    public int AudioChannels { get; init; } = 1;

    /// <summary>Audio sample rate; null keeps the source's.</summary>
    public int? AudioRate { get; init; }

    public int Width { get; init; } = Constants.DefaultWidth;

    public int Height { get; init; } = Constants.DefaultHeight;

    /// <summary>"crop" fills the frame and discards the overflow, "fit" pads, "stretch" distorts.</summary>
    public string FitMode { get; init; } = "crop";

    /// <summary>Which edge survives a crop: center, top, bottom, left, right.</summary>
    public string CropAnchor { get; init; } = "center";

    /// <summary>Bar colour for "fit", e.g. black, white or 0xRRGGBB.</summary>
    public string PadColor { get; init; } = "black";

    public string ScaleAlgo { get; init; } = "neighbor";

    /// <summary>Output pixel format; null keeps the source's when it is compatible.</summary>
    public string? PixFmt { get; init; }

    public ColorTags Color { get; init; } = new();

    /// <summary>swscale dither for low-colour output; "auto" emits no flag.</summary>
    public string Dither { get; init; } = "auto";

    public DeinterlaceSettings Deinterlace { get; init; } = new();

    /// <summary>Hardware decode method: "off", "auto", or a name from <c>ffmpeg -hwaccels</c>.</summary>
    public string Hwaccel { get; init; } = "off";

    public X264Settings X264 { get; init; } = new();

    /// <summary>Shard count for the parallel pipeline; 1 disables it.</summary>
    public int Parallel { get; init; } = 1;

    public SubtitleDefaults Subtitles { get; init; } = new();

    public RecompressChain Recompress { get; init; } = new();

    /// <summary>
    /// The same repair as <see cref="SettingsDocument.Normalised"/>, one level
    /// down. Every member that holds a collection is rebuilt, so a <c>null</c>
    /// written by a hand edit cannot reach a consumer as a null list.
    /// </summary>
    public EncodeDefaults Normalised() => this with
    {
        FpsLevels = FpsLevels is { } levels ? [.. levels] : [.. Constants.DefaultFpsLevels],
        FpsThresholds = FpsThresholds is { } thresholds ? [.. thresholds.Where(t => t is not null)] : [],
        Color = Color ?? new(),
        Deinterlace = Deinterlace ?? new(),
        X264 = X264 ?? new(),
        Subtitles = Subtitles is { } subtitles ? subtitles.Normalised() : new(),
        Recompress = Recompress is { } chain ? chain.Normalised() : new(),
    };
}

/// <summary>One rung's thresholds.</summary>
/// <param name="Fps">The rung this threshold belongs to.</param>
/// <param name="DropAboveKbps">Descend once the bitrate reaches this.</param>
/// <param name="RiseBelowKbps">Climb back once it falls to this.</param>
public sealed record FpsThreshold(double Fps, double? DropAboveKbps, double? RiseBelowKbps);

/// <summary>
/// Colour metadata written onto the output. These are tags, not conversions:
/// the numbers go into the stream header unchanged, so a wrong value is a lie
/// rather than a transform. Null keeps whatever the source declared.
/// </summary>
public sealed record ColorTags
{
    public string? Range { get; init; }

    public string? Space { get; init; }

    public string? Primaries { get; init; }

    public string? Transfer { get; init; }

    [JsonIgnore]
    public bool Any => Range is not null || Space is not null || Primaries is not null || Transfer is not null;
}

/// <summary>
/// Deinterlacing. <see cref="MarkedOnly"/> keeps the filter to frames the
/// source flags as interlaced, which is what a film telecined in places needs.
/// </summary>
public sealed record DeinterlaceSettings
{
    /// <summary>"off", "yadif", "bwdif", "nnedi", ... </summary>
    public string Filter { get; init; } = "off";

    /// <summary>
    /// What the filter emits: <c>send_frame</c> (one frame per input frame,
    /// the normal choice) or <c>send_field</c> (one frame per field, which
    /// doubles the rate). These are two different pictures, so the value is
    /// persisted rather than assumed.
    /// </summary>
    public string FieldMode { get; init; } = "send_frame";

    /// <summary>Field order handed to the filter: auto, tff, bff.</summary>
    public string Parity { get; init; } = "auto";

    public bool MarkedOnly { get; init; }

    [JsonIgnore]
    public bool Any => Filter != "off";
}

/// <summary>x264 tuning knobs; ignored by every other encoder.</summary>
public sealed record X264Settings
{
    public string Preset { get; init; } = "medium";

    public string Tune { get; init; } = "";

    public string Profile { get; init; } = "";

    public string Level { get; init; } = "";
}

/// <summary>Subtitle defaults, persisted. Mirrors what a run may ask for.</summary>
public sealed record SubtitleDefaults
{
    /// <summary>"burn", "copy" or "off".</summary>
    public string Mode { get; init; } = "burn";

    /// <summary>Sidecar files (.ass/.ssa/.srt/.vtt), in order.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>Absolute index of an embedded track; null means none picked.</summary>
    public int? Stream { get; init; }

    /// <summary>
    /// Absolute indices of subtitle tracks to carry into the output as streams of
    /// their own, for "copy" mode.
    ///
    /// <para>A list rather than one index because the files this feature exists
    /// for carry several: a WEB-DL with simplified and traditional Chinese plus
    /// Japanese is three tracks, and carrying one of them is not what the user
    /// asked for.</para>
    /// </summary>
    public IReadOnlyList<int> CopyTracks { get; init; } = [];

    /// <summary>Explicit codec for copy mode; null picks one per container.</summary>
    public string? Codec { get; init; }

    /// <summary>Extra directory libass looks in, on top of the program's own fonts/.</summary>
    public string? FontDir { get; init; }

    /// <summary>Family forced over whatever the subtitle asks for.</summary>
    public string? FallbackFont { get; init; }

    /// <summary>Shape the burned text is laid out in: "source", "4:3" or "16:9".</summary>
    public string BoxAspect { get; init; } = "source";

    /// <summary>Files, an embedded track and a carried track are mutually exclusive; reports whether any is set.</summary>
    [JsonIgnore]
    public bool Any => Mode != "off" && (Files.Count > 0 || Stream is not null || CopyTracks.Count > 0);

    public SubtitleDefaults Normalised() => this with
    {
        Files = Files is { } files ? [.. files.Where(f => f is not null)] : [],
        CopyTracks = CopyTracks is { } tracks ? [.. tracks] : [],
    };
}

/// <summary>One hop of the "upload it to a video site" chain.</summary>
/// <param name="Args">Everything between <c>-i</c> and the output path.</param>
/// <param name="Container">Container extension without the dot.</param>
public sealed record RecompressPass(string Args, string Container);

/// <summary>
/// The second-pass chain. A recording can be published more than once -- a 2007
/// portal first, then the 2011 one -- and each hop is a real transcode, so this
/// is a list rather than one fragment.
///
/// <para>It is a setting and never an implicit default: a run re-encodes its own
/// output only when the command line says so. v7.9.8.6 shipped a release that
/// merged the stored chain into the live run, and every plain encode silently
/// recompressed itself until the e2e suite caught it.</para>
/// </summary>
public sealed record RecompressChain
{
    /// <summary>Whether the chain runs at all. Off means the list is kept but unused.</summary>
    public bool Enabled { get; init; }

    public IReadOnlyList<RecompressPass> Passes { get; init; } = [];

    /// <summary>Passes with a blank fragment are dropped; FFmpeg with no arguments is just a copy.</summary>
    public IReadOnlyList<RecompressPass> Active() =>
        [.. Passes.Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Args))];

    /// <summary>
    /// Rebuild the pass list, dropping nulls. A hand-written
    /// <c>"passes": [null]</c> would otherwise reach <see cref="Active"/> as a
    /// null array, and every read path would have to defend itself.
    /// </summary>
    public RecompressChain Normalised() => this with
    {
        Passes = Passes is { } passes ? [.. passes.Where(p => p is not null)] : [],
    };
}

/// <summary>
/// A preset the user wrote, stored as plain values only.
///
/// <para>No delegate, no pre-rendered FFmpeg fragment: the argument string is
/// regenerated from these numbers on load, so a stored preset can never carry a
/// stale or hand-edited command. v7 settled on this shape after a preset saved
/// by an older build produced arguments the newer build no longer understood.</para>
///
/// <para>Nothing in this record is required. A preset is a bundle of the values
/// it overrides and nothing else, so an unset field means "leave the stored
/// default alone" -- which is what makes a two-field preset useful instead of
/// thirty-field boilerplate.</para>
/// </summary>
public sealed record CustomPreset
{
    /// <summary>Stable identifier the command line takes, e.g. <c>portal-2006</c>.</summary>
    public string Id { get; init; } = "";

    /// <summary>What to call it in a list; falls back to the id.</summary>
    public string? Label { get; init; }

    public string? Note { get; init; }

    public string? Encoder { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public double? Fps { get; init; }

    public IReadOnlyList<double>? FpsLevels { get; init; }

    public double? LimitKbps { get; init; }

    /// <summary>Live-pass quality in both scales; the encoder picks the one it reads.</summary>
    public int? LiveQv { get; init; }

    public int? LiveCrf { get; init; }

    public double? WindowSeconds { get; init; }

    public int? BufferFrames { get; init; }

    public string? Deinterlace { get; init; }

    public string? ScaleAlgo { get; init; }

    public string? AudioEncoder { get; init; }

    public int? AudioKbps { get; init; }

    public int? AudioChannels { get; init; }

    public int? AudioRate { get; init; }

    public int RcWidth { get; init; } = 320;

    public int RcHeight { get; init; } = 240;

    public double RcFps { get; init; } = 15;

    public int RcVideoKbps { get; init; } = 300;

    public int RcCrf { get; init; } = 28;

    public string RcProfile { get; init; } = "baseline";

    public int RcAudioKbps { get; init; } = 32;

    public int RcAudioRate { get; init; } = 22050;

    public int RcAudioChannels { get; init; } = 1;

    public string VideoCodec { get; init; } = "libx264";

    public string AudioCodec { get; init; } = "aac";

    /// <summary>Container for the second pass; null keeps whatever the encoder writes.</summary>
    public string? Container { get; init; }
}

/// <summary>
/// A plugin the host knows about. Declared here rather than discovered only at
/// runtime so a machine that cannot reach the plugin directory still knows what
/// was configured -- and can say so instead of silently running nothing.
/// </summary>
public sealed record PluginRegistration
{
    public required string Id { get; init; }

    /// <summary>Executable, resolved relative to the plugin directory when not absolute.</summary>
    public required string Command { get; init; }

    /// <summary>Stages this plugin may run at: "pre-encode", "post-encode", "between-passes".</summary>
    public IReadOnlyList<string> Stages { get; init; } = [];

    public bool Enabled { get; init; } = true;
}
