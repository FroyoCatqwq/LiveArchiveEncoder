# 变更记录（CHANGELOG）

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

---

## v7.9.6.1（2026-10-24）

一个补丁版：**时代预设的动态帧率挡位也要按当年走。**

### 只改基准帧率是不够的

v7.9.6 及以前，预设只设 `Fps`，挡位留着工具默认的 **13/8/5/3**。于是
`--era mp4_10` 用 25 fps 采集，第一次降档直接掉到 13——**一档砍掉一半帧率**，
那不是 2010 年任何编码器会做的事。`hd14` 的 30 fps 配 13/8/5/3 更离谱：三档之和
还不到基准的一半。

挡位现在属于预设：

| 预设 | 基准 | 挡位 |
| --- | --- | --- |
| `dialup98` | 10 | 10 → 8 → 5 → 3 |
| `broadband03` | 15 | 15 → 12 → 10 → 8 → 5 |
| `flv07` | 15 | 15 → 12 → 10 → 8 → 5 |
| `mp4_10` | 25 | 25 → 20 → 15 → 10 |
| `hd14` | 30 | 30 → 25 → 20 → 15 |

依据写在 [`era-presets.md`](era-presets.md) 的"动态帧率挡位"一节：RV8/9 在 150 Kbps
时 12 fps 仍可用；实测土豆文件落在 11.8–12.0 fps，正是 12 这一档；480p 之后不再
降到 5；720p 降到 15 已经是可见缺陷。**站点预设不设挡位**——它只描述"网站怎么
转码"，画面怎么采集是用户的事（`test_a_site_preset_only_reshapes_the_second_pass`
盯着这条）。

### 顺带修掉的两处不一致

* **`Constants.DefaultFpsLevels` 成为唯一来源**。以前 CLI 的默认阶梯是
  `13,8,5,3`，而菜单里"改帧率后重置挡位"推导的是 `[基准, 8, 5, 3]`——基准高于 13
  时 13 这一档就悄悄消失了。三处（CLI 默认、菜单重置、预设无挡位时的推导）现在
  同一个表。
* **钉了阈值的挡位跟着走**。`FpsLevelKbps` 按帧率键控，换挡位时不属于新挡位的
  阈值被 `PruneRungs` 丢掉，不会套到别的帧率上。

### 自定义预设

菜单 14 → `n`/`e` 的向导在直播层多问一行"直播帧率挡位"（逗号分隔，留空＝按基准
推导）。存进 `config.json` 的 `CustomEras[].FpsLevels`；手改乱的会被清洗：降序、
去重、丢弃非正数与超过 120 的值，只剩一档则视为"没写"。

### 优先级

`--fps` / `--fps-levels` 显式给了就以用户为准，预设只在两者都没给时生效——和
v7.9.3.1 定下的"预设是兜底，不是覆盖"一致。

### 验证

| 项 | v7.9.6 | v7.9.6.1 |
|---|---|---|
| 单元测试（Windows） | 503 × {net10.0, net11.0} | **512 × {net10.0, net11.0}**，全绿 |
| 单元测试（Linux cia1） | 502 = 501 + 1 跳过 | **511 = 510 + 1 跳过** × 双 TFM |
| e2e 收集 | 193 = 191 + 2 跳过 | **194 = 192 + 2 跳过**，两平台一致（Windows 417s+137s，cia1 314s+233s） |

新增 `EraFpsLadderTests`（9 条：五个预设的挡位逐项对齐、首档等于基准、严格降序、
站点预设没有直播挡位、应用预设时替换默认挡位并清理阈值、无挡位预设按基准推导、
自定义预设往返、乱序挡位清洗、站点行丢弃挡位）与 e2e
`test_an_era_ladder_belongs_to_its_period`（五个预设各自的 `--fps-levels` +
显式覆盖优先）。

不给 `--era` 时行为完全不变。

---

## v7.9.6（2026-10-24）

两个目标：**画面能按指定的像素格式/色彩落盘**，以及**把项目再过一遍、修掉潜伏的
缺陷**。第二个目标的结果不是"改了很多"，而是"改的每一处都能说出它是怎么被漏掉的"。

### 1. 输出像素格式与色彩：`--pix-fmt` / `--color-*` / `--dither`

以前"像素格式"是**推导**出来的：源是什么就用什么，源不行就取同家族里编码器接受的
第一个。用户无法要求 `pal8`、`gray`、`rgb555le`、`yuv420p10le` 这类输出——而 8/16 位
画面正是老录像归档里最容易被问到的一件事。

- **`--pix-fmt FORMAT`**：输出像素格式。`pal8`（256 色索引）、`rgb8`（3:3:2）、
  `rgb555le`/`rgb565le`（16 位色）、`rgb48le`、`gray`、`yuv420p10le`/`12le`/`16le`、
  `yuv444p16le`、`nv12`、`p010le` …… 共 **86 种**，全部实测过帧长。
  `auto`（默认）等于 v7.9.5 的自动推导，行为一字不改。
- **请求要么被满足，要么被拒绝**。不接受"降级"：`--pix-fmt yuv420p10le` 给 mpeg4
  （它只声明 yuv420p）会直接 exit 2 并列出它真正有的格式，而不是偷偷写 8 位。
- **`--list-pix-fmts`**：把实测表按家族列出来，并用 `OK` / `---` 标出当前编码器
  声明的那一列，不用试错。
- **`--color-range` / `--colorspace` / `--color-primaries` / `--color-trc`**：覆盖
  源文件的色彩标记（不转换画面）。不填就沿用源；填了就只覆盖那一项。
- **`--dither MODE`**：`auto`（默认，不加参数）/ `none` / `bayer` / `ordered` /
  `random` / `error_diffusion`。**只在目标格式真的会掉色时才发** `-sws_dither`
  （pal8、5:5:5、5:6:5、3:3:2、1 位黑白、8 位灰度）；yuv420p 上这个参数什么也不做，
  发了只会让日志说谎。
- CUI 新增**主菜单 20「画面格式与色彩」**：像素格式（列出编码器接受的、也可直接
  输入名字）、四项色彩标记、抖动、格式清单。换编码器时，新编码器吃不下当前格式
  会**清掉并说明**，不会留到预检才炸。

四件事只有实测才会发现，都写进了代码注释与测试：

1. **`yuv440p` 的帧长一直算错**：旧表写 1.5 B/px，实测 **2.0**（4:4:0 的色度是全宽
   半高）。它的可整除约束也被当成 4:2:0 的 2×2，实际只要高为偶数。历史代码从来
   没有选中过 4:4:0，所以这个"每帧错 1/2"的缺陷一直躺着——`tools/pixfmt_sweep.py`
   就是为这类事情写的：**86 种格式逐帧量一遍**，表里每一行都是量出来的。
2. **调色板 + 丢调色板的容器，以前只在分片时拦**。v7.9.4 的规则写在 `ParallelEncoder`
   里，于是 `--encoder zmbv --pix-fmt pal8 out.mkv` 这种**单遍**运行照样写出黑画面
   （同一个片段 AVI 里 YAVG 123、Matroska 里 16）。现在这条规则在
   `PixelFormatUtil.PaletteContainerConflict`，单遍/分片/CLI/CUI 都查。
3. **libx264 收下 `-color_primaries`/`-color_trc` 但不写**：实测输出仍是
   `primaries=unknown`，同样的值走 `-x264-params colorprim=…:transfer=…` 才进码流。
   所以 libx264 现在两个都发（FFmpeg 与 x264 拼写不同的四个值做了翻译：
   `gamma22→bt470m`、`gamma28→bt470bg`、`log→log100`、`log_sqrt→log316`）。
