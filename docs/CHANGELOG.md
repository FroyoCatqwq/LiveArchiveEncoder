# 变更记录（CHANGELOG）

[English summary](CHANGELOG.en.md) · **中文全文**

本项目从 Python 单文件脚本（`reference/live_archive_encoder.py`）迁到 C#（.NET 10 /
.NET 11）的完整变更记录。**新的在上，旧的在下面。**

本文件只记"改了什么、为什么"，以及会影响老用户的地方。命令用法、参数表、排错都
在 [`usage.md`](usage.md)；每个正式发布还有一份独立说明，放在
`publish/release/notes-*.md`（GitHub / Codeberg 的 release 正文就是它）。

## 关于史料与写法

* **v7.0 起**：每一条都能在本仓库的提交或 release 说明里对上号。
* **Python 时代（6.2.2 → 6.3.3）**：建立本仓库时只保留了 **v6.3.3 的最终源码**
  （`reference/`），更早的 6.2.x 源码与发布记录没有留档。那一节因此只写能证实的
  部分，并明确标出缺口 —— 猜出来的版本历史比没有更糟。
* 标 **⚠ 行为变化** 的条目会改变既有输出的字节或形状；只影响 CLIR/CUI 体验、
  不影响产物的条目不标。

> **v7.9.8 以前的变更记录已拆到 [`CHANGELOG-archive.md`](CHANGELOG-archive.md)**；本文件只保留 v7.9.8 及以后。

---

## v7.9.9.9（2026-11-13）

把光盘介质并入「时代预设」，并修掉 v7.9.9.8 镜像实现里的三个真实缺陷。

### 1. 介质预设并入 `--era`

`dvd` / `vcd` / `svcd` / `cd`（CD-i）现在是**时代预设表里的四行**，id 为
`disc/dvd` … `disc/cd`，并接受简写 `dvd` / `vcd` / `svcd` / `cd` / `cd-i` / `cdi`：

```powershell
LiveArchiveEncoder.exe in.mp4 out.mpg --era dvd          # 刻成 DVD-Video
LiveArchiveEncoder.exe in.mp4 out.mpg --era vcd          # 刻成 Video CD
LiveArchiveEncoder.exe in.mp4 out.mpg --era disc/svcd    # 完整 id 也认
```

* 四行的数字不再重复：它们由 `DiscProfiles` 派生，`DiscProfile` 仍是唯一来源。
* 行上多了新字段 `EraPreset.Disc`（介质 id），CLI 的介质逻辑改为从选中的 era 行取，
  原来三处重复的 `args.Disc ?? stored.Disc` 判断合并成一处。
* `--disc dvd` 保留为**简写**（内部解析成 `--era disc/dvd`），v7.9.9.8 写的脚本不受影响；
  `--era` 与 `--disc` 同时给时 `--disc` 优先。
* CUI 的「时代预设」（菜单 14）自动多出 `disc` 组：先列 dvd/vcd/svcd/cd，选中即应用
  尺寸、帧率、码率、音频，并把 `-maxrate/-bufsize/-g` 追加到附加编码参数。
  参数页原来的 `39. 介质目标` 与 `Config.Disc` 字段随之删除——入口只有一个。

### 2. 镜像实现的三个缺陷（v7.9.9.8 引入）

* **7-Zip 退出码被丢弃**：解包时只检查「有没有解出 `.DAT`」，7-Zip 返回 1（部分成功）
  或 2（失败）时若恰好解出一个文件，就会拿截断的载荷去编码。现在 `>= 2` 直接判失败，
  并把 7z 的尾部输出带进错误里。
* **`Path.GetFullPath` 在 try 之外**：DVD 分支的超长路径 / 畸形路径会抛异常穿透
  `Resolve` 的「要么镜像要么原因」契约，CLI 直接崩栈而不是打一行 `ERROR:`。现在整个
  `ResolveCore` 被 try 包住。
* **失败原因被压成一句话**：`7z l` 非 0 时（杀毒拦截、镜像被独占、网络共享超时）
  原来只报「不是可读的光盘镜像」，`listing` 被整条丢弃。现在附 `[7z <code>] <尾部输出>`。
* 顺带：7-Zip 子进程改为 250 ms 轮询 + `Interrupt.CurrentToken`，Ctrl+C 可以终止一次
  挂住的镜像扫描（原来只能等满 180 秒）。

### 3. 测试

* `EraFpsLadderTests` / `PostProcessingTests` 的四条通用不变量（阶梯至少两级、
  像素与码率单调递增、id 形状 `site/year`）现在明确只作用于站点行；介质行用
  `disc/<id>` 形状并自带单级阶梯。
* `test_disc.py` 改为验证新入口：`--era vcd`、`--era disc/dvd`，并保留 `--disc cd` /
  `--disc dvd --fps 29.97` 两条简写路径。
* 单测 **762 全过**，中英双语键与清单双向校验通过。

---

## v7.9.9.8（2026-11-12）

新增光盘镜像输入与 DVD / VCD / CD-i 介质约束。

### 1. ISO 输入（DVD-Video 与 Video CD）

`*.iso` / `*.img` 现在可以直接当输入。识别是**结构性**的，不看扩展名也不靠「ffprobe
打得开就算」：

| 布局 | 判据 | 处理 |
| --- | --- | --- |
| **DVD-Video** | 含 `VIDEO_TS/` 与 `*.IFO` | 直接把 ISO 交给 FFmpeg：它的 `dvdvideo` demuxer 读 MicroUDF、按 IFO 组装 `VTS_zz_1..9.VOB`。实测 `ffprobe -f dvdvideo` 正常，无需解包 |
| **Video CD** | 含 `MPEGAV/` 与 `*.DAT` | FFmpeg 没有 VCD demuxer，用 7-Zip 把 `MPEGAV/*.DAT` 解到临时目录再编码，退出时清理 |
| 其它 | 两者都不含 | `exit 2` 明确拒绝：`<名字> 不是 DVD-Video 或 VCD 镜像` |

判定用 7-Zip 的目录列表（一份 `7z l -ba`），因为它同时支持 UDF 与 ISO 9660；损坏或
不可读的镜像报「不是可读的光盘镜像」。输出文件名仍按**原 ISO 名**推导，不会因为 VCD
解包成 `AVSEQ01.DAT` 而变成 `AVSEQ01_live.mpg`。

`.iso` 与 `.dat` 也加进了 CUI 的目录列举扩展名，所以菜单里能直接看到。

### 2. `--disc dvd|vcd|svcd|cd`：介质约束

新增 `--disc ID`（CUI 参数页 `39.`）。它和 `--era` 是两个正交的轴：`--era` 管
「哪个站、哪一年」，`--disc` 管「刻到哪种盘上」。约束取自公开规范：

| 目标 | 编码器/容器 | 分辨率（PAL / NTSC） | 视频 | 音频 | GOP |
| --- | --- | --- | --- | --- | --- |
| `dvd` | mpeg2video / mpeg | 720×576 @25 / 720×480 @30 | 6000 kbps，maxrate 8000，bufsize 1835k | ac3 384k 48 kHz 2ch | 15 |
| `vcd` | mpeg1video / mpeg | 352×288 @25 / 352×240 @30 | 1150 kbps CBR，bufsize 327k | mp2 224k 44.1 kHz 2ch | 15 |
| `svcd` | mpeg2video / mpeg | 480×576 @25 / 480×480 @30 | 2520 kbps，bufsize 917k | mp2 224k 44.1 kHz 2ch | 15 |
| `cd`（CD-i） | mpeg1video / mpeg | 同 VCD | 同 VCD | 同 VCD | 15 |

* DVD-Video 的数字来自用户指定的 Wikipedia 条目：MPEG-2 视频上限 9.8 Mbit/s、
PAL 720×576 / NTSC 720×480、AC-3 48 kHz 且 ≤448 kbit/s、4:2:0 8 bit。
* VCD 的数字来自同站 White Book 条目：MPEG-1、352×288/352×240、1150 kbit/s CBR、
MP2 44.1 kHz 224 kbit/s。
* **CD-i（Green Book）与 VCD 的 MPEG-1 包络相同** —— 该条目只说两者因「规范的细微
  差异」互不兼容，编码约束未给差异，所以这里不编造不同参数，只让 CD-i 可以被点名。
* **GOP 长度（15 帧 ≈ 0.6 s）不在所引页面里**，是业界通行的实际限制；代码注释与文档
  都标注了这一点。
* 制式由 `--fps` 判定（29.97/30/23.976 = NTSC，25 = PAL）；不给 `--fps` 时默认 PAL。
  **NTSC 源（如 720×480 的 DVD ISO）需要显式 `--fps 29.97`**。
* 介质是硬约束：`--disc` 会覆盖 `--encoder` / `--container` / 尺寸 / 帧率 / 码率 /
  音频，并在覆盖时打印提示。`-maxrate/-bufsize/-g` 通过 `ExtraEncoderArgs` 追加，
  因此与 v7.9.9.7 的 VBV 成对补全共用同一条路径。
* 持久化：CUI 里选过之后写进 `config.json` 的 `Disc`，命令行 `--disc` 只作用于本次。

### 3. 实测（用户提供的素材）

| 素材 | 结果 |
| --- | --- |
| `SJ01_SCN.iso`（1501 s，DVD-Video PAL） | 识别为 DVD-Video 镜像；`--disc dvd` → 720×576@25、mpeg2video、ac3 48 kHz 384 kbps，10 s 产物时长 10.005 s |
| `D2_SCN.iso`（689 s，DVD-Video PAL） | 同上（probe 720×576@25 + ac3） |
| `Gamehalo 271.iso`（NTSC，含两个 VTS） | `--fps 29.97 --disc dvd` → 720×480@30 |
| `西部大案纪实（1）.dat`（VCD 2.0 PAL 裸流） | `--disc vcd` → 352×288@25、1150 kbps、mp2 44.1 kHz 224 kbps，产物 9.99 s |
| 伪造的 `.iso` | `exit 2`「不是可读的光盘镜像」 |

**已知限制**：一个 ISO 里有多个 title（`Gamehalo 271.iso` 有两个 VTS）时，FFmpeg 的
dvdvideo demuxer 默认读**第一个** title（该盘约 21 s）；`--start-time/--end-time` 只能
在所选 title 内裁切，暂不支持跨 title 或选择 title。需要别的 title 时先用外部工具
提取对应 VOB。

### 4. 测试

* 新增 `DiscProfileTests`（9 项：四档参数与规范逐条对照、别名、VBV/GOP 成对、
  PAL/NTSC 尺寸、镜像判据、不可读镜像、中英键）——单测 **762 全过**。
* 新增 `tests/e2e/test_disc.py`（5 项：VCD 包络、DVD 包络、CD-i + NTSC 切换、
  未知目标 exit 2、非视频 ISO 拒绝）。

---

## v7.9.9.7（2026-11-12）

修用户实跑中报出的 `flv` 编码器 `EINVAL`，补齐二次压缩的 `-b:v` 菜单，并做第三轮全项目审计修复。

### 1. 报告中的 bug：`encoder exited before final frame (code -22)`

用户的配置是 `--encoder flv` + 时代预设 `tudou/2006` + 附加编码参数
`-maxrate 300k -minrate 128k`，46 分钟的素材编码在第 3 个槽失败：

```
[enc:flv] Error while opening encoder - maybe incorrect parameters ...
code -22
```

**根因**（手工用同一个 FFmpeg 逐个消元后确证）：

```
-q:v 11 -maxrate 300k -minrate 128k  =>  -22
  Either both buffer size and max rate or neither must be specified
-q:v 11 -maxrate 300k -bufsize 600k -minrate 128k  =>  0
```

FFmpeg 的 MPEG-video 系编码器（`flv1` / `mpeg4` / `h261` …）在 `avcodec_open2`
阶段要求 `-maxrate` 与 `-bufsize` **成对出现**，缺一个直接 EINVAL。而
`libx264` / `mpeg4` / `libopenh264` 对同样的半套参数是接受的 —— 所以不能一律硬拒绝。

**为什么之前没抓到**：`--extra-encoder-args` 只做了注入防护解析就原样拼进 live 命令，
3 帧预演**完全不带这些 token**，于是预检证明的是另一条命令；FFmpeg 收到真实命令才在
第一个帧打开编码器时崩溃。用户看到的是等了很久之后一句晦涩的 `code -22`。

**修法**（四处一起改，缺一不可）：

* 新增 `FragmentEditor.NormalizeVbvPair`：缺 `-bufsize` 时按 `-maxrate × 2`（两秒缓冲，
  与 `ArgsBuilder.VideoQualityArgs` 的 CRF 分支同一规则）自动补上；缺 `-maxrate` 时补
  `-bufsize ÷ 2`。半套参数变成合法的一套，用户的原命令**不用改就能跑**。
* `BuildEncoderCommand` 与 `Preflight.Video` **调用同一个补全函数**，预检从此验证的就是
  真实命令。实测用户原参数：exit 0、产出 818 KB 的 `.flv`。
* `Preflight.Video` 新增 `extraEncoderArgs` 参数；`--extra-encoder-args` 现在的任何组合
  都会在编码前 3 帧预演里被验证。
* `ValidateCommonConfig` 对**最终生效值**（包括从 `config.json` 恢复的旧值）做注入防护
  与 `minrate <= maxrate` 校验，坏值在 exit 2 明确报出，而不是等 FFmpeg 崩。
* 主编码 FFmpeg 命令现在也记 `encoder.argv`（Info 级，默认 Error 阈值下零开销）——
  之前只有 ffprobe / 预检会记日志，出问题时恰恰缺最关键的命令行。

### 2. 二次压缩新增 `-b:v` 选项（菜单 `h.`）

* CUI 二次压缩菜单新增 `h. 视频码率（-b:v）`，与 `6. 质量（crf / q:v）` 分开：
  行 6 是编码器的质量旋钮，行 h 直接改目标码率。两者互斥——设置码率会移除
  `-crf` / `-q:v` / `-qscale:v`，改质量时 `ComposeSecondPass` 会移除 `-b:v`。
* 修 **M1**：`--recompress-video-kbps` 此前只在「编码目录生成 fragment」的分支里被读取，
  用户手写 `--recompress-args "-c:v ... -b:v 240k"` 时这个 flag **静默失效**。
  现在会改写手写片段里的 `-b:v`，并保留 catalog 生成片段的既有语义。
* 中英双语键 `rc.menu.videobitrate` 已加入 `Strings.cs`。

### 3. 审计修复（按严重性）

**严重**

* **S1 无限递归**：`--parallel N` + `--audio-mode separate/auto` 时，`RunSplitCore` 的三条
  正常返回路径（无音轨 / 时长未知 / 复制字幕 / muxer 不可分）都 `return Run(o, ...)`，
  而 `Run` 又按「Parallel>1 且非 Inline」重新进入 `RunSplit` —— 栈溢出。三处改为
  `RunSingle`，与 v7.9.9.6 的异常回退一致。
* **S2 iperf3 绕过抖动检查**：`--iperf3` 曾用单样本的 iperf3 结果**取代**读源测量，
  `jitter` 与首字节延迟被强制算成 0，任何 ≥2.5x 的吞吐都会放行分片，在不稳定链路上
  误开并行。现在 iperf3 只作参考（写 Info 日志），判定始终用真实读源测量，与
  `--iperf3` 的帮助文案和 `ParallelEncoder` 的注释重新对齐。
* **S3 分片否决文案自相矛盾**：用户日志里 `125.8x headroom` 与「余量不足」并列 ——
  真正否决的是 51% 抖动（>30% 上限），但所有否决都套同一个 `parallel.nettooslow`。
  现在 `NetworkMeasurement` 记录 `RejectReason`（吞吐 / 抖动 / 延迟），中英各三句分别说明
  哪个门槛没过。
* **S5 误删并发运行的 `.part`**：旧规则用「创建时间早于本进程类型初始化」判断残留，
  并发进程 B 会把 A 正在写的 part 当旧文件删掉（POSIX/UNC 上打开句柄不阻止删除）。
  现在解析 `PartPath` 里的 `pid+guid` tag：只有**该 pid 已退出**的 part 才清理；
  无法解析的旧格式（短 tag / 非 hex）仍按残留处理，pid 复用或查不到则保守保留。

**中等**

* **M2** `FragmentEditor.Kbps` 只认 `k` 后缀且拒绝 0，导致 catalog 自己生成的
  `-b:v 0`（VP9/AV1 CRF 模式）被 `ValidateVbv` 误判成「没有目标码率」。现支持 `M` 后缀
  与显式 0。
* **M3** `FragmentEditor.Merge` 只按 base 去重，extras 内部重复的 `-maxrate` 会让
  FFmpeg last-wins 无声覆盖显式值；末尾统一走 `FirstWins`。
* **M9** 非实时一帧一槽握手的消费侧自旋没有上界，解码器卡住但没退出时会永久挂住；
  加 30 秒超时并给出可读错误。
* **M8** `Toolchain.RunLong` 在两个管道恰好于首个 250 ms 窗口内完成时跳过循环体，
  Ctrl+C 不触发 kill；窗口结束后补一次检查。
* **L1** `CliOptions.TryValue` 只拒绝**已登记**的 `--long` 选项，未知 `--bogus` 会被当成
  前一个选项的值吞掉；现在任何 `--` 开头都拒绝（单破折号的 FFmpeg 片段仍合法）。
* **L2** `FragmentEditor.SetFlag` 对 `-an` / `-shortest` 这类无值开关会吞掉紧邻的下一个
  token；改用 `PostArgs.IsValueLess`。

### 4. 测试

* 新增 `V7997Tests.cs`（12 项：VBV 自动补全的三种形态、库编码器不被误拒、
  无值开关、`Merge` 去重、`M`/`0` 解析、未知长选项、手写片段码率、catalog 片段保持质量、
  中英键、否决原因与 headroom）。单测 **753 全过**。
* 新增 4 个 e2e：半套 VBV 的 `flv` 真实出片、不可能的 min/max 提前拒绝、
  手写片段的 `-b:v` 落到真实 FFmpeg argv（从 verbose 日志的 `child.spawn` 断言）。
* Windows 全量 e2e、Linux 双平台验证见 Release 说明。

### 5. 已知遗留（本轮未改，评估后保留）

`S4` 网络探测缓存的 `required` 在 `--net-test` 与编码器里算法不同（缓存必然 miss，
同一次运行测两遍）；`M7` 自更新 `.rollback` 用固定目录，并发更新会互踩；
`M4/M5/M6` 网络探测的 jitter 分母、`FromHttp` 无长度时取不到样本、8000/8388.608
单位偏差；`L3/L4/L5` KillAll 计数、CTS 不 dispose、AppLog 降级半构造对象。
这些都不影响本轮报告的路径，且改动面较大，留待下一轮带独立测试矩阵处理。

---

## v7.9.9.6（2026-11-09）

第二次全项目审计的收尾版本：把"CUI 菜单里能改、运行时却被丢掉"的选项逐条对齐到
编码器，并让交互外壳在坏配置、坏目录、坏输入面前存活。默认编码参数与 v6.3.3
等价性未改变。

### 1. CUI：菜单承诺必须真的发生

* **OpenH264 两个拨盘真正生效**：参数菜单 32/33 设置 `-rc_mode` /
  `-allow_skip_frames` 后，`StartEncoding` 没有把它们传给 `RealtimeOptions`，
  运行用的始终是默认值。
* **手动 q:v 不再被标定覆盖**：`QualityExplicit` 进入 CUI；菜单 6 输入的值标为
  显式，应用预设、切换编码器、加载配置时复位。此前 intra-only 标定会把用户
  显式值当成默认值改写。
