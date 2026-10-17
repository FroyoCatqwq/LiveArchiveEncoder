namespace LiveArchiveEncoder.Localization;

/// <summary>
/// Two-language string table for everything a user actually reads.
///
/// Diagnostics that come straight out of FFmpeg stay in English on purpose:
/// translating them makes the message impossible to search for, and the whole
/// point of surfacing them is that they can be looked up verbatim.
///
/// A missing key renders as the key itself, so a typo is loud instead of silent.
/// </summary>
public static class L
{
    public const string English = "en";
    public const string Chinese = "zh";

    private static readonly Dictionary<string, string> En = new()
    {
        ["app.title"] = "Live Archive Encoder CUI v7.3 (.NET 10)",
        ["app.impairments"] = "Impairments",
        ["app.impairments.value"] = "handled separately by frame_damage.py",

        ["banner.executable"] = "Executable",
        ["banner.btbn"] = "BtbN default",
        ["banner.gpl"] = "GPL build",
        ["banner.legacy"] = "Legacy video",
        ["banner.deinterlacers"] = "Deinterlacers",
        ["banner.scalers"] = "Scalers",
        ["banner.legacycount"] = "{0} detected from ffmpeg -encoders",

        ["value.yes"] = "yes",
        ["value.no"] = "no",
        ["value.none"] = "none",
        ["value.auto"] = "auto",
        ["value.custom"] = "custom",
        ["value.fallback"] = "fallback/custom",
        ["value.notdetected"] = "not detected",
        ["value.notselected"] = "(not selected)",
        ["value.notdeclared"] = "(not declared)",

        ["menu.heading"] = "=== Main menu ===",
        ["menu.input"] = "Input file",
        ["menu.output"] = "Output file",
        ["menu.encoder"] = "Video encoder",
        ["menu.parameters"] = "Parameters",
        ["menu.rescan"] = "Rescan encoders",
        ["menu.filters"] = "Show FFmpeg filters",
        ["menu.showconfig"] = "Show configuration",
        ["menu.start"] = "Start encoding",
        ["menu.turbo"] = "Turbo mode",
        ["menu.language"] = "Language",
        ["menu.parallel"] = "Parallel shards",
        ["menu.parallel.off"] = "off (single pass)",
        ["parallel.prompt"] = "Parallel shards (1 = off) [{0}]: ",
        ["parallel.set"] = "Parallel shards set to {0}. Only concatenation-safe containers "
            + "(avi, mp4, flv, asf, mpeg) are actually sharded; the rest fall back to a single pass.",
        ["parallel.off"] = "Parallel sharding off.",
        ["parallel.clamped"] = "Clamped to 1..{0}.",
        ["menu.exit"] = "Exit",
        ["menu.select"] = "Select: ",
        ["menu.invalid"] = "Invalid selection.",
        ["menu.notselected"] = "(not selected)",
        ["menu.auto"] = "(auto)",

        ["turbo.on"] = "ON (no realtime pacing)",
        ["turbo.off"] = "off (realtime)",
        ["turbo.enabled"] = "Turbo ON: the 1/fps realtime pacing is disabled. Much faster, but the "
            + "fresh/repeat mix and the adaptive bitrate ladder change, so the result no longer "
            + "matches a genuine live recording.",
        ["turbo.disabled"] = "Turbo off: realtime pacing restored.",

        ["config.settingsfile"] = "Settings file",
        ["config.restored"] = "Restored settings",
        ["config.savefailed"] = "WARNING: could not write settings to {0}",

        ["input.prompt"] = "\nInput file or directory: ",
        ["input.notfound"] = "Path not found.",
        ["input.novideo"] = "No common video files found in that directory.",
        ["input.selectnumber"] = "Select number: ",
        ["input.invalid"] = "Invalid selection.",
        ["input.select"] = "Select a valid input video first.",

        ["encoder.selectfirst"] = "Select a video encoder first.",
        ["encoder.heading"] = "=== Legacy / classic encoders detected from current FFmpeg ===",
        ["encoder.pixfmts"] = "pixel formats",
        ["encoder.prompt"] = "Select encoder number (r=refresh): ",
        ["encoder.outofrange"] = "out of range",
        ["encoder.invalid"] = "Invalid selection: {0}",
        ["encoder.fpsadjusted"] = "{0}: requested {1} fps is unsupported; using {2} fps.",
        ["encoder.sizesdjusted"] = "{0}: adjusted output size to {1}x{2}.",
        ["encoder.amvnote"] = "AMV: fixed audio is 22050 Hz IMA-ADPCM; video FPS is limited to 10/14/15/18/21/25/30.",
        ["encoder.roqnote"] = "RoQ: roq_dpcm requires 22050 Hz and mono/stereo audio; settings adjusted accordingly.",
        ["encoder.roqnoaudio"] = "RoQ: roq_dpcm is unavailable; output will be video-only.",
        ["encoder.videoonly"] = "{0}: container {1} will be video-only.",
        ["encoder.rescanok"] = "Rescan complete: {0} legacy video encoders detected.",

        ["output.prompt"] = "Output file path (blank=auto): ",
        ["output.sameasinput"] = "Output must differ from input.",

        ["error.prefix"] = "ERROR: ",

        ["param.heading"] = "=== Parameters ===",
        ["param.limit"] = "limit (video kbps)",
        ["param.recover"] = "recover (video kbps)",
        ["param.window"] = "window (seconds)",
        ["param.basefps"] = "base FPS",
        ["param.levels"] = "motion FPS levels",
        ["param.threads"] = "threads",
        ["param.buffer"] = "capture buffer (frames)",
        ["param.buffer.frames"] = "frames",
        ["param.size"] = "size",
        ["param.crop43"] = "crop 4:3",
        ["param.scalealgo"] = "scale algorithm",
        ["param.deinterlace"] = "deinterlace filter",
        ["param.deintmode"] = "deinterlace mode",
        ["param.parity"] = "field parity",
        ["param.markedonly"] = "deinterlace marked only",
        ["param.audiokbps"] = "audio bitrate",
        ["param.audiochannels"] = "audio channels",
        ["param.audiorate"] = "audio sample rate",
        ["param.cuda"] = "CUDA decode",
        ["param.audioencoder"] = "audio encoder",
        ["param.x264preset"] = "x264 preset",
        ["param.x264tune"] = "x264 tune",
        ["param.x264profile"] = "x264 profile",
        ["param.x264level"] = "x264 level",
        ["param.start"] = "segment start (HH:MM:SS)",
        ["param.end"] = "segment end (HH:MM:SS)",
        ["param.availdeint"] = "available deinterlacers",
        ["param.back"] = "Back",
        ["param.select"] = "Select parameter: ",
        ["param.invalid"] = "Invalid selection or x264-only option is not active.",
        ["param.badvalue"] = "Invalid value: {0}",
        ["param.badconfig"] = "Invalid configuration: {0}",
        ["param.fpsadjusted"] = "Adjusted to supported FPS: {0}",
        ["param.sizefmt"] = "use WIDTHxHEIGHT",
        ["param.scalebad"] = "scale algorithm '{0}' is not exposed by this FFmpeg build",
        ["param.deintbad"] = "choose one of: {0}",
        ["param.modebad"] = "choose send_frame or send_field",
        ["param.paritybad"] = "choose auto, tff, or bff",
        ["param.timerangebad"] = "end time must be later than start time",
        ["param.prompt.limit"] = "limit [{0}]: ",
        ["param.prompt.recover"] = "recover [{0}]: ",
        ["param.prompt.window"] = "window [{0}]: ",
        ["param.prompt.fps"] = "base FPS [{0}]: ",
        ["param.prompt.levels"] = "levels [{0}]: ",
        ["param.prompt.quality"] = "{0} [{1}]: ",
        ["param.prompt.threads"] = "threads [{0}]: ",
        ["param.prompt.buffer"] = "buffer frames [{0}]: ",
        ["param.prompt.size"] = "size [{0}x{1}]: ",
        ["param.prompt.scalealgo"] = "scale algorithm [{0}] ({1}): ",
        ["param.prompt.deinterlace"] = "deinterlace [{0}] [{1}]: ",
        ["param.prompt.deintmode"] = "deinterlace mode [send_frame/send_field] [{0}]: ",
        ["param.prompt.parity"] = "parity [auto/tff/bff] [{0}]: ",
        ["param.prompt.audiokbps"] = "audio kbps [{0}]: ",
        ["param.prompt.audiochannels"] = "audio channels [{0}]: ",
        ["param.prompt.audiorate"] = "audio sample rate [{0}]: ",
        ["param.prompt.audioencoder"] = "audio encoder [{0}]: ",
        ["param.prompt.preset"] = "preset [{0}]: ",
        ["param.prompt.tune"] = "tune [{0}]: ",
        ["param.prompt.profile"] = "profile [{0}]: ",
        ["param.prompt.level"] = "level [{0}]: ",
        ["param.prompt.start"] = "start time [{0}] (HH:MM:SS[.mmm]): ",
        ["param.prompt.end"] = "end time [{0}] (HH:MM:SS[.mmm], or 'end' for source end): ",

        ["start.confirm"] = "\nStart encoding? [Y/n]: ",

        ["note.fpsunsupported"] = "NOTE: {0} does not support {1} fps; using {2} fps.",
        ["note.specialsizes"] = "NOTE: {0} requires a special frame size; using {1}x{2}.",
        ["note.sizeadjusted"] = "Adjusted {0} to supported output size: {1}x{2}",
        ["note.transportadjusted"] = "Adjusted output size for {0}: {1}x{2}",
        ["note.splitunavailable"] = "\nNOTE: split audio pipeline unavailable ({0}); "
            + "falling back to single-process encoding.",
        ["note.splitmux"] = "NOTE: split audio pipeline unavailable ({0}); using single-process encoding.",
        ["note.shardingskipped"] = "Sharding skipped: {0}",

        ["run.encodercommand"] = "Encoder command",
        ["run.sourcepix"] = "Source pix_fmt",
        ["run.pipepix"] = "Pipe pix_fmt",
        ["run.outputsize"] = "Output size",
        ["run.timerange"] = "Time range",
        ["run.filters"] = "Filters",

        ["report.heading"] = "=== Current configuration ===",
        ["report.ffmpeg"] = "FFmpeg",
        ["report.source"] = "Source",
        ["report.legacy"] = "Legacy found",
        ["report.input"] = "Input",
        ["report.timerange"] = "Time range",
        ["report.output"] = "Output",
        ["report.outputmode"] = "Output mode",
        ["report.videocodec"] = "Video codec",
        ["report.container"] = "Container",
        ["report.pixfmts"] = "Pixel formats",
        ["report.fpssupport"] = "FPS support",
        ["report.quality"] = "Quality",
        ["report.fps"] = "FPS",
        ["report.limit"] = "Limit",
        ["report.recover"] = "Recover",
        ["report.window"] = "Window",
        ["report.resolution"] = "Resolution",
        ["report.crop43"] = "Crop 4:3",
        ["report.scalealgo"] = "Scale algorithm",
        ["report.deinterlace"] = "Deinterlace",
        ["report.deintmode"] = "Deint mode",
        ["report.audio"] = "Audio",
        ["report.threads"] = "Threads",
        ["report.buffer"] = "Buffer",
        ["report.cuda"] = "CUDA decode",
        ["report.deintavail"] = "Deint available",
        ["report.scaleavail"] = "Scale available",
        ["report.impairments"] = "Impairments",
        ["report.sourcebtbn"] = "BtbN GPL preferred build",
        ["report.sourcepath"] = "PATH/custom",
        ["report.kbps"] = "{0} kbps video",
        ["report.seconds"] = "{0} s",
        ["report.deintparams"] = "{0}, parity={1}, marked_only={2}",
        ["report.audioline"] = "{0} / {1}k / {2}ch / {3}",
        ["report.roqnote"] = "RoQ note",
        ["report.roqaudio"] = "RoQ audio",
        ["report.roqaudio.value"] = "requires 22050 Hz, mono/stereo; 22050 Hz is enforced when set to auto",

        ["result.done"] = "Done: {0}",
        ["result.timerange"] = "Time range",
        ["result.duration"] = "Duration",
        ["result.sourcepix"] = "Source pix fmt",
        ["result.transportpix"] = "Transport pix fmt",
        ["result.resolution"] = "Output resolution",
        ["result.videoavg"] = "Video average",
        ["result.totalavg"] = "Total average",
        ["result.fresh"] = "Fresh frames",
        ["result.repeated"] = "Repeated slots",
        ["result.kbps"] = "{0} kbps",

        ["filters.heading"] = "=== Relevant FFmpeg video filters ===",
        ["filters.scalers"] = "Scaling algorithms exposed by scale/libswscale:",

        ["lang.prompt"] = "Language (en/zh) [{0}]: ",
        ["lang.switched"] = "Language set to {0}.",

        ["smb.share"] = "SMB share",
        ["smb.smb3"] = "SMB3 in use",
        ["smb.smb2"] = "SMB2 in use (server did not offer SMB3)",
        ["smb.smb1"] = "SMB1 in use - obsolete and insecure; enable SMB2/3 on the server",
        ["smb.unknown"] = "UNC input; read-resilience options applied (dialect {0})",
        ["smb.notconnected"] = "UNC input; read-resilience options applied (dialect unknown until the share is connected)",
    };