4. **声明的格式不等于写出的格式**：`-c:v libx264 -pix_fmt gray` 出来是 yuvj420p。
   编码结束后用 ffprobe 读回真实 `pix_fmt`，不一致就打一行 note（同 v7.9.5
   "能力表只收三重校验"的做法）。

### 2. 全项目复查：修掉的潜伏缺陷

| 位置 | 缺陷 | 为什么一直没被发现 |
|---|---|---|
| `ParallelEncoder` 分片参数 | **没传 `FpsLevelKbps`**，用户钉的每档阈值被换成推导值 | 只在 `--parallel > 1` 时发生，且阈值只影响码率控制，产物照样能放 |
| `RealtimeEncoder.RunSplitCore` | 同上，还漏了后来新增的字段 | 只在 `--audio-mode auto/separate` 时发生，默认 inline 走的是另一条路 |
| `PixelFormatUtil.ChooseTransportPixFmt` | 源格式与声明表用**大小写敏感**比较 | FFmpeg 自己都打印小写；只有当配置/缓存里出现大写时才会把 10 位降成 8 位 |
| `Constants.PixelFormatSize` | `yuv440p` = 1.5（应为 2.0） | 没有任何编码器/源在这个项目里用到 4:4:0 |
| `PixelFormatUtil.NormalizeTransportDimensions` | 4:4:0 被当成 4:2:0，多砍了宽度 | 同上 |
| `ParallelEncoder.BlockReason` | 调色板判断用的是**自动推导**的格式，忽略 `--pix-fmt` | v7.9.5 时还不能指定格式 |
| `Validation` | 像素格式/色彩标记/抖动没有任何校验入口 | 这些设置是新加的 |

`--quiet` 仍然只静音进度；格式不符的 note 与它同一开关（脚本要完全可预期时用
`--quiet`，那是用户自己选择的沉默）。

### 3. 验证

| 项 | v7.9.5.1 | v7.9.6 |
|---|---|---|
| 单元测试（Windows） | 475 × {net10.0, net11.0} | **503 × {net10.0, net11.0}**，全绿 |
| 单元测试（Linux cia1） | 474 = 473 + 1 跳过 | **502 = 501 + 1 跳过** × 双 TFM（映射盘用例 Windows-only） |
| e2e 收集 | 186 = 184 + 2 跳过 | **193 = 191 + 2 跳过**（a64 无 demuxer） |

新增：`PixelFormatCatalogTests`（18 条，86 行帧长逐行对齐实测）、
`PictureFormatConfigTests`（6 条）、`CliParserTests` 4 条、`CliArgsTests` 往返补
五个字段、`tests/e2e/test_picture_format.py`（7 条）。

不给 `--pix-fmt` 时行为与 v7.9.5.1 完全一致；配置文件新增六个可选键，老文件照常读，
不认识的值在载入时被丢弃而不是带进编码。CUI 菜单编号新增 **20**，1–19 不变。

---

## v7.9.5.1（2026-10-24）

四个目标，一句话概括：**预设要像当年，字体要能查到，第二遍要能选编码器。**

### 1. 时代预设：18 个站点、按站点指定编码器、可自定义

- **站点从 6 个扩到 18 个**：补上 `ku6_07`、`56_07`、`cctv06`、`acfun08`、
  `bilibili09`（2000 年代）与 `bilibili11`、`youku11`、`iqiyi11`、`cctv12`、
  `youtube11`、`bilibili14`、`youtube15`（2010 年代）。
- **预设现在指名编码器**：`VideoCodec` / `AudioCodec` / `RecompressContainer`
  三个字段，第二遍默认用站点当年的编码器（flv1 在 `.flv`、H.264 在 `.mp4`、
  VP9 在 `.webm`）。同码率下编码器不同，画面根本不是一回事——这是本次最重要的
  行为变化。
- **`--era-modern`** 回到 v7.9.5 的行为（libx264 + AAC 的 mp4 观看版）；
  `--era-native` 是默认，也是给脚本自证用的显式写法。**CLI 不从配置继承这个
  偏好**（脚本必须可预期），CUI 菜单 14 的 `m` 才会记进配置。
- **直播层兜底**：`--era bilibili09` 在不给 `--encoder` 时用 flv1 编码直播那一遍；
  显式 `--encoder` 永远优先；构建里没有那个编码器时保留当前选择并打 note。
- **`qq06` 的 15 fps 改成 25 fps**：腾讯当年的播放器是那批站点里唯一把帧率拉高的。
- **自定义预设**（`config.json` 的 `CustomEras`）：CUI 菜单 14 的 `n`/`e`/`d`
  向导，同名 id 覆盖内置行，读的时候会纠正坏编码器/写不通的容器，不会让程序
  启动失败。CLI 的 `--era` 参数解析不再只认内置表。
- 顺带：`EraPresets.All` 变成"自定义在前、内置在后"，`Find` 因此天然支持覆盖。

### 2. 系统与硬件需求写成了文档

- 新增 [`docs/requirements.md`](requirements.md)：操作系统（Windows 10 1809+ /
  Linux x86_64 glibc ≥ 2.35 / 不支持 musl 与 32 位）、CPU（2 核起、4–8 核推荐）、
  内存（2 GB 起、8 GB 推荐，附"一帧 1080p ≈ 3.1 MB × 缓冲 × 分片"的算法）、
  磁盘（输出体积 3 倍）、终端、字体、权限、网络、实测环境与性能预期。
- CUI 启动时的横幅会在**低于文档下限**时多打一行（核心数 / 内存 / 输出盘空间），
  这是提示，不是拒绝运行。

### 3. 字幕字体：读 ASS 的样式表、导入、替代

- 新增 `PostProcess/SubtitleFonts`：解析 ASS/SSA 的 `[V4+ Styles]`（按 `Format:`
  行定位 `Fontname` 列，列顺序变了也能读）与 `[Fonts]` 内的嵌入字体名。
- **字体族名直接从字体文件的 `name` 表读**（TTF/OTF/TTC，含 TTC 里的多个 face），
  所以 `msyh.ttc` 同时报 "Microsoft YaHei" 与 "微软雅黑"；匹配支持中英对照
  （宋体↔SimSun、黑体↔SimHei、微软雅黑↔Microsoft YaHei…）与同族不同 face
  （Microsoft YaHei / Microsoft YaHei UI），"随程序走"的字体优先于系统字体。
- 新增 `--list-subtitle-fonts`（要哪些、有没有、在哪）、`--font-import`
  （拷进程序 `fonts` 目录）、`--subtitle-font`（强制家族，写成
  `force_style='FontName=…'`）、`--subtitle-font-dir`。CUI: 菜单 **17 → 5**。
- 烧录前会打一行 `NOTE` 指出不在程序字体目录里的家族——缺字体**不阻止编码**
  （libass 回退），但"字幕变成方块却说成功"从此有人提醒。
- SRT/VTT 本来就不指定字体，那是"让用户自己选"的情形：`--subtitle-font` 或默认。

### 4. 二次压缩支持更多编解码器

- 新增 `PostProcess/RecompressCodecs`：视频 `libx264` / `libx265` /
  `libvpx-vp9` / `libvpx`(VP8) / `libaom-av1` / `libsvtav1` / `flv` / `mpeg4` /
  `mpeg2video` / `mpeg1video` / `mjpeg` / `wmv2` / `h263`，音频 `aac` /
  `libmp3lame` / `libopus` / `libvorbis` / `ac3` / `flac` / `copy`。