* **0 = 保持**：时代预设、容器、分辨率三个子菜单的提示都写了 0，但代码只接受
  空行；输入 0 分别得到 "unknown era preset"、"'0' is not a container"、
  "格式错误"。现在三者都有 `0. 返回` 行并接受 0。
* **编码器列表不再显示 "?"**：懒探测的像素格式列改为"未探测"；`r` 的提示从
  "刷新" 改为 "返回"（刷新在主菜单 5）。
* **坏目录不再结束外壳**：输入目录不可读（ACL、断开的共享、系统目录）时捕获
  IO/权限异常；目录超过 200 个文件时明确说明只显示前 200 个。
* **坏 config.json 不被默认值覆盖**：CUI 改用 `TryLoad`；文件存在但无法解析时
  拒绝任何写回（菜单编辑、退出、EOF），并在启动时警告。此前第一次菜单编辑就会
  用全默认对象覆盖用户的真实设置。
* **菜单处理器兜底**：主循环的 switch 包一层 `try/catch`（OOM 除外），一个坏
  选择只打印错误并回到菜单，不再让整个进程退出。
* 删除自定义预设时清掉指向它的当前标签；`RecompressFile` 的同文件比较按平台
  区分大小写，Ctrl+C 后静默返回菜单。

### 2. CLI：选项不能再被静默忽略

* **`--subtitle-mode` / `--subtitle-codec` / `--subtitle-font` /
  `--subtitle-font-dir` 单独出现时生效**：`TouchedSubtitles` 只认文件/流，导致
  `--subtitle-mode off` 被忽略、`--subtitle-font` 被丢弃。新增显式标记；只改
  一个字段时保留设置文件里的其余字幕字段，只给文件/流时才替换整块。
* **`--save-config` 的字幕快照不再二次重建**：已合并的 `config.Subtitles` 直接
  捕获；此前重新拼块会把存储的字体目录/字体族丢成 null。
* **`--recompress-only` 遵守 `--play`**：此前 `--play ffplay` 在该路径被静默忽略。
* **`--help` / `--version` 短路**：`prog --help --bogus` 打印帮助并返回 0，
  而不是报未知参数。
* **`--` 终止符**：`-- -clip.mp4 out.avi` 可以把以 `-` 开头的文件名当作输入。
* **`--parallel` + 分离音频不再静默降级**：`RunSplit` 提示分片不适用并按单遍
  执行；其 inline 回退改为直接 `RunSingle`，避免 Auto 模式下递归。
* **`--parallel` + 复制字幕流拒绝分片**：每片的字幕时间轴会被重定基，拼接后
  错位；现在回退单遍并说明原因（烧录字幕不受影响）。

### 3. 一致性与显示

* 首次运行向导（`FirstRun`）与 CUI 一样拒绝覆盖无法解析的 `config.json`。
* `ReadDouble` 拒绝非有限值（NaN/Infinity）；此前每次重绘都报配置错误。
* 预发布标签提示统一为本地化文本，CLI 的 `--update-check` 与 CUI 更新菜单都会显示。
* `Report.PrintConfig` 补上实时/直接模式、OpenH264 码控/跳帧、附加编码参数；
  此前这些设置能改、能保存，却在"显示配置"里看不到。
* 路径比较（CUI 输出=输入、二次压缩输出=输入、`CorrectExtension` 避让源文件）
  改为平台相关：Linux 上 `A.avi` 与 `a.avi` 是两个文件。

### 4. 质量保障

* 新增 `V7996Tests.cs`（7 个用例）：`--help`/`--version` 短路、`--` 终止符、
  字幕单字段的显式标记与合并。单测 734 → **741**。
* 运行检查（指定 pwsh）：CUI 的 0 行/未探测列/坏目录/坏配置、分离音频并行提示、
  `--help --bogus`、`--`、以及 `--save-config` 的字幕模式覆盖。
* e2e 两阶段：并行 213 passed + 2 skipped、串行 29 passed（共 242 passed /
  2 skipped）；双 TFM（net10.0 / net11.0）各 741 passed、0 警告；
  `tools/check-slnx.py` 清单与双语门禁通过。

## v7.9.9.5（2026-11-09）

一次全项目审计与收尾版本：把 v7 的进程生命周期、取消、并发与"三态设置"漏洞
系统性清掉，固定 v8 重写的行为基线。**v7 自此进入维护模式**（bugfix / hotfix /
小功能），v8 是用 Rust 彻底重写的版本。

审计覆盖 83 个产品源文件（约 29.5k 行）、52 个单测文件、23 个 e2e 文件，外加
CLI 与 CUI 的实际运行检查。以下每条都能在源码或测试里对上号。

### 1. 取消与进程生命周期

| 修复 | 原来的后果 |
| --- | --- |
| `ProcessRunner.Run` 把进程级 `Interrupt.CurrentToken` 链接进每一次短命调用 | 只有长驻编码进程响应 Ctrl+C；ffprobe / 预检 / 下载 / 探测器仍跑满 30–300 s 超时，临时目录与进程残留 |
| 子进程退出后对 stdout/stderr 读取加 5 s 上界，并观察被放弃的 Task | 孙进程继承管道句柄时 `ReadToEndAsync` 永不见 EOF，整个进程挂死；Ctrl+C 无效 |
| `Program.Main` 统一 `OperationCanceledException` → 130、其它未捕获异常 → `ERROR:` + 1 | 早期路径的取消会变成未处理异常 + 堆栈，退出码脱离约定 |
| `Interrupt.Request` 捕获回调抛出的 `AggregateException` | 一个异常回调在 `e.Cancel=true` 之后终止进程且无法接管 |
| `RealtimeEncoder.Execute` 在启动两个 FFmpeg 之后、进入主 `try` 之前的初始化失败也会杀干净子进程 | 两个孤儿 FFmpeg 持有网络源连接与 `.part` 文件 |
| `WriteFrame` 超时后观察底层写任务 | 超时路径留下未观察的任务异常，帧缓冲所有权不明 |
| `Toolchain.DownloadHttp` 加 60 s 响应头超时并使 GetAsync / 响应体可取消 | "连上了但永不返回响应头"的 CDN 永久挂起；Ctrl+C 无效 |
| `Toolchain.RunLong` 取消后等待管道读取任务、给 `WaitForExit` 加界 | 被释放的流仍被三个后台 Task 读取；子进程关了管道却活着时永久阻塞 |
| `RunSplitCore` 最多等 20 s 让音频任务释放 staging 目录再删除 | Windows 上删不掉目录，中间文件与 FFmpeg 残留 |

### 2. 并发与资源

* `BuildProfileStore` 用一个可重入门闩保护能力档案的读写与序列化，`--parallel N`
  下不再并发写 `Dictionary` 或互踩缓存文件；缓存临时名带随机段。
* `ConfigStore.Save` 同样加唯一临时名与锁；`AppLog.MirrorWriter` 的
  `StringBuilder` 加锁，分片进度与 stderr 日志不再写出乱码或抛
  `ArgumentOutOfRangeException`。
* 工具链 staging 目录每次安装唯一；`ToolchainPath` 写 `.profile` / `.bashrc` /
  `.zshrc` 改成"临时文件 + 原子替换"，保留原 CRLF/LF，并对 POSIX 路径做 shell
  转义；7-Zip 安装包在成功后删除。
* `ParallelEncoder` 把 staging 目录创建移入 `try`（"输出被占用"不再泄漏空目录）、
  在编码器层 clamp 分片数到 `MaxParallelShards`、join/remux 超时不再整数溢出；
  `ShardPlanner` 对超长范围钳到 `int.MaxValue` 槽。
* `HighResolutionTimer.WaitUntil`、`PixelFormatCatalog.FrameBytes`、
  `FrameBufferPool` 构造参数补边界检查，超大几何给出可读的参数错误而不是在帧池
  里变成 `OverflowException` 或负长度。
* `SubtitlePlan` 改为 `using` 声明覆盖整个 `RunSingle`，预检之后的任意异常路径都
  不再泄漏 `%TEMP%\lae_subs_*`。

### 3. CLI / CUI 的三态与一致性

* **`--no-realtime` 真正进入 `Config`**：CUI 里持久化的"直接编码"开关此前被 CLI
  忽略；CLI 的 per-run 开关也不会改写 `config.json`（`--save-config` 仍把它写进
  用户明确要求的文件，回放保持等价）。
* **`H264Impl` 保持三态**：`Cli.Run` 不再把"预设解析出的编码器"当成用户显式选择
  写回设置文件，`--emit-args` 不再凭空多出 `--h264`。
* **`CliArgs.FromUserConfig` 输出 `--no-gh-proxy`**：`GhProxy=""`（明确关闭）过去
  在等价命令行里消失，回放时又去探测并自动选择前端。
* `--gh-proxy` 的 bare 形式不再吞掉位置参数（`--gh-proxy in.rm out.rm` 曾把
  `in.rm` 写进配置当前缀），并且不可解析的值在解析阶段就拒绝。
* `CliArgs.Num` 从 `G6` 改为 `R`：`1234567.89` 不再被打印成 `1.23457E+06`；
  `Quote` 补上 `'`、`$`、反引号、`%`、`!`、通配符与换行。
* `SubtitleSetup` 区分"按名字发现的 sidecar"与"用户显式给出的 `--subtitle`"，
  后者不再被当作一次性发现而从设置快照里抹掉。
* `L.T(key, args)` 统一 `InvariantCulture`，且模板与参数数量不符时不再抛
  `FormatException`（历史上被菜单吞成 "Invalid selection"）。
* CUI 用"设置文件是否存在"判断是否恢复，精简的合法 `config.json` 不再被忽略。
* `OptionCatalog` 新增 `OptionalValue` 种类：`--emit-args`、`--gh-proxy`、
  `--ffmpeg-download`、`--ffmpeg-env` 的 bare 形式不再被 zsh 当成"必须填值"；
  `--recompress-args` 从"文件路径"改回自由文本；PowerShell 补全取
  `CommandElements[-2]` 作为前一个词，值补全终于能命中。
* **⚠ 行为变化**：无输入文件却带编码类选项时（如 `--encoder libx264`），不再静默
  丢弃并进入菜单，而是报 `ERROR` 并返回 2，提示补 `INPUT [OUTPUT]` 或去掉选项。
  仅影响这种本来就没有意义的用法；`--lang`、`--quiet`、`--update-feed`、
  `--ffmpeg-source` 等真正配置菜单的选项照旧。
* **⚠ 行为变化**：`--update` 与 `--update-check` 同时给出时报参数错误（2），
  不再由 check 分支静默吞掉 `--update`。
* `RunUpdate` 与 `--ffmpeg-download` 改用 `TryLoad`：一次更新检查或工具链安装不再
  用全默认对象覆盖损坏的 `config.json`；`UserConfig.TryLoad` 补
  `NotSupportedException` / `ArgumentException`；
  `Normalise` 的 ladder 迁移加一次性标记，用户显式选的 `13,8,5,3` 不再每次启动
  被改写。

### 4. 安全与输出正确性

* `SelfUpdate.SafeName` 按 feed 的 tag 长度分配栈内存 → 改为堆分配并截断到 192
  字符；`asset.Name` 拒绝盘符/ADS/`..`/超长名，落地文件名统一清洗，杜绝
  `StackOverflowException` 与路径逃逸。
* **⚠ 行为变化**：带 `-rc1` / `+build` 后缀的 tag 不再被自动安装（仍可被
  `--update-check` 看到，并给出提示）。`VersionTag` 的解析语义不变，单测仍断言
  后缀在比较时被忽略。
* `UpdateState.IsFresh` 拒绝未来的 `CheckedAt`（时钟回拨不再永久跳过更新检查）。
* `HttpSource.FileNameFromUrl` 过滤冒号、Windows 非法字符、`.` / `..` 与保留设备名。
* 路径比较改用平台相关比较：Linux 上 `clip.mp4` → `CLIP.MP4` 不再被误判为同文件，
  MRU 目录也不会把 `/mnt/A` 与 `/mnt/a` 合并。
* 报告的平均码率取 `max(-progress 累计字节, 实际 .part 文件大小)`，并在取消
  progress reader 前给它 500 ms 消费最后的 `total_size` / `progress=end`。
* `RunSplit` 只在**可行性阶段**回退 inline；视频编码开始后的超时、磁盘或参数错误
  直接上报，不再重跑整段实时编码并掩盖真实原因。

### 5. 质量保障

* 新增 `V7995Tests.cs`（16 个用例）覆盖上述三态、往返精度、代理值、URL 文件名、
  预发布标记、shell 转义、补全种类、帧池/分片边界与一次性 ladder 迁移。
  单测：718 → **734**，双 TFM（net10.0 / net11.0）各全绿、0 警告。
* e2e：`test_realmedia.py` 的四个用例改用独立 scratch 子目录，xdist 下不再因共享
  `tests/out/realmedia` 的 `.part` 清理与占位目录 rmtree 撞锁（`SHFileOperationW
  0x20`）。全量：**213 passed + 2 skipped**（并行阶段）、**29 passed**（串行独占
  阶段），共 242 passed / 2 skipped。
* 运行检查：`--version` / `--help` / `--cpu-info`、无输入与互斥开关的退出码、
  `--gh-proxy` 位置参数、四种 shell 的补全脚本、CUI 菜单进入与退出（rc=0）、
  真实编码（direct 模式，产物 901 294 B）、`--save-config` → `--emit-args` 往返。
* `tools/check-slnx.py` 清单门禁、双语成对检查通过。

### 6. 为 v8 做准备

* 新增 [`v8-handover.md`](v8-handover.md) / `v8-handover.en.md`：冻结的行为契约、
  本次审计的完整修复清单、**明确不修而留给 v8 的取舍**、以及 Rust 重写的验收
  前置条件（argv/产物黄金基准）。
* 仍保留给 v8 的项目（都是会改变 v6.3.3 等价性或需要结构性改动的）：
  `OutputMonitor` 窗口淘汰用墙钟、速率分母用媒体时间；`-threads` 的位置；CUI
  不接受 CLI 编码选项（现在只是明确报错）；`Toolchain.CopyInto` 非事务；巨型类与
  全局状态；无 `--json` / metrics / tracing；自更新无代码签名与回滚；并行无
  CPU/IO 配额；Linux NativeAOT 的 glibc 绑定。

## v7.9.9.4（2026-11-08）

一次纯清理与文档工程版本：删掉没人用的代码与脚本，把文档理顺、补齐、并做成中英双语。

### 1. 清理：删除无引用项

| 删除 | 理由 |
| --- | --- |
| `tools/benchmark.ps1` | 只在 README 与验收报告里被"提到"，没有任何地方调用；性能数据早已并入 `docs/test-report-v7` |
| `tools/probe_audio_limits.py` | 一次性测音频边界的脚本，结论已固化进 `Core/AudioCatalog.cs` 与单测 |
| `publish/release/notes-v7.9.8.{7,8,9,10}.md` | 历史发布说明；GitHub / Codeberg Release 页面是权威副本，仓库里再存一份只会与 `publish/` 的 gitignore 规则自相矛盾 |
| `PostProcessOutcome.RecompressRan` / `RecompressPath` | 只写不读的属性（`Passes` / `FinalPath` 已完整表达） |
| `Recompress.Apply(settings)` / `Apply(pass)` / `BuildArgv(settings)` | 单遍兼容壳，唯一调用方是测试；生产路径一律走 `ApplyWithPath` |
| `EncoderMath` 里指向自身命名空间的 `using` | 编译器本来就会忽略 |

同时把 `.pytest_cache/` 加进 `.gitignore`（此前每次跑测试都会在仓库根留下未跟踪目录）。

### 2. 文档：中英双语

* 新增英文版：`docs/usage.en.md`、`requirements.en.md`、`era-presets.en.md`、
  `toolchain-builds.en.md`、`MAINTENANCE.en.md`、`capability-matrix.en.md`、
  `CHANGELOG.en.md`、`CHANGELOG-archive.en.md`、`archive/README.en.md`，以及中文版
  `docs/test-report-v7.md`（英文原文改名为 `.en.md` 保留）。
* 根入口：`README.md` 以英文为主（仓库首页），新增 `README.zh.md`；两份互相链接。
* 新增 `docs/README.md` / `README.en.md` 作为全量索引，并写明语言约定。
* 每个文件第一行是语言切换；英文页的相对链接一律指向 `*.en.md`，中文页指向 `*.md`
  —— 混用会让读者点一次就掉回另一种语言。
* 历史型文档（`CHANGELOG*`）以中文为唯一权威全文，英文版给逐版摘要 + `⚠` 行为变化
  标记，并链回中文原文：历史条目按原样保留，重写会毁掉它作为记录的价值。

### 3. 文档：理顺、补齐、修错

* **章节顺序**：`usage.md` 的 `7.17`/`7.18` 原本排在 `9` 之后，现在按编号重排；
  删掉「编码器修复记录」整节（内容早已并入本文件与 CHANGELOG）。
* **文件截断**：`usage.md` 的排错表曾被 v7.9.9.1 插入的日志小节拦腰截断，现已接回；
  `archive/README.en.md` 的退役清单同样被空行断开，已修。
* **数量口径统一**：FFmpeg 来源统一说「六个（五个可自动下载 + `avbuild` 手动）」，
  之前 `toolchain-builds.md` 标题写五个、表里列六个，`usage.md` 又写六个。
* **修正过期事实**：`MAINTENANCE.md` 的日志文件名写的是 `lae-yyyyMMdd.log`（v7.9.9.1
  已改为按体积滚动的 `lae.log`）；发布流程补上「先建 Release，再分批传资产」
  （四个资产一次性传会卡住，v7.9.9.2 实测）。
* **修正错误命令**：`capability-matrix.md` 的 `python tools/capability_sweep.py --json`
  与 `pixfmt_sweep.py --json` —— 两个脚本都不接受 `--json`，改为 shell 重定向。
* **断链与入口**：`README.md` 的版本历史链接指向已拆分出的 `CHANGELOG-archive.md`；
  `usage.md` 顶部补 `../README.md` 与文档索引；`docs/*` 全部纳入索引表。
* **精简**：删掉 `README.md` 与 `usage.md` 里重复的 AVX2 说明、`README.md` 的旧
  工具/文档清单（`benchmark.ps1` 等已不存在）、`usage.md` 的旧测试读数（155 条 /
  706 项等易过时数字改为「以实际收集结果为准」）；性能预期只保留在
  `requirements.md`，其余链接过去。
* **归档标注**：`capability-matrix`、`test-report-v7` 开头显式标 `已归档`，避免读者
  把 v7.9.6 的读数当成现行规范。

### 4. 校验

* `tools/check-slnx.py` 新增**双语成对**检查：`docs/*.md` 必须有 `*.en.md`
  （以及 `archive/README.md`、根 `README.md`），只翻一半直接让 CI 失败。
* 新增一次性链接校验（24 份文档、全部相对链接可解析，含代码段内的命名示例不误报）。
* 清单：208 文件 / 2 项目，双向无漂移。

### 5. 验证

Windows：构建 0 警告 0 错误、单测 **718 全过**、e2e 全过、清单与语言成对校验通过。

---

## v7.9.9.3（2026-11-08）

全项目审计修复，并把上一版只存在于 CLI 的通用编码参数补进 CUI。

### 1. CUI 里看得见 bufsize / minrate / maxrate

* 用户反馈：v7.9.9.2 的 `--recompress-maxrate/-minrate/-bufsize/-recompress-extra` 在 CUI
  **没有任何入口**，直播侧只能借参数菜单 `35.` 手写字符串。更混淆的是二次压缩菜单 `8.`
  的标签叫 `rc.menu.extra`（第 2+ 遍链），与 CLI 的 `--recompress-extra`（第一遍自由参数）
  同名不同义 —— 现改名为 `rc.menu.chain`（「后续遍数（链）」）。