    private static readonly Dictionary<string, string> Zh = new()
    {
        ["app.title"] = "Live Archive Encoder 交互界面 v7.3 (.NET 10)",
        ["app.impairments"] = "损坏处理",
        ["app.impairments.value"] = "由 frame_damage.py 单独负责",

        ["banner.executable"] = "可执行文件",
        ["banner.btbn"] = "BtbN 默认",
        ["banner.gpl"] = "GPL 构建",
        ["banner.legacy"] = "复古编码器",
        ["banner.deinterlacers"] = "反交错滤镜",
        ["banner.scalers"] = "缩放算法",
        ["banner.legacycount"] = "从 ffmpeg -encoders 检测到 {0} 个",

        ["value.yes"] = "是",
        ["value.no"] = "否",
        ["value.none"] = "无",
        ["value.auto"] = "自动",
        ["value.custom"] = "自定义",
        ["value.fallback"] = "回退/自定义",
        ["value.notdetected"] = "未检测到",
        ["value.notselected"] = "(未选择)",
        ["value.notdeclared"] = "(未声明)",

        ["menu.heading"] = "=== 主菜单 ===",
        ["menu.input"] = "输入文件",
        ["menu.output"] = "输出文件",
        ["menu.encoder"] = "视频编码器",
        ["menu.parameters"] = "参数设置",
        ["menu.rescan"] = "重新扫描编码器",
        ["menu.filters"] = "查看 FFmpeg 滤镜",
        ["menu.showconfig"] = "查看当前配置",
        ["menu.start"] = "开始编码",
        ["menu.turbo"] = "Turbo 模式",
        ["menu.language"] = "界面语言",
        ["menu.parallel"] = "并行分片数",
        ["menu.parallel.off"] = "关闭（单次编码）",
        ["parallel.prompt"] = "并行分片数（1=关闭）[{0}]: ",
        ["parallel.set"] = "并行分片数已设为 {0}。只有可安全拼接的容器（avi、mp4、flv、asf、mpeg）"
            + "会真正分片，其余容器自动回退为单次编码。",
        ["parallel.off"] = "并行分片已关闭。",
        ["parallel.clamped"] = "已限制到 1..{0} 范围内。",
        ["menu.exit"] = "退出",
        ["menu.select"] = "请选择: ",
        ["menu.invalid"] = "无效的选择。",
        ["menu.notselected"] = "(未选择)",
        ["menu.auto"] = "(自动)",

        ["turbo.on"] = "开启（无实时节流）",
        ["turbo.off"] = "关闭（实时）",
        ["turbo.enabled"] = "Turbo 已开启：1/fps 实时节流被关闭。速度大幅提升，但 fresh/repeat 比例和"
            + "自适应码率阶梯都会改变，产物不再等同于真实的直播录制。",
        ["turbo.disabled"] = "Turbo 已关闭：恢复实时节流。",

        ["config.settingsfile"] = "配置文件",
        ["config.restored"] = "已恢复上次配置",
        ["config.savefailed"] = "警告：无法写入配置到 {0}",

        ["input.prompt"] = "\n输入文件或目录: ",
        ["input.notfound"] = "路径不存在。",
        ["input.novideo"] = "该目录下没有找到常见视频文件。",
        ["input.selectnumber"] = "请选择序号: ",
        ["input.invalid"] = "无效的选择。",
        ["input.select"] = "请先选择有效的输入视频。",

        ["encoder.selectfirst"] = "请先选择视频编码器。",
        ["encoder.heading"] = "=== 当前 FFmpeg 检测到的复古/经典编码器 ===",
        ["encoder.pixfmts"] = "像素格式",
        ["encoder.prompt"] = "选择编码器序号（r=刷新）: ",
        ["encoder.outofrange"] = "超出范围",
        ["encoder.invalid"] = "无效选择：{0}",
        ["encoder.fpsadjusted"] = "{0}：请求的 {1} fps 不受支持，改用 {2} fps。",
        ["encoder.sizesdjusted"] = "{0}：输出尺寸已调整为 {1}x{2}。",
        ["encoder.amvnote"] = "AMV：音频固定为 22050 Hz IMA-ADPCM；视频 FPS 仅限 10/14/15/18/21/25/30。",
        ["encoder.roqnote"] = "RoQ：roq_dpcm 要求 22050 Hz、单声道或立体声，已自动调整。",
        ["encoder.roqnoaudio"] = "RoQ：roq_dpcm 不可用，输出将只有视频。",
        ["encoder.videoonly"] = "{0}：容器 {1} 将只包含视频。",
        ["encoder.rescanok"] = "重新扫描完成：检测到 {0} 个复古视频编码器。",

        ["output.prompt"] = "输出文件路径（留空=自动）: ",
        ["output.sameasinput"] = "输出文件不能与输入文件相同。",

        ["error.prefix"] = "错误：",

        ["param.heading"] = "=== 参数设置 ===",
        ["param.limit"] = "limit（视频 kbps）",
        ["param.recover"] = "recover（视频 kbps）",
        ["param.window"] = "window（秒）",
        ["param.basefps"] = "基础 FPS",
        ["param.levels"] = "动态 FPS 档位",
        ["param.threads"] = "线程数",
        ["param.buffer"] = "采集缓冲（帧）",
        ["param.buffer.frames"] = "帧",
        ["param.size"] = "输出分辨率",
        ["param.crop43"] = "裁剪 4:3",
        ["param.scalealgo"] = "缩放算法",
        ["param.deinterlace"] = "反交错滤镜",
        ["param.deintmode"] = "反交错模式",
        ["param.parity"] = "场序",
        ["param.markedonly"] = "仅反交错标记帧",
        ["param.audiokbps"] = "音频码率",
        ["param.audiochannels"] = "音频声道",
        ["param.audiorate"] = "音频采样率",
        ["param.cuda"] = "CUDA 解码",
        ["param.audioencoder"] = "音频编码器",
        ["param.x264preset"] = "x264 preset",
        ["param.x264tune"] = "x264 tune",
        ["param.x264profile"] = "x264 profile",
        ["param.x264level"] = "x264 level",
        ["param.start"] = "片段起点（HH:MM:SS）",
        ["param.end"] = "片段终点（HH:MM:SS）",
        ["param.availdeint"] = "可用反交错滤镜",
        ["param.back"] = "返回",
        ["param.select"] = "请选择参数编号: ",
        ["param.invalid"] = "无效的选择，或该选项仅在 x264 下可用。",
        ["param.badvalue"] = "无效的值：{0}",
        ["param.badconfig"] = "配置无效：{0}",
        ["param.fpsadjusted"] = "已调整为受支持的 FPS：{0}",
        ["param.sizefmt"] = "请使用 宽x高 的格式",
        ["param.scalebad"] = "当前 FFmpeg 构建未提供缩放算法 '{0}'",
        ["param.deintbad"] = "请从以下选项中选择：{0}",
        ["param.modebad"] = "请选择 send_frame 或 send_field",
        ["param.paritybad"] = "请选择 auto、tff 或 bff",
        ["param.timerangebad"] = "结束时间必须晚于开始时间",
        ["param.prompt.limit"] = "limit [{0}]: ",
        ["param.prompt.recover"] = "recover [{0}]: ",
        ["param.prompt.window"] = "window [{0}]: ",
        ["param.prompt.fps"] = "基础 FPS [{0}]: ",
        ["param.prompt.levels"] = "档位 [{0}]: ",
        ["param.prompt.quality"] = "{0} [{1}]: ",
        ["param.prompt.threads"] = "线程数 [{0}]: ",
        ["param.prompt.buffer"] = "缓冲帧数 [{0}]: ",
        ["param.prompt.size"] = "分辨率 [{0}x{1}]: ",
        ["param.prompt.scalealgo"] = "缩放算法 [{0}]（{1}）: ",
        ["param.prompt.deinterlace"] = "反交错滤镜 [{0}] [{1}]: ",
        ["param.prompt.deintmode"] = "反交错模式 [send_frame/send_field] [{0}]: ",
        ["param.prompt.parity"] = "场序 [auto/tff/bff] [{0}]: ",
        ["param.prompt.audiokbps"] = "音频码率 [{0}]: ",
        ["param.prompt.audiochannels"] = "音频声道 [{0}]: ",
        ["param.prompt.audiorate"] = "音频采样率 [{0}]: ",
        ["param.prompt.audioencoder"] = "音频编码器 [{0}]: ",
        ["param.prompt.preset"] = "preset [{0}]: ",
        ["param.prompt.tune"] = "tune [{0}]: ",
        ["param.prompt.profile"] = "profile [{0}]: ",
        ["param.prompt.level"] = "level [{0}]: ",
        ["param.prompt.start"] = "起点时间 [{0}]（HH:MM:SS[.mmm]）: ",
        ["param.prompt.end"] = "终点时间 [{0}]（HH:MM:SS[.mmm]，或 end 表示到片尾）: ",

        ["start.confirm"] = "\n开始编码？[Y/n]: ",

        ["report.heading"] = "=== 当前配置 ===",
        ["report.ffmpeg"] = "FFmpeg",
        ["report.source"] = "来源",
        ["report.legacy"] = "复古编码器",
        ["report.input"] = "输入",
        ["report.timerange"] = "时间范围",
        ["report.output"] = "输出",
        ["report.outputmode"] = "输出模式",
        ["report.videocodec"] = "视频编码",
        ["report.container"] = "容器",
        ["report.pixfmts"] = "像素格式",
        ["report.fpssupport"] = "FPS 支持",
        ["report.quality"] = "质量",
        ["report.fps"] = "FPS",
        ["report.limit"] = "码率上限",
        ["report.recover"] = "恢复码率",
        ["report.window"] = "统计窗口",
        ["report.resolution"] = "分辨率",
        ["report.crop43"] = "裁剪 4:3",
        ["report.scalealgo"] = "缩放算法",
        ["report.deinterlace"] = "反交错",
        ["report.deintmode"] = "反交错模式",
        ["report.audio"] = "音频",
        ["report.threads"] = "线程数",
        ["report.buffer"] = "缓冲",
        ["report.cuda"] = "CUDA 解码",
        ["report.deintavail"] = "可用反交错",
        ["report.scaleavail"] = "可用缩放",
        ["report.impairments"] = "损坏处理",
        ["report.sourcebtbn"] = "BtbN GPL 首选构建",
        ["report.sourcepath"] = "PATH/自定义",
        ["report.kbps"] = "{0} kbps 视频",
        ["report.seconds"] = "{0} 秒",
        ["report.deintparams"] = "{0}，场序={1}，仅标记帧={2}",
        ["report.audioline"] = "{0} / {1}k / {2}ch / {3}",
        ["report.roqnote"] = "RoQ 提示",
        ["report.roqaudio"] = "RoQ 音频",
        ["report.roqaudio.value"] = "要求 22050 Hz、单声道或立体声；设为自动时会强制 22050 Hz",

        ["result.done"] = "完成：{0}",
        ["result.timerange"] = "时间范围",
        ["result.duration"] = "时长",
        ["result.sourcepix"] = "源像素格式",
        ["result.transportpix"] = "管道像素格式",
        ["result.resolution"] = "输出分辨率",
        ["result.videoavg"] = "视频平均码率",
        ["result.totalavg"] = "总平均码率",
        ["result.fresh"] = "新帧数",
        ["result.repeated"] = "重复帧槽数",
        ["result.kbps"] = "{0} kbps",

        ["filters.heading"] = "=== 相关 FFmpeg 视频滤镜 ===",
        ["filters.scalers"] = "scale/libswscale 提供的缩放算法:",

        ["lang.prompt"] = "界面语言（en/zh）[{0}]: ",
        ["lang.switched"] = "界面语言已切换为 {0}。",

        ["smb.share"] = "SMB 共享",
        ["smb.smb3"] = "正在使用 SMB3",
        ["smb.smb2"] = "正在使用 SMB2（服务端未提供 SMB3）",
        ["smb.smb1"] = "正在使用 SMB1 —— 已过时且不安全的协议，请在服务端启用 SMB2/3",
        ["smb.unknown"] = "UNC 输入；已启用读取重试参数（协议版本 {0}）",
        ["smb.notconnected"] = "UNC 输入；已启用读取重试参数（共享尚未连接，协议版本未知）",
    };