- 新旗标：`--recompress-codec`、`--recompress-quality`、`--recompress-audio-codec`、
  `--recompress-audio-kbps`。**容器跟着编码器走**（VP9→`.webm`、x265→`.mp4`、
  flv→`.flv`），写不通的组合在开跑前报错（退出码 2）。
- `--recompress-args` 里的 `-c:v` 也会被识别：不指定容器时 `_web` 的扩展名按它推导。
- 编码器不在当前 FFmpeg 构建里时直接说清楚，而不是跑到一半才失败。
- CUI 菜单 15 新增 5/6/7（视频编码器 / 质量 / 音频编码器），改的是同一段片段
  （保留用户写的 `-vf`），不会多出第二个真相来源。
- 能力表缓存多存一份"这个构建有哪些视频编码器"（`Schema` 2，旧缓存自动失效）。

### 验证

| 项 | 结果 |
| --- | --- |
| 单测 Windows | **475 × {net10.0, net11.0}** 全绿（v7.9.5 是 449，新增 26 条） |
| 单测 Linux | **474 = 473 通过 + 1 跳过 × 双 TFM**（映射盘用例 Windows-only；跳过的是读真实字体） |
| e2e Windows | **186 = 184 通过 + 2 跳过**（新增 9 条：原生编码器、站点自带编码器、VP9 容器推导、容器冲突、字体清单/强制/缺失提示） |
| e2e Linux | **186 = 184 通过 + 2 跳过**；两阶段 317 s + 224 s（2 核） |
| 实测产物 | `--era youku07` → `.flv` 里 flv1 352x264@15 320k；`--era-modern` → mp4 里 h264；`--recompress-codec libvpx-vp9` → `.webm` 里 vp9+opus；`--recompress-codec libx265 --recompress-audio-codec libopus` → mkv 里 hevc+opus |

⚠ 行为变化：

1. `--era <站点>` 的二次压缩产物**扩展名变了**：2000 年代站点是 `.flv`，
   `youtube15` 是 `.webm`。需要原来的 mp4 就加 `--era-modern`。
2. `--era <站点>` 在没给 `--encoder` 时会改变直播层编码器（这是需求）。
3. `--recompress-container` 的默认值不再是 `mp4`，而是"由编码器决定"。
   只有 `--recompress-args` 里带 `-c:v` 的写法才会改推导结果；不带就仍是 mp4。

## v7.9.5（2026-10-23）

**主题：不再相信 `ffmpeg -h encoder` 的声明表。** v7.9.4 修的是 mpeg1video 一个
坑，这一版把 35 个编码器 × 14 个容器 × 13 个帧率全部跑了一遍
（`tools/capability_sweep.py`），把同类坑一次性扫干净；顺带补上 2000 年代六个
视频站的二次压缩码率、让二次压缩可以脱开直播编码单独跑，并把每次运行的进程启动
次数从 8–10 次压到 3 次。完整矩阵见 [`capability-matrix.md`](capability-matrix.md)。

### ⚠ RealMedia / RoQ 的帧率是整数

`--encoder rv20 --fps 23.976` 以前会"成功"：工具按 23.976 编码、报告里写 23.976，
但 **RealMedia 头里的 fps 字段是整数**，文件里存的是 23.000。实测：
23.976→23、29.97→29、59.94→59，三档全部被改写。`roqvideo` 更糟 —— 同一个字段，
它直接拒编（`Error while opening encoder`），整个 run 死在前置检查。

两个编码器都**没有声明帧率表**（`-h encoder=rv20` 只有一行像素格式），所以以前的
"空列表 = 无约束"逻辑对它们等于没校验。现在 `EncoderMath.UsesWholeFps` 让这三者
四舍五入到最近的整数（23.976→24、29.97→30、59.94→60），文件与报告从此一致。
报告里"FPS 支持"一栏对它们显示"只接受整数"，而不是"（未声明）"。

### ⚠ 容器表按实测重写

`ContainerChoicesByEncoder` 原来是手写的"看起来合理"的清单。这次每一对都用三重
校验量过：muxer 接受、`ffprobe` 用编码器自己的名字读回来、抽帧确认画面还在。
只测第一条会得到假表 —— AVI 会把 RV20 存成 `rawvideo`、rm 会把 MPEG-4 的 tag 改成
`rv20`、Matroska/ASF 会把 msrle 的调色板丢掉（同一段素材 AVI 里 YAVG 123，Matroska
里 YAVG **16**）。

新增可用的组合（此前一律报错）：

| 编码器 | 新增 |
| --- | --- |
| mpeg1video / mpeg2video | **matroska、mov、mp4** |
| mpeg4 / libxvid | **matroska、mov** |
| libx264 | **matroska、mov、flv** |
| libtheora | **matroska、avi** |
| flv | **avi、matroska** |
| mjpeg | **matroska、mov、mp4** |
| h261 / h263 | **matroska** |
| msrle | **mov**（avi 本来就有；Matroska/ASF 会黑屏，永久排除） |
| cinepak / asv1 / asv2 / cljr / snow / zmbv / msmpeg4v2 / msvideo1 / flashsv | **matroska** |

没进表的组合仍然是拒绝：`rv20 → avi`、`mpeg4 → rm`、`qtrle → avi` 这类"muxer 收下
但存成别的东西"的搭配没有一条被放进来。

### 无帧率的容器会说明（h261 / h263 / a64）

原始 `.h261` / `.h263` / `.a64` 是裸码流，**文件里没有帧率字段**：15 fps 编出来的
文件，ffprobe 一律读回 25/1（它对裸流的缺省猜测）。画面没问题，但"帧率随文件走"
这件事不成立。工具现在会打印一条 note 并指出 AVI 是保留帧率的选择，CLI 与 CUI
都有。

### 站点预设：2000 年代六个视频站

`--era` 除了五个"时代"，现在还有六个**站点**：`sina06`、`sohu06`、`qq06`、
`tudou06`、`youku07`、`youtube07`。每个给的是那个站自己的转码档位 ——
分辨率、帧率、视频码率、音频码率 —— 用 `-b:v`（站点是按码率转的，不是按 CRF），
并且**不碰直播编码层**：站点说明不了你的素材是怎么录的，所以"不限定编码器"。

```bash
# 直播层还是 mpeg4/AVI，只有第二遍变成优酷 2007 档
LiveArchiveEncoder in.mkv out.avi --encoder mpeg4 --era youku07 --recompress
```