* **二次压缩菜单**新增 `b.` `-maxrate`、`c.` `-minrate`、`d.` `-bufsize`、`e.` `-preset`、
  `f.` `-profile:v`、`g.` `-ac`；**参数菜单**新增 `36.` `-maxrate`、`37.` `-minrate`、
  `38.` `-bufsize`（直播编码侧）。输入 `none` 删除该 flag，留空不改。
* 新增 `PostProcess/FragmentEditor.cs`：`SetFlag` / `Merge` / `ValidateVbv` /
  `ValidateBounds` / `FirstWins`。CUI 与 CLI 都把选择写进 fragment 本身（二次压缩写
  `Recompress.Args`、直播写 `ExtraEncoderArgs`），**不新增持久化字段**，于是
  `--emit-args`、`CloneRecompress`、`ComposeSecondPass` 自动跟着走。
* VBV 校验抽成共享规则，CLI 与 CUI 都调；校验放在**最终 fragment 生成之后**，覆盖手写
  `--recompress-args`、era 预设、CUI 编辑。二次压缩要求有 `-b:v`，直播侧只校验
  `minrate <= maxrate`（编码器自己生成 `-b:v`）。
* 修 `--extra-encoder-args` 按文档「会被记住」却没写回 `Config` 的问题。

### 2. 全量审计：严重问题

* **非实时捕获线程无限自旋**：v7.9.9.2 的一帧一槽握手里，捕获线程的等待条件只看
  `ReadyCount` 与取消，主循环达到 `targetSlots` 停止消费后没人再清 `ReadyCount`，
  线程永久自旋并占住帧缓冲，`Join(5000)` 每次白等 5 秒。现检查 `captureState.Stop`、
  收到停止即释放缓冲退出，两处 `Thread.Sleep(0)` 改为带退避的 `SpinWait`。
  修复后 `Fresh 65 / Repeated 0`、A/V 时长与实时模式一致，耗时仍约 0.8 s。
* **后处理整条链不响应 Ctrl+C**：`PostPipeline.Run` / `Recompress.ApplyWithPath` 此前没有
  `CancellationToken`，FFmpeg 超时 3600 s，取消异常还会穿透并留下 `.part`。现从 CLI/CUI
  主链路与 `--recompress-only` 两条路径透传 token，catch 纳入 `OperationCanceledException`
  并清理 `.part`。
* **`PostArgs.Validate` 只查最后一个 token**：中间的裸路径（`-c:v libx264 evil.mp4 -crf 20`）
  会被 FFmpeg 当成额外输出文件，可覆盖无关路径。现逐 token 跟踪「选项的值」，任何游离的
  非选项 token 一律拒绝；`ValueLessFlags` 表处理 `-y`/`-an` 等无值开关。
* **`FlushContext` 上下文平方级放大**：`ctx[...]` 事件经同一个 Logger 又回写进 200 条
  ring，第二次错误把它们再输出一遍，真正的上下文被挤出。`ContextSink.Emit` 现过滤该模板。
* **自更新只复制 payload 顶层文件**：`runtimes/`、`cs/`、`de/` 子目录里的 native 库与
  卫星资源被整批丢弃，更新后的程序可能起不来。现递归枚举并按相对路径重建安装/回滚目录。
* **占用检查会删掉并发运行正在写的 `.part`**：现用 glob 过滤 + 「创建时间早于本进程」+
  可独占打开三重判定，只清崩溃残留；`ChildProcesses` 记录播放器打开的是**哪个文件**，
  只结束该文件对应的进程，不再误杀正在播放 B/C 的窗口；`WaitForExit` 移出锁。
* **`Toolchain.RunLong` 无取消、异常不 kill 子进程**：现接 `Interrupt.CurrentToken`、
  轮询等待、取消/读异常时 `Kill(entireProcessTree)`，consumer 整体免疫
  `AppLog.Sampled` 在 `Close()` 后抛的 `ObjectDisposedException`。
* **`AppLog.Redact` 漏脱敏**：`SelfUpdate` 会 Trim token，日志按原值替换；现同时替换
  Trim 后与 URL 编码后的变体。
* **`CliParser.TryValue` 吞掉后续选项**：`--recompress-extra --quiet` 会把 `--quiet`
  当值。现只在下一个 token **确实是本程序选项**时拒绝，因此以 `-` 开头的 FFmpeg 片段
  （`--recompress-args "-c:v libx264"`）仍然合法。
* **`ComposeSecondPass` 重复 `-preset`**：用户手写的 `-preset slow` 被 catalog 追加的
  `veryfast` 覆盖（FFmpeg last-wins）。现对单值 flag 保留首次出现；`WithExtras` 的纯
  字符串拼接换成 `FragmentEditor.Merge`，不再产生两个 `-maxrate` / `-b:v`。

### 3. 全量审计：中等问题

`Interrupt.Request` 把 `Cancel()` 放锁外导致边界丢 Ctrl+C（改用可重入 Monitor 并在锁内取消）；
`NetworkProbe` 把失败的 `null` 永久缓存（只缓存成功结果 + 256 上限）；`EraPreset.Fragment`
的 `-maxrate/-bufsize` 分支是死代码（native 参数恒含 `-b:v`）；`RunSplit` 的 Auto 回退漏
`UnauthorizedAccessException`；`AppLog.Init` 降级路径重复注册同一个 `ContextSink` 且泄漏
旧 logger（先 Dispose 再重建）；`PipeReaders` 在 `Emit=false` 时直接返回、停止排空管道会
让子进程写满 stderr 死锁，取消时未 `AdvanceTo`，`CompleteAsync` 异常会覆盖真实错误。

### 4. 测试有效性

修掉 4 处恒真/条件断言（`AppLog.RunId` 自比、`KillAll() >= 0`、三处 `if report:`/`if levels:`
让核心回归可静默跳过），并把一个「声称验证回调停止」实则只数行的测试改成真实契约。
顺带发现 `_frame_report` 的正则按逗号匹配、而报告是两行格式 —— 它**从未匹配过**，A/V
回归一直被跳过；修正后重新钉住 `Fresh/Repeated`。新增 `V7993Tests`（9 项：路径注入、
fragment 编辑/合并/去重、共享 VBV 校验、CLI 解析、CUI 双语键、era VBV、`--emit-args` 往返）。
e2e `conftest.py` 加了一个 Windows/CPython 3.15.2 兼容处理：pytest 在 sessionfinish 对
陈旧 symlink 调 `resolve()` 抛 WinError 448，会把全绿的运行误报 exit=1。

### 5. 验证

Windows：单测 **718 全过** / e2e **148 全过** / 构建 0 警告 0 错误；清单无漂移。
CUI 端到端实测：参数菜单 36/37/38 写入 `-maxrate 300k -minrate 100k -bufsize 600k`，
二次压缩菜单 b–g 写入完整的 `-preset slow -profile:v high -ac 1` fragment。

---

## v7.9.9.2（2026-11-08）

直接压缩音画同步修复、时代标准默认档位、路径记忆、文件占用治理、通用编码参数，外加一轮全项目审查。

### 1. 直接压缩音画不同步（严重回归）

* v7.9.9.1 的 `--no-realtime` 去掉两处节流后，主循环跑得比解码快：
  `FrameBufferPool.TryTakeLatest` 每次丢弃除最新帧外的所有帧，捕获线程全速读帧又会在池满时
  evict 最旧帧。实测 5 s / 65 槽只有 **3 个真实帧 + 62 个重复帧** —— 5 秒静帧配完整音频。
* 非实时模式改为**一帧一槽握手**：捕获线程在池非空时等待（不丢帧），主循环在池空时等待
  （不占槽、不重复）。修复后 `Fresh frames: 65 / Repeated slots: 0`，视频 5.076923 s / 66 帧、
  音频 5.041633 s，与实时模式逐项一致；耗时仍 0.53 s（约 10×）。
* 顺带修：持久化的 `RealTime=false`（CUI「参数 → 34.」）此前没有强制单级挡位，只有 CLI flag 会。

### 2. 默认 fps 档位按时代标准

* 13/8/5/3 只是 Python v6.3.3 移植基线的自造值，首级一步砍掉 38% 帧率、无年代依据。
  改为项目自己 `EraPreset.SiteLadder()` 对 13–15 fps 站点使用的档位 **[13, 12, 10, 8, 5]**；
  基础帧率仍 13，保持 v6.3.3 对比矩阵不变。
* 收敛到 `Constants.DefaultFpsLevels` / `DefaultFpsLevelsSpec` 单一来源，消除 11 处硬编码
  （Models / UserConfig / RealtimeOptions / CliOptions / EncoderMath / CliArgs / OptionCatalog / 帮助文本）。
  旧 `config.json` 里恰好还是 `[13,8,5,3]` 的自动迁移，用户自定档位不动。
* `--fps-levels none|off` 继续可删除动态挡位。

### 3. 输入/输出路径记忆与自动补全

* 新增 `Configuration/PathMemory.cs`：最多 8 个最近目录 MRU，持久化到 `config.json`；
  读取时只保留存在、绝对、去重的路径。CLI 与 CUI 每次运行/保存都刷新。
* `--output` 给**已存在的目录**时自动用 `<源文件名>_live.<容器扩展名>` 补全；
  实测输入 `D:\...\Compressed` 得到 `test_src_live.avi`。CUI 输出菜单列出 `r1..rN`，
  输入 `rN` 或直接给目录都可用。

### 4. 文件占用与二次压缩链路

* 新增 `OutputFile.EnsureTargetAvailable`：编码前用 `FileShare.None` 独占探测目标，
  扫描并删除同目录其它运行残留的 `<stem>.<tag>.part<ext>`。inline / split / parallel /
  二次压缩四条路径都调用。
* 占用时先结束**本程序自己启动的**播放器（`Core/ChildProcesses.cs` + `Player.Launch` 跟踪），
  再复查；仍占用才明确报错。**不碰用户从外部打开的程序**。
* 修 v7.9.8.13.2 引入的回归：`Promote` 把 `UnauthorizedAccessException` 当永久 ACL 错误直接重抛，
  而 Windows 上**共享冲突也抛这个异常** —— `name (2).ext` 另存兜底被整个绕过，完成的编码被丢掉。
  现在两者统一重试 + 另存。
* 修 `Recompress.Apply` 丢弃 `Promote` 返回值：新增 `ApplyWithPath` 返回真实落地路径，
  `PostPipeline.Run` 与 CLI/CUI 的 `--recompress-only` 多遍链一路跟随，
  「Recompressed:」报告也指向真实文件。竞态实测：目标在编码中途被锁 → 另存
  `clip_web (2).mp4`、报告指向 `(2)`、exit 0。

### 5. 二次压缩与直播编码的通用参数

* CLI 新增 `--recompress-maxrate` / `--recompress-minrate` / `--recompress-bufsize`（kbps）、
  `--recompress-extra`（自由 FFmpeg 参数）、`--extra-encoder-args`（直播编码侧）。
  可表达 `-b:v` / `-c:v` / `-ac` / `-profile:v` / `-preset` / `-b:a` 等全部通用项；
  项目原有的 `--x264-*` / `--openh264-*`、libx264 / libopenh264 选项保留。
* **VBV 三件套必须与 `-b:v` 配套**：校验放在最终 fragment 生成之后（只看 flag 是否解析，
  会漏掉「无 codec/era 时空 fragment」的情况），并拒绝 `minrate > maxrate`。
  实测缺 `-b:v` → exit 2 明确报错；完整 → `-b:v 250k -maxrate 300k -minrate 100k -bufsize 600k`。
* 修 `ComposeSecondPass`（CUI 改编码器时）只保留 `-vf`、把用户手写的 `-preset` / `-profile:v` /
  `-maxrate` / `-bufsize` / `-minrate` / `-x264-params` / `-map` / `-t` 全部抹掉：
  改为只替换由 `VideoArgs` / `AudioArgs` 重新生成的 flag，其余原样保留。
* 直播侧 `--extra-encoder-args` 用与 `--recompress-args` 相同的 tokenizer 解析后追加到输出参数段。

### 6. 全项目审查修复

* `Toolchain.RunLong` 的 tail 缓冲外层 `if (tail.Length < 4000)` 守卫把诊断**冻结在前 4000 字符**，
  失败原因往往在后面；改为始终追加再裁剪。
* `AppLog.Redact` 只认带 `://` 的凭据、且多个 `@` 时用第一个：`--proxy user:pass@host:8080`
  曾原样落盘。改为取最后一个 `@`，且仅当 userinfo 含 `:` 才脱敏。
* `PipeReaders` 无换行的超长输出永远不进 `Emit`、`PipeReader` 内部缓冲无限增长：
  单行限 256 KB，并按 `MaxLine` 分块冲刷。
* 其它：`CliArgs.LadderSpec` 默认判定不再写死旧档位；新 VBV 数值一律 InvariantCulture。

### 7. 测试

* 新增 `V7992Tests`（8 项：默认档位、none、PathMemory、通用参数往返、VBV 校验、占用预检）
  与 `tests/e2e/test_direct_and_paths.py`（9 项：A/V 同步、无挡位、目录补全与 MRU、VBV、
  通用参数、残留 part、独占锁拒绝、另存竞态）。
* Windows：单测 **709 全过** / e2e **148 全过** / 清单 195 文件无漂移。

---

## v7.9.9.1（2026-11-08）

日志精简、fps 挡位可关、直接压缩（不模拟直播）、二次压缩码率与音频可选，外加一轮
全项目 bug 修复。

### 1. 日志精简分级：默认只记 error

* 默认阈值从 `Information` 改为 **`Error`**。上一版每个运行都会把 Console 的每一行
  （含每 0.5 秒一次、用 `\r` 重绘的进度行）写进日志，一次长编码能到几十 MB；普通运行
  不该付这个代价。`LAE_LOG_LEVEL=verbose|information|warning` 仍可取回完整链路。
* **错误上下文快照**：结构化事件仍进一个 200 条的进程内环形缓冲，第一次 `Error`
  把它以 `ctx[级别]` 前缀落到文件。所以默认配置下日志很小，但出错时仍带着前因。
* `Ui()` 在阈值高于 Information 时**直接返回**，不再为注定被丢弃的记录付一次队列投递
  （`\r` 重绘尤其如此）。
* 修 `LogPath` 指向一个不存在的文件：原来报 `lae-.log`，而 Serilog 的按天滚动实际写成
  `lae-YYYYMMDD.log`。现在按**体积**滚动（`lae.log`，16 MB × 7），启动日志里报的路径
  就是用户能找到的那个文件。
* 修异常正文绕过脱敏：`LaE_UPDATE_TOKEN` / `user:pass@host` 之前只作用于字符串参数，
  `Exception.ToString()` 是原样落盘的。现在异常文本作为一个整体先脱敏。
* 修兜底 sink 忽略 `LAE_LOG_LEVEL`、且之后 `Sampled` 限流失效；修 `Close()` 的
  可重入窗口（两个线程会各写一次 `run.end`）。

### 2. 动态 fps 挡位

* **挡位本来就跟随时代预设**（`--era` 未配 `--fps`/`--fps-levels` 时用预设的 ladder，
  首级等于基础帧率），本版补上**删除**入口：`--fps-levels none`（也可写 `off`）。
  单级挡位下控制器永不降档，`motion` 全程保持基础帧率；CUI 的档位提示已说明可输入 `none`。
* 非法值（`abc`）仍是硬错误（exit 2），不会被当成 "自动"。

### 3. 直接压缩：`--no-realtime`

* 以前要"不模拟直播"只能用 `--recompress-only`，那等于跳过直播编码器/尺寸/帧率/音频
  整条链路，只跑第二遍。现在直播链路本身可以不等时间：`--no-realtime` 去掉捕获线程与
  出槽两处节流，按机器速度尽快编码，输出仍是同一份产物。
* 非实时下**强制单级挡位**：码率窗口不再对应"节目秒"，阶梯的迟滞常数会瞬间打到底级。
  该设置**持久化**（CUI 参数菜单 `34.` 实时模拟 on/off），命令行 flag 只作用于本次运行。
* 实测（5 s / mpeg4）：实时 5.58 s，非实时 **0.44 s（12.7×）**，两者输出时长完全一致
  （5.076923 s）。

### 4. 二次压缩的码率与音频

* `--recompress-video-kbps` 现在对**所有**编码器生效，不再只限 `libopenh264`。质量型
  编码器（libx264/x265/VP9）在**没有**同时给 `--recompress-quality` 时改用 `-b:v`，
  并自动剔除编码器自带的 `-b:v 0`；两者都给时质量优先，混合链里"每个数字给能用的那一遍"
  的既有语义不变。
* CUI 二次压缩菜单新增 `a.` 音频码率（`--recompress-audio-kbps` 一直有 CLI 但没菜单）。
* 修**严重缺陷**：CUI 改视频或音频编码器时会重写整个 fragment，而音频侧一直传 `null`，
  于是 `-b:a` / `-ar` / `-ac` 被抹回目录默认值（例如自定的 64k 变成 96k）。现在从原
  fragment 回读这些数字；这正是 v7.9.8.8 在视频侧修过的同一个 bug。

### 5. 其它 bug 修复

* **`Toolchain.RunLong` 消费者孤儿**：管道读线程抛异常时 `TryComplete` 被跳过，消费者
  永远挂在 `ReadAllAsync` 上；同时队列无界，多 GB 下载期间可无限增长。现在 `finally`
  完成队列、消费者对回调异常免疫、队列改为有界（4096，丢最旧）。
* `--recompress-only` 忽略 `--no-era`（两个入口语义不一致）。
* `EncoderMath.FormatLadder` 用当前区域性格式化，逗号小数区域下产物无法被自己的解析器
  回读（`12,5` 与分隔符 `,` 冲突）；`Cui` 的质量输入解析与提示用的区域性也不一致。
* `targetSlots` 的 `double → int` 溢出会让一次运行在 0 槽处停下并误报"输出为空"。
* 手写 `--subtitle` 路径跳过了存在性检查（校验被放在"自动发现"分支里）。
* `--recompress-container` 直接拼进文件名，未净化分隔符/`..`。
* `PipeReaders` 的超长行缓冲可被无换行输出撑大（现限制 256 KB）。

---

## v7.9.9（2026-11-08）

改善 bug 复现与追溯链路：结构化日志、三个并发/IO 库、显式的「不使用（删除）」入口，
以及补齐 .NET 6-10 的现代语法落点。

### 1. Serilog + Microsoft.Extensions.Logging，全量可追溯日志

* 主项目此前**零 NuGet 依赖**、全仓 663 处 `Console.*` 直写、没有任何日志抽象。
  本版引入 `Serilog` 4.4 / `Serilog.Extensions.Logging` 10 / `Serilog.Sinks.File` 7 /
  `Serilog.Sinks.Console` 6 / `Microsoft.Extensions.Logging` 10 / `System.Threading.RateLimiting` 10。
  Serilog 用**代码式配置**（不依赖 XML/JSON 绑定），与 Release 的裁剪开关兼容。
* `Diagnostics/LaePaths.cs`：`LAE_LOG_DIR` 独立日志根，默认
  `<数据根>/logs/lae-yyyyMMdd.log`（按天滚动、留 7 天）。**刻意不放进
  `ConfigStore.RootDirectory`**，避免污染用户配置目录与 e2e 隔离语义。
* `Diagnostics/AppLog.cs` 两层设计，用户可见输出一字不改：
  * **镜像层**：`TextWriter` 装饰器原样转发 `Console.Out/Error`，同时把每一行记成
    `ui.stdout` / `ui.stderr` 事件。CR/LF 分段，避免 `\r` 进度重绘刷屏。
  * **结构化层**：子进程 argv、退出码/耗时/最后 400 字符 stderr、FFmpeg 路径与来源、
    输出文件实际落地路径、中断、操作边界、运行开始/退出码等 20 余处插桩。