    public static string Lang { get; private set; } = English;

    public static bool IsChinese => Lang == Chinese;

    /// <summary>Accepts en/eng/english and zh/cn/zh-cn/zh-hans; unknown falls back to English.</summary>
    public static string SetLanguage(string? value)
    {
        string key = (value ?? "").Trim().ToLowerInvariant().Replace('_', '-');
        Lang = key switch
        {
            "zh" or "cn" or "zh-cn" or "zh-hans" or "zh-hant" or "chinese" or "中文" => Chinese,
            _ => English,
        };
        return Lang;
    }

    public static string T(string key)
    {
        Dictionary<string, string> table = IsChinese ? Zh : En;
        return table.TryGetValue(key, out string? value) ? value : En.GetValueOrDefault(key, key);
    }

    /// <summary>Formatted lookup; {0}..{n} are substituted positionally.</summary>
    public static string T(string key, params object?[] args)
    {
        string template = T(key);
        return args.Length == 0 ? template : string.Format(template, args);
    }

    /// <summary>yes / no, localised.</summary>
    public static string YesNo(bool value) => T(value ? "value.yes" : "value.no");

    /// <summary>
    /// Console columns a string occupies. CJK (and friends) are two cells wide,
    /// so <c>string.Format("{0,-17}")</c> aligns English and Chinese differently
    /// and the colons of a label list end up ragged.
    /// </summary>
    public static int DisplayWidth(string text)
    {
        int width = 0;
        foreach (char ch in text) width += IsWide(ch) ? 2 : 1;
        return width;
    }

    /// <summary>Right-pad to a <em>display</em> width so mixed scripts line up.</summary>
    public static string Pad(string text, int width) =>
        text + new string(' ', Math.Max(0, width - DisplayWidth(text)));

    private static bool IsWide(char ch) => ch switch
    {
        >= '\u1100' and <= '\u115F' => true,   // Hangul Jamo
        >= '\u2E80' and <= '\u303E' => true,   // CJK radicals, punctuation
        >= '\u3041' and <= '\u33FF' => true,   // Hiragana, Katakana, CJK compat
        >= '\u3400' and <= '\u4DBF' => true,   // CJK ext A
        >= '\u4E00' and <= '\u9FFF' => true,   // CJK unified
        >= '\uA000' and <= '\uA4CF' => true,   // Yi
        >= '\uAC00' and <= '\uD7A3' => true,   // Hangul syllables
        >= '\uF900' and <= '\uFAFF' => true,   // CJK compat ideographs
        >= '\uFE30' and <= '\uFE6F' => true,   // CJK compat forms
        >= '\uFF00' and <= '\uFF60' => true,   // fullwidth forms
        >= '\uFFE0' and <= '\uFFE6' => true,
        _ => false,
    };
}