依据与取值见 [`era-presets.md`](era-presets.md#站点预设v795)。

### ⚠ 二次压缩可以单独跑

以前 `--recompress` 只存在于一场直播编码之后，于是"把这个文件过一遍网站的转码"
必须先重新录一遍。现在：

```bash
# 直接吃一个已存在的文件，输出 <name>_web.mp4
LiveArchiveEncoder clip.mkv --recompress-only --era youku07
# 自己给参数
LiveArchiveEncoder clip.mkv --recompress-only \
    --recompress-args "-vf scale=320:240,fps=15 -c:v libx264 -b:v 240k" \
    --recompress-container flv
```

没有编码器探测、没有分片、没有预检：输入、片段、输出三件事，跑完就退出。
CUI 里是菜单 **15 → 4**（"直接二次压缩一个文件"）。

### ⚠ 每次运行的进程启动次数：8–10 → 3

`ffmpeg` 每次启动约 **210 ms**（本机 10 次 `ffmpeg -version` 共 2.10 s）。以前一场
单遍编码在解码第一帧之前就要起 8–10 个进程：`-encoders` 两次（视频、音频各一次）、
`-filters`、`-h filter=scale`、`-h encoder=NAME`、`-version`、视频预检、音频预检。

三处改动：

1. **能力表落盘缓存**（`FFmpeg/BuildProfileStore.cs`）。这些答案只跟二进制有关，
   与本次运行无关。"二进制身份" = 路径 + 大小 + mtime 的哈希，缓存写
   `<配置目录>/cache/caps-<hash>.json`，一次 `-encoders` 同时喂视频/音频/字幕三个
   列表。首次 4 次启动，之后 **0 次**。
2. **预检合并成一次**。音频预检本来只证明 `-c:a` 能打开，而视频预检的命令行里加
   一条 `-map 0:a:0 -c:a ...` 就能问同一个问题 —— 现在预检就是最终命令的缩样，
   还顺带验了真实的码率/声道/采样率。
3. 缓存坏了当没有：schema 版本不符、身份不匹配、JSON 损坏都退回"重新扫一遍"，
   不会让运行失败。CUI 菜单 **5**（重新扫描编码器）会强制重建；
   `LAE_NO_CAPS_CACHE=1` 对所有入口生效。

实测（同一台机器、同一份 2 秒素材）：改动前 10 次启动，改动后**首次 8 次、
之后 3 次** —— 剩下的三次正好是预检、解码、编码。

### 顺手清掉的

- `FFmpegCapabilities.DetectVideoFilters` / `DetectSubtitleEncoders` 与
  `FFmpegDiscovery.DetectLegacyEncoders` / `DetectAudioEncoders`：都是"起进程再
  解析"的薄包装，现在只剩解析那一半（`ParseFilterNames` / `ParseLegacyEncoders` /
  `ParseAudioEncoders` / `ParseSubtitleEncoders`），进程调用集中在
  `BuildProfileStore.Sweep` 一处。
- `Preflight.Audio` 整个删掉（被合并的预检取代）。

### 验证

| 项 | 结果 |
| --- | --- |
| 单测 Windows | **449 × {net10.0, net11.0}** 全绿（v7.9.4 是 424；新增 25 条：整数帧率、容器表自洽、pal8 容器、缓存身份、站点预设、`--recompress-only`） |
| 单测 Linux（cia1，Ubuntu 22.04 / glibc 2.35） | **448 × {net10.0, net11.0}** 全绿（少的 1 条是映射网络驱动器用例，Windows-only） |
| e2e Windows | 179 = 177 通过 + 2 跳过（a64 无 demuxer）；398 s + 99 s |
| e2e Linux | 179 = 177 通过 + 2 跳过；321 s + 209 s（2 核） |
| 进程启动 | 单遍 10 → 首次 8 / 热 3（`ffmpeg` 进程计数实测） |
| 实测产物 | `rv20 --fps 23.976` 文件里是 24/1；`roqvideo --fps 29.97` 是 30/1；`mpeg4 --container mkv`、`libx264 --container flv`、`msrle --container mov` 的 codec 与画面都对 |

### 升级注意

- 缓存文件在 `<配置目录>/cache/` 下，可以随手删；删了只是下次慢一点。
- 站点预设只写二次压缩层：用它之后菜单 15 显示的参数是站点档位，**直播层仍是
  你原来选的编码器**。
- `--recompress-only` 不读设置文件里的输入输出，只认命令行给定的那个文件。

---

## v7.9.4（2026-10-22）

**主题：把三个报上来的编码缺陷修到底。** 一个是"能编码但画面全黑"，一个是"合法的
容器组合被拒"，一个是"声明支持、实际拒绝的帧率把整场分片跑废"。修第三个时又牵出
第四个：并行产物写 AVI 时，容器头里的帧率是假的。

### ⚠ msrle 编码出来是黑屏

`--encoder msrle` 出的片子能播、有时长、有音轨，但画面纯黑。根因有两个，都在
pal8 这条路上：

* **rawvideo 的 pal8 帧比 `w*h` 多 1024 字节**（调色板跟着每一帧走）。管道读写
  按 `w*h` 切帧，于是每帧少读 1024 字节、帧数据整体前移了 1 KB —— 实测这份
  BtbN 构建：64x48 的 pal8 rawvideo 每帧 **4096** 字节，不是 3072。索引颜色编码器
  的全部颜色都在调色板里，调色板位置一错就是黑屏。
  `PixelFormatUtil.RawFrameSize` 现在对 pal8 加回 1024 字节（`rgb8` / `bgr8` 是
  打包的 8 位色，不加）。
* **Matroska 暂存会丢调色板**。分片一律先写 `.mkv` 再拼（v7.9.2 的铁律），而
  Matroska 里未知的 VfW 编码器只存 `V_MS/VFW/FOURCC` + `BITMAPINFOHEADER`，那个
  头里没有调色板。实测同一段 2 秒素材：直接写 AVI 是 YAVG **123**，过一趟 `.mkv`
  之后是 YAVG **16**（纯黑）。所以 pal8 编码器现在**拒绝分片**，改单遍编码
  （`--parallel` 会打印"已跳过分片"并说明原因），而不是交出黑屏产物。

实测（2026-10-22，用户报告的完整命令）：修复前画面纯黑，修复后首帧 YAVG 122.6，
颜色条与运动元素都在；20 秒 384x216 msrle + mp3，302 槽 / 106 新帧。

### ⚠ mpeg1video / mpeg2video 不能写 AVI

`... --encoder mpeg1video ... out.avi` 被拒：
`Output extension '.avi' does not match selected muxer 'mpeg'`。AVI 从 v7.8 起就是
这两个编码器的合法目标（`ContainerChoicesByEncoder` 里有），但扩展名检查只认
**编码器的默认容器**，于是"用户把输出名写成 .avi"这件事完全没有生效路径 ——
只有额外再给一个 `--container avi` 才行。

现在**没给 `--container` 时，输出扩展名就是容器请求**：`EncoderMath.ResolveByExtension`
在 mpeg1video/mpeg2video（以及 mpeg4→mp4、libxvid→mp4、libx264→mkv/avi）上生效，
CLI 与 CUI 都会打印一句说明。扩展名不属于该编码器任何容器时（`rv20` + `.mkv`）
仍按原样报错，不做静默兜底。

### ⚠ mpeg1video 的 fps 校验（声明值与实际能力不一致）

`ffmpeg -h encoder=mpeg1video` 的 "Supported framerates" 里**明明列着 5/1、10/1、
12/1、15/1**，但真正开编码器时全部被拒：

```
MPEG-1/2 does not support 15/1 fps
```

那是 libmpeg3 的"unofficial economy rates"，libavcodec 只在降低 strict 标准时
才允许。实测（2026-10-22）：mpeg1video 接受 24/23.976/25/30/29.97/50/60/59.94，
20/22/45 这类会被悄悄重定到最近的合法值，而 5/10/12/15（以及最近邻落在它们身上的
13/14/16/18）直接失败；mpeg2video 因为有 frame rate extension，5/10/12/15/75 全部
真的能编。

于是新增 `EncoderMath.UsableRates`：mpeg1video 只报**八个真正的 frame_rate_code**
（报告里的"FPS 支持"也跟着改），amv 用它自己的 22050 整除表，其余编码器照旧用声明
列表。请求 15 fps 现在会就近落到 23.976 并打印一句说明，而不是让四个分片在预检里
同时炸掉。

**CUI 的另一半**：这条校验以前只在"手动换编码器"和"改 fps"时跑。设置文件里存着
`Encoder=mpeg1video` + `Fps=15` 时，菜单 9 直接开跑 —— 正是报告里的场景。现在
`StartEncoding` 在编码前用同一套规则再校验一次（顺带按上面的扩展名规则定容器）。

### ⚠ 并行产物写 AVI 时，容器头里的帧率是假的

修 AVI 容器这条路时量出来的：AVI 把帧率写在文件头里（dwRate/dwScale），而从
Matroska 暂存流复制进来的码流没有 codec 帧率，只有 1/1000 的时间基，于是 muxer
自己编一个。实测：13 fps 的 mpeg4 探回来是 `r_frame_rate 13/1` 但
`avg_frame_rate 1000/1`；mpeg1video 是 `48000/1001` / `1000/1`。按 avg_frame_rate
播放的播放器会拿到荒唐的帧率。

并行路径在回封到 AVI 时补上 `-fps_mode passthrough -r <编码帧率>`：只改容器头，
不丢帧不插帧（480 槽进、480 帧出已核对）。其它目标容器不加（MP4/Matroska 自己
带时间基，MPEG-PS 的帧率在码流序列头里）。

### 验证

| 项 | 结果 |
| --- | --- |
| 单测 Windows | **424 × {net10.0, net11.0}** 全绿（新增 16 条：pal8 帧长、可用帧率、扩展名定容器） |
| 单测 Linux（ci1 Ubuntu 26.10） | **423 = 422 通过 + 1 跳过** × 两个 TFM（跳过的是 `PathMeansSystemFfmpeg`） |
| e2e Windows | **167 项 = 165 通过 + 2 跳过**，两阶段 409 s + 113 s |
| e2e Linux（ci1） | 两阶段全绿（27 独占 + 138 通过 + 2 跳过，330 s + 190 s） |
| 产物实测（JIT / AOT × Windows / Linux） | 四个产物各自跑 2 秒 msrle：YAVG 121（非黑）；`mpeg1video out.avi --fps 15` 四个都写出 AVI + 23.976 fps，容器头 `r_frame_rate = avg_frame_rate` |

新增回归：pal8 帧长与 rgb8 的区别、mpeg1video 拒绝 unofficial 帧率、mpeg2video
保留全部声明帧率、扩展名→容器（含不可能组合仍报错）、msrle 产物非黑、AVI 头帧率
一致、CUI 保存的 fps/扩展名在开跑前被纠正。

---

## v7.9.3.1（2026-10-22）

**主题：全项目复查。** 没有新功能，只修真实缺陷、清死代码、拆文档。结论是找到
**11 处该修的地方**，其中 3 处会让程序崩掉或静默丢设置。

### 会崩 / 会丢设置

* **`--audio-mode` 拼错会让程序崩掉**。非法值从 `Parse()` 里抛 `FormatException`，
  而 `Main` 只看返回的错误串，于是 `--audio-mode bogus` 变成未处理异常 + 堆栈；
  现在和其它非法值一样打印 `ERROR: ...` 并退出 2。
* **配置文件里 `null` 会崩在启动**。`"FpsLevels": null` / `"Recompress": null` /
  `"Language": null` 这类手写或第三方文件，`Apply` 会在 `Count` 上 NRE，进程直接
  死掉且报错指向 `ConfigStore`（与用户刚编辑的文件毫无关系）。所有列表、嵌套对象、
  字符串都做了空值兜底，落到各自的默认值。
* **读不懂的设置文件会把当前设置冲成默认值**（三处同一个根因）。
  `ConfigStore.Load` 对读不懂的 JSON 返回"默认快照"，于是：菜单 19 载入一个坏
  档案 → 整场会话的设置被静默清空还报"载入成功"；`--emit-args=坏文件` → 打印
  **默认设置**的命令行并退出 0。新增 `ConfigStore.TryLoad`（读不懂返回 null），
  两处都改成明确报错、一个字段不动。

### 会算错 / 会崩产物

* **`--era` 会盖掉显式旗标**（⚠ 行为变化）。手册一直写"预设是兜底，不是覆盖"，
  但代码是 `era?.Width ?? args.Width` —— 而每个属性都有非空默认值，于是
  `--era flv07 --width 320` 实际按预设的 352 编码。现在 fps / 宽高 / limit /
  scale-algo / 音频码率与声道都记住"用户是否显式给过"，显式值优先；只给 `--era`
  时仍然是预设生效（`flv07` → 352x264 @15 / 300 kbps）。
* **`--parallel` 的 1 ms 定时器会被先完成的分片关掉**。`HighResolutionTimer` 原来
  是一个布尔量，N 个分片各自 `Enable`/`Disable`，第一个跑完的分片就执行了
  `timeEndPeriod(1)`，剩下还在编码的分片被退回 15.6 ms 粒度。改成引用计数 + 锁，
  最后一个持有者才交还。
* **`FrameBufferPool.Release` 没有重复释放保护**。自由表是定长数组，重复释放会写出界
  或把同一块 buffer 交给两个消费者。现在超量释放直接忽略（当前流程不会触发，但失败
  形态是静默花屏而不是调用点报错，值得一道护栏）。
* **分片的音频判定用浮点相等**：`hasAudio` 原来是
  `AvgTotalKbps != AvgVideoKbps` 这个双精度比较，现在由编码器直接给出
  （`EncodeResult.HasAudio`）。

### 远程源与跨平台

* **SMB 源的预演与 hwaccel 探测缺 `-rw_timeout`**。`Preflight.Video` /
  `Preflight.Audio` / `Hwaccel.Probe` 只加了 HTTP 的 `-reconnect` 家族，漏了共享
  侧的超时；一次卡住的 CIFS 读会把"1 秒预演"拖成 120 秒超时，看起来像"这个
  hwaccel 方法不可用"，理由完全错了。
* **档案名归一化在两个平台上不一致**（Linux 跑单测才抓到）。`Path.GetFileName` 只按
  **原生**分隔符切，所以 `..\..\evil.json` 在 Linux 上留下反斜杠、变成书架里一个
  奇怪的文件名（不逃逸目录，但同一输入两平台行为不同）。改成显式按 `/` 和 `\` 切。

### 界面与补全

* **CUI 换编码器时音频编码器被"先清后覆盖"**。原代码先把不合容器的编码器清空，
  下一行又无条件重新选择，中间那句"still works 就留下"的意图从未生效。现在只在
  真的要替换时提示一句，然后按容器选。
* **`--completion` 漏了 7 个旗标**。`--video-stream` / `--audio-stream` /
  `--subtitle-stream` / `--subtitle-codec` / `--x264-profile` / `--x264-level` /
  `--list-streams` 在任何 shell 里都补不出来，`--video-str<Tab>` 无反应。
* CUI 启动横幅重复打印了两次配置文件路径，现在一行给路径、一行给"已恢复 + 时间"。

### 优化与归档

* 全项目逐个声明交叉核对引用，零引用的 5 个成员移入
  `archive/retired/RetiredHelpers.cs`（带退役版本与替代说明）：
  `Probe.HasAudio`、`Probe.Stream`（都被单次运行的 `Probe.Source` 取代）、
  `SubtitleTrack.FormatMode`、`EncoderMath` 里最后一份传输像素格式偏好表、
  v7.7 的布尔版 `ArgsBuilder.BuildFilters`。
* `SubtitleTrack.CopyInputArgs` 删掉一个从未被读取的 `duration` 参数（输出时长由
  编码器统一加 `-t`）。
* `Smb.Normalize` 两分支返回同一个值，合并成一行。
* `AudioCatalog.SupportsContainer` 上挂错了一段"已退役成员"的文档注释，改成描述
  这个方法本身。

### 文档

* 本文件独立记录变更；`usage.md` 第 11 节缩成"与 Python 版的关系"，逐版清单搬到这里
  （1925 → 1670 行）；`README.md` 删掉五个逐版小节，改为指向本文件。
* `usage.md` 里 `--era` 那条改成列出全部 8 个"显式优先"的旗标，并注明 v7.9.3.1
  之前只是文档里成立；菜单 19 补上"坏文件被拒绝、一个字段都不动"。

### 验证

| 项 | 结果 |
| --- | --- |
| 单测 Windows | **408 × {net10.0, net11.0}**（新增 16 条） |
| 单测 Linux（ci1 Ubuntu 26.10） | **407 = 406 通过 + 1 跳过** × 两个 TFM |
| e2e Windows | **162 项 = 160 通过 + 2 跳过**，两阶段 384 s + 111 s |
| e2e Linux（ci1） | 两阶段全绿（26 独占 + 134 通过 + 2 跳过） |
| NativeAOT 冒烟 | Windows `CS_EXE` 跑 cui_smoke + config_conversion 16 条全过 |
| Linux 产物 | 解包后 `--cpu-info`、CUI 横幅 v7.9.3.1、2 秒编码 → mpeg4+mp3，JIT/AOT 都过 |

新增回归：非法 `--audio-mode`、`null` 配置、坏档案、`--era` 与显式旗标、
定时器计数、帧池重复释放、补全旗标、`--emit-args` 坏文件。

---

## v7.9.3（2026-10-22）

两个交互式 shell 的小功能（未单独发布 release，随 v7.9.3.1 一起发布）：

* **菜单 18 显示命令行**：把当前设置打印成一条等价命令行，和 `--emit-args` 同源
  （同一个 `CliArgs` 发射器，不是手写复述）；正式编码前的确认提示之前还会自动
  再打印一次。
* **菜单 19 管理配置档案**：`configs/*.json` 列出、按编号载入、另存当前设置、
  按路径载入、删除。载入走启动恢复同一套 `Apply`，旧档案装不进当前 FFmpeg 没有
  的编码器；名字做了消毒（`../../evil` → `configs/evil.json`）。新增
  `LAE_CONFIG_DIR` 整体改配置根目录（便携 / 测试隔离）。

测试：单测 392（`ConfigProfilesTests` 7 条），e2e 157（`test_run12_*`）。

---

## v7.9.2（2026-10-22）

**修复：并行分片直接拼 flv 会把画面拼坏**（v7.8.0 起潜伏）。

现象：243 秒 / 196 Mbit/s rawvideo 源、5 段分片、flv 容器，成品播到
**00:03:14 画面停住**，声音继续到结尾。

| 检查 | 结果 |
| --- | --- |
| 产物分流时长 | 视频 194.82s / 音频 238.6s，194.8 ≈ 第 4 段边界 |
| 5 个分片逐个解码 | 各 730 帧、**0 解码错误**、全长 48.6s |
| 两两拼接 | `0+1` `1+2` `2+3` 全 0 错误，**`3+4` 直接 5589 处错误** |
| 分片转 mkv 再拼 | 3651/3651 帧、**0 错误** |
| mkv 拼完 `-c copy` 转回 flv | 3651 帧、0 错误、243.54s |

结论：分片没问题，`ffmpeg -f concat -c copy` 拼 flv 有问题——从第 4 次拼接起视频
码流被写坏（`Bad picture start code`），而音频一路正常、把 format 时长撑到了
238.6s，于是原来的长度校验（容差 2%）恰好放行。

修复：

1. 分片一律落 Matroska：`sNNN.mkv` → `concat` → `joined.mkv` → `-c copy` 目标
   容器（已经是 mkv 就直接 move）。多一次 remux，只搬字节不解码。
2. 拼接后**按视频包数**复核：成品视频包数要对得上各段槽位总和（容差 2%），对不上
   直接报错并提示去掉 `--parallel`。
3. 排查开关 `LAE_KEEP_SHARDS=1`：保留分片目录并打印路径（默认仍然清掉）。

回归：单测 `ShardStagingTests`（4 条）锁住"分片必须落 matroska"；e2e 新增
`parallel_flv_join_has_no_corrupt_frames`。

> 20 秒测试素材复现不出这个 bug（5 片各 4 秒恰好正常），真正的锁是单测那条。

本次只发 Windows 产物（用户在外地，只测 Windows）；Linux 数字沿用 v7.9.1。

---

## v7.9.1（2026-10-21）

* **水印功能整段退役**（⚠ 行为变化）：`--watermark` / `--watermark-position` /
  `--watermark-size` / `--watermark-color` / `--watermark-font` / `--no-watermark`
  六个旗标、CUI 第 15 项、配置里的 `Watermark` 段、`_wm` 中间产物全部移除，老脚本
  再传这些旗标会得到 `unrecognized argument`。代码归档在
  `archive/retired/RetiredWatermark.cs`（含恢复说明），drawtext 时代的字体挑选
  逻辑归档在 `RetiredFontPicking.cs`。**二次压缩保留**，`_web` 产物照旧。
* **CUI 补上字幕（菜单 17）**：选模式（burn / copy / off）、列内嵌字幕轨、指定
  外挂文件、换 copy 模式的编码器；文件与内嵌流互斥，模式 `off` 时明确提示。
  以前只有 CLI 能用字幕。
* **网络驱动器保护**：映射盘（`DriveType == Network`）也算远程路径——打印稳定性
  提示，并把并行分片强制成单遍（CLI / 菜单 11 / 配置恢复三处都拦，
  `ParallelEncoder.BlockReason` 再兜一次）。
* **配置 ↔ 命令行互转**：`--emit-args[=FILE]` 把设置打印成一条等价命令行，
  `--save-config FILE` 把这次运行写成配置文件，两者都不编码。
* **`publish/aot-avx2` 彻底删除**：产物目录、CI 步骤、文档说法一起清掉。
* **e2e 提速**：新增 `tools/e2e.py`，155 条拆成"排他 25 条独占跑 + 其余 130 条交给
  xdist"，Windows 32 核实测 **794 s → 492 s**。排他规则用 marker（`timing` /
  `shared_config`）表达，`conftest.py` 拒绝裸 `pytest -n`，宁可拒跑也不让时序断言
  变偶发失败。
* 产物：win-x64 / win-x64-aot / linux-x64.tar.gz / linux-x64-aot.tar.gz。

测试：单测 381 × 两个目标框架；e2e 153 passed + 2 skipped。

---

## v7.9.0（2026-10-21）

* **视频流 / 音频流选择**：`--video-stream N` / `--audio-stream N` 用 ffprobe 打印的
  绝对流编号（`--list-streams` 先列出）；编号不存在或类型不符在编码前被拒绝并列出
  全部流。VOB 这类多音轨源终于能点名要哪条。CUI 当时是第 17 项（v7.9.1 删水印后
  左移到 16）。
* **字幕**：`--subtitle FILE`（可重复）与 `--subtitle-stream N` 二选一，
  `--subtitle-mode burn`（默认，libass 渲染，带 `fontsdir`）或 `copy`（按容器从
  `ffmpeg -encoders` 挑编码器封装成字幕轨）。位图字幕（dvdsub/dvbsub）只能 copy。
* **Turbo 彻底移除**（⚠ 行为变化）：`--turbo` → `unrecognized argument`；实现归档
  在 `archive/retired/RetiredTurbo.cs`。
* **字体相对路径修复**：裸文件名先到工具字体目录找；顺带修了 simsun.ttc 首个 face
  是单色 1bpp 被 drawtext 拒绝的问题（水印随后在 v7.9.1 退役，这段逻辑归档）。
* **AVI 容器扩容**：h261 / h263 / h263p 加入 AVI 白名单，从裸流（无音频）变成能带
  音轨。
* **HTTP/HTTPS 输入（初步）**：`https://archive.org/download/...` 这类直链可作输入，
  带 `-reconnect` 家族 + 30s socket 超时，seek 依赖服务器支持 Range。不处理鉴权、
  Cookie、HLS/DASH。
* **顺手修的两个老 bug**：`ffmpeg -filters` 的 flags 列宽是 2 字符（旧正则要求 3 →
  所有未标记滤镜从未被检出，字幕滤镜因此"不存在"）；ffprobe 源超时 30s 不够
  （179MB VOB 实测 2m15s），放宽到 300s。

---

## v7.8.3（2026-10-20）

* **测试框架双换代**：单元测试 NUnit → **TUnit 1.75.0**（走 Microsoft.Testing.Platform，
  原生 AOT 友好、源码生成、无反射）；e2e 六个独立脚本合并为 **pytest 9.1.1** 套件
  （`tests/e2e/test_*.py` + 共享 `harness.py`）。用例数与判据不变，只换引擎。
  迁中顺修 NUnit 时代遗留的集合断言按引用比较（TUnit 的 `IsEqualTo` 对集合是引用
  相等，统一改 `IsEquivalentTo`）。
* **"并行分片 + SMB 冻结"修复取消**：根因定位到"解码器提前 EOF 时旧代码静默用上一帧
  填满槽位"，但这是 `option_smoke` 既有断言的依赖项，改了会动长年稳定的输出形态，
  决定不修，根因留在代码注释里等 v8 评估。

---

## v7.8.2（2026-10-20）

* **音频数字边界表**：以前只有采样率白名单、没有码率约束，白名单本身也不全
  （`mp2` 实际从 16000 Hz 起、`aac` 支持 96000、`libvorbis` 支持 12000/24000）。
  现在 `Core/AudioCatalog.cs` 同时约束采样率与码率，**所有数字都是拿工具链下载的
  FFmpeg master 逐个编码器 × 采样率 × 码率探出来的**：
  - 码率按编码器、必要时按采样率分带：`mp2` 在 44.1 kHz 起要 32 kbps 且只认标准
    码率（36 不行、144 只在低采样率合法），`wmav1/wmav2` 最低 24，`libvorbis` 整条
    带随采样率缩放（8 kHz：8–40；22050：16–80；48 kHz：32–224），`libopus` 最高 256；
  - **容器级约束**：FLV 对 MP3 只接受 11025/22050/44100/48000，MP4 拒绝低于
    16 kHz 的 MP3 —— 这类失败以前只在 FFmpeg 跑到一半才暴露。
* **显式报错、预设纠正**：命令行显式写的采样率/码率不合规 → 退出码 2 并列出合法值；
  `--era` 带来的值 → 就近纠正（dialup98 + FLV 的 16 kHz → 11025，+ WMV 的
  16 kbps → 24）并在日志说明。菜单 16 也改成"纠正并告知"。
* **拒绝实验性编码器**：`vorbis` / `opus`（不带 lib 前缀）要 `-strict` 才打开，
  选了必然失败；现在直接报错并提示换 `libvorbis` / `libopus`，OGG 默认音频列表也只剩
  这两个。

单测 298（新增 18 条边界护栏）。

---

## v7.8.1（2026-10-19）

* **一个产物同时支持 .NET 10 与 .NET 11**：默认目标框架回到 `net10.0`，发布带
  `RollForward=Major`；只有 .NET 11 运行时的机器也能启动。显式
  `-p:LaeTfm=net11.0` 仍可用，CI 两套都跑。
* **采用 .NET 10 / C# 14 特性**（行为不变）：扩展成员
  `Core/TextExtensions.cs`（`value.HasText` / `value.Or(...)`，替掉 28 处
  `string.IsNullOrWhiteSpace`）、`System.Threading.Lock`、`FrozenDictionary` /
  `FrozenSet`（Constants 里所有只读查表）、28 条"是否老编码器"正则合成一条
  `[GeneratedRegex]`。
* **只构建 JIT 与 AOT**：第三种产物 `publish/aot-avx2` 移除——指令集基线只影响 C#
  侧，编码在 FFmpeg 里自行派发 SIMD，实测差异在噪声内。
* **代码整理与归档**：全项目交叉核对引用，零引用成员移入 `archive/retired/`；
  删 3 处无用赋值、2 组未使用参数；新增 `Directory.Build.props` 统一属性、
  `AnalysisLevel=latest-recommended`，构建零告警。

---

## v7.8.0（2026-10-19）

* **按编码器 + 容器扫描音频编码器**：只列出当前容器**真的能复用的**编码器，显式
  选择时校验采样率/声道数。
* **容器可切换**：mpeg1video / mpeg2video / mpeg4 / libxvid 可用 AVI，libx264 可用
  MKV；`--container avi|mp4|mkv`，菜单"参数 29"。
* **画面比例与三种适配**：分辨率预设区分 4:3 与 16:9；`--fit crop|fit|stretch`
  决定 16:9 画面进 4:3 帧时是裁掉、加黑边还是拉伸。
* 配置升级到 v4（`Crop43` 布尔迁移为 `FitMode/CropAnchor/PadColor`）。

---

## v7.7.0（2026-10-19）

* **修复：并行分片末尾越界**。帧槽网格比媒体时间轴粗，最后一片会要一个超过片尾的
  时间（`end time 00:03:34.333 exceeds the source duration (00:03:34.280)`），整次
  编码失败。现在边界全部夹在片段终点内、最后一片精确对齐终点；规划逻辑抽成
  `ShardPlanner`，有单测。
* **并行分片显示进度**：7 个分片各打一行没法看，改成一行聚合（已完成片数 / 总帧槽 /
  百分比 / 用时），每 0.2 秒刷新。
* **升级到 .NET 11 / C# 15**（v7.8.1 起回退为默认 net10 + RollForward）。
* **Turbo 标记弃用**：功能不变，每次使用打印弃用警告。
* **动态档位不能超过基础 FPS**：阶梯只往下压，写 `20` 而基础 `15` 是笔误，菜单拒绝、
  命令行显式给 `--fps-levels` 时报 rc=2。
* **档位码率可调**：`--fps-levels` 支持 `FPS:降档码率:升档码率`（如
  `12:128:115,5:140:130`），菜单"参数 27"。不写就是 v7.6 老行为。

---

## v7.6.0（2026-10-18）

**从这一版起，C# 版不再承诺与 Python v6.3.3 的输出一致**——原有行为全部保留，新功能
只在显式开启时生效，默认行为不变。

* **后处理管线**：live 编码之后接一个独立阶段——二次压缩（模拟 0 几年视频网站转码，
  你写 FFmpeg 参数段，程序负责 `-i` 和输出），产物 `clip_live.rm` →
  `clip_live_web.mp4`。默认关闭。文字水印阶段（同版加入）在 v7.9.1 退役。
* **时代预设**（`--era ID`）：五个年代各一套 live + 二次压缩参数，从 1997 年拨号
  时代的 RealVideo G2 到 2016 年的 720p H.264。数字有出处（RealSystem G2 官方手册、
  实测的土豆 FLV 文件、杂志里的网速报道），见 [`era-presets.md`](era-presets.md)。
* **安全设计**：二次压缩的参数段逐 token 校验，`-i`、`-o`、末尾裸路径直接拒绝——
  这些写法会让 FFmpeg 覆盖你的源文件。校验发生在 live 编码**之前**。
* **字体目录**：字体放程序自己的 `fonts` 目录（与受管理 FFmpeg 同级），两个平台都不
  查系统字体；`LAE_FONT_DIR` 可覆盖。原因见 v6.3.3 段的说明。
* `compare_matrix` 从"等价性判据"降级为回归检查。

---

## v7.5.0（2026-10-19）

* **Windows + Linux 双平台**：可执行文件名、受管理 FFmpeg 目录、压缩包格式（新增
  `.tar.xz`）、播放器调用（`xdg-open`）、SMB 路径识别（CIFS 挂载点，读
  `/proc/self/mountinfo`）都按平台分支；`tools/build.sh` 对应 `tools\build.ps1`。
* **Linux 上的 SMB 读写**：识别挂载点并显示协商到的 dialect，读写两侧都实测过。
* **硬件解码改为默认关闭**（⚠ 行为变化）：`--hwaccel NAME`（`off` / `auto` /
  `ffmpeg -hwaccels` 里的任意方法）。原来默认开 CUDA 的行为在无显卡机器（含 CI
  容器）上会直接崩在 `Cannot load libcuda.so.1`。启用前会真的解一帧验证，不通过就
  回退软件解码。
* **去掉 SMB 读取侧的 `-reconnect*`**：这三个选项属 HTTP/FTP 协议，UNC 走的是
  `file`，新 FFmpeg 构建直接拒绝，导致共享上的源解出 **0 帧**。
* **FFmpeg 清单按平台过滤**：gyan.dev 不发 Linux 构建，Linux 上不再列出。
* CI 双轨：GitHub Actions（windows + ubuntu 矩阵）与 Woodpecker。

---

## v7.4.1（2026-10-18）

* **引入 NUnit**（`tests/LiveArchiveEncoder.Tests`，当时 121 条）：纯逻辑单测，不
  依赖 FFmpeg 和网络；Python harness 继续负责"与 v6.3.3 是否等价"那一层。
* 首次跑就抓到真 Bug：`note.*` / `run.*` 共 13 个词条只有英文没有中文，中文界面下
  漏回英文并把对齐顶歪；现在有词条完整性测试盯着。

---

## v7.4.0（2026-10-18）

* **SMB 读写**：修掉 `-stimeout` 这个让 UNC 输入必然失败的参数（它是 ffplay 的选项，
  FFmpeg 报 `Unrecognized option 'stimeout'`）；新增写入侧韧性参数、写前可写探测、
  输出侧协议版本报告。
* **FFmpeg 工具链**：四个发行版（BtbN / gyan.dev / avbuild / nonfree），支持切换、
  下载、删除；默认每日构建版；下载可选代理；有 `aria2c` 就多线程下载；安装目录
  `%LOCALAPPDATA%\LiveArchiveEncoder\ffmpeg\<来源>\bin`。
* **编码后播放**：`--play ffplay|external`，外部播放器可指定，留空走系统关联；默认
  `off`。
* 源码纳入 git 管理：v7.3 基线 + 按改动逐步提交。

---

## v7.3（C# 移植基线）

* **AMV 修复**（⚠ 行为变化，对 v6.3.3 的**有意偏离**）：v6.3.3 的 AMV 无论怎么调
  都只产出 **0 字节**。四条实测约束（容器必须恰好 2 条流、音频采样率须是帧率的
  整数倍、ADPCM 块大小 = 22050/fps、宽高应为 16 的倍数）逐条修正后，默认参数产出
  正常（amv 256x192 @14fps + adpcm_ima_amv 22050Hz mono）。
* **全编码器扫描**：对当前 FFmpeg 探测到的全部 35 个 legacy 编码器各跑 2 秒片段，
  检查退出码、产物非空、含视频流、时长合理。v7.3 实测 35/35 无非预期失败。
* 这一版作为"移植完成的基线"归档，后续改动在其上逐步提交。

---

## v7.0 → v7.2（C# 移植期）

Python 版（`reference/live_archive_encoder.py`，2640 行）逐行重写为 C#，目标**行为
等价**而不是"重新设计"：

| 方面 | Python v6.3.3 | C# v7 |
| --- | --- | --- |
| 帧存储 | `deque(maxlen=N)`，每帧新分配 `bytes` | 预分配 `capacity + 2` 块的缓冲池，所有权转移而非拷贝，每帧零分配 |
| 管道读取 | 3 个阻塞线程 | 3 个异步读取器直接读字节缓冲，命中关键字前不产生 string |
| 槽位定时 | `time.sleep`（~15.6 ms 粒度） | `timeBeginPeriod(1)` + 睡到绝对截止时刻（~1 ms 粒度） |
| 进度解析 | 每行 `str.split` | 基于 span，无中间字符串 |
| 取消 | `KeyboardInterrupt` | `CancellationToken` 传到循环，子进程在 `finally` 里杀 |
| 结果 | dict | 类型化 `EncodeResult` |

**刻意没动的**：FFmpeg 命令行（包括 `-threads` 的位置）、自适应 FPS 状态机、选帧
规则、滚动码率算法、预演步骤、全部默认值与常量表。因此同一条命令在两版上应产出
等价结果（`compare_matrix` 曾是这条的判据，v7.6 起降级为回归检查）。

---

## v6.3.3（Python 基线，移植起点）

单文件、只依赖标准库（Python 3.13+）、Windows 友好的 CUI/CLI 前端。设计要点：

* 优先使用本地 BtbN FFmpeg GPL 构建，运行时从 `ffmpeg -encoders` 发现老编码器；
* 探测编码器支持的像素格式与帧率，而不是把所有编码器都塞进 yuv420p；
* 保持固定输出节拍，按实测码率调整"新鲜帧"的比例（自适应降帧）；
* 只管归档/直播味的编码，压缩码流破坏、丢包、解码后损伤交给
  `frame_damage.py`（独立工具）；
* 支持 bwdif / yadif / w3fdif 反交错与多种 libswscale 算法；
* x264 专属选项只对 libx264 生效；正式编码前跑一次短预演验证滤镜/编码器/容器组合。

移植时记录的基线缺陷（C# 版逐条修过，保留在此以免被当成"行为差异"）：

| 缺陷 | 现象 | 修在哪 |
| --- | --- | --- |
| AMV 四条容器约束全错 | `--encoder amv` 只产出 0 字节 | v7.3 |
| 默认开 CUDA | 无显卡机器第一帧就崩在 `Cannot load libcuda.so.1` | v7.5（改默认 off + 真解一帧验证） |
| SMB 用 `-stimeout` | UNC 输入必然失败（那是 ffplay 的选项） | v7.4 |
| SMB 用 `-reconnect*` | 新 FFmpeg 构建拒绝，共享源解出 0 帧 | v7.5 |
| 音频只有采样率白名单、没有码率约束 | `ac3` 被要 16 kHz → 0 字节产物；`mp2`/`wmav2` 码率越界编码中途失败 | v7.8.2 |
| 字体不落地 | 最小化 Linux 没有 `/usr/share/fonts`；点名中文字体的 ASS 在只找到拉丁字体时静默变方块 | v7.6（`fonts` 目录 + `fontsdir`） |
| `-filters` 正则过严 | flags 列只有 2 字符，未标记滤镜（含 `subtitles`）从未被检出 | v7.9 |

---

## v6.2.2 → v6.3.3（Python 时代）

> **史料缺口**：建立本仓库时只保留 v6.3.3 的最终源码，6.2.2 与中间版本的源码、
> 提交记录、发布说明都没有留档。这一节不做推测，只留下能证实的东西。

能确证的只有"6.3.3 长什么样"这一件事，即上一节描述的基线：单文件、标准库、
自适应降帧、运行时发现老编码器、预演、反交错、`frame_damage.py` 独立成工具。

如果需要补齐 6.2.2 → 6.3.3 的逐版差异，只能以当时的工作记录为准；在拿到那部分
材料之前，本节保持为空而不是编造。