* 每次运行一个 `RunId`（pid+guid8），`Interrupt.BeginOperation()` 推进 `OperationId`，
  每条日志都带 `<RunId>/<OperationId>`，CUI 里连续多次编码也能拆开。
* **绝不阻塞实时编码**：调用方只做 `Channel.Writer.TryWrite`，单一后台消费者写盘；
  Main 用 `try/finally` 保证所有 early return 与异常路径都 `Close()` 落盘。
  日志初始化/写盘失败自动降级到临时目录，再失败则静默关闭，**不会中断编码**。
* 脱敏：`LAE_UPDATE_TOKEN` 与 `user:pass@host` 凭据不会进入日志。
* 开关：`LAE_LOG=0` 关闭，`LAE_LOG_LEVEL=verbose|warning|error` 调级别，
  `LAE_LOG_MIRROR=0` 关闭 UI 镜像；e2e 的 `harness.py` 已把日志目录重定向到私有根。

### 2. Channels / RateLimiting / Pipelines

* **Channels**：`AppLog` 的无界单读者队列；`FFmpeg.Toolchain.RunLong` 的两个管道读线程
  改为生产者 + 单消费者 `Channel<string>`，去掉 `tailGate` 锁，保持到达顺序与末 4000 字符。
* **RateLimiting**：`TokenBucketRateLimiter`（40 令牌/秒）用于高频重复的 FFmpeg stderr /
  安装器输出采样；关键事件与 UI 输出**不限流**，被抑制的条数会另行记录。
* **Pipelines**：`FFmpeg/PipeReaders.ForEachLineAsync` 的手写 `byte[]` + `BlockCopy` 行切分
  改为 `PipeReader.Create(stream)` + `SequenceReader<byte>`。公开 API、trim 规则、CRLF、
  无换行尾行、取消与错误回调逐项保持；新增单测钉住这些边界。
* `System.Threading.Channels` 与 `System.IO.Pipelines` 在 net10.0 已是 BCL 自带
  （显式引用会触发 NU1510），因此只从源码使用；`System.Threading.RateLimiting` 需包引用。

### 3. 时代预设与二次压缩的「不使用（删除）」

* CUI 时代预设菜单新增 `x.`：清除当前预设（`c.EraPreset = null`），实时编码回到用户显式参数；
  原有的 `d.` 删除自定义预设保持。
* CUI 二次压缩菜单新增 `9.`：一次性删除整条链 —— `Enabled=false`、`Args=""`、
  `Container="mp4"`、`Extra.Clear()`。此前只有菜单 8 能清 `Extra`，第一遍参数清不掉。
* CLI 新增 `--no-era`：不使用时代预设，并**从写回的 `config.json` 快照里移除**已保存的预设
  （`Cli.cs` 的 `EraPreset` 组装与 `era` 解析都让该 flag 优先）。`--no-recompress` 沿用。
* en/zh 各补 4 个本地化键，双语键集合与补全表的双向校验单测全过。

### 4. .NET 6-10 语法与新 API

项目此前已用 `System.Threading.Lock`（11 处）、`FrozenDictionary`（52 处）、
`TextExtensions` 的 C# 14 extension members、`u8` 字面量与 collection expressions；
本版补齐剩余低风险落点，不改任何行为：

* `Core/CharSets.cs`：缓存 `SearchValues<char>`（.NET 8+）替换 4 处
  `IndexOfAny(char[])`（URL 查询、版本后缀、CR/LF），省掉每次调用的 char[] 分配。
* `AppLog.LogPath` 使用 C# 14 **`field` 关键字**；`MirrorWriter` 用 C# 12 **primary constructor**、
  `AppLog.Entry` 用 `readonly record struct`；`PipeReaders` 用 .NET 6 的
  `ArgumentNullException.ThrowIfNull`。
* 新增测试 `V799Tests`（`--no-era`、双语键、Pipelines 行解析边界、SearchValues、关联 id）
  与 `tests/e2e/test_logging.py`（日志落盘 + stdout 字节不变 + `--no-era` 实跑）。

---

## v7.9.8.13.2（2026-11-07）

全项目审计、文档拆分与可维护性整理。四个只读子代理分别审查了核心编码链路、
配置/更新/工具链、测试与工具脚本、文档与项目结构，逐条核对证据后修复。

### 1. 变更记录按 v7.9.8 拆分

* `docs/CHANGELOG.md` 现在只保留 **v7.9.8 及以后**；v7.9.7 及更早（含 v6.2.2 →
  v6.3.3 移植基线）整体搬到新的 `docs/CHANGELOG-archive.md`。拆分按**版本号**找边界，
  只搬文本不改写历史；两个文件互相链接，`README.md` 同步。
* 新增 `docs/MAINTENANCE.md`：目录职责、常用命令、改动后检查清单、测试分层与配置隔离、
  文档拆分约定、以及「双平台 × 两种运行时形态」的四包发布流程。

### 2. 真实缺陷修复

* **标定失败污染码率**：`IntraQuality` 探测失败曾返回 `NaN`，扩散到 `LimitKbps` /
  `RecoverKbps`，让整个帧率阶梯失效并把画质钉在最差档。现在返回 0 表示"不可用"，
  调用方保持用户原本的质量与阈值；探测异常一律降级（原来只捕获 3 种，漏掉超时、
  进程启动失败等），并补齐 HTTP / UNC 读取参数、超时 30 → 60 s。
* **改名成功却统计旧文件**：`OutputFile.Promote` 会另存为 `name (2).ext`，但三处调用方
  忽略返回值，仍读原路径的字节数、报告原路径、甚至把旧文件送进二次压缩。
  新增 `EncodeResult.PlacedPath`，inline / split / parallel 与 CUI / CLI 全链路跟随实际
  落地文件。另修候选名撞上目录时 `FileInfo.Exists` 返回 false 的误判、以及权限拒绝 /
  目录占位这类永久失败仍白等 1.5 s 重试。
* **per-run 字幕写进配置**：`SubtitleSetup.Prepare` 原本在 `Program.cs` 的前置命令段执行，
  使 `--save-config` / `--emit-args` 也去解压字体包、提示交互，并把自动发现的字幕折进
  `config.json`，下一次无关运行会继承它。现下沉到 `Cli.Run` 的转换退出之后，并在写回
  快照时排除标记过的发现值。交互判定补上 `Console.IsOutputRedirected`，避免脚本挂起。
* **自更新回滚不完整**：失败回滚只恢复已有文件，新增文件残留形成新旧混合目录；恢复时
  也丢了 Unix 执行位。现在按 `created` 列表删除新文件并补回 0755。
* **PATH 条目累积**：切换 FFmpeg 来源时旧的受管 `bin` 目录一直留在用户 PATH 前面，
  新 shell 仍跑到旧构建。新增 `ToolchainPath.PruneManagedBins`，发布前清掉其它受管条目
  （用户自己的 PATH 条目不动）。
* **稳定版回退是死代码**：`FfmpegDist.StableUrl` 全空，而固定 URL 的四个源本身就是
  rolling 文件、没有更稳定的替代品。现在给唯一走动态 feed 的 `autobuild` 配一个已验证的
  固定 commit 资产，并修正 `FfmpegCatalog`、`usage.md`、`toolchain-builds.md` 里
  "五个源都有回退" 的过时表述。
* **工具链健壮性**：`SevenZip.Ensure` 的 `/D=` 未加引号（`%LOCALAPPDATA%` 常含空格）、
  `chmod` 失败被忽略、下载 7-Zip 本身不带 `--proxy` / `--gh-proxy`、失败留下残件；
  `ReleaseFeed.Resolve` 遇到畸形 JSON 会崩；字体包解压的临时目录创建不在 try 内、
  异常时残留解压出的字体；字幕目录不可读直接抛到交互界面之前。均按证据修复。
* 其它：`ValidateRoqDimensions` 作为"查询"却抛异常（CUI 配置页 / 报告会崩）改为返回
  字符串；分离音频通路漏了 `-af asetpts`、合流子进程没接取消令牌；并发分片跨卷回退
  漏 `UnauthorizedAccessException`；`Smb.MountFor` 的 `GetFullPath` 未保护；
  `CliArgs.FromUserConfig` 漏发 OpenH264 模式、era 形状 / modern、H.264 实现五个字段。

### 3. 测试有效性

* 修掉 3 处**恒真或空集断言**：`V79812Tests` 用两个 `EndsWith` 自比（改成真正调用
  `SelfUpdate.Pick` 验证 FDD / SCD 资产不互串）、`FontPackTests` 两边都过同一个归一化
  函数（改成独立期望值）、`ToolchainBuildsTests` 的 `foreach` 可能遍历空集合（加数量断言）。
* `conftest.py` 的 `pytest -n` 防护栏用子串判断，恰好放行了本该拒绝的排他组
  （`-m "timing or shared_config"` 也含两个词）；改成只放行显式排除两组的表达式，
  并补 `LAE_E2E_PHASE` 通道。`build.sh --help` 不再把可执行代码打印出来。
* 新增 `V798132Tests`（roq 查询契约、PATH 清理、稳定版回退）。

### 4. 项目整理

* `LiveArchiveEncoder.V7.slnx` 的 Solution Items 早已漂移：按真实目录重建
  （22 个目录 / 185 个文件），新增 `tools/check-slnx.py` 做**双向**漂移校验
  （列出的路径是否存在 + 源码/测试/工具/文档是否都列出），CI 里每次跑。
* CI 补 `actions/setup-python`、清单校验，以及 `net11.0` 目标的 build + test ——
  `Directory.Build.props` 一直声称"CI 会验证双 TFM"，实际并没有。
* 清理过时文档：`usage.md` / `toolchain-builds.md` 的来源数量与稳定版回退表述、
  `test_cui_smoke.py` 头注释和 `usage.md` 里"会备份并恢复真实配置文件"的说法
  （v7.9.8.6 起 e2e 已全程用私有 `LAE_CONFIG_DIR`）。

---

## v7.9.8.13.1（2026-11-07）

统一工具链、专门文档，以及用原始素材元数据 + 史料交叉校正时代预设。

### 1. 7-Zip 双平台统一

* 新增 `FFmpeg/SevenZip.cs`：两端都从 `ip7z/7zip` release `26.05` 取构建。
  Windows 用 `7z2605-x64.exe`（官方 FAQ 的 `/S` + `/D=` 静默安装到用户目录），
  Linux 用 `7z2605-linux-x64.tar.xz`（实测包内含 `7zz` / `7zzs`），解包后 `chmod +x`。
  查找顺序：系统 PATH → Windows `Program Files\7-Zip` → 我们装的那份；都没有才下载。
* `Toolchain.FindSevenZip()` 现在两端都会认出自动安装的副本，`Toolchain.Extract` 的
  `.7z` 分支也改为 `SevenZip.Ensure()`，Linux 不再出现 "install p7zip by hand" 死路。
  `FontPack.EnsureSevenZip` 收敛为同一个实现，字体包（含 Rar5）与 FFmpeg 归档共用。

### 2. FFmpeg 工具链：5 个源，daily 优先

* 新增 `ytdlp`（yt-dlp/FFmpeg-Builds，Win+Linux，`latest` daily）与
  `autobuild`（AnimMouse，每次 commit 一个带哈希的 tag）。`btbn` / `nonfree` / `gyan`
  / `avbuild` 全部保留，旧配置不受影响。
* **AnimMouse 实测只有 win32/win64**（翻查最近 30 个 release 均无 Linux 资产），因此只在
  Windows 提供；其资产名含提交哈希，新增 `FfmpegDist.FeedUrl` + `AssetPattern` 与
  `FFmpeg/ReleaseFeed.cs`，安装时读 `releases/latest` 的 `assets[]` 正则选取。
* 「daily 优先、无 daily 才用稳定版」：`FfmpegDist.StableUrl` 作为回退字段；当前五个源
  都有 rolling 构建（实测 `latest` 标签的发布时间与资产名），正常路径不触发回退。
* 新增 `docs/toolchain-builds.md`：五个源的 release 标签/资产/平台/归档格式对照、
  **两端 `ffmpeg -encoders` 实测编解码器表**（Windows nonfree N-128412 vs Linux btbn
  GPL N-128532，例如 `libfdk_aac` 仅前者有）、构建定位差异、自行核对命令、7-Zip 统一方案。

### 3. 时代预设更正（不看视频偏信，先做元数据取证）

* 用 `ffprobe -show_format/-show_streams`、`MediaInfo --Output=JSON`、opencv 抽帧扫描
  `D:\VideoHaners\EraPresents-Original-Source` 的 20 份素材，并结合当年网页/博客/论坛。
* **元数据决定了谁能当证据**：`2004年9月20日新闻.wmv` 的 `title/copyright = cctv.com`、
  `creation_time = 2004-09-20`，是 A 级原始；而 `cctv-2003` 那两份 `《新闻调查》` 的
  `creation_time` 都是 **2006-04-20**（后期批量转制），`youku-2008` 是 2010 + GPAC，
  2013 的 MP4 用 `Lavf60.10.100`，`youku-early` / `ku6-2005` 的 FLV 编码库是
  `x264 core 54/56`（H.264+AAC，Flash 9 之后）——**全部降为 B 级，不采信其编码格式**。
  cctv 的 FLV 则用 `LAME3.97/3.98`，与 2007–2009 年份相符，抽帧可见 CCTV.com 水印。
* 史料交叉：央视网 2003《视频点播说明》（只有 Real + Windows Media，无 FLV）、2006
  《走过 CCTV.com 的第一个十年》（2004 直播改 28K/56K/128K 自适应）、Cash Chou 博客
  （FLV1=Sorenson H.263，Flash 8=VP6，Flash 9 才 H.264+AAC）、博客园/ChinaUnix/CSDN
  （站点 FLV 320×240、12–15fps、<300 Kbps）、论坛实测帖（自动转 320×240、295 Kbps）。
* 更正 3 行：`cctv/2003` → 290k / 11.025kHz；`cctv/2006` → 320x240@15 / 300k /
  11.025kHz stereo；`youku/2007` → 320x240@15 / 280k。交付编码器仍是 `flv`+`libmp3lame`。
  完整可信度分级、更正清单与「哪些没改、为什么」见 `docs/era-presets.md`。
* 同步更新 5 个硬编码数值的单测断言（`EraAspectTests`、`BuildProfileTests`、
  `EncoderRateAndContainerTests`）。

---

## v7.9.8.13（2026-11-07）

字幕工作流：字体包与按素材名自动找字幕。

### 1. 字体包（.zip / .7z / .rar）

* 以前一次只能 `--font-import` 一个字体文件或目录；发布会随片附带一整个
  `Fonts.rar`（实测 `Rar5`，13 个字体、解压约 123 MB），只能自己先解开。
* 新增 `--font-pack FILE`：用 **7-Zip** 解压（唯一同时支持这三种格式、含 Rar5 的工具），
  枚举其中 `.ttf/.otf/.ttc/.otc`，再按字幕里 ASS `Style:` 的 `Fontname` 列筛出需要的字体，
  拷进程序自己的 `fonts/` 目录（libass 读的那一处）。
  * **系统没有 7-Zip** 时自动下载官方安装器
    `https://github.com/ip7z/7zip/releases/download/26.05/7z2605-x64.exe`，按 7-Zip FAQ 的
    `/S`（静默）+ `/D=dir`（目标目录，大小写敏感）装到用户可写目录，不需要提权；
    已装过一次就直接复用。
  * `.zip` 在 7-Zip 装不上时（如 Linux 未装 p7zip）回退到内置 ZIP 读取器；`.7z` / `.rar`
    仍明确要求外部工具。解压后只接受真正落在暂存目录下的文件，防 `../` 穿越。
* 「根据 ASS 字体建议或按个人喜好」：交互式会列出包内字体编号，留空 = 按 ASS 建议，
  `*` = 全选，`1,3` = 自选。字体名归一化后比较（`FOT-Matisse ProN B` ↔
  `FOT-MatisseProN-B.otf` 都是 `fotmatissepronb`），实测能命中真实发布包的命名。
* CUI 的「字幕 → 字体」子菜单新增第 4 项「导入字体包」。

### 2. 按素材名自动找字幕

* 新增 `--auto-subtitle`：在输入同目录找 `<视频名>.<语言>.ass/.ssa/.srt/.vtt`
  与 `<视频名>.ass` 等（实测 `X.mkv` → `X.sc.ass` / `X.tc.ass`）。匹配刻意收窄为
  **同名前缀 + 点号**，不会把隔壁集数 `X[06].ass` 挂到 `X[05].mkv` 上；ASS/SSA 优先
  （只有它们带字体名），再按名字稳定排序。
* 找到多个 → 交互按编号选（非交互取排名第一）；**一个都没找到 → 提示用户手动输入路径**，
  输入不存在的文件会明确报错退出 2，留空则本次不带字幕继续。
* `--font-pack` 隐含启用自动字幕发现（不知道字幕就无从谈起「它建议的字体」）。
* CUI 的「字幕」菜单新增第 7 项「按素材名自动查找字幕」。

### 3. 配置文件

* 两个功能都是**一次性命令行/菜单选择**，不新增任何持久化配置字段，也不会改写
  `UserConfig` 的结构；字体的落点仍是原有的 `fonts/` 目录（`LAE_FONT_DIR` 可重定向）。
  测试全程用 `LAE_CONFIG_DIR` 隔离。

### 验证

* 新增 `FontPackTests`（同名发现、邻集不误配、真实 ASS/字体文件名归一化、选择语法、
  三种归档扩展名）与 `tests/e2e/test_font_pack.py`（造 1 秒素材 + `.sc.ass` + `.zip`
  字体包，断言只导入 ASS 点名的那个字体）。
* 官方依据：7-Zip FAQ 的静默安装参数；`Fonts.rar` 与用户 `.sc.ass` 的实测内容。

---

## v7.9.8.12（2026-11-07）

修 5 个使用中暴露的问题。

### 1. rm 容器默认音频改回 ac3

* 用户产物实测（rv20 + ra_144，282 s）：**所有音频包 PTS/DTS 恒为 0**，音频流
  `duration=0.000000`；二次压缩后两个 MP4 的音频都只有 0.355 s，ffplay 以音频为主时钟
  因而无法拖动进度条，"没有声音"也由此而来。
* A/B 实测确认与 `-af asetpts` 无关：无 filter / `PTS-STARTPTS` / `N/SR/TB` 三种写法下
  real_144 的 RM 包时间戳全是 0。FFmpeg Doxygen `ra144enc.c:529` 只从
  `ff_af_queue_remove` 取 pts，`rmenc.c:94-116` 的 duration 取各流最大时间戳。
* 修复：`Constants.DefaultAudioByContainer["rm"]` 与 `AudioCatalog.ContainerCodecs["rm"]`
  回到 `ac3 → real_144 → aac`；`real_144` 仍可显式选择。同步更新 `RealMediaTests`、
  e2e（新增音频时长断言），并删除 `test_compare_matrix` 里 rv20 的基线例外。

### 2. 自更新按 flavour 选资产

* 现象：self-contained 安装检测到新版后下载了 framework-dependent 包，覆盖后报
  `You must install or update .NET to run this application`。
* 根因：`AssetSuffix()` 只用 `RuntimeFeature.IsDynamicCodeSupported` 分 AOT/JIT；
  self-contained JIT 仍支持动态代码，于是选中 `-win-x64.zip`。
* 修复：新增 `IsSelfContained()` —— 应用目录含 `coreclr.dll` / `libcoreclr.so` /
  `System.Private.CoreLib.dll` 即自包含（实测 FDD 目录只有 4 个文件、SCD 目录带运行时）。
  `AssetSuffix()` 现在输出 `-aot` / `-self-contained` / 无后缀三档；裸 `EndsWith` 在
  新命名下仍互斥。`Flavour()` 同步显示。

### 3. rv10/rv20 尺寸

* FFmpeg Doxygen `rv10enc.c:69`：`(width | height) & 15` 直接报
  "width and height must be a multiple of 16"，rv20 共用该 init。
* 修复：`EncoderMath.NormalizeDimensions` 对 rv10/rv20 复用 AMV 的 16 像素网格吸附
  （320×180 → 320×176，沿用已有的尺寸调整提示）。新增单测与 e2e。

### 4. 输出被播放器占用

* 现象：ffplay 打开目标后编码完成，`File.Move(overwrite)` 抛
  `IO_SharingViolation_NoFileName`，长时间实时录制只能留在 `.part`。
* 修复：`OutputFile.Promote` 先做 4 次短重试；仍冲突则自动另存为首个空闲的
  `name (2).ext` 并打印实际路径。目录占位用 `Directory.Exists` 排除，保持 v7.9.8.10
  "改名失败保留 part" 语义不变。

### 5. 两点实测澄清

* "first frame is no keyframe" / "Changing video frame properties on the fly"：二次压缩
  产物实测有 56 个关键帧、帧属性唯一（320×180 yuv420p left），并非缺关键帧或属性变化；
  进度条异常的根因是第 1 条的音频时间轴损坏。
* 二压到 RM 时 `RecommendedAudio("rv20")` 仍为 `real_144`（用户显式走该 codec），
  本次只改实时通路的默认值；修改了默认音频后，其输入 RM 已带正常音频时间轴。

---

## v7.9.8.11.1（2026-11-07）

修 mjpeg 使用中暴露的两个问题。

### 1. 音画不同步（开不开并发都一样）

* 实时通路里视频是 rawvideo 管道（时间戳按帧号从 0 生成），音频是**第二次读源文件**，
  自带容器时间戳、没有输入级时长上限、也没有 PTS 归一化。FFmpeg 官方手册把 `-shortest`
  定义为"Finish encoding when the shortest output stream ends"，并说明 `-fps_mode cfr`
  的输出时间戳"may be further modified by the muxer"。
* 修复：音频输入加 `-t <duration>`（与视频管道同跨度，避免 pipe 结束后留下纯声音尾巴）；
  输出音频流加 `-af asetpts=PTS-STARTPTS`（把 seek / 容器偏移归零）；
  `--audio-mode separate/auto` 的合流加 `-shortest`。预检同步携带同一组参数。
* 顺带修 `CanSplitMux` 探测漏掉 `-pix_fmt yuvj420p` / `-color_range pc`：mjpeg 会在这次
  探测里因 full-range 检查失败，使 `--audio-mode auto/separate` 静默退回 inline。
* 本地理想素材（含带 0.7 s 音频偏移的合成源）未复现可测偏移，因此新增 e2e 钉住
  inline / auto / parallel 三种模式的首包时刻与流时长差，作为回归基线。

### 2. 码率设置与 `-q:v` 不生效、fps 暴跌

* **量化实测**（256×192 yuvj420p）：q:v 2 / 6 / 16 / 31 约 10465 / 6616 / 3866 / 2548 B 每帧；
  13 fps 下 688~265 kbps，连最差画质的 q:v 31 都达不到默认 `--limit 200`。q:v 6 降到
  3 fps 仍有 159 kbps > `--recover 150`，阶梯一旦到底就回不来（旧测试报告
  `fresh=24 repeat=41` 即此现象）。
* 根因：`VideoQualityArgs` 的 Qv 分支只返回 `-q:v`，`limitKbps` 从未变成任何 FFmpeg 参数；
  FFmpeg 的 MJPEG 编码器（Doxygen `mjpegenc.c` / `mjpegenc_common.c`）只有固定量化器、
  没有 CBR/VBV，`--limit` 对它只是降档阈值而不是编码器上限。
* 修复：新增 `IntraQuality` —— 编码前用真实源的 4 帧试编码二分标定 q:v（用户显式 `--qv`
  时只测量、不覆盖），把 `--limit` 落到量化器；再把阶梯上限抬到物理可达码率的 1.15 倍，
  并让已标定的 intra-only 编码不因小窗口码率估算误降帧率（保留 3 倍上限的安全阀；
  严重超标时按每帧字节数选最浅可达档，无解则不降）。
* 实测（5 s / 13 fps / 默认参数）：`Fresh frames` 22 → **60 / 65 槽**，`motion` 3fps → **13fps**，
  视频平均码率 688 → **300 kbps**，不再 THROTTLED。
* **⚠ 行为变化**：mjpeg 未显式给 `--qv` 时，量化器由 `--limit` 标定；显式值仍完全生效，
  只是不再触发无意义的帧率阶梯。

---

## v7.9.8.11（2026-11-06）

修 `--encoder mjpeg` 在预检阶段直接失败，并清理文档。

### 1. MJPEG 必须写 full-range 画面

* **现象**：用 MPEG-2 录像源（`tv` 色彩范围）编码成 `.avi` / mjpeg 时，
  `Video preflight failed for mjpeg`，FFmpeg 报 `Non full-range YUV is non-standard,
  set strict_std_compliance to at most unofficial to use it.`，随后
  `ff_frame_thread_encoder_init failed`。
* **根因**：已对 FFmpeg 官方源码核实，不依赖某个构建的行为推断。
  `libavcodec/mjpegenc_common.c:569` 的 `ff_mjpeg_encode_check_pix_fmt()` 在
  `strict_std_compliance > FF_COMPLIANCE_UNOFFICIAL` 且 `color_range != AVCOL_RANGE_JPEG`，
  并且（`pix_fmt` 是 `yuv420p` / `yuv422p` / `yuv444p`，**或** `color_range == AVCOL_RANGE_MPEG`）
  时返回 `EINVAL`；`mjpegenc.c:552` 的 `mjpeg_encode_init` 调用它，失败即连锁报帧线程初始化失败。
  `mjpegenc.c:666` 的 `mjpeg_get_supported_config` 说明默认严格度只暴露 `AVCOL_RANGE_JPEG`，
  limited range 要降到 `unofficial` 才可用；`mjpegenc.c:699` 虽声明六种像素格式，后三种属于非标准扩展。
* **修复**：实时编码和预检都显式写 `-pix_fmt yuvj420p`（按色度族对应 `yuvj422p` / `yuvj444p`），
  并把该编码器的 `-color_range` 钉为 `pc`。由 swscale 做真正的 tv→pc 转换，不依赖 FFmpeg 的
  自动协商，也不传 `-strict`（本工具从不降级标准符合度）。二次压缩路径的 mjpeg 一直是这么做的。
* **⚠ 行为变化**：`--encoder mjpeg` 与 `--color-range tv` / `--pix-fmt yuv420p` 同时给出时，
  输出仍是 full-range JPEG；这是编码器的硬性要求。
* **回归**：`MjpegPixelFormatTests`（参数与色彩标签）、`tests/e2e/test_mjpeg_range.py`（真实编码后 ffprobe 校验）。

### 2. 下载 FFmpeg 后 shell 里找不到 ffmpeg / ffprobe

* **现象**：`--ffmpeg-download` 装好的构建只对工具自身的探测可见；新开的 cmd /
  PowerShell / bash 里没有 `ffmpeg`。根因是安装只落在私有目录
  （`%LOCALAPPDATA%` / `~/.local/share`）下，从未写进 shell 的 `PATH`。
* **修复**：安装成功后发布该 `bin` 目录 —— Windows 写用户级 `PATH` 并广播
  `WM_SETTINGCHANGE`；Linux 幂等写入 `~/.profile`（及已有的 `.bashrc` / `.zshrc`）的
  标记块，并在 `~/.local/bin` 建符号链接。新开终端直接可用，重复安装不堆叠条目。
* 子进程改不了父 shell 的环境，因此新增 `--ffmpeg-env [SHELL]`，打印当前终端可粘贴或
  求值的一行（`powershell` / `cmd` / `bash` / `zsh` / `fish`，裸用自动判断）；安装结束时也会打印。
  卸载（`--ffmpeg-delete`）撤销这些改动。
* **回归**：`ToolchainPathTests`（PATH 幂等、标记块替换、五种 shell 的激活行），
  测试只跑纯文本变换，不碰真实用户环境。
* 下载源统一指向两个 GitHub 项目 `latest` 标签的每日构建：`btbn/ffmpeg-builds`（GPL）与
  `BnqDzj/FFmpeg-Builds-nonfree`，两端资产齐全，URL 写法一致；其余来源仅为旧配置兼容保留。

### 3. 文档

* `README.md` 删除开头堆叠的八段版本公告与"Changes since the port"的逐版复述（共 131 行），
  版本历史只保留 `docs/CHANGELOG.md` 一处。
* 删除 `usage.md`、`requirements.md` 与本文件中指向另一条开发线的失效描述。

---

## v7.9.8.10（2026-11-04）

一次全项目审查加清理，**没有新功能**。五路只读审查（正确性、资源与生命周期、
CLI/CUI 与文档契约、死代码、测试有效性）逐条复核后，修的都是"绿着但其实坏了"
或"只在老用户/长任务上暴露"的那一类。

### 1. CUI 选编码器时吞掉异常，还跳过半段同步（P0）

`encoder.containers` 模板要两个参数，`Cui.cs` 只传了一个；`string.Format` 抛
`FormatException`，被 `ChooseEncoder` 的 catch 打成"Invalid selection"。而此时
`c.Encoder` 已经换掉，**后面的 fps、尺寸、质量、音频、默认输出路径全部没跑**——
菜单显示新编码器，配置却是旧编码器的。`audio.replaced` 是同一个错误（三个槽传两个）。
两处都补齐，并加了一条参数化断言：两种语言里每个带占位符的模板都必须能用自己的
参数格式化，且参数不被吞。还补了一条 CUI e2e，直接钉住"选编码器后不能出现
Invalid selection、必须打印容器列表"。

### 2. 编码成功但改名失败时，唯一的好成品被删掉（P0）

目标被播放器占用、共享盘拒绝替换、路径过长——这些只会让 `OutputFile.Promote` 失败，
编码其实已经完整结束。三处调用方（inline / split / 分片）的 catch 一律 `Discard(part)`，
于是几十分钟的实时录制被删得只剩一句"could not put ... in place"。现在用
`finished` 标志把"编码失败"与"改名失败"分开：后者保留 part、打印路径让用户手工改名
（`OutputFile.Keep`）。二次压缩那条同样修了，`test_realmedia.py` 的断言从
"不许留 .part" 改成"必须留下非空的 part 并告知路径"。

顺带把 `.part` 名加上进程 ID + 随机标签：固定的 `clip.part.mp4` 可能正好是用户
自己的成品，而 Windows 上只有大小写不同的两个输出会共用同一个 part 互相截断。
分片合并从 `%TEMP%` 跨卷搬到目标目录时 `File.Move` 也加了 copy 回退。

### 3. 自更新的三条信任边界（P0）

- `LAE_UPDATE_TOKEN` 以前会发给 `--update-feed` 指定的**任意主机**；现在只有
  GitHub 官方域名才带凭据。
- 资产 URL 现在只认 https（本地测试用的回环 http 保留），明文下载可执行二进制不再可能。
- **⚠ 行为变化**：发布没有 SHA-256 摘要时，以前"只校验大小"照样解压、跑
  `--version`、交出程序，而脚本还拿到 exit 0。现在直接 `Outcome.Failed`（exit 1）。
  size 和 URL 出自同一份不可信 JSON，不构成完整性证明。
- `TryReplace` 的注释声称"中途失败会留下完整的旧版本"，实现却相反：前 N-1 个文件
  已被覆盖且没有备份。现在先把每个目标复制进 staging 的 `.rollback`，任一文件失败
  就从副本恢复；同时不再只复制 `LiveArchiveEncoder*` 前缀文件。

### 4. 参数与命令的一致性

- `--era-native` 以前解析了却没人读，`--era-modern --era-native` 会反向生效；
  现在显式的 native 优先，编码与 `--recompress-only` 共用 `Cli.EraNativeOf`。
- `AudioCatalog.KbpsBand(codec, null)` 以前返回**最后**（最窄）那一档，与"最宽 band"
  的注释相反：`--audio-kbps 16` 配 `mp2`/`libvorbis` 被判非法，或悄悄改成 32。
  rate 未知时现在取所有 band 的并集；显式 rate 仍旧按窄档校验。
- CUI 应用 era 预设时没有调用 `AudioCatalog.Effective`，内置 rv20/rm 预设写下
  16000 Hz 而 `real_144` 只认 8000，点"开始编码"就拒绝**程序自带的预设**，CLI 却能跑。
- `--qv 999` / `-q:v 0` 以前只在 preflight 里以一句无关的 "Video preflight failed"
  晚失败。现在按 `EncoderMath.QualityRange` 在校验阶段报出区间；无质量刻度的
  `libopenh264` 跳过。
- `RealtimeOptions` 的音频码率现在把 snap 后的值写回，校验、preflight、inline 的
  `-b:a`、分离音轨、码率估算五处同源；preflight 也终于拿到 `--audio-stream` 选中的轨。
- preflight 只限了视频 3 帧，带音轨的长源会把整条音轨编完再撞 120 秒上限；
  现在加 `-t 0.5`。
- `Program.Main` 的顶层 catch 漏了 `IOException` 本身（只列了 `FileNotFoundException`），
  磁盘满/UNC 断开会变成带 stack trace 的崩溃。

### 5. 资源、进程与测试基础设施

- `Smb.cs` 重定向了 stderr 却从不读：PowerShell 写满 4 KB 管道就死锁，
  `WaitForExit(15000)` 超时后共享盘一律显示"未连接"。现在并发抽干两条管道并在
  查询失败时打印原因。
- `ProcessRunner` 超时/取消时丢弃两个读取 Task，FFmpeg 的最后一句诊断全丢，
  还会留下未观察异常；现在先 drain 再把已读到的输出附进异常。
- `tools/e2e.py` 把 pytest 的 exit 5（0 个用例）无条件当成功——marker 写错、
  模块改名、xdist 全挂都会打印"both phases green"而一个测试没跑。现在每个 phase
  先 `--collect-only` 核对收集数，只有用户显式传 `-k` 且确实 0 个用例才容忍。
- 4 个交互提示传了可用编码器清单却没在模板里显示，现在真的列出来。

### 6. 清理（零引用，逐项 grep 复核）

- 16 个 en/zh 成对残留的本地化 key、5 个零引用成员（`DefaultDither`、
  `ModernRecompressArgs`、`ForSite`、`RefreshEncoderCaps`、`NetworkProbe.Reset`）、
  8 个未使用的 using，以及随"无 digest 仍可更新"一起失效的 `DigestMissing` 字段、
  两处打印和 `update.digestmissing` 文案。
- `docs/runs/` 下 12 份历史 harness 输出（含 3 份字节级完全相同的错误日志）与
  `docs/parallel-split-verification.md`（结论推荐已被删除的 Turbo，引用不存在的
  `WaitUntil`）；`README.md` 的目录树、`archive/README.md` 的归档清单、
  `.slnx` 里悬空的 `Watermark.cs` 一并校正。
- 文档里照抄即失败的命令：`-o`（本程序没有这个旗标，输出是第二个位置参数）、
  `-args`、`--size`、`--list-encoders`；`--help` 补上一直没列的 `--cpu-info`；
  退出码表补 3、参数页补 32/33、单测数与编解码器清单对齐、GitHub CI 的
  `dotnet test` 改回 MTP 需要的 `--solution` 写法。

### 数

单测 **654**（653 通过 / 1 跳过）、零警告；完整 v7 e2e 绿。新增断言逐组做过
变异验证（把修复改回去必须变红并指名）。这一版只出依赖版（JIT），Linux 包仍
只做结构核对。

---

## v7.9.8.9（2026-11-04）

三个"默认值站错了地方"，都属于**不该让用户为程序的默认选择买单**。

### 1. 网络驱动器上的素材，默认不再把输出写回那条链路

`_live` 文件一直放在源文件旁边，对本地素材是对的。源在共享盘上时这条规则就变成了：
读和写挤同一条链路，几十 GB 的产物还要落在那台本来只负责共享的机器上。

现在源目录被判定为网络路径（UNC、映射盘符、Linux 的网络挂载）时，默认输出改到

```
%LOCALAPPDATA%\LiveArchiveEncoder\output\
```

（就是 `config.json` 所在的那个目录，启动横幅里一直印着它。）**路径照样可以改**：菜单里的
输出路径、命令行给的输出位置参数都还是原样，用户给了路径就再也不动它；不给时每次按规则重算。
程序会打印一行说明它去了哪里。

### 2. mp4 + H.264 的音频默认是 AAC

`libx264` / `libopenh264` 写 mp4 时，音频默认 AAC——这是 2010 年代上传、播放器、以及
二次压制一直用的那一对。以前的坑在"记住"上：2007 年的 FLV 预设会把 `libmp3lame` 写进设置，
而 **MP3 确实能封进 mp4**，所以没有任何规则会去替换它，之后每次 mp4 都是 mp3。

现在这条规则在三个"确定编码器/容器"的地方都会跑：启动载入设置、改编码器、改容器。
只在**默认**被改时生效（用户手工在音频行里选的编码器仍然算数），并且会说明换掉了什么。

### 3. 扩展名写错时自动更正，而不是拒绝

`out.rm` + `libx264` 以前在开跑前报 "Output extension '.rm' does not match selected muxer
'mp4'"（退出码 2）。用户要的是一个文件，不是一节 muxer 课。现在更正成编码器真正能写的
后缀，并打印 `'.rm' is not a container libx264 can write; the file is written as .mp4`。

更正出来的名字**不会覆盖没人指名的文件**：`out.mp4` 已存在就写 `out (2).mp4`；更正后正好
是源文件（`src.rm`，源是 `src.mp4`）时同样让位成 `src (2).mp4`。CLI 与 CUI 走同一条规则。

### 顺带

- CLI 跑完写回设置时，`outputIsAuto` 现在反映"这一次是不是用户给的路径"，以前一律写成
  `true`，于是手写的输出路径下次会被当成自动路径重算。

### 数

单测 **645**（644 通过 / 1 跳过），零警告。新增 6 组断言，全部做过变异验证。
真机验证：`out.rm` → `out.mp4`、`out.mp4` 已存在 → `out (2).mp4`、`src.rm` → `src (2).mp4`
（源文件字节数不变）；CUI 带一份 mp3 设置启动，横幅后打印换用 AAC，菜单第 6 行显示 aac，
退出后设置里存的是 aac。

网络默认值那一条只有单测与代码审查——本机没有可用的共享盘可做真机验证。

---

## v7.9.8.8（2026-11-04）

主题是**把 OpenH264 的两个旋钮接上**：一个从来没被拧过（二次压制），一个一直拧错了方向
（码率模式）。产物格式没变，但用 `libopenh264` 的二次压制**体积会明显变小**——那是修好的
证据，不是退化。

### 1. 二次压制选 libopenh264 时，参数是空的

`RecompressCodecs.VideoArgs` 对"没有质量旗标"的编码器只在调用方给了码率时才写 `-b:v`，
而 CLI 只传质量号、不传码率。于是 `--recompress-codec libopenh264` 生成的命令是：

```
-c:v libopenh264 -pix_fmt yuv420p
```

没有码率、没有模式，OpenH264 落在自带的 2 Mbps 目标上。实测 640x480 testsrc2：要 300 kbps，
产出 **2002 kbps**，6.7 倍。用户看到的是"预设应用了"，拿到的是一个大七倍的文件。

修法：目录里的 `libopenh264` 补上 `-rc_mode bitrate` 和 `DefaultKbps = 300`；`VideoArgs`
对这类编码器**永远**写码率（显式值 → 目录默认值），不再裸奔。新增
`--recompress-video-kbps` 供显式指定。

同一类"静默吞掉"还修了两处：

- `--recompress-quality` 给一条**没有任何质量旗标**的链（`libopenh264`）现在**报错退出 2**，
  说明该编它的码率；以前那个数字被丢掉，命令看起来却像是生效了。
- `--recompress-video-kbps` 给一条只有 `-crf` 编码器的链同样报错，指出该用
  `--recompress-quality`。
- 两种编码器混在一条链上（`libx264` 出观看版、`libopenh264` 出站点版）是合法的写法，
  这时两种数字**各落到用得上的一遍**，对用不上的那遍打印一行 NOTE —— 报错会让这种链
  根本写不出来（一个数字没法跳过某一遍，写两遍也一样）。

### 2. OpenH264 的码率模式（`-rc_mode`）

以前无论什么情况都发 `-rc_mode bitrate`，用户无从选择。现在三个可用值：

| 模式 | 含义 | 实测（640x480，目标 300k） |
| --- | --- | --- |
| `bitrate`（默认） | 严格按码率 | 354 kbps |
| `quality` | 优先画质，`-b:v` 只作目标 | 362 kbps |
| `timestamp` | 按时间戳分配（实时/可变帧率） | 358 kbps |

`buffer` 和 `off` **被拒绝**，理由带实测数字：

- `buffer` 官方文档自己写着 "no bitrate control"，实测要 300 kbps 给 **3.1 Mbps**。
- `off` 把 QP 锁死在库内部的 26，而 FFmpeg 没有提供改它的入口（见下一条）。

CLI 是 `--openh264-rc <bitrate|quality|timestamp>`，CUI 在参数菜单 32/33 行（只在
`libopenh264` 时出现，别的编码器上那两行等于不存在）。

### 3. 为什么没有 QP：`-qp` 在这个编码器上不存在

网上流传 `-rc_mode off -qp 23` 的写法。查 `libavcodec/libopenh264enc.c`：选项表里没有
`qp`，`svc_encode_init` 也从没读过 `avctx->global_quality`。FFmpeg 会把这句话说得很清楚：

```
AVOption qp (Quantization parameter (luma)) has not been used for any stream.
```

`-qmin/-qmax` 倒是会被写进 `param.iMinQp/iMaxQp`，但在 `RC_OFF_MODE` 下不起作用——实测
qmin = qmax 取 10、26、51，产出**字节完全相同**（1840204 B）。所以这个编码器的"质量"
只能是码率的函数，`-rc_mode quality` 是它唯一接近质量控制的模式。

### 4. 可选跳帧

`-allow_skip_frames 1` 是码率真正落地的前提。源码 `param.bEnableFrameSkip = skip_frames`，
OpenH264 自己也在日志里说："bitrate can't be controlled ... without enabling skip frame"。
实测 300 kbps：不开 354 kbps，开 302 kbps。

默认**不开**：这个程序的帧率由自适应阶梯掌握，让编码器再偷偷丢一层，阶梯会看到一个
自己没造成的码率下降，原因就查不清了。要严格码率时用 `--openh264-skip-frames`。

### 5. 顺带

- `EncoderCaps.HasRcMode`：从 `ffmpeg -h encoder=NAME` 读 `-rc_mode` 是否存在。这个选项
  在源码里，但**官方手册的 libopenh264 一节没有记载**；探测到没有就不发，退回旧参数，
  而不是让整个运行死在 "Unrecognized option"。
- **`BuildProfile.SchemaVersion` 3 → 4**。`EncoderCaps` 多了一个字段，而它是**磁盘缓存的
  一部分**：旧缓存里没有 `HasRcMode`，反序列化出来就是 `false`，于是程序会认为所有人的
  FFmpeg 都不支持 `-rc_mode`，整个功能**只在升级过的机器上静默失效**。代价是升级后第一次
  运行要重新扫一遍 FFmpeg（四次进程启动）。
- 改正一条撒谎的注释：`ArgsBuilder` 曾说 OpenH264 "reads neither `-maxrate` nor
  `-bufsize`"。实际是 `iMaxBitrate = FFMAX(rc_max_rate, bit_rate)`——它读，只是当
  `maxrate == bitrate` 时两者相同，那个旗标没有信息量。
- `--openh264-rc` / `--openh264-skip-frames` 只对 `libopenh264` 有意义，给了别的编码器
  现在**报错退出 2**（`--recompress-only` 这种没有直播编码器的路径也一样），不再静默忽略。
- CUI 第二遍的第 6 行：`libopenh264` 时标题变成"码率（kbps）"、问的是码率，改音频编码器
  这类无关操作也不会再把已设的码率抹回默认值。

### 数

单测 **639**（638 通过 / 1 跳过），零警告。本轮新增的 7 组断言各做了一次变异验证
（缓存 schema、混合链、码率保留、目录默认值、0 码率、旗标守卫），每一组都变红并指名；
上一轮那 11 条同法验过。真素材（`[晚街与灯][Re Zero 4th - 04]` 20 秒片段）：默认 300k
实测 346 kbps、显式 700k 实测 622 kbps，两条拒绝路径均 exit 2。

---

## v7.9.8.7（2026-11-03）

两件事，都是"以前根本用不了"：**检查更新永远是失败的**，**PGS 内封字幕不能烧**。

### 检查更新

`SelfUpdate.Repo` 写的是 `fr0yo0v0/LiveArchiveEncoder`——仓库原先所在的账号。那个账号被封，
API 对它下面的一切都返回 404，于是每次检查都拿不到 JSON，用户看到的是一条看不出原因的失败。
仓库现在在 `FroyoCatqwq/LiveArchiveEncoder`，常量改过去。

**⚠ 从 v7.9.8.6 升级要手动下载一次**：那一版里的常量还是旧账号，它自己看不到这一版。

### PGS / 图形字幕烧录

`Constants.BitmapSubtitleCodecs` 里**没有 `hdmv_pgs_subtitle`**。所以 PGS 轨根本没被认成
图形字幕，规划器按文本字幕去拆包（`-c:s ass`），FFmpeg 拒绝，报出来的是：

```
ERROR: could not extract subtitle stream 2 (hdmv_pgs_subtitle) from the source;
use --subtitle-mode copy to carry it instead, or --subtitle with a file
```

听起来像文件坏了，其实是这个构建不认识的编码。补上 `hdmv_pgs_subtitle` / `pgssub` 之后，
图形字幕走 overlay：

```
[0:v][0:2]overlay=format=auto:eof_action=pass[vmain];[vmain]<scale/crop/fps chain>[vout]
```

- **不拆包**：`[0:<index>]` 直接进 filter graph，少一次进程、少一个 30 MB 的 `.sup`
  临时文件；只读共享目录上也不会因为写不出来而在编码前失败。
- **overlay 在缩放链最前面**：字幕图是全屏 RGBA、与源视频同尺寸，先按原始分辨率合成，
  再让整幅画面走 scale/crop/fps——字幕跟着同一个变换走，位置不用第二套偏移去对。
- `-ss` 起点时字幕跟着源一起 seek，时间戳天然对齐，不需要文本字幕那套 `setpts` 位移。

实测 `[晚街与灯][Re Zero 4th - 04]`（hdmv_pgs_subtitle ×2），256x192 输出，顶部歌词与
底部对白位置、时间都正确。

### 数

单测 **618**（617 通过 / 1 跳过），零警告。

---

## v7.9.8.6（2026-10-28）

第九个补丁号，主题是**把"能跑"变成"跑得住"**：全量过一遍代码，修掉 15 处会崩、会丢
配置、会留下孤儿进程或在取消后仍然写成品的问题；补全脚本从手抄改成从一张表生成；把
程序里几句会让人误解的话改成实话。

产物格式没有改。⚠ 标出的两条会改变用户看到的东西。

### 1. 一次命令行编码会清空菜单里的配置（⚠ 数据丢失）

`Cli.Run` 从零构造 `Config`，跑完再 `Capture` 回写设置文件 —— 而它没赋值的那些字段
（二次压缩链、字幕块、GitHub 前沿、NNEDI3 权重路径、自动更新开关、工具链来源、播放器）
用的是**默认值**，于是"跑一次 `--encoder mpeg4 in.rm out.avi`"就把菜单里配好的东西
换成了默认值。二次压缩链里的多跳 `Extra` 尤其惨：菜单里加了三跳，编码一次就全没了。

现在 rule 是"这一次运行只拥有它说过的字段"：`CliOptions` 新增 `FfmpegSourceExplicit`
/ `PlayExplicit` 与两个按内容判断的 `TouchedRecompress` / `TouchedSubtitles`，
`MergeStoredPreferences` 逐字段决定用命令行的还是文件里的。`--no-gh-proxy`（存 `""`）
和"没提这件事"（`null`）仍然是两件事。

### 2. 补全脚本落后解析器 43 个选项

`Completion` 自己维护一份选项表，`CliOptions` 的 `switch` 是另一份。数了一下：解析器
认 **112** 种写法，补全只认 **69**。v7.9.8.2 之后加的东西基本都没进去 —— `--era-aspect`、
`--h264`、`--pix-fmt`、整块颜色参数、`--update`/`--update-feed`、两个 NNEDI3 开关、
`--subtitle-aspect`，在四个 shell 里都补不出任何东西。

现在唯一来源是 `UI/OptionCatalog.cs`（`Flag` / `Value` / `File` 三种，附可选值），
`Completion` 只负责排版。两条新测试直接**读 `CliOptions.cs` 的源码**核对两个方向：
解析器有而表里没有 → 红；表里有而解析器不认 → 也红。

顺带发现 fish 有个真问题：它没有"没匹配上就列所有选项"的兜底，所以**纯文本取值的选项
名字根本不会出现**在生成的脚本里（`--threads`、`--net-test` 之类）。现在 fish 先把每个
选项名登记一遍，再补取值列表。

### 3. 菜单里打错一个字，整个进程带堆栈退出

`Cui.ReadInt` / `ReadDouble` 用的是裸 `int.Parse` / `double.Parse`，而菜单 `switch`
外面没有任何 catch —— 在"并行分片"或"自定义时代预设"里输入 `abc`、`3,5`、`15fps`，
得到的是 `Unhandled exception. System.FormatException` 和一堆堆栈，且本次改动全部丢失
（`Persist` 在 case 末尾，没走到）。现在两个读取器都改成 `TryParse` + 一行
`无效的值：…`，回车照旧沿用默认值。

### 4. "已重新选择"其实什么都没选（⚠ 产物变化：修好了没声音的片子）

第 4 菜单换容器时，如果当前音频编码器不适合新容器，提示语是
`'{0}' does not fit the new container '{1}'; picking another.`，而代码只做了
`c.AudioEncoder = null` —— 于是产出一个**没有音轨**的视频，界面上那一行显示
`(none)`，用户多半以为是显示问题。现在真的挑一个能封进新容器的编码器
（`EncoderMath.ChooseAudioEncoder`），一个都挑不出来时才说 `audio.none`。

### 5. 界面上出现过一行 `sub.fonts.missing`

`Strings.cs` 里根本没有这个键，而 `L.T` 的既定行为是"缺键就把键名当文案返回"。字幕 →
字体 → 检查字体，只要某个字体没装，屏幕上就是那串点号。两张表都补了文案。

新测试 `EveryKeyTheSourceAsksForExists` 扫源码里所有 `L.T("…")` 字面量，要求每个键在
每种语言里都存在 —— 原有的两条测试只比对两张表**互相**是否齐，**两边都缺**的键能一路
绿灯，这次的 bug 正是这么溜过去的。

### 6. 时代预设菜单把序号印出来，却不接受序号

列表按 `1. 2. 3.` 编号，输入 `3` 得到 `unknown era preset '3'`（`Find` 只认 id，站点
名匹配也不成立），而下一层的年份列表**是**接受序号的。现在上层也认序号。

### 7. 两处 Option 克隆漏字段 → 文档化的组合直接不可用

分片管线与"视频/音频分离"管线都手写了一整块 `new RealtimeOptions { … }`，其中
**都没有 `NnediWeights`**，于是 `--deinterlace nnedi --parallel 4`（以及
`--deinterlace nnedi --audio-mode separate`）会在每个分片里抛"needs its NNEDI3
weights file"——反过来指责用户没装权重。这已经是同一个坑的第二次（v7.9.6 漏的是每档
码率阈值）。

根因是"复制靠人抄"，所以 `RealtimeOptions` 改成了 `record`，两处改成 `with` 表达式：
以后加字段不用再记得补任何一处。分离管线的视频阶段显式写 `Parallel = 1`，因为分片发生
在上一层（`Run` 已经把 `--parallel N` 交给 `ParallelEncoder`），照抄会把每个分片再切一次。

### 8. 编码器卡住时 Ctrl+C 无效，两个 FFmpeg 变孤儿

写帧是同步 `encoderStdin.Write`，管道满（网络目标卡住、muxer 死锁）就永久阻塞；而取消
只在循环头检查，于是回不去循环头，`finally` 里的 kill 也永远不执行。现在写帧带
**30 秒**截止（`WriteAsync` + `WaitAsync`），超时按"编码器不再收帧"报错并走正常清理；
取消则原样抛 `OperationCanceledException`。

同一方法里还有两处：

- **排空阶段**用 `WaitForExit(2×时长 + 60s)` 一次等到底 —— 一小时片子的最后两小时里
  Ctrl+C 没人理。改成 250 ms 切片等待。
- `HighResolutionTimer.Enable()` 写在 `try` 外面，编码器启动失败就漏一次计时器引用；
  而 `Process.Start` 失败时**已经起来的解码器**没人 kill（`Dispose` 不会杀子进程），
  网络源的那条连接会挂到进程结束。现在 `Enable()` 在 `try` 内第一行，编码器启动失败
  先杀解码器再抛。

### 9. 取消之后仍然把成品写到最终文件名

分片管线的 `join` 与最终 `remux` 调 `ProcessRunner.Run` 时没传 `cancellationToken`，
取消后这两步照跑（最长 `2 × 片段时长`），跑完继续 `Promote` —— 一个被取消的任务把成品
落到了最终名字上。两处都传了 token；`Parallel.For` 里的
`catch (Exception)` 也补了 OCE 前置分支，取消不再被报成
`shard encoding failed: … The operation was canceled.`。

### 10. 解码器中途死掉时，真正的诊断被一条无意义的异常盖掉

`decoderDiagnostics` 由 stderr 读线程加锁追加，主循环里 `TailLines(...)` 却无锁复制。
解码器非零退出的瞬间正好是高频 `Add`/`RemoveAt`，这时 `[.. list]` 可能抛
"集合已修改"，把"到底是哪一行 ffmpeg 报了错"换成一句和问题无关的话。读取处加锁。

### 11. 其余四处

| 位置 | 问题 | 修法 |
| --- | --- | --- |
| `FrameBufferPool.Release` | 防重复归还的判断是 `_freeCount >= _free.Length`，只在**池全空闲**时成立——而那正是重复归还算不上事的状态；还有缓冲借出时，第二次归还会让同一块被两个写者拿到，画面静默错乱 | 增加真实状态位 `_borrowed[]`；顺带把取帧时每帧一次的 `new int[]` 去掉（类注释本来承诺"构造后不再分配"） |
| `RealtimeEncoder` 字幕暂存 | `BuildDecoderCommand` / `BuildEncoderCommand` 抛错时，为内嵌字幕建的临时目录不会被清理 | 两个 builder 外面加 guard，与 preflight 那段一致 |
| `Interrupt` | `_source` 的"检查-后-替换"没有同步，第二次 Ctrl+C 可能落在窗口里被丢掉 | `Lock` 包住这对字段，`Cancel()` 放在锁外（`System.Threading.Lock` 不可重入） |
| `--help` / 报告 / 横幅 | 三处写着"损坏由 `frame_damage.py` 处理"，那个脚本既不在仓库也不在任何发布的包里 | 改成实话：本工具只做编码，损坏效果属于另一个独立项目 |

### 12. 使用前提示

新增一行 `note.realtime`，在编码开始前打印（`--quiet` 时不打）：

```
注意：实时节奏——编码按素材本身的时长跑，10 分钟的片段就要等约 10 分钟
（--parallel N 能缩短，代价是分片）。输出在通过校验前，文件名里一直带 '.part'。
```

这是第一次用这个工具的人**必然**会误解的两件事：以为进程卡死，以及以为 `.part` 是失败
产物。

### 语法与特性

代码库在 v7.8.1 已经做过一轮现代化（`FrozenDictionary`、`[GeneratedRegex]`、
`System.Threading.Lock`、集合表达式、`IsNullOrWhiteSpace` 的替代扩展），这次只补三处
**有实收益**的，不为好看改结构：

- `RealtimeOptions` → `record` + `with`（第 7 条，同时根治一类 bug）；
- 写帧的 `WriteAsync(...).AsTask().WaitAsync(TimeSpan)`（第 8 条）；
- 新测试里的 `[GeneratedRegex]`（AOT 友好，不在运行时编正则）。

### 验证

- 单测 **618（617 过 / 1 跳过＝本机无字体）**，新增 25 条：补全目录 11 条（含两条源码
  扫描）、配置合并 5 条、本地化键扫描 1 条等。
- e2e **218 通过 / 2 跳过 / 0 失败**（phase 1 独占 28、phase 2 并行 190；跳过的是本机
  FFmpeg 没有 `.a64` 解复用器）。
- 断言口径：补全与本地化这两组测试都直接读**源码文本**，所以它们验证的是"代码与表一致"，
  不是"手写的期望值恰好相等"。
- e2e 顺带修了一处脏：这套测试原来把设置文件写进开发机真实的
  `%LOCALAPPDATA%\LiveArchiveEncoder\config.json`。本版开始程序会**读**这个文件，于是
  一个测试存下的二次压缩链会被后一个测试继承（本次先出现的 5 个失败就是这么来的）。现在
  整套 e2e 跑在自己的 `LAE_CONFIG_DIR` 里。

---

## v7.9.8.5（2026-10-28）

第八个补丁号，只做一件事：**程序能更新自己**。顺带补上它需要的那个版本号，以及开发
过程中自己踩出来的三处坑。

### 1. 自更新（`--update` / `--update-check` / `--version`）

以前更新是三件手工活：盯 release、找对平台那个包、解压覆盖。现在一条命令。

```powershell
LiveArchiveEncoder --update-check    # 0 已最新 / 3 有新版 / 2 读不到
LiveArchiveEncoder --update          # 下载 → 校验 → 就地替换（终端里还会重启）
LiveArchiveEncoder --version         # 7.9.8.5
```

替换一个**正在运行**的可执行文件，做错就是"程序没了"，所以顺序是死的：

- **哈希**：GitHub 上传资产时记的 `digest: sha256:...` 逐字节核对（老发布没有这个
  字段时退化成只核对大小并打 NOTE）。对不上就删文件、报错，安装目录一个字节不动。
- **跑一遍**：解压后先执行下载到的 `--version`，要求能跑、且不比当前旧。这一步拦的
  是哈希拦不住的东西——架构不对、JIT 包装进没有 .NET 的机器、包里不是可执行文件。
- **能写才动手**：在程序目录写探针文件；写不进去（`Program Files`、别人的目录）在
  下载之前就说清楚。

**换文件必须是第二个进程。** Windows 不允许替换一个正被映射为可执行映像的文件，包括
自己。所以旧程序把新版本解到 `<程序目录>/.update-<tag>/`，启动**下载到的那一份**，
自己退出；那份新程序等旧进程退出后逐个文件落位（先 `name.new` 再 move 覆盖），需要
时把目标程序重新拉起来。中途失败留下的是完整的旧版本，不是两个版本的混合物。

配套的三件小事：

- 暂存目录替换成功后打 `.done`，下一次启动删掉（重试几秒等接管进程退干净）；没打
  标记的保留十分钟——那可能是另一个还在写的更新。
- **不留 `.old`**：一个包 4–10 MB，回退一个版本是"下载旧 tag"一条命令的事。
- 重启只在**交互终端**里做；管道里的运行不会把菜单吐进日志。

菜单 **21「更新程序」**：现在检查 / 下载安装 / 启动时自动检查（默认开）。启动时的检查
成功结果缓存 24 小时、预算 3 秒、失败一个字都不打印，而且只在 stdin 不是管道时才跑
——脚本化运行永远不会因为网络多等一秒。

`--update` 认 `--proxy` 与 `--gh-proxy`，语义和 FFmpeg 下载完全一样：没给前沿时先花
5 秒探测，不通就走 `gh-proxy.com`。`--update-feed` 指向非 GitHub 的源时不探测、不套
前沿（把镜像 URL 前面拼一个前缀只会把地址弄坏）。

### 2. 版本号终于有单一来源

以前版本号只活在界面横幅那句 `app.title` 里。**拿一句翻译过的话去解析数字**不叫版本
检查，所以 v7.9.8.5 起 `Constants.Version` 是唯一来源，横幅改成插值。发布时这个常量
和 tag 必须一起动——忘了改，程序会一直把自己当成有新版。

### 3. 开发中暴露、顺手修掉的三处

- **让父进程自己换文件**：第一版在启动接管进程之前就先替换了一遍，而它正锁着那些
  文件——`LiveArchiveEncoder.dll: UnauthorizedAccess_IODenied_NoPathName`。替换只能由
  接管进程做，父进程只保留"目录能不能写"的预检。
- **畸形 feed 直接崩**：`browser_download_url` 是相对路径时，`HttpClient` 抛
  `InvalidOperationException`，而 `Toolchain.DownloadFile` 的 catch 里没有它——程序
  带着未处理异常退出（0xE0434352），而不是报错。现在两处都补上：`SelfUpdate` 先验
  URL 是绝对 http(s)，`DownloadFile` 也接住 `InvalidOperationException`。
- **`.part` 名字让解压认不出类型**：下载暂存名带 `.part` 后缀，`Extract` 按扩展名判
  断，于是 `.zip.part` 被当成未知格式。改成校验通过后先改成正式名字再解压，暂存目录
  也顺带变得可读。
- **带 token 又走前沿会 404**（拿真发布页试出来的）：前沿是替你把请求重发一遍，
  `Authorization` 不跟过去。现在设了 `LAE_UPDATE_TOKEN` 就**自动绕过前沿**并说明一句；
  带 token 时下载也不走 aria2c——凭据只能进命令行参数，而同机每个进程都能读它。

### 4. 验证

- 单测 **593（592 过 / 1 跳过＝无字体）**，新增 `UpdateTests` 18 条：标签解析与比较
  （四段号、缺位补零、`7.9.10 > 7.9.9`、`-rc1` 后缀、垃圾输入）、feed 解析与资产挑选、
  恶意资产名拒绝、zip 目录穿越拒绝、`Capture` 与 `--emit-args` 都不丢 `AutoUpdateCheck`。
- e2e **218 通过 / 2 跳过**（无 `.a64` 解复用器；phase 1 独占 28 + phase 2 并行 190），
  新增 `test_update.py` 8 条：本地 HTTP 服务扮演发布页，覆盖"有新版/已最新/替换落位/
  整包哈希不符被拒/没有本平台资产被拒/接管重启/菜单开关/暂存目录清理"，全部不需要外网、
  不需要发版，也不碰测试用的那个二进制。
- 实跑：`--update-check` 走真实 GitHub（带 token）认出 `v7.9.8.4` 已是最新；本地 feed
  走完整条"下载 → SHA-256 → 解压 → `--version` 校验 → 接管 → 替换 → 清理"链路。

---

## v7.9.8.4（2026-10-27）

第七个补丁号。一条用户报障，一处补漏，外加两个自己踩出来的坑。

### 1. 字幕跟视频对不上时间（报障）

命令行给的是 `--start-time 00:22:45`，产物里的字幕却从 00:00 开始算——整条字幕轨
落在"22 分 45 秒之后"，也就是**一行都不会出现**。

原因在 FFmpeg 的两个事实之间：输入侧的 `-ss` 把画面**重新基准到 00:00:00**（这是它
该做的事，产物要从 0 开始播），而字幕文件里的时间戳仍然是"从素材开头算"。`subtitles`
滤镜拿帧的 PTS 去比对事件时间，于是 22:45 之前的帧对不上任何一行。

- 修法是在 `subtitles` 前面把时间轴抬回素材的时钟，后面再抬回来：
  `setpts=PTS+1365/TB,subtitles=...,setpts=PTS-1365/TB`。抬回来这一步不能省——
  `-t <时长>` 是按离开滤镜链的时间戳算的，留着 1365 秒的偏移会让编码在第一帧就结束。
- `--start-time 0`（或没给）时整条链**一个字节都不变**，老命令看到的命令行长完全一样。
- 字幕方框（`--subtitle-aspect`）走的是 `-filter_complex`，偏移跟着 `subtitles`
  进到 `crop` 之后的那条分支里，再在 overlay 之前抬回来，两路时间轴仍然对齐。
- `--subtitle-mode copy` 那条路本来就是对的：额外输入自己也带了 `-ss`，两边一起平移。

### 2. CUI 里终于能编"多遍压缩"了

v7.9.8.3 把二次压缩改成了链式（`--recompress-args` 可重复），命令行能用，**菜单不
能用**：`rc.extra.*` 那一批文案写好了，却没有任何代码接上去——菜单 15 只有一遍。

- 菜单 15 新增 `8. 追加压缩遍数（链式）`：列出现有的第 2..N 遍，输入一段参数=追问
  加一遍，输入编号=删掉那一遍，输入 `-`=全部清空。新增那遍的容器按它的编码器推导，
  和命令行的规则一致。
- 主菜单那行摘要改成 `on | 2 遍 | <第一遍参数>`，链有多长一眼能看出来。
- `4. 直接二次压缩一个文件` 以前**只跑第一遍**，把配好的后续几遍丢掉了；现在整条链
  都跑，逐条打印 `1/2 -> ...`、`2/2 -> ...`。

### 3. 命令行每个"遍"都能单独给质量和音频

`--recompress-quality` / `--recompress-audio-codec` / `--recompress-audio-kbps`
改成可重复、按下标与 `--recompress-codec` 配对；只写一次仍旧覆盖整条链（老写法
一个字符都不用改）。

这不是补充功能，是修一个刻度错误：`26` 对 libx264 是合理的 CRF，对 MPEG 家族是离谱
的量化器。一条 `libx264 -> flv` 的链共用一个数字，就是 v7.9.8.2 在时代预设里修过的
同一个病。

### 4. 标准输入结束后，交互界面不再空转

发现方式很不体面：给 CUI 喂的按键少了一个，它没退出，**几秒钟刷出 96 MB 菜单文本**。
`Console.ReadLine()` 在 EOF 返回 null，每个菜单都当成"空行"重画，于是死循环。

- 新增 `Ask()`：EOF 时按"0"那条路收尾——存配置、退出 0。菜单里的 42 处读取全部
  换成它；工具链子菜单单独判断。Ctrl+Z、以及任何少喂一个键的驱动脚本都走这里。

### 验证

- 单测 **575（574 过 / 1 跳过=无字体）**：新增 `SubtitleTrack.BurnInto` 三条
  （偏移存在、偏移为零时不动、方框图里也跟着走）、`CliParser` 两条
  （逐遍配对、每写一次 `--recompress-args` 多一遍）。
- e2e **210 通过 / 2 跳过**（无 `.a64` 解复用器）：字幕时间轴两条（从 00:00:08 切 6 秒，
  字幕线在 10-14 秒，断言输出 1 秒处黑、4 秒处有字；以及从 0 开始时不偏移）、逐遍质量
  一条、CUI 链式一条（菜单加完写进 `config.json` 的 `Extra`）。
- 实跑：`--start-time 00:00:08` 的字幕帧在输出 2.5 秒处（素材 10.36 秒）正确盖上；
  带方框的 16:9 画面同样正确；`--recompress` 两遍产出 `_web.mp4` 与 `_web2.flv`。

---

## v7.9.8.3（2026-10-27）

第六个补丁号，四件事：**反交错补上 NNEDI3**、**GitHub 下载能走前沿**、**二次压缩
变成多遍**、**字幕可以按 4:3 / 16:9 声明自己的画面**。

前三件都来自同一类现实：这条链路的目标是"像当年的站点那样产出文件"，而当年的站点
既不给你把 CDN 摆到国内，也不会只压你一遍；至于 NNEDI3，它是 2006 年以后做去隔行
的事实标准，FFmpeg 的 `nnedi` 滤镜只是没有权重文件就不能启动。

### 1. NNEDI3 权重（`--nnedi-update`）

`--deinterlace nnedi` 以前只用得上别人装好的权重，现在自己管：

- `--nnedi-update` 拉最新（`sekrit-twc/znedi3`，当前 `r3.3`，**13,574,928 字节**），
  `--nnedi-status` 打已装的版本/大小/sha256/安装时间，`--nnedi-weights PATH`
  指向别处的文件或整个解压目录。
- 安装位置 `%LOCALAPPDATA%\LiveArchiveEncoder\nnedi`（Linux 是 `~/.local/share/...`），
  旁边一份 `install.json` 记 tag、来源 URL、字节数、sha256、时间——"要不要更新"
  因此不用每次哈希一个 13 MB 的文件。
- **下载完不是"大小对了就算"**：`nnedi` 滤镜按固定偏移读这个 blob，13 MB 的 HTML
  和 13 MB 的权重在大小上无法区分。装之前会让 FFmpeg 真的用
  `nnedi=weights=...` 初始化一帧，装完再比 sha256 决定是否真的替换。
- 没有权重时 `--deinterlace nnedi` 在预检**之前**报一句人话（"run `--nnedi-update`"），
  不是二十秒后 FFmpeg 的 `Cannot open file`。

### 2. GitHub 下载前沿（`--gh-proxy`）

从国内直连 `raw.githubusercontent.com` 的实际情况（2026-10-27 实测）：**连接能建立，
然后一个字节也不来**，curl 20 秒超时 0 字节。下载器看到的是"请求还活着"，于是整个
stall 预算都耗在等一个不会来的 body 上——就是"下载卡住"。

- `--gh-proxy [URL]`（裸写 = `https://gh-proxy.com/`，`off` = 不用）给
  `github.com` / `api.github.com` / `raw.githubusercontent.com` /
  `objects.githubusercontent.com` / `codeload.github.com` /
  `gist.githubusercontent.com` **整条 URL 加前缀**，由前沿自己回源。不是设代理：
  这个功能的用途正是"替代本地 VPN"，所以必须在没有代理的机器上也能用。
- 同一份权重经前沿实测 **206 + 正确的 `Content-Range`**（`bytes 0-1023/13574928`），
  所以断点续传照常；整包 13.5 MB 约 70 秒。
- **没指定时自己判断**：先发一个 5 秒的 1 KB range 探测，通就直连（海外机器不会多
  一跳），不通才用前沿并说明用了哪个。下载中途死掉还会从零重试一次走前沿——探测
  通过而传输失败，正是"能连上但不给数据"的常态。
- `--no-gh-proxy` 与 `--gh-proxy off` 会被**记住**（配置文件里存 `"off"`）。"从没问过"
  （null）和"明确不要"（`off`）是两件事，前者才允许自动选路。
- 同样的自动选路也用在 `--ffmpeg-download` 的 GitHub 源上；gyan.dev / SourceForge
  不匹配任何前沿主机，URL 原样发出。

### 3. ⚠ 多次压缩（第二遍之后）

`Recompress` 从"一遍"变成"一条链"：`--recompress-args` / `--recompress-codec` /
`--recompress-container` 都可以**重复给出**，第 N 次出现的三件套就是第 N 遍。

```powershell
# 先当 2007 年的门户压一遍（flv1），再当 2011 年的站点压一遍（H.264）
LiveArchiveEncoder.exe clip.mkv --recompress-only `
  --recompress-args "-c:v flv -q:v 9" --recompress-container flv `
  --recompress-args "-c:v libx264 -crf 26" --recompress-container mp4
```

- 每一遍读**上一遍的产物**，所以 N 遍就是 N 次真实转码——这正是"上传到 2007 的
  门户、被人扒下来又传到 2011 的站点"对文件做的事。
- 产物名 `stem_web.ext`、`stem_web2.ext`、`stem_web3.ext`……**全部保留**：每一遍
  都是过程里的真实文件，只有最后一个是交付物。
- 失败只报第几遍失败并停在那里，直播产物一个字节都不动。
- **每一遍都会按编码器推断容器**（审查时补的）。原来只有第一遍会推断，于是
  `--recompress-args "-c:v flv -q:v 9"` 当第二遍时会被
  `flv cannot be written to .mp4` 拒掉——容器在问编码器之前就已经填成 mp4 了。
  现在"这一遍没给容器"就按编码器定，与第一遍同一套规则。

### 4. ⚠ 字幕按 4:3 / 16:9 声明画面（`--subtitle-aspect`）

libass 永远画在**交给它的那帧**上，所以 4:3 的字幕压进 16:9 画面时，要么横向摊开、
要么以画面宽度算字号；反过来 16:9 的字幕进 4:3 画面就会跑出边界。

`--subtitle-aspect 4:3|16:9|source` 改成"方框合成"：把已完成的帧裁成目标比例的正中
一块，在那里渲染字幕，再按同样的偏移贴回。裁与贴都是同尺寸同位置、不缩放，所以
方框外的像素原样不动，方框内则是这个比例的帧本该有的排版。

实测（352x264 → 640x360 交付）：4:3 字幕进 16:9 画面，墨迹 **144x27 未被拉伸**，
只是整体右移 106 px 居中；16:9 字幕进 4:3 画面，方框缩到 556 宽居中、不裁字。
`source` 是默认值，也就是老行为（整帧渲染，无 `-filter_complex`）。

### 5. 顺带修的

- **aria2c 现在拿到系统代理**。它不会自己读系统代理，而回退用的 `HttpClient` 会：
  实测同一台机器上，aria2c 直连 `raw.githubusercontent.com` 死在 TLS 握手，紧接着
  单连 `HttpClient` 就把同一个文件下完了。给 aria2c 显式 `--all-proxy`，两条路才一致。
- **权重下载可以续传**：暂存文件名从每次随机的 `.download.xxxxxxxx` 改成固定的
  `.nnedi3_weights.bin.part`——随机名字让 aria2c 的 `-c` 完全失效，中断一次就白下
  13 MB（实测清理出两个残留，7.6 MB）。失败会保留已下载部分并提示再跑一次续传。
- **verbose 的"滤镜"行打印实际跑的那条**。v7.9.8.3 引入方框合成后，这一行还在打印
  缩放链，把字幕滤镜整段藏了——e2e 的强制字体断言因此失败，是回归不是新问题。
- **`--save-config` / `--emit-args` 不再丢字幕**。CLI 的字幕参数只进编码层的
  `Recompress`/`SubtitleSettings`，从来没写回 `Config`，于是"等价命令行"里
  `--subtitle` 与 `--subtitle-aspect` 都不见了（往返一次就少一次烧录）。现在转换
  之前先把命令行上的字幕请求折进快照。
- **`--emit-args` 会输出每一遍压缩**。第一遍之后的 `Extra` 以前根本不打印，一个三遍
  的配置被写成一遍的命令行；另外多遍时每一遍都带 `--recompress-container`，因为
  args/container/codec 是**按出现次序配对**的，漏一个 mp4 会把配对挪到下一遍。
- **CUI 补上两个下载菜单**：工具链子菜单新增 `6. GitHub 前沿` 与 `7. NNEDI3 权重`
  （显示版本/大小/sha256，`u` 下载或更新）。此前这两项只有命令行能到，交互界面
  即使选了 `nnedi` 也拿不到权重——`--nnedi-weights` 也没传进 `RealtimeOptions`，
  选它必然报错。顺带把字幕的 `排版画面`（4:3 / 16:9）接进了字幕菜单第 6 项。
- 配置摘要（CUI 的"当前设置"与 `Report`）多打两行：GitHub 前沿与 NNEDI3 权重；
  字幕行带上 `box=`，压缩行打印整条链而不是只有一遍。

---

## v7.9.8.2（2026-10-27）

第五个补丁号，一件事：**时代预设终于把直播画质、窗口、缓冲和反交错也一起说了**。

起因是一句现场观察：用 `libx264`/`libopenh264` 时 `q:v` 没被改，直播编码的码率
特别高，帧率一下子掉到底。查下去发现预设**从来没设过画质**——`ApplyLive` 只写
尺寸、帧率、码率上限、缩放和音频，`Qv` 一直是上一个编码器留下的数字。

### 1. ⚠ 画质数字现在带"刻度"

`q:v` 和 `crf` 不是一个东西。`EncoderMath.DefaultQuality` 里早就写着两者的分别
（mpeg4 = 8，libx264 = 28），但**只有一个数字在流动**：`2007` 那一行要的是
352x264@15 / 320 kbps，`q:v 9` 在 mpeg4 下是 375 kbps；同一个 9 交给 libx264
就是 `-crf 9`，实测 **935 kbps**——2.9 倍上限。而帧率阶梯读的正是这个实测码率，
于是第一秒就走到了最后一档，"15 fps 的抓取"实际从头到尾没跑过 15 fps。

- `EncoderMath.QualityScale`（`Qv` / `Crf` / `Bitrate`）+ `ScaleOf(encoder)`：
  哪些编码器读哪种刻度，从此只有这一处判断。
- `EncoderMath.ConvertQuality(from, to, value)`：按**等码率**标定，`crf = 7.5 + 1.17 × q:v`
  （q:v 9 ↔ crf 18、q:v 12 ↔ crf 22，13 组 45–2500 kbps 的实测配对拟合）。
  换编码器时数字跟着换刻度，不再原样搬运。
- `EraPreset` 增加 `LiveQv` 与 `LiveCrf`，**每一个内置行都写了两个数**（27 站点行
  + 5 通用行）。每行的 crf 都是让参考素材落在该行上限 0.76–1.04 倍的那个值，不是
  换算出来的。
- `Cli` 与 CUI 的 `ApplyPreset` 都改为"预设按**当前生效的编码器**给数字"。CUI 那
  条路径正是这次报障的来源：`ApplyPreset` 换了 `c.Encoder` 却没动 `c.Qv`。

### 2. ⚠ CRF 编码器多了一道 VBV 上限

只把数字改对还不够——用户显式 `--qv 9` 时仍然会得到近无损。libx264 现在会连同
`-maxrate <上限>k -bufsize <2×上限>k` 一起下发：实测 `crf 9` 在 320 kbps 上限下
从 935 kbps 压到 413 kbps，x264 不会像 MPEG 家族那样刷 `rc buffer underflow`。
固定量化器 + 紧 VBV 对 mpeg4/flv/wmv2/rv20 是自相矛盾的请求（实测每帧刷一条
warning 且照样超），所以这条只给 CRF 编码器。

### 3. `libopenh264` 永远有一个码率

它没有 CRF 也没有可用的 `q:v`，码率是唯一的旋钮。以前只在"有上限"时才发
`-b:v`，漏了就落回它自己的默认值（352x264 实测 1.1 Mbps，等于把所有档位一次
用完）。现在缺上限时用 200 kbps 兜底，参数菜单第 6 行也照实标成 `q:v（不生效）`
而不是假装它管用。

### 4. 窗口、缓冲帧、反交错按年代给值

`EraPreset` 新增 `WindowSeconds` / `BufferFrames` / `Deinterlace`（含 mode、parity、
marked-only），三个阈值都有实体理由：

| 年 | window | 缓冲帧 | 反交错 |
| --- | --- | --- | --- |
| ≤2003 | 3 s | 4 | `off` |
| 2006–2009 | 2 s | 6 | `yadif` |
| ≥2010 | 2 s | 8 | `yadif` |

- **2006**：`yadif` 2006 年随 MPlayer 出现、同年进 FFmpeg。更早的行写 `off`，
  因为当年没有这个滤镜可调——2003 年的 WMV 抓取确实是把隔行结构写进文件的。
- **2006**：调制解调器和 WMV 年代的流服务器要持续几秒才降档，FLV 门户在自己
  两秒的缓冲里就反应了。
- **2003 / 2009**：帧队列随帧变大——320x240 四帧、384x288 六帧、640x360 以上
  八帧。
- 所有时代行都带 `DeintMarkedOnly`：只处理源标记为隔行的帧，否则一份逐行素材
  会被 yadif 白白磨软。
- 三者的显式开关（`--window` / `--buffer-frames` / `--deinterlace`）优先于预设，
  和其余"预设是兜底不是覆盖"的字段一致。`--deinterlace` 一旦由用户给出，mode /
  parity / marked-only 也归用户——半个预设加半个参数是合成不出来的配置。

### 5. 自定义预设与老配置

`CustomEraConfig` 增加 `LiveQv` / `LiveCrf` / `WindowSeconds` / `BufferFrames` /
`Deinterlace`，CUI 的新建/编辑向导问一次画质（按该行编码器的刻度问），两个数
一起存。老配置文件没有这些字段时，按行自己的年份取 `LivePipeline`，而不是套一个
现代默认值。

### 验证

- 单测 **564 例 → 563 过 / 1 跳过**（需要字体文件）。新增
  `tests/LiveArchiveEncoder.Tests/EraLiveQualityTests.cs` 10 条：刻度判定、q↔crf
  换算与往返、`-crf` + VBV 与 `-q:v` 的分配、OpenH264 兜底码率、每行两个数都在
  合法刻度且 crf 严格大于 qv、`ApplyLive` 按生效编码器写数、三个阈值的年份表、
  以及自定义行与老文件的回退。
- e2e **205 例两阶段全绿**。新增 `tests/e2e/test_era_quality.py` 5 条：2007 行
  + libx264 的总码率不超过上限 1.3 倍（修复前是 2.9 倍）、`--qv 9` 也压得住、
  2011 行的 OpenH264 真的被钉在 700 kbps、2003/2014 行的 window/缓冲/反交错、
  以及显式开关压过预设。
- 实测对照（`tests/media/test_src.mp4`，352x264@15，上限 320 kbps）：

| 情形 | 视频码率 |
| --- | --- |
| 修复前 `q:v 9` → `-crf 9` | 935 kbps（掉底） |
| 修复后该行的 CRF（22），实跑 | 262–306 kbps |
| 强制 `--qv 9` + VBV | 413 kbps |
| OpenH264 700k | 689 kbps |

---

## v7.9.8.1（2026-10-27）

第四个补丁号，三件事：**RealMedia 能出当年那样的文件**、**第二遍也走 `.part`**，
以及工具链升到 .NET 10.0.403。

### 1. ⚠ `.rm` 的音频从 `ac3` 改成 `real_144`

`rv10`/`rv20` 一直能写 `.rm`，但音频从来没指定过，于是落进 **rm muxer 自己的默认
`ac3`**：文件确实能 mux，ffprobe 也读得出来，可是 2003 年的 RealPlayer 一个都解不了。
"能装进容器"和"当年那个文件"是两件事，这个工具存在的理由正是后者。

- `AudioCatalog` 新增 `real_144`（RealAudio 1.0）：**只支持 8 kHz、单声道、s16**，
  其它采样率会被吸附（44100→8000）、双声道会被降到单声道，各打一行提示。
  它有负一档码率：FFmpeg 收下 `-b:a` 但不理会（实测 `-b:a 32k` 与不写完全一样），
  所以码率这一维是宽松的，写多少都不影响产物。
- `--container`/扩展名推到 `rm` 时，默认音频链变成 `real_144 → ac3 → aac`，
  前两个都在 `Constants.DefaultAudioByContainer` 和 `AudioCatalog.ContainerCodecs` 里。
- 显式 `--audio-encoder ac3` 仍然照办——想做"技术上能装的怪文件"是用户的自由。
- `--audio-mode separate` 可用了：音频先写成 `.ra`（同一个 rm muxer），再和视频
  `-c copy` 合并。工具本来就会先用 3 帧样本探一次能不能合并、再解码验证，这次让
  它第一次有东西可探。

### 2. ⚠ 第二遍编码也走 `name.part.ext`

v7.9.8 给三条直播写入路径都加了 `.part`，**漏了 `PostProcess/Recompress`**：
第二遍是 `ffmpeg -y` 直接写 `clip_web.mp4`，所以一次失败的二次压缩会把上一版交付
文件截断，硬盘上留一个"看起来完成了"的半截。现在它和直播层用同一条规则：写
`clip_web.part.mp4`、校验非空、`File.Move` 换名，失败删掉 `.part`。

### 3. 第二遍认识 RealVideo 了

`RecompressCodecs` 的视频表里**没有** `rv10`/`rv20`，于是 `--recompress-codec rv20`
在 `FindVideo` 拿到 null 之后一路回退到 `libx264 + mp4`——一个自称 RealVideo 的
H.264 文件。现在两者是正式条目，容器只有 `rm`，默认音频 `real_144`；
`--recompress-codec rv20` 会真的产出 rv20 + ra_144 的 `.rm`。

顺带把"这两个编码器装不进别的容器"写成了明确约束：AVI 没有 rv10 的 fourcc
（读回来是 `rawvideo`），Matroska 直接拒（`does not yet support muxing rv10`），
而 `--parallel` 的分片恰恰用 mkv 暂存——所以 `.rm` 的 `--parallel` 会明确拒绝并
退回单遍（`Format 'rm' cannot be joined safely`）。

### 4. 死代码与工具链

- `FrameBufferPool` 里那句 `Monitor.Pulse(_sync)` **没有等待者**（消费者是轮询
  `TryTakeLatest` 的），它唯一的作用是让这把锁没法换成 `System.Threading.Lock`。
  删掉之后门锁换成 `Lock`（`lock` 直接编译成 `Lock.EnterScope()`）。
- `FFmpegCapabilities` 的两处 `Regex.IsMatch` 用了**运行时拼出来的模式**，既无法
  缓存、又在 NativeAOT 下走解释器；改成按行读文本的 span 比较。
- `Cui` 的 `^(\d+)x(\d+)$` 换成 `[GeneratedRegex]`，并用 `TryParse` 接住
  "99999999999x10" 这种正则匹配得上、`int` 装不下的输入（以前抛 `OverflowException`，
  提示是"值太大或太小"，跟输入框毫无关系）。
- 三台机器的 .NET SDK 升到 **10.0.403**，并且删掉旧的 10.0.402：
  `mise use -g dotnet@10.0.403` + `mise uninstall dotnet@10.0.402`。
  产物由 10.0.403 重新编译。

### 验证

- 单测 554 例 → 553 过 / 1 跳过（需要字体文件）。新增 `RealMediaTests` 7 条：
  默认音频是 real_144、8 kHz 单声道吸附、只属于 rm 容器、第二遍两个编码器存在、
  容器配对、以及 AVI/Matroska 被拒的理由。
- e2e 200 例两阶段全绿。新增 `tests/e2e/test_realmedia.py` 3 条：`.rm` 里是
  rv10+ra_144 且能干净解码；`--recompress-codec rv20` 真出 rv20 而不是 H.264；
  第二遍失败时旧交付文件原样保留、不留 `.part`。
- 对照矩阵（`test_compare_matrix`）的 `rv20_*` 两条从"全等"变成"**一处显式例外**"：
  Python 基准 v6.3.3 里 `.rm` 的音频是 `ac3`，C# 侧现在是 `ra_144`。例外写在
  `DIVERGENCES` 表里并带原因，只允许 `audio` 这一个字段不同——其它任何字段漂移
  仍然会红。这是 v7.6 之后"允许偏离基准但不许悄悄偏离"的规则第一次被真正用上。

---

## v7.9.8（2026-10-27）

第六个补丁版，没有新功能：**把整份代码按"会不会在用户机器上出事"过了一遍**，
修掉十五处缺陷，其中三处会毁数据、崩进程，或者把程序挂在那里不动。每条都先在当前
代码上复现、再改。

### 1. ⚠ 输出先写 `.part`，成功才改名

**问题**：FFmpeg 带 `-y` 打开目标文件的那一刻就把它截断了。后果有两个，
对一个归档工具都是致命的：

- 重编码一个已有成片时失败 → 旧的好文件没了，硬盘上只剩半截；
- 取消/失败留下的半截文件**看起来是完成的**，从文件管理器到后续 remux 都当它是成品。

**改法**：编码写 `name.part.ext`（同目录、同扩展名，所以 FFmpeg/ffprobe 看到的
格式判断完全不变），全部校验通过后 `File.Move(overwrite)` 改名到位；失败的路径
删掉 `.part`。"归档"这个用途下，宁可留一个名字就写着"没完成"的文件。

实测：20 秒的任务在第 5 秒被杀，原有 64,383 字节的成片**一字节没动**，半成品在
`lae_keep.part.mp4` 里（332,912 字节）。改动覆盖三条写入路径：单进程、分离音频的
mux、以及 `--parallel` 的 remux。

### 2. 解码器中途死亡不再"补帧"成成功

**问题**：`Execute` 里那句解码器死亡检查带着 `&& !captureState.Done`，而
`Done` 正是采集线程在管道 EOF 时置位的——也就是说，这个检查在它唯一该起作用的
场景里**永远不成立**。HTTP 源断流、坏扇区，解码器死在第 10 秒之后，主循环继续把
最后一帧重复到时长跑满，编码器退出码 0，于是工具报告成功。产物后半段是定格的。

**改法**：去掉那个条件——循环结束时才会主动杀解码器，所以循环内出现的非零退出码
一定是解码器自己的问题。

### 3. 取消/失败不再留下孤儿 FFmpeg 和临时目录

**问题**：分离音频（`--audio-mode auto/separate`）起了一个 `CancellationToken.None`
的音频任务。视频段一旦抛异常或取消，这个任务永远不被等待：它的 FFmpeg 继续跑到
`audioTimeout`（时长 ×2 +120 秒），而 `finally` 去删一个正被写入的目录 → 删除失败，
`%TEMP%\live_archive_v7_*` 连同半个音频文件永久留下。

**改法**：`ProcessRunner.Run` 新增可选取消令牌（现在按 250 ms 一片等待，取消能及时
被观察到）；音频任务用与运行绑定的 CTS，`finally` 先取消、等 5 秒、再删目录。

### 4. 坏配置文件不再崩进程（`--emit-args`）

**问题**：`"FpsLevels": null` 这种写法 System.Text.Json 会照单全收，而属性类型不允许
null。此前只有 `ConfigStore.Apply` 自己兜底，于是走另一条路的 `--emit-args=file.json`
直接在 `CliArgs.LadderSpec` 里抛 `NullReferenceException`，进程带堆栈退出。

**改法**：在读取边界统一修一次（`ConfigStore.Normalise`）：集合、子对象、以及有默认值
的字符串字段，null 一律回到默认。所有读者看到的就是类型承诺的形状。

### 5. 链路探针会在服务器中途停发时永久挂住

**问题**：`--parallel` 对网络源先测一次链路（`NetworkProbe.FromHttp`）。它用
`HttpCompletionOption.ResponseHeadersRead` 拿到流，然后**同步**读满声明的 body。
`HttpClient.Timeout` 在响应头收全的那一刻就失效了，剩下的读取没有任何上限——服务器
声明的 `Content-Length` 只要多于它肯给的字节（半死的链路、掉线的 NAS、不规范的
Range 实现），程序就停在那里等一次永远不会来的读完，而且**发生在任何输出之前**，
用户看到的只是"双击之后什么都没有"。

e2e 的 HTTP 分片用例就是这么卡满 300 秒超时失败的。用 v7.9.7 的二进制同样复现，
所以不是本次改动引入的回归——它从 v7.9.6.3 引进这套测量起就在。

**改法**（`Remote/NetworkProbe.cs`）：

- 整个测量绑定一个 15 秒预算，每次读走 `ReadAsync` 把令牌注册到连接上——取消即断开，
  测量退化成"测不出来"，而调用方本来就把这种情况当"不分片"处理；
- 采样大小跟着文件走（`2 MiB`、`文件/4`、`512 KiB` 三者取中）：原来对 800 KiB 的
  文件直接请求 2 MiB，第二刀必然 416，于是小文件永远只有一个样本；
- 首次读只计入延迟、不计入速率，第一个样本作预热丢弃——冷连接的慢启动会把 loopback
  测成 96% 抖动，把一个 300 MB/s 的链路判成"太不稳定，不许分片"；
- 抖动只由**时长 ≥ 10 ms** 的样本计算。512 KiB 两毫秒读完时，计时噪声本身就超过 30%
  的阈值：那不是链路质量，是秒表。

**顺带修的测试基础设施**：e2e 自带的 Range 服务器不把 `last-byte-pos` 夹到文件末尾
（RFC 9110 §14.1.2 要求夹）。于是它对 800 KiB 的文件答应 2 MiB 的 body、只发 800 KiB，
然后挂在 keep-alive 上等下一个请求——客户端在等剩下的字节，服务端在等下一条请求，
`curl` 一样挂。这不是产品缺陷，但它把一个用例变成了 300 秒的假失败。

**新测试**：`test_http_stalled_body_does_not_hang`——一个声明 200 MiB、发 4 KiB、
然后沉默 300 秒的服务器。旧代码永不返回；现在 48 秒内给出 `could not measure` 并以
非零退出码结束。

### 6. 其余十处

| 位置 | 问题 | 触发条件 |
| --- | --- | --- |
| `Remote/NetworkProbe.cs` | catch 过滤器漏 `HttpRequestException` / `OperationCanceledException` | 无代理的机器上 `--net-test` 或 `--parallel` 访问坏 HTTP 源 → 未捕获异常 |
| `FFmpeg/Toolchain.cs` | `new WebProxy("http://a b")` 抛 `UriFormatException`，两层 catch 都不含它 | `--proxy` 打错一个字 → 崩溃而不是报错 |
| `FFmpeg/Toolchain.cs` | 下载用 `Timeout.InfiniteTimeSpan` + 阻塞 `Read` | 连接半死时永久挂起，只能 Ctrl+C |
| `FFmpeg/Probe.cs` | `Duration` / `VideoPacketCount` 用 30 秒默认超时 | 慢共享上的大文件 → 已经成功的分片合并被判失败 |
| `Encoding/ParallelEncoder.cs` | 时长测不出来当 0 秒 → 报"合并丢数据" | 容器不存 duration |
| `Remote/Smb.cs` | server/share 直接拼进 PowerShell `-Command` | 共享名带 `'` → 语法错误；恶意路径可注入命令 |
| `FFmpeg/BuildProfileStore.cs` | 缓存 JSON 合法但集合为 null → NRE 发生在 try 之外 | 手工改坏的缓存文件，每次启动都崩 |
| `Core/Timecode.cs` | 小时字段任意长度，`int.Parse` 抛 `OverflowException`，而调用点只 catch `FormatException` | `--start-time 99999999999:00:00` |
| `Encoding/StreamSelection.cs` | 重建 `SourceInfo` 时丢了 `BitRateKbps` | `--video-stream N` + 网络源 → 带宽判定按输出上限而不是源码率 |
| `Encoding/SubtitleTrack.cs` | 暂存目录在探针超时、或预检失败时泄漏 | 内嵌字幕 + 编码器/封装组合被拒 |
| `UI/CliArgs.cs` | `--emit-args` 只给空格和引号加引号 | 文件名含 `&` / `|` / `(` → 粘贴回 cmd 被拆成两条命令 |

`--ffmpeg-download` 后跟一个拼错的发行版 id（如 `gyan-dev`）时，那个词会被当成输入
文件、同时装上默认发行版——这个**没改**：`--ffmpeg-download in.mp4 out.mp4` 是合法
用法（裸写＋位置参数），两者在语法上无法区分，改成报错会打断脚本。

### 7. 语言层

- `FileInfo.HasContent`（C# 14 扩展块）：`Refresh() + Exists && Length > 0` 原本在五处
  各写一遍，其中一处已经忘了 Refresh——`FileInfo` 缓存的是构造时的大小，对刚写出的
  文件永远是"不存在"。
- 互斥量尽量换成 `System.Threading.Lock`：`lock` 语句在静态类型是 `Lock` 时直接编译成
  `Lock.EnterScope()`（专用自旋路径、不进 Monitor 膨胀）。`FrameBufferPool` 的那把
  **不能**换——它靠 `Monitor.Pulse` 唤醒采集线程，而 `Lock` 没有 `Pulse`；把 `Lock` 交给
  `Monitor.Pulse` 会装箱并去 signal 一个没人持有的 monitor（编译器 CS9216 就是在说这个，
  它是对的），所以那里保留 `object` 并写明了原因。
- `SearchValues<char>` 替掉 `ContainsAny(' ', '\t', '"')`，命令行引号规则顺手补上
  cmd 的 `& | < > ^ ( ) ;`（`%` 和 `!` 在引号内仍会展开，这是 cmd 的限制，文档里写明）。
- `ProcessRunner.Run` 新增取消令牌，顺带把 `args[0]` 的空集合情况变成明确异常。

### 验证

- 单测 547 例 → 546 过 / 1 跳过（需要字体文件的用例），0 警告 0 错误。
- e2e 197 例两阶段全绿（独占 27 + 并行 170，2 跳过＝没有 .a64 解复用器；新增的"停发不
  挂死"用例要 48 秒，因为它必须先让 ffprobe 的 30 秒 `-rw_timeout` 到期）。
- 三条关键行为实测：坏配置不再崩、时间码溢出变成 `ERROR: ...` + 退出码 2、
  停发的 HTTP 源 48 秒内报错退出；中断测试见第 1 条。

---

### 产物

| 文件 | 大小 | 构建机 |
| --- | --- | --- |
| `LiveArchiveEncoder-v7.9.8-win-x64.zip` | 325,889 B | 本地 JIT |
| `LiveArchiveEncoder-v7.9.8-win-x64-aot.zip` | 3,988,726 B（exe 9,211,904 B / 46 s） | Windows Server 2019 + MSVC 14.44 |
| `LiveArchiveEncoder-v7.9.8-linux-x64.tar.gz` | 285,934 B | AlmaLinux 8.10 JIT |
| `LiveArchiveEncoder-v7.9.8-linux-x64-aot.tar.gz` | 4,795,833 B（ELF 10,233,152 B / 49 s） | AlmaLinux 8.10 + copr clang 24 |

Linux AOT 最高只要求 `GLIBC_2.17`。四个包都在最低系统上解包跑过一遍。

---

## 更早的版本

v7.9.7 及以前（含 v6.2.2 → v6.3.3 的移植基线）见 [`CHANGELOG-archive.md`](CHANGELOG-archive.md)。
