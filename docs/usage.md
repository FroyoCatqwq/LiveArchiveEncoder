# Live Archive Encoder v7 (.NET 10 / .NET 11 / C#) — 使用教程

面向使用者的操作手册。想了解迁移细节与验证数据，看 `README.md` 和
`docs/test-report-v7.md`。

> **一句话说明**：把一个现代视频，以"当年直播录档"的方式，实时节奏地重新编码成
> RV20 / RoQ / MPEG-1 / MPEG-4 / WMV2 等老式编码器格式，并带自适应码率控制。
> **画面丢帧、损坏、花屏等后处理不在本工具范围内**，那是同级的 `frame_damage.py`
> 的职责。

---

## 1. 安装与构建

### 平台支持

| | Windows | Linux |
| --- | --- | --- |
| 状态 | 主要开发平台 | v7.5 起完整支持 |
| 构建 | `tools\build.ps1`（JIT / AOT） | `tools/build.sh`（JIT，可加 `--aot`） |
| NativeAOT | ✅ 需要 MSVC + Windows SDK | ✅ v7.9.1 起出包，构建需要 `clang` + `zlib1g-dev` |
| SMB | UNC 路径（`\\host\share`） | CIFS 挂载点（`/mnt/…`），需要 `cifs-utils` |
| 播放器 | `ffplay` 或 `start` 关联程序 | `ffplay` 或 `xdg-open` |
| 受管理 FFmpeg | ✅ | ✅ |

### 从 Release 下载

每个版本都在 GitHub 与 Codeberg 双端发布四个产物：

| 文件 | 平台 | 说明 |
| --- | --- | --- |
| `…-win-x64.zip` | Windows | JIT，需要 .NET 10 **或** .NET 11 运行时（`RollForward=Major`） |
| `…-win-x64-aot.zip` | Windows | NativeAOT 单 exe，不需要运行时 |
| `…-linux-x64.tar.gz` | Linux | JIT，需要 .NET 10 **或** .NET 11 运行时 |
| `…-linux-x64-aot.tar.gz` | Linux | NativeAOT 单文件，不需要运行时 |

Linux 两个包解压即用（`tar xzf … && ./LiveArchiveEncoder`，可执行位已带上）。
AOT 版在 Ubuntu 26.10 上链接，**要求目标机的 glibc 不低于构建机**；老发行版用 JIT 版
更稳。两个版本都还需要 `PATH` 上有 FFmpeg/ffprobe，或让工具自己下一套（§7.10）。

跨平台规则（写脚本时按这个来）：

- 可执行文件名：Windows `LiveArchiveEncoder.exe`，Linux `LiveArchiveEncoder`
  （无扩展名）。端到端脚本用 `EXE_NAME` 处理了这个差异。
- 受管理 FFmpeg 目录：Windows
  `%LOCALAPPDATA%\LiveArchiveEncoder\ffmpeg\<id>\bin`，Linux
  `~/.local/share/LiveArchiveEncoder/ffmpeg/<id>/bin`。
- 配置文件同级：`config.json`。
- 路径分隔符两边都接受；UNC 用 `\\host\share`，Linux 挂载点用普通路径。

### 依赖

| 需求 | 说明 |
| --- | --- |
| FFmpeg + ffprobe | 必须在 `PATH` 里，或用 `--ffmpeg` / `--ffprobe` 显式指定；也可以让本工具自己装一套（见 §7.10） |
| .NET SDK | 仅**构建**时需要，.NET 10 或 .NET 11 都行（`winget install Microsoft.DotNet.SDK.Preview`，Linux 用 `mise use -g dotnet@11.0.100-rc.2`） |
| 运行 | JIT 版只需要 .NET 10 **或** .NET 11 运行时；NativeAOT 版什么都不需要 |
| Linux 额外 | `xz-utils`（解压 `.tar.xz` 的受管理构建）、`cifs-utils`（挂载 SMB）、`samba`（要开共享时） |

老式编码器（rv20、roqvideo、amv 等）只在完整构建的 FFmpeg 里才有。推荐
[BtbN GPL 构建](https://github.com/BtbN/FFmpeg-Builds/releases)；进交互式菜单时，
启动横幅会显示 `BtbN default` 和 `GPL build` 两行，可以直接确认当前用的是哪一套。

> **注意发行版自带的 FFmpeg。** 很多 Linux 发行版为了许可证原因裁掉了 rv20、
> roqvideo 等编码器，桌面版 Ubuntu 的 `ffmpeg` 也可能来自受限仓库。用
> `--list-encoders` 确认，或者直接让本工具下载一份完整构建。

### 构建（Windows）

```powershell
# 常规（JIT）构建
dotnet build src/LiveArchiveEncoder/LiveArchiveEncoder.csproj -c Release
# -> src\LiveArchiveEncoder\bin\Release\net10.0\LiveArchiveEncoder.exe

# 或者用构建脚本，顺便出 NativeAOT 单文件版
tools\build.ps1 -Aot
# -> publish\jit\LiveArchiveEncoder.exe       162 KB，需要 .NET 10 / .NET 11 运行时
# -> publish\aot\LiveArchiveEncoder.exe       3.8 MB，单个文件、零依赖

# 按 .NET 11 目标框架构建（默认是 net10.0，见下）
tools\build.ps1 -Tfm net11.0
```

### 目标框架：一个产物，两个运行时（v7.8.1）

默认目标是 **`net10.0`**，并且发布时带 `RollForward=Major`。含义：

- 机器上只有 .NET 10 运行时 → 用它；
- 机器上只有 .NET 11 运行时 → 自动前滚到 .NET 11，照样能启动。

也就是说 JIT 产物**不需要为两个运行时出两份**。想显式按 .NET 11 编译时，用同一个
开关（`LaeTfm`，定义在仓库根目录的 `Directory.Build.props`）：

```bash
tools/build.sh --tfm net11.0
dotnet build -p:LaeTfm=net11.0
dotnet test  -p:LaeTfm=net11.0     # CI 就是这么把两套都跑一遍的
```

NativeAOT 是自包含的，选哪个目标框架都不影响它。脚本里请别再假设 `net11.0`：
`src\LiveArchiveEncoder\bin\Release\` 下的目录名是 `net10.0`（除非显式换 TFM）。

### 构建（Linux / macOS）

```bash
# JIT（默认）
tools/build.sh
# -> publish/jit/LiveArchiveEncoder

# 顺便出 NativeAOT（先装 clang 和 zlib 开发包）
#   Debian/Ubuntu: sudo apt install clang zlib1g-dev
#   Fedora:        sudo dnf install clang zlib-devel
tools/build.sh --aot
# -> publish/aot/LiveArchiveEncoder

# 只出 AOT
tools/build.sh --aot --no-jit
```

`DOTNET` 环境变量可以换掉被调用的 `dotnet`。用 mise / asdf 之类 shim 管理器装
SDK 时很有用——非交互 shell 不会把 shim 目录放进 `PATH`：

```bash
DOTNET="$HOME/dot" tools/build.sh
```

Linux 上的 NativeAOT 是**实验性**的：ILCompiler 在 Linux 上要链接系统库，不同
发行版的库布局差异很大，出问题优先用 JIT 版。

### 只构建两种产物：JIT 与 AOT（v7.8.1 起）

v7.8.1 起不再产出第三种 NativeAOT 变体 `publish\aot-avx2`（
`-p:IlcInstructionSet=avx2`）。原因：指令集基线只影响本工具的 C# 侧，而真正的编码
发生在 FFmpeg 子进程里，FFmpeg 自己按 CPU 运行时派发 SIMD。实测（`--cpu-info`
确认两套的 `Vector<byte>.Count` 分别是 16 与 32）。进程启动耗时：

| 产物 | 进程启动（`--help`） |
| --- | --- |
| `publish\jit` | 0.336 s |
| `publish\aot` | 0.268 s |
| ~~`publish\aot-avx2`~~ | ~~0.278 s~~（v7.8.1 起不再构建） |

AOT 比 JIT 快约 0.07 s（省掉 JIT 预热）。**AVX2 相对 AOT 在噪声内**，
却换来了"只能在 AVX2 CPU 上运行"的代价，所以 v7.8.1 起不再构建。

NativeAOT 需要 Visual Studio 的 **Desktop development with C++** 工作负载（要用到
`link.exe`）。如果构建环境里 `vcvarsall.bat` 定位不到 Windows SDK，会报
`LNK1181: cannot open input file 'advapi32.lib'`，此时显式给它版本号：

```powershell
tools\build.ps1 -Aot -WindowsSdkLibVersion 10.0.26100.0
```

---

## 2. 快速开始

```powershell
# 最典型：把一段视频压成 RealVideo 20 的 .rm
LiveArchiveEncoder.exe input.mp4 output.rm --encoder rv20

# 只压前 10 秒
LiveArchiveEncoder.exe input.mp4 out.rm --encoder rv20 --end-time 00:00:10

# 输出文件可以省略，自动生成 <原名>_live.<容器扩展名>
LiveArchiveEncoder.exe input.mp4 --encoder mpeg4

# 不带任何参数 → 进入交互式菜单
LiveArchiveEncoder.exe
```

**重要预期**：这是实时模拟，不是离线转码。压 60 秒的素材就要花约 60 秒（外加几秒
启动与收尾）。这是刻意设计——采集线程按源帧率取帧、主循环按 `1/fps` 吐输出槽，
只有这样才能复现当年"边录边压"的码率行为。**不要指望它快。**

---

## 3. 命令行（CLI）

### 语法

```
LiveArchiveEncoder.exe [选项] [输入文件] [输出文件]
```

输入/输出是位置参数，顺序固定；输出省略时自动生成。

### 参数全表

| 参数 | 默认 | 说明 |
| --- | --- | --- |
| `--start-time` / `--start` | `00:00:00` | 片段起点，格式 `HH:MM:SS[.mmm]` |
| `--end-time` / `--end` | 到源尾 | 片段终点，**不含**（exclusive），同样格式 |
| `--encoder` | `rv20` | 视频编码器名，必须是 FFmpeg 里能探测到的老式编码器 |
| `--audio-encoder` | 按容器自动选 | 见第 5 节对照表 |
| `--limit` | `200` | 码率上限（kbps），超了就降帧率 |
| `--recover` | `limit × 0.75` | 码率下限，低于它才允许升回帧率 |
| `--window` | `2.0` | 滚动统计窗口（秒） |
| `--fps` | `13` | 目标帧率 |
| `--fps-levels` | `13,8,5,3` | 自适应帧率阶梯，逗号分隔、从高到低 |
| `--qv` | 按编码器 | 画质：libx264 是 CRF，其它是 `q:v`，**越小越好** |
| `--threads` | `16` | 传给 FFmpeg 的线程数 |
| `--buffer-frames` | `6` | 采集缓冲深度（帧） |
| `--audio-kbps` | `32` | 音频码率；超出编码器/采样率允许的范围会报错（见 §5 音频边界表） |
| `--audio-channels` | `1` | 声道数 |
| `--audio-rate` | 跟随源 | 采样率（Hz）；编码器或容器不接受会报错（见 §5 音频边界表） |
| `--video-stream` | 第一条 | **v7.9**：要编码的视频流编号（ffprobe 打印的绝对编号），多视频轨的源用 |
| `--audio-stream` | 第一条 | **v7.9**：要保留的音频流编号，多音轨的源用（配 `--list-streams`） |
| `--list-streams` | — | **v7.9**：打印源里全部流（含编号、类型、编码、语言）后退出 |
| `--subtitle` | 无 | **v7.9**：外挂字幕文件（`.ass/.ssa/.srt/.vtt`），可重复给多份（如 clip.sc.ass + clip.tc.ass），配合 `--subtitle-mode` |
| `--subtitle-stream` | 无 | **v7.9**：使用源内嵌的字幕轨（编号同上） |
| `--subtitle-mode` | `burn` | **v7.9**：`burn` 把字幕渲染进画面（默认）/ `copy` 作为字幕轨封装进输出 / `off` |
| `--subtitle-codec` | 按容器自动 | **v7.9**：copy 模式的字幕编码器，默认按容器从 `ffmpeg -encoders` 里挑 |
| `--width` `--height` | `256` `192` | 输出分辨率（16:9 预设见 §7.15） |
| `--container` | 编码器默认 | 容器切换（见 §7.14） |
| `--fit` | `crop` | 画面适配：`crop` 裁切填满 / `fit` 完整画面＋补边 / `stretch` 拉伸变形（见 §7.15） |
| `--crop-anchor` | `center` | `crop` 时保留哪一边：`center` / `top` / `bottom` / `left` / `right` |
| `--pad-color` | `black` | `fit` 时补边的颜色，FFmpeg 颜色名或 `0xRRGGBB` |
| `--no-crop` | 默认裁剪 | 旧写法，等价于 `--fit stretch` |
| `--scale-algo` / `--scale-flags` | `neighbor` | 缩放核；`cubic` 是 `bicubic` 的别名 |
| `--deinterlace` | `off` | `bwdif` / `yadif` / `w3fdif` |
| `--deint-mode` | `send_frame` | `send_frame` 或 `send_field` |
| `--deint-parity` | `auto` | `auto` / `tff` / `bff` |
| `--deint-marked-only` | 关 | 只反交错被标记为 interlaced 的帧 |
| `--x264-preset` | `medium` | 仅 libx264 |
| `--x264-tune` / `--x264-profile` / `--x264-level` | 空 | 仅 libx264，留空＝自动 |
| `--hwaccel` | `off` | 硬件解码：`off` / `auto` / `ffmpeg -hwaccels` 里的任意名字（cuda、vaapi、qsv、d3d11va…）。**默认关闭**；开启前会真的解一帧验证驱动和显卡，验证不过就自动回退软件解码 |
| `--no-cuda` | — | `--hwaccel off` 的旧写法，保留兼容 |
| `--ffmpeg` / `--ffprobe` | PATH 里找 | 显式指定可执行文件 |
| `--list-filters` | — | 打印当前 FFmpeg 支持的反交错滤镜和缩放算法后退出 |
| `--quiet` | 关 | 不打印进度横幅和结果摘要 |
| `--no-cui` | 关 | 无输入参数时不进菜单，直接报错退出（给脚本用） |
| `--audio-mode` | `inline` | `inline` / `separate` / `auto`，见第 7 节 |
| `--emit-args` / `--emit-args=FILE` | — | **v7.9.1**：把设置打印成等价命令行后退出；带 `=FILE` 时从一个配置文件读设置（见 §7.6） |
| `--save-config` | — | **v7.9.1**：把这次运行的最终设置写成配置文件，不启动编码（见 §7.6） |
| `--help` / `-h` | — | 打印用法后退出（退出码 0） |
| `--cpu-info` | — | 打印运行时检测到的指令集（诊断用） |
| `--parallel` | `1` | 把时间范围切成 N 段并行编码再拼接（见 7.5 节） |
| `--lang` | `en` | 界面语言：`en` 或 `zh`（见 7.6 节） |
| `--completion` | — | 打印 shell 补全脚本后退出：`bash` / `zsh` / `fish` / `powershell`（见 7.7 节） |

`--start-time` / `--end-time` 都支持 `HH:MM:SS` 和 `HH:MM:SS.mmm`。`--end-time`
必须晚于 `--start-time`，且不能超出源时长。

### 退出码

| 码 | 含义 |
| --- | --- |
| `0` | 成功 |
| `1` | 编码过程中出错（输入损坏、FFmpeg 失败、超时） |
| `2` | 命令行/配置/环境问题（参数非法、找不到 ffmpeg、扩展名不匹配） |
| `130` | 被 Ctrl+C 中断 |

Ctrl+C 会被接管而不是立刻杀进程：先取消编码循环，再在 `finally` 里把子 FFmpeg
进程一起收掉，**不会留下孤儿进程**。中断后交互式菜单不会退出，改改参数可以直接
再开一次编码——每次开始编码都会拿一个全新的取消令牌，不会受上一次中断影响。

---

## 4. 交互式菜单（CUI）

不带参数运行即进入。主菜单：

```
 1. Input file        选输入文件（也可以填目录，会列出视频文件让你编号选）
 2. Output file       留空＝自动命名 <原名>_live.<扩展名>
 3. Video encoder     从 FFmpeg 探测到的老式编码器列表里选
 4. Parameters        参数页（见下）
 5. Rescan encoders   重新扫一遍视频编码器
 6. Audio encoder     扫描音频编码器：只列出"当前视频编码器 + 容器"装得下的（见 §7.13）
 7. Show FFmpeg filters
 8. Show configuration
 9. Start encoding
10. Language          界面语言，en / zh
11. Parallel shards   并行分片数，1＝关闭（见 §7.5）
12. FFmpeg toolchain  切换/下载/删除 FFmpeg 构建（见 §7.10）
13. Playback after run 编码完成后用什么播放（见 §7.11）
14. Era preset        时代预设（见 §7.12）
15. Recompress        二次压缩（见 §7.12）
16. Streams           视频/音频流选择（见 §7.16）
17. Subtitles         字幕：烧录 / 封装 / 关闭（见 §7.16）
 0. Exit
```

6 – 17 改完立即写进配置文件，下一次打开就是这次的值。

**Turbo 已彻底移除**（v7.9）：菜单里没有，命令行 `--turbo` 直接报
`unrecognized argument`（rc=2），配置文件里遗留的 `Turbo` 键被静默忽略
（见 §7.5）。

选编码器时会自动做三件事：把不支持的帧率吸附到合法值、把尺寸归一化到编码器
支持的尺寸、按容器挑一个默认音频编码器。AMV 和 RoQ 还会额外强制音频参数
（AMV 固定 22050 Hz IMA-ADPCM；RoQ 固定 `roq_dpcm` @ 22050 Hz 单/双声道），
并在屏幕上说明为什么。

**参数页**（编号 → 含义）：

```
 1. limit            2. recover          3. window           4. base FPS
 5. FPS levels       6. CRF / q:v        7. threads          8. capture buffer
 9. size (含比例)   10. picture fit     11. scale algorithm 12. deinterlace
13. deint mode      14. field parity    15. deint marked only
16. audio bitrate   17. audio channels  18. audio sample rate
19. hardware decode 20. audio encoder
21. x264 preset     22. x264 tune       23. x264 profile    24. x264 level
                    （21–24 仅在编码器是 libx264 时出现）
25. segment start   26. segment end     27. level bitrates
28. container       29. crop anchor     30. bar colour      31. resolution presets
 0. Back
```

每一项都是"回车＝保留当前值"。15、19 是开关，选中即翻转。`26` 可以填
`end` / `none` / `eof` 表示"到源尾"。16/17/18 会按音频编码器的能力**自动纠正**
（例如选了 ac3 却填 22050 Hz，会改成 32000 并说明原因；填 16 kbps 配 44.1 kHz 的
mp2 会改成 32），20 只允许填当前容器装得下的编码器。

选 `9` 之后会再打印一遍完整配置并问 `Start encoding? [Y/n]`，回车即开始。

---

## 5. 编码器 → 容器 → 默认音频 对照表

**输出扩展名必须和容器匹配**，否则直接报错退出（码 2）。

| 编码器 | 容器 | 允许的扩展名 | 默认音频 |
| --- | --- | --- | --- |
| `rv10` `rv20` | rm | `.rm` `.rmvb` | `ac3` |
| `mpeg1video` `mpeg2video` | mpeg | `.mpg` `.mpeg` `.mpe` `.vob` | `mp2` |
| `mpeg4` `libxvid` `mjpeg` `h263p` | avi | `.avi` | `libmp3lame` |
| `wmv1` `wmv2` | asf | `.asf` `.wmv` | `wmav2` |
| `libx264` | mp4 | `.mp4` `.m4v` | `aac` |
| `libtheora` | ogg | `.ogg` `.ogv` | `libvorbis` |
| `flv` `flashsv` `flashsv2` | flv | `.flv` | `aac` |
| `qtrle` `rpza` `smc` `svq1` | mov | `.mov` `.qt` | `aac` |
| `amv` | amv | `.amv` | `adpcm_ima_amv` |
| `roqvideo` | roq | `.roq` | `roq_dpcm` |
| `h261` | h261 | `.h261` | **无音频** |
| `h263` | h263 | `.h263` | **无音频** |
| `a64multi` | a64 | `.a64` | **无音频** |

### 几个会咬人的约束

* **RoQ**：宽高必须能被 16 整除。不满足会报
  `roqvideo requires width and height to be divisible by 16; got 250x190`。
  另外 RoQ 视频不是 AVI 的 codec/container 组合，**别用 `.avi` 输出**。
* **`roq_dpcm`**：只接受 22050 Hz + 单/双声道。设了别的采样率会直接被拒：
  `roq_dpcm requires --audio-rate 22050 (or leave it unset for automatic correction)`。
* **AMV**：帧率只能在 `10/14/15/18/21/25/30` 里选，音频固定 22050 Hz IMA-ADPCM。
* **MPEG-1/2**：帧率会被吸附到 `23.976 / 24 / 25 / 29.97 / 30 / 50 / 59.94 / 60`
  里最接近的一个。
* **h261 / h263**：尺寸会被强制成标准档（h261：`176x144` 或 `352x288`）。
* **h261 / h263 / a64**：容器不支持音频，自动输出纯视频。

不确定当前 FFmpeg 有哪些编码器，就用 `--list-filters` 看滤镜，或者进菜单第 3 项
直接看探测出来的列表（会连支持的像素格式一起列出来）。

### 音频数字的边界：采样率与码率（v7.8.2）

编码器和容器对音频数字各有一套硬性要求，达不到就在 FFmpeg 里半路报错、留下一个
坏文件。v7.8.2 把这些边界做成了表（`Core/AudioCatalog.cs`），所有数字都是拿工具链
下载的 FFmpeg master 实测出来的，不是抄规格书。规则分两种：

* **显式写的值**（命令行 `--audio-rate` / `--audio-kbps`、菜单里手填）——直接报错
  退出，把合法值列出来，不会带着错值开跑；
* **时代预设带来的值**（`--era`）——预设是写给"那个年代"的，不是写给某个编码器的，
  所以自动就近纠正，并在日志里说明改了什么。

已知的边界：

| 约束 | 值 |
| --- | --- |
| `mp2` / `mp2fixed` 采样率 | 16000 起（8/11.025/12 kHz 会被编码器拒） |
| `mp2` 码率 | 16/22.05/24 kHz 用 MPEG-2 带 8–160 kbps，32/44.1/48 kHz 用 MPEG-1 带 32–384 kbps；且**只认标准码率**（36 不行，144 只在低采样率合法） |
| `wmav1` / `wmav2` 码率 | 最低 24 kbps |
| `libvorbis` 码率 | 整条带随采样率缩放：8 kHz 是 8–40，22050 是 16–80，44.1/48 kHz 是 32–224 |
| `libopus` 码率 | 最高 256 kbps |
| **FLV 容器 + MP3** | 只接受 11025 / 22050 / 44100 / 48000（FFmpeg 原话 "choose from (44100, 22050, 11025)"） |
| **MP4 容器 + MP3** | 低于 16 kHz 会被 muxer 拒（"not standard"）；AVI / MKV / MOV / ASF 没这个限制 |
| `ac3` / `eac3` 采样率 | 只有 32 / 44.1 / 48 kHz |

两个容易踩的组合：dialup98（16 kHz / 16 kbps）配 FLV 会被纠正成 11025 Hz，配
WMV(ASF) 会被纠正成 24 kbps——都是就近值，不是报错。

另外，FFmpeg 里名为 `vorbis` / `opus`（不带 lib 前缀）的**实验性**编码器需要
`-strict` 才会打开，本工具不传这个参数，所以选择它们会直接报错并提示用
`libvorbis` / `libopus`；OGG 的默认列表也从 v7.8.2 起只提供这两个能用的。

---

## 6. 自适应码率控制怎么调

这是本工具的核心，也是它和普通转码最大的区别。机制：

1. 采集线程按源帧率把帧喂进一个有界缓冲（默认 6 帧）；
2. 主循环每 `1/fps` 秒吐一个输出槽；缓冲里有新帧就编码，没有就**重复上一帧**；
3. 同时滚动统计最近 `window` 秒的输出字节数，换算成 kbps；
4. 超过 `limit` 就降到阶梯里的下一档帧率；低于 `recover` 且持续够久才升回来。

所以你会看到 `Fresh frames` 和 `Repeated slots` 两个计数——**重复槽是特性，不是
bug**，它模拟的就是"带宽不够时画面卡住"。

调参建议：

| 目标 | 怎么调 |
| --- | --- |
| 想要"卡得更狠"（更像烂网直播） | 降低 `--limit`（比如 80），阶梯拉宽 `--fps-levels 13,6,3,1` |
| 想要画面尽量流畅 | 提高 `--limit`（比如 500），阶梯收窄 `--fps-levels 13,11` |
| 统计太敏感、帧率反复横跳 | 加大 `--window`（比如 4.0） |
| 源本身很糊/很简单 | 降 `--qv`（数值越小画质越好、码率越高） |
| 想关掉节流做基线对比 | `--limit 99999`（`--recover` 会自动取 limit×0.75） |

`--recover` 必须大于 0 且小于 `--limit`，否则报错
`recover must be > 0 and < limit`。

### 每档位的码率阈值（v7.7）

阶梯不只是"几档帧率"，还可以直接规定**哪一档在多少 kbps 时切入、多少 kbps 时退出**。
写法是给 `--fps-levels` 的每一项加冒号：

| 写法 | 含义 |
| --- | --- |
| `12` | 只给帧率，阈值按 `--limit` / `--recover` 推导（v7.6 及以前的老行为） |
| `12:128` | 视频码率到 **128 kbps 就降到 12 fps**，升档阈值仍用 `--recover` |
| `12:128:115` | 降到 12 fps 的阈值 128 kbps，**回落到 115 kbps 才升回上一档** |

```bash
# 基础 15 fps；到 128 kbps 降 12 fps、到 140 kbps 降 5 fps，回落到 115/130 才升回
LiveArchiveEncoder src.mp4 out.avi --encoder mpeg4 --fps 15 \
    --fps-levels 12:128:115,5:140:130
```

交互菜单里同样写在 **参数设置 → 5（动态 FPS 档位）**；
**参数设置 → 27（档位码率）** 是逐档问答式的编辑器，会先列出每档当前的推导值：

```text
=== 动态 FPS 档位码率 ===
 基础 FPS: 15
  1. 12 fps：视频达到 225 kbps 降到该档，回落到 168.75 kbps 升回上一档
  2. 10 fps：视频达到 281.25 kbps 降到该档，回落到 168.75 kbps 升回上一档
  3. 5 fps：视频达到 360 kbps 降到该档，回落到 168.75 kbps 升回上一档
 0. 返回
```

规则与边界：

- **升档阈值必须低于降档阈值**，否则阶梯会来回横跳，输入时直接报错。
- **档位帧率不得超过基础帧率**：阶梯只会往下压，写 `20` 而基础是 `15` 是笔误，
  菜单里拒绝并提示，命令行显式写了 `--fps-levels` 时报 rc=2。
  没写 `--fps-levels` 时**不做**这个检查——默认阶梯 13 fps 起，而 mpeg2video
  这类编码器会把基础帧率吸附到 12，那是正常情况，不是笔误。
- 阈值按**帧率值**记住，不是按位置：改阶梯顺序或删掉一档，阈值不会串到别的档上；
  基础帧率调低之后，已经够不着的档位连同它的阈值一起被丢弃。
- 不写阈值就是老行为：第 1 档 `--limit`×1.00、第 2 档 ×1.25、第 3 档 ×1.60，
  升档统一用 `--recover`。

---

## 7. 音频通路：`--audio-mode`

视频和音频怎么合进最终文件，有三种模式：

| 模式 | 做法 | 何时用 |
| --- | --- | --- |
| `inline`（**默认**） | 单个 FFmpeg 进程，视频走管道 + 音频同进程编码 | 默认就行 |
| `separate` | 视频单独编码，音频由第二个 FFmpeg 并行编码，最后 `-c copy` 合流 | 只在你需要它时用 |
| `auto` | 同 `separate`，但 muxer 不接受时自动退回 `inline` | — |

**别指望 `separate` 更快。** 视频段被墙钟钉死，音频单编和重封装是纯增量。实测
5 秒片段：`inline` 6.45 秒 / `separate` 7.17 秒。而且它有两个副作用：

* 并行的音频进程抢采集线程的 CPU → 帧选择漂移（mpeg1video 的 fresh 从 56 掉到 32）；
* 额外一次 `-c copy` 重封装会改写某些容器的 duration 字段（wmv2/ASF 的时长从
  5.046 变成 5.015，帧序列其实完全一样）。

音画同步不用担心：两个阶段的 `-ss` 都作用在输入上，两条流都从 PTS 0 开始，
并且用同一个 `-t` 截断。

---

## 7.5 提速：`--parallel`

> **v7.9 起 Turbo 彻底移除**：命令行 `--turbo` 会直接报
> `unrecognized argument`（退出码 2），配置文件里遗留的 `Turbo: true` 被静默忽略，
> 代码归档在 `archive/retired/RetiredTurbo.cs`。理由见 v7.9 说明——它去掉的正是
> 本工具存在的理由（模拟直播录档的节奏），且自适应码率控制在全速下必然失效。
> 想快速验证编码链路，用 `--parallel`（白名单容器）或短片段即可。

并行分片在交互式菜单里是主菜单第 11 项 `Parallel shards`，改完立即写进配置
（**默认关闭**）。命令行用 `--parallel N`（分片上限 8）。

> **v7.9.1：远程源强制单遍。** 输入在 UNC 共享、`smb://`、Linux CIFS 挂载或
> Windows 映射网络驱动器上时，`--parallel N`（N>1）会被忽略并打印原因——多个分片
> 各自 seek 同一个网络文件，会把共享吞吐打满、让每段都掉帧。判定与提示见 §7.8。

### 前提：默认慢是**故意的**，不是性能问题

v7 按 1/fps 的墙钟节奏输出，用来模拟直播录档。实测 20 秒素材：

| 模式 | 墙钟 |
| --- | --- |
| 实时（默认） | 21.4 s |

也就是说默认模式 96% 的时间在 sleep，CPU 只用了约 1.4%。
`--limit` 是码率上限（触发降 FPS），**不是速度开关**，调大它不会加速。

### `--parallel N`

把时间范围切成 N 段并发编码，再用 `ffmpeg -f concat -c copy` 拼回去。

**只对白名单容器生效**，其余自动回退单段并在终端说明原因：

| 容器 | 是否可分段 | 实测 |
| --- | --- | --- |
| `avi` `mp4` `flv` `asf` `mpeg` | ✅ | 帧数守恒，时长误差 < 0.03s |
| `rm` | ❌ 回退 | 时长漂移 +0.031s，容器被重写而非拷贝 |
| `roq` | ❌ 回退 | **第二段整段丢失**（250 帧 → 125 帧，990KB → 222KB） |

另外每段不足 4 秒也回退——每段要额外付约 1.5s 的进程启动开销。

> **v7.9.2：分片一律先落 Matroska，拼接后再 `-c copy` 转成你要的容器。**
> 起因是一个真实事故：243 秒 / 196 Mbit/s 的 rawvideo 源切 5 段，**直接拼 flv**
> 得到的成品播到 3:14 画面就停了，声音却一直到 3:58。逐片检查 5 个分片全是好的
> （各 730 帧、零解码错误），坏的是拼接本身——3651 帧里 730 帧解不出来，报
> `Bad picture start code`。同一批分片改用 Matroska 拼接，再 `-c copy` 转回 flv，
> 3651/3651 帧全好、零错误。
>
> 所以现在的链路是：`分片 → sNNN.mkv → concat → joined.mkv → -c copy → 目标容器`。
> 多出来的一次 remux 只搬字节不解码，几分钟的片子是毫秒级；换来的是拼接不再碰
> 目标容器的坑。白名单仍然有效——它管的是"这个容器/编码组合值不值得分段"。
>
> 拼接后还会**按视频包数**复核一遍：成品视频包数必须对得上各分片产出的槽位总数，
> 差超过 2% 直接报错。以前只比 format 时长，而 flv 不写流时长，音频把总时长撑住了，
> 整片画面丢失也能"检查通过"。

v7.7 起分片会打进度（每 0.2 秒刷新同一行，不会被 7 个分片刷屏）：

```text
Sharding         : 7 shards x 69 slots (4.600s each), format 'avi'
Shards           : 3/7 done, 214/481 slots (44%) 00:00:02.1
```

分片的边界按**整帧槽**划分（落在槽中间会让各段的槽数凑不齐，悄悄把成品改长或改短），
但帧槽网格比媒体时间轴粗：`ceil(时长×fps)` 个槽覆盖的时间比源多一点点。
v7.7 之前最后一个分片会因此要一个超过片尾的时间，被 ffprobe 直接拒绝：

```text
错误：shard encoding failed: shard 6: end time 00:03:34.333 exceeds the source duration (00:03:34.280)
```

现在每个边界都夹在片段终点上，最后一片精确对齐到终点——既不会越界，也不会少尾巴。

```powershell
# 只在 avi/mp4/flv/asf/mpeg 上真正生效
LiveArchiveEncoder.exe src.mp4 out.avi --encoder mpeg4 --parallel 4

# roq / rm 会打印 "Sharding skipped: ..." 后走单段，不会损坏产物
LiveArchiveEncoder.exe src.mp4 out.roq --encoder roqvideo --parallel 4
```

**实测结论：目前分段基本是净亏损。** 白名单内编码器的边际成本只有 0.06–0.26 s
（20s 素材），而每段要多付约 1.5 s 启动开销，所以 20s 源 `--parallel 4`
反而比单段慢。唯一边际成本够高、值得分段的是
**roqvideo（8.3 s）**，而 roq 恰好不能拼接。
接口留着，等固定开销压下去之后才有意义。

### 固定开销花在哪

| 阶段 | 耗时 |
| --- | --- |
| .NET 进程启动 | 0.34 s（NativeAOT 可压到 ~0.05 s） |
| FFmpeg 能力探测（`-encoders` ×2、`-filters`、`-h filter=scale`、`-h encoder=X`） | ~0.9 s |
| ffprobe 探测 | ~0.25 s（已由 3 次合并为 1 次） |
| Preflight（Video + Audio） | ~0.2 s（已改为并发） |

想再快：用 `publish\aot\LiveArchiveEncoder.exe`（AOT 单文件）。

---

## 7.6 配置持久化与中英文

### 配置自动保存

交互式菜单（CUI）在**退出时、切换语言/分片数等设置时、每次开始编码前**都会把当前
设置写进 JSON，下次打开自动恢复（顶部会打印 `Restored settings / 已恢复上次配置`）。
位置：

```
%LOCALAPPDATA%\LiveArchiveEncoder\config.json
```

恢复时会做校验：输入文件若已不存在则忽略；编码器名在当前 FFmpeg 里找不到也忽略
（换过 FFmpeg 版本不会恢复出一个坏状态）；分片数会被夹到 1..8。文件损坏只会退回
默认值，不会阻止启动。写入是原子的（先写 `.tmp` 再 move），中途崩溃不会留下半个
文件。

命令行（CLI）**不会把配置读进来**——脚本里参数都是显式给的，被一份旧配置悄悄改掉
会很危险。但每次 CLI 运行**会把这次的参数写回去**，所以 CLI 跑完之后再打开菜单，
菜单里就是上一次 CLI 的设置。语言是个例外：没给 `--lang` 时会保留配置里已存的语言，
不会因为跑了一次 CLI 就把中文界面重置成英文。

### 配置文件 ↔ 命令行互转（v7.9.1）

菜单里调好的一套设置想变成脚本，或者反过来把别人给的命令行存成菜单可恢复的配置，
用这两个旗标，都**只做转换、不编码**：

```powershell
# 菜单设置 -> 命令行：把这个文件的设置打印成一条等价命令行
LiveArchiveEncoder.exe --emit-args=my.json

# 这次运行的命令行 -> 配置文件（写完整形状，可被菜单恢复）
LiveArchiveEncoder.exe src.mkv out.avi --encoder mpeg4 --fps 15 --save-config my.json
```

| 写法 | 含义 |
| --- | --- |
| `--emit-args` | 打印**本次运行解析出来的**设置（含配置默认值与 `--era` 展开结果），然后退出 |
| `--emit-args=FILE` | 从 `FILE` 读设置并打印等价命令行；不需要输入/输出参数，也不需要 FFmpeg |
| `--save-config FILE` | 把本次运行的最终设置写成配置，**不启动编码** |

细节：

- `--emit-args` 故意**不吃下一个位置参数**：`--emit-args in.rm out.avi` 里的路径
  仍然是输入/输出。要指定设置文件只能写成 `--emit-args=FILE` 这种带等号的形式。
- 打印出来的整行可以直接回喂：`--emit-args=my.json` 的输出里已经带上输入/输出
  路径，`$(...)` / `& ( ... )` 的形式即可复现同一次运行。
- 只发射**与默认值不同**的项。派生值不发射：`--recover` 默认是 150，跟它一样的值
  不出现在输出里；帧率阶梯只发射**你真正带阈值敲进去的档位**，基础帧率由 `--fps`
  承载——否则 `13,8,5,3` 会被误当成用户自定义阶梯来回震荡。
- 退出码与普通运行一致：`--save-config` 写不进去是 `2`，`--emit-args` 的文件不存在
  也是 `2`。

### 中英文

```powershell
LiveArchiveEncoder.exe --lang zh          # 命令行用中文
LiveArchiveEncoder.exe --lang en          # 命令行用英文
```

CUI 主菜单第 10 项 `Language / 界面语言` 可直接切换，并写进配置。
接受 `en` / `zh` / `zh-CN` / `中文` 等写法。优先级：**命令行 `--lang` > 配置里存的
语言 > 默认英文**。

已翻译的范围：主菜单、参数页 31 项、编码器选择页、`Show configuration`、编码结果
摘要、进度横幅里的标签（PixFmt / Output size / Time range 之类）、SMB 提示、
字幕菜单（v7.9.1）。

注意：**FFmpeg 自身的报错和 `ERROR:` 前缀不翻译**。那些信息是要拿去搜索的，也是
和 Python 版对拍时用来比较的，翻译了就搜不到了、也不好比对了。

---

## 7.7 Shell 自动补全

程序自己就是补全数据的来源——编码器列表是从当前 FFmpeg 实时读出来的，不是写死的。

```bash
# bash
LiveArchiveEncoder --completion bash > /etc/bash_completion.d/livearchiveencoder
# 或 source 进 ~/.bashrc

# zsh
LiveArchiveEncoder --completion zsh > "${fpath[1]}/_livearchiveencoder"

# fish
LiveArchiveEncoder --completion fish > ~/.config/fish/completions/livearchiveencoder.fish

# PowerShell
LiveArchiveEncoder --completion powershell > lae.ps1
. .\lae.ps1
Register-ArgumentCompleter -Native -CommandName 'LiveArchiveEncoder' -ScriptBlock $laeCompleter
```

补全内容：所有选项名；`--encoder` 补当前 FFmpeg 实际支持的编码器；
`--scale-algo` / `--deinterlace` / `--x264-preset` / `--audio-mode` / `--lang`
等补合法取值；`--ffmpeg` / `--ffprobe` 走文件路径补全；
前两个位置参数按文件补全。

---

## 7.8 远程资源（SMB 共享 / HTTP 直链）

输入**和输出**都可以直接给远程路径，两边写法不同：

```powershell
# Windows —— UNC，正反斜杠都认
LiveArchiveEncoder.exe \\nas\share\record.mp4 out.rm --encoder rv20
LiveArchiveEncoder.exe //nas/share/record.mp4 out.rm --encoder rv20
LiveArchiveEncoder.exe record.mp4 \\nas\share\out.rm --encoder rv20   # 直接写回共享

# v7.9 —— HTTP/HTTPS 直链（初步支持，见下）
LiveArchiveEncoder.exe https://archive.org/download/<item>/<file>.mp4 out.rm --encoder rv20
```

```bash
# Linux —— 先把共享挂到某个目录，然后用普通路径
sudo mkdir -p /mnt/lae
sudo mount -t cifs //192.168.1.220/lae /mnt/lae -o guest,vers=3.1.1,uid=$UID,gid=$UID
LiveArchiveEncoder /mnt/lae/record.mp4 /mnt/lae/out.rm --encoder rv20
```

### 读取侧

识别为远程路径时会给 FFmpeg 加上 **`-rw_timeout 30000000`**（30 秒），避免网络
抖动时整个管道挂死。本地文件不会加这个参数。

> **v7.4 修正**：v7.3 的列表里有 `-stimeout`，但那是 **ffplay 的选项**，FFmpeg
> 根本不认识（`ffmpeg -h full` 里查不到）。任何 UNC 输入都会在一帧还没读到之前
> 直接失败：`Unrecognized option 'stimeout'. Error splitting the argument list`。
> 已移除。
>
> **v7.5 修正**：之前还给读取侧加了 `-reconnect 1`、
> `-reconnect_streamed 1`、`-reconnect_delay_max 5`。这三个选项属于 **HTTP /
> FTP 协议**，不属于 `file` 协议——UNC 路径走的正是 `file`。老一些的 FFmpeg 构建
> 会静默忽略未知选项，新的构建直接拒绝：
> `Option reconnect not found. Error opening input file`。结果是**源放在 SMB 共享
> 上会解出 0 帧**（fresh=0、0 字节、码率显示 48 kbps），而报错还被缓冲吃掉了。
> 现在读取、写入、ffprobe 三处统一只加 `-rw_timeout`。

### 映射网络驱动器与并行分片（v7.9.1）

Windows 上把共享映射成盘符（`net use Z: \\nas\share`、资源管理器"映射网络驱动器"，
Tailscale / SMB 网关也常常这么挂在 `Z:` 或 `Y:`）时，路径看起来是普通的
`Z:\record.mp4`，`Path.IsPathRooted` 也说是本地盘。v7.9.1 起会额外问一句
`DriveInfo.DriveType`，`Network` 就按远程处理：

```
Input          : Z:\record.mp4
SMB share      : Z:\ -- mapped network drive (remote path)
```

同时打印一条稳定性提示——这句不是客套话：

> **网络连接应尽量稳定可靠、速率稳定。**

原因在于这是个**实时采集**工具：它按墙钟节奏从源取帧、按当前码率反馈调档。线路一
抖，取帧就落后于墙钟，自适应控制器会以为"码率太高"而连续降档；恢复之后又要花时间
爬回来。结果是产物中间出现一段画质塌陷，而编码本身"成功"了。

所以远程源（UNC / `smb://` / Linux CIFS 挂载 / 映射盘）下**并行分片强制为单遍**：

| 位置 | 行为 |
| --- | --- |
| CLI | 打印提示；`--parallel N`（N>1）被忽略并打印 `parallel.networkoff`，实际用 1 |
| CUI 启动恢复 | 远程输入的配置里 `Parallel` 直接按 1 恢复 |
| CUI 菜单 11 | 远程输入下拒绝 >1，提示原因 |
| `ParallelEncoder` | 硬性回退：`BlockReason` 里再挡一次，不只靠 UI 层拦 |

分片本身没错，错的是**分段读同一个网络文件**：多路 seek + 多路解码会把共享的吞吐
打满，而每段都以为自己是唯一的读者。本地盘没有这个问题（`--parallel` 在本地照常）。

> 已知遗留：低带宽下仍有极少数场景会卡住（实测样本
> `REZero-Season2-OP-SingleTest-Subs.flv`，v7.9.1 未能复现）。真遇到时先用
> `--audio-mode inline`、关掉字幕烧录缩小范围，并保留当时的源与参数。

### HTTP/HTTPS 直链（v7.9，初步支持）

输入可以是 `http://` / `https://` 的**渐进式直链**（`/download/…/文件名` 这类，
archive.org、各种 CDN 都是）。FFmpeg 原生会说这两个协议，本工具做的是：

- 识别 URL，按 HTTP 协议给 FFmpeg 加 **`-reconnect 1 -reconnect_streamed 1
  -reconnect_on_network_error 1`**（这三个只属于 HTTP/FTP——放到 UNC 路径上正是
  v7.5 修掉的那个 bug）+ `-rw_timeout 30000000`。404 不会重试
  （`-reconnect_on_http_error` 故意不加：错 URL 重试五次只是把 2 秒的错误拖成 2 分钟）。
- 片段裁剪（`--start-time`）依赖服务器支持 **Range 请求**——archive.org 和 CDN
  都支持；不支持的本地服务器（如 `python -m http.server`）会在 seek 处报错。
- **不处理**：鉴权、Cookie、HLS/DASH 列表、需要二次读的非可寻址流。字幕烧录对
  URL 源会先把字幕轨落盘到临时文件再渲染。
- 默认输出名取 URL 的最后一段（去查询串、百分号解码、消毒分隔符），落在当前
  目录，如 `clip_live.avi`。

### 写入侧

`-reconnect*` 系列在 `ffmpeg -h full` 里标记为 `D`（decoding only），只能用在
输入端，放到输出端会被拒。写入侧唯一可用的韧性选项是 **`-rw_timeout`**（它同时
覆盖读写），所以输出为远程路径时只加这一个。

另外加了两个写侧保护：

- 编码前对输出目录做一次**可写探测**（建一个 0 字节标记文件再删掉）。实时节奏
  编码很慢，20 秒之后才发现共享是只读的太亏了。
- 输出为远程路径时在 banner 里同样打印协商到的协议版本（`SMB output : ...`）。

### SMB 版本协商

**版本协商是操作系统做的，应用程序无法直接指定。** 本程序能做的是把实际协商
结果查出来并显示，让"悄悄降级到 SMB1"这种事暴露出来：

```
SMB share       : \\nas\share -- SMB3 in use
SMB output      : \\nas\share -- SMB3 in use
```

- 协商到 SMB3 → `SMB3 in use`
- 只有 SMB2 → `SMB2 in use (server did not offer SMB3)`
- 落到 SMB1 → `SMB1 in use - obsolete and insecure; enable SMB2/3 on the server`（警告）

Windows 上通过 `Get-SmbConnection` 查询；**Linux 上通过解析
`/proc/self/mountinfo`（回退 `/proc/mounts`）**，找出该路径所属挂载点的文件系统
类型是 `cifs` / `smb3`，再从挂载选项里读 `vers=`。两者都只是"把 OS 已经决定好
的事情显示出来"。

Linux 挂载点识别不出来的常见原因：用了 symlink（指向别处的符号链接会让路径
匹配失败）、`mountinfo` 不可读、或者挂载时没写 `vers=`（此时会显示"已启用读取
重试参数，协议版本待连接后可知"）。

在 Linux 上搭一个测试共享（Fedora/RHEL 系，SELinux 会拦 samba 的家目录共享，
测试时先放宽）：

```bash
sudo dnf install samba samba-client cifs-utils
sudo mkdir -p /srv/lae && sudo chmod 777 /srv/lae
printf '[lae]\n   path = /srv/lae\n   browseable = yes\n   read only = no\n   guest ok = yes\n' \
  | sudo tee -a /etc/samba/smb.conf
printf '   map to guest = Bad User\n' | sudo tee -a /etc/samba/smb.conf
sudo setenforce 0                      # 或给 /srv/lae 打 samba_share_t 标签
sudo systemctl enable --now smb
```

客户端：

```bash
sudo mount -t cifs //<server>/lae /mnt/lae -o guest,vers=3.1.1,uid=$UID,gid=$UID
```

共享还没连接（协议版本暂时查不到）时也不会沉默，会说明"已启用读取重试参数，
协议版本待连接后可知"——至少让你知道韧性参数已经生效了。查询失败（无权限、
PowerShell 不可用）同样不影响编码。

---

## 7.9 编码器修复记录

### AMV（v7.3 修复）

v6.3.3 的 AMV 是**完全不能用的**——无论怎么调都只产出 0 字节。原因和修复：

| 约束（实测） | v6.3.3 行为 | v7.3 行为 |
| --- | --- | --- |
| 容器必须有且仅有 2 条流 | 纯视频也能进，写完才报 `AMV files only support 2 streams` | 源无音轨时**提前报错**，不再白跑一遍 |
| 音频采样率必须是视频帧率的整数倍，合法帧率 `10/14/15/18/21/25/30` | 用默认 13fps，muxer 直接拒收 | 自动对齐到最近的合法帧率（默认 13 → 14） |
| ADPCM 块大小 = 22050 / fps | 不加 `-block_size`，默认 1024 会被拒 | 自动算并加 `-block_size:a` |
| 宽高应为 16 的倍数 | 原样输出，FFmpeg 只警告，产物播放器打不开 | 自动向下取整到 16 的倍数 |
| 音频必须是 adpcm_ima_amv @ 22050Hz 单声道 | 已正确 | 保持 |
| 码率：ADPCM 无码率控制，`-b:a` 被静默忽略 | 仍写 `-b:a 32k`，误导 | 对 adpcm 不再输出 `-b:a` |

修复后默认参数实测：`amv 256x192 @14fps` + `adpcm_ima_amv 22050Hz mono`，产物正常。

> 这是对 Python 基线（v6.3.3）的**有意偏离**：原实现在这里是坏的，不是"行为差异"。

### 全编码器扫描

```powershell
pytest tests\e2e\test_feature_sweep.py                             # 默认 JIT
CS_EXE=publish\aot\LiveArchiveEncoder.exe pytest tests\e2e\test_feature_sweep.py
```

对当前 FFmpeg 探测到的**全部 35 个** legacy 编码器各跑 2 秒片段，检查退出码、
产物非空、包含视频流、时长合理，并留一份产物在 `tests\out\sweep\`。

三种结局：`PASSED` 正常；`SKIPPED` 指 FFmpeg 只有 muxer、没有 demuxer（例如
a64），产物写出来了但 ffprobe 读不回来——这是 FFmpeg 的属性，不是本程序的问题；
容器不写时长（amv / h263 / roq / h261）算通过，只打印一条 note（同样是容器
本身的行为，用纯 ffmpeg 生成的对照文件也没有时长）。只有 `FAILED` 才是真问题。

v7.3 实测 35/35 无非预期失败。少数编码器会自动修正参数并给出提示，属正常行为：

```
NOTE: h261 requires a special frame size; using 176x144.
NOTE: mpeg1video does not support 13 fps; using 23.976 fps.
```

---

## 7.10 FFmpeg 工具链（多发行版：切换 / 下载 / 删除）

v7.4 起程序可以自己管理 FFmpeg，不再依赖你手动往 PATH 里塞一个版本。

### 布局

```
Windows : %LOCALAPPDATA%\LiveArchiveEncoder\ffmpeg\<来源>\bin\ffmpeg.exe
Linux   : ~/.local/share/LiveArchiveEncoder/ffmpeg/<来源>/bin/ffmpeg
```

（`~/.local/share` 就是 .NET 的 `SpecialFolder.LocalApplicationData`；
`XDG_DATA_HOME` 设了別处就跟着变。）多版本**并存**，切换是瞬时的；下载失败也
不会破坏已经在用的那个。Linux 上解压后会自动给 `bin/` 下的文件补 `+x`。

### 来源清单

| id | 项目 | Windows | Linux |
| --- | --- | --- | --- |
| `btbn`（**默认**） | [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) | ✅ 自动，`.zip`（win64 GPL） | ✅ 自动，`.tar.xz`（linux64 GPL） |
| `gyan` | [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) | ✅ 自动，`.7z`（需要 7-Zip） | ❌ 不提供 Linux 构建 |
| `avbuild` | [avbuild（SourceForge）](https://sourceforge.net/projects/avbuild/files/) | **手动**（见下） | **手动** |
| `nonfree` | [BnqDzj/FFmpeg-Builds-nonfree](https://github.com/BnqDzj/FFmpeg-Builds-nonfree) | ✅ 自动，`.zip` | ✅ 自动，`.tar.xz` |

**清单是按平台过滤的。** `--ffmpeg-list`、shell 补全和交互式菜单只会列出当前
平台能装的来源——在 Linux 上看不到 `gyan`，因为装不了它。

默认来源是 `btbn` 的**每日构建版**（GitHub release 标签 `latest`）：Windows 取
`ffmpeg-master-latest-win64-gpl.zip`，Linux 取
`ffmpeg-master-latest-linux64-gpl.tar.xz`。

### 命令行

```powershell
# Windows
LiveArchiveEncoder.exe --ffmpeg-list
LiveArchiveEncoder.exe --ffmpeg-download
LiveArchiveEncoder.exe --ffmpeg-download nonfree --proxy http://127.0.0.1:10808
LiveArchiveEncoder.exe src.mp4 out.rm --encoder rv20 --ffmpeg-source btbn
LiveArchiveEncoder.exe src.mp4 out.rm --encoder rv20 --ffmpeg-source path   # 回到系统 FFmpeg
LiveArchiveEncoder.exe --ffmpeg-delete gyan
LiveArchiveEncoder.exe --ffmpeg-download avbuild --ffmpeg-archive C:\Downloads\xxx.7z
```

```bash
# Linux —— 参数完全一样，只是可执行文件不带 .exe
./LiveArchiveEncoder --ffmpeg-list
./LiveArchiveEncoder --ffmpeg-download
./LiveArchiveEncoder src.mp4 out.rm --encoder rv20 --ffmpeg-source btbn
./LiveArchiveEncoder --ffmpeg-download avbuild --ffmpeg-archive ~/Downloads/xxx.tar.xz
```

`--proxy` 可以省略；省略时会复用配置文件里记住的代理。

### 下载细节

- **有 `aria2c` 就优先用它**，16 连接、断点续传、走代理；没有则退回 `HttpClient`
  单连接，同样支持代理。aria2c 失败也会自动退回 `HttpClient`。
- **会校验压缩包的 magic bytes**（`PK` / `7z¼¯'` / `FD 37 7A 58 5A 00`）。有些
  站点（比如 SourceForge）会返回一个 HTML 拦截页，下载器照样会把它"下载成功"——
  没有这步你就会得到一个 131 KB 的假压缩包，然后看到解压器报一堆莫名其妙的错。
  v7.5 新增 `.tar.xz` 的 magic，Linux 构建用的就是它。
- `.zip` 用内建的 `ZipFile` 解压；`.7z` 需要 PATH 里的 `7z` / `7za` / `7zz`，
  或装在 `C:\Program Files\7-Zip\7z.exe`。找不到会明确告诉你去装 7-Zip，
  或者改用 `.zip` 的 btbn / nonfree。
- `.tar.xz` 直接调系统的 `tar -xJf`。**Linux 上如果没装 `xz-utils`，`tar` 会报
  `xz: Cannot exec`**——程序会提示 "is xz-utils installed?"，装上即可
  （Debian/Ubuntu `sudo apt install xz-utils`，Fedora `sudo dnf install xz`）。
- 装完会检查 `--enable-gpl`：非 GPL 构建会少掉 libx264 / libxvid / libtheora 等
  一批编码器，屏幕上会提示。

### 为什么要手动下载 avbuild

SourceForge 的镜像跳转被一层 Cloudflare "Just a moment..." 拦截页挡着：aria2c
和 HttpClient 都只能拿到那个 HTML 页面，而伪装成 Chrome 的完整 User-Agent 会直接
403。与其让下载器悄悄写下一个 HTML 文件当作构建，不如明确让你在浏览器里下载，
再用 `--ffmpeg-archive` 装进去。程序会直接把该打开的网址和该执行的命令打印出来。

### 交互式菜单

主菜单 `12` 进入工具链子菜单：`1` 列表、`2` 切换、`3` 下载/更新（手动来源会提示
粘贴本地压缩包路径）、`4` 删除、`5` 设置下载代理。切换或安装之后会**重新探测**
编码器/滤镜/缩放器列表——换了个构建，可用编码器是会变的；如果原来选中的编码器
在新构建里不存在，会提示你重选而不是带着一个必然失败的配置继续。

### 优先级

`--ffmpeg` 显式路径 **>** 配置文件里选定的受管理构建 **>** 程序目录 / `BTBN_FFMPEG_DIR`
**>** PATH。

> 注意：受管理构建只有在被**显式选定**时才会生效。一个没动过的配置会一直用系统
> FFmpeg——否则你随手装一个构建就会把所有脚本化运行的产物悄悄换掉。

---

## 7.11 编码完成后播放

```powershell
# 用当前 FFmpeg 自带的 ffplay 播放（推荐：rm / roq / amv 这些老容器它都能开）
LiveArchiveEncoder.exe src.mp4 out.rm --encoder rv20 --play ffplay

# 用外部播放器；不填 --player 就走系统文件关联
LiveArchiveEncoder.exe src.mp4 out.rm --encoder rv20 --play external
LiveArchiveEncoder.exe src.mp4 out.rm --encoder rv20 --play external --player "C:\...\mpc-hc64.exe"
```

- `--play off`（默认）什么都不做，脚本里跑不会弹窗。
- ffplay 优先从**当前 FFmpeg 所在目录**找（受管理构建里通常自带），找不到再查
  PATH；都没有会明确报错提示改用外部播放器。播放时加了 `-autoexit`，放完自己关。
- Linux 上 `--play external` 且不填 `--player` 时走 **`xdg-open`**（Windows 上
  是 Shell 文件关联）。
- 交互式菜单 `13` 可以设置播放方式和外部播放器路径（填 `-` 清除，回到系统关联）。
  设置成 ffplay / external 之后，每次编码结束会多问一句 `Play the result now? [Y/n]`。

---

## 7.12 后处理：二次压缩（v7.6；水印于 v7.9.1 移除）

直播录制之后、上传网站之前，网站还会做一件事：**转一遍码**。v7.6 把这一步做成
独立阶段，串在 live 编码之后：

```
live 编码 → (14) 时代预设   ← 可选，把两层参数都填好
         → (15) 二次压缩    ← 独立阶段，默认关闭
         → 成品
```

> **v7.9.1 变更**：静态文字水印阶段已移除（`--watermark*` 六个旗标、菜单里的
> 水印项、配置里的 `Watermark` 段全部没有了，命令行传旧旗标会直接报
> `unrecognized argument`）。代码归档在 `archive/retired/RetiredWatermark.cs`，
> 日后想作为独立项目捡回来见该文件头部说明。字幕烧录（§7.16）走的是同一条
> drawtext 技术路线，需要"往画面上印字"的用它。

产物命名（都在 live 产物旁边）：

| 阶段 | 产物 | 说明 |
| --- | --- | --- |
| live | `clip_live.rm` | 第一次编码，永远保留 |
| 二次压缩 | `clip_live_web.mp4` | 模拟网站转码；即最终产物 |

二次压缩**默认关闭**。

### 时代预设（菜单 14 / `--era ID`）

一代一套完整方案，同时填好 live 层和二次压缩层。**选预设不会自动打开二次压缩**
——那是你手动开的东西，预设只把参数填好。依据和出处见
[`docs/era-presets.md`](era-presets.md)。

| id | 年代 | live 层 | 二次压缩 |
| --- | --- | --- | --- |
| `dialup98` | 1997–2000 拨号 | rv20 160x120@10，上限 45 kbps | crf 34，160x120@10，音频 16k 单声道 |
| `broadband03` | 2001–2004 ADSL 初期 | rv20 320x240@15，上限 225 kbps | crf 31，320x240@15 |
| `flv07` | 2005–2008 视频网站 | flv 352x264@15，上限 300 kbps | crf 28，352x264@15，音频 22.05k 单声道 |
| `mp4_10` | 2009–2012 | libx264 640x480@25，上限 900 kbps | crf 25，640x480@25 |
| `hd14` | 2013–2016 | libx264 1280x720@30，上限 2500 kbps | crf 23，720p@30，立体声 96k |

```powershell
# 一条命令跑完"2007 年传优酷"：live 用 flv1，二次压缩参数自动填好
LiveArchiveEncoder.exe src.mp4 out.flv --era flv07 --recompress
```

`--era` 不碰你显式给的 `--encoder` / `--width` / `--limit`：预设是兜底，不是覆盖。

### 二次压缩（菜单 15）

你只写 FFmpeg 命令的**中间参数段**，`-i <live产物>` 和输出路径由程序生成：

```powershell
LiveArchiveEncoder.exe src.mp4 out.rm --era flv07 --recompress
# -> clip_live.rm      (live)
# -> clip_live_web.mp4 (二次压缩，用 flv07 预设的参数)

# 或者完全自定义（这就是 0 几年视频网站转码的写法）
LiveArchiveEncoder.exe src.mp4 out.rm --recompress --recompress-args ^
  "-vf scale=320:240,fps=15 -c:v libx264 -profile:v baseline -preset veryfast -crf 28 -ac 1 -ar 16000 -b:a 16k"
```

参数段会被**逐 token 校验**，以下写法直接拒绝（在编码**之前**，不会白跑一遍）：

| 写法 | 原因 |
| --- | --- |
| `-i other.mp4` | 输入就是 live 产物，由程序指定 |
| `-o anything` | FFmpeg 没有这个旗标 |
| 末尾出现 `out.mp4` 这样的裸路径 | FFmpeg 会把它当成第二个输出，覆盖你指定的文件 |

其余随意——`-filter_complex`、多 `-map`、`-ss` 都是你的事。

相关选项：`--recompress`（启用）、`--recompress-args "…"`（设参数并启用）、
`--recompress-container EXT`（默认 mp4）、`--no-recompress`。

### 字体目录（字幕烧录用）

字幕烧录**不用系统字体**，而是程序自己的 `fonts` 目录——和受管理 FFmpeg 同级，
libass 通过 `fontsdir=` 在那里找 ASS 文件点名的字体：

```
Windows : %LOCALAPPDATA%\LiveArchiveEncoder\fonts\
Linux   : ~/.local/share/LiveArchiveEncoder/fonts/
```

为什么不查系统字体：

- 最小化安装的 Linux（实测 Ubuntu 26.10 云镜像）**根本没有 `/usr/share/fonts/`**，
  `subtitles` 过滤器直接失败，报错看起来像编码器的 bug；
- 更糟的是，一个点名"微软雅黑"的 ASS 文件在只找到拉丁字体时**编码会成功、
  字幕却是空的方框**——错了还不报错，这是最难排查的一类问题。

- **Windows**：目录为空时可以把系统的微软雅黑 / 黑体 / 宋体复制一份进去，
  之后一直用这份拷贝。
- **Linux**：需要你自己放一个（放进去即可，不用改任何设置）。最简单的是从
  Windows 拷 `msyh.ttc` 或 `simhei.ttf` 过来；也可以从发行版字体包取：
  ```bash
  # Debian/Ubuntu
  sudo apt install fonts-noto-cjk
  cp /usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc \
     ~/.local/share/LiveArchiveEncoder/fonts/
  # Fedora/RHEL
  sudo dnf install google-noto-sans-cjk-fonts
  cp /usr/share/fonts/google-noto-sans-cjk-vf-fonts/NotoSansCJK-VF.ttc \
     ~/.local/share/LiveArchiveEncoder/fonts/
  ```
- 目录位置可以用环境变量 `LAE_FONT_DIR` 改（CI 或无用户配置目录的环境用）。
  v7.9.1 之前写作 `LAE_WATERMARK_FONT_DIR`，旧名字仍然有效。

### CLI 与配置的关系

**CLI 不读取配置里的二次压缩状态**——脚本必须可预期，`--recompress` 不给
就没有二次压缩，哪怕昨晚在菜单里开过。参数段会随 `--era` 预设填好；交互菜单
（14/15）里设置的会保存，菜单下次打开还在。`--era` 不会替你打开二次压缩，
要配 `--recompress`。

---

## 7.13 音频编码器：扫描、匹配与参数校验（v7.8）

v7.7 及以前只有一张"按容器挑默认值"的对照表，从不检查参数。于是 `dialup98`
预设（rv20 → RealMedia → ac3 @ 16 kHz）在 FFmpeg 报
`Specified sample rate 16000 is not supported by the ac3 encoder` 之后**照样
留下一个 0 字节的产物**：整轮编码跑完了才失败。v7.8 改了三处。

**① 主菜单第 6 项「扫描音频编码器」**：先扫出 FFmpeg 里所有音频编码器，再过
滤成"当前视频编码器的容器装得下"的那些，并显示各自的采样率与声道限制：

```
=== Audio encoders ===
rm container: 84 audio encoders in this build, 2 of them mux here.
   1. ac3             32000, 44100, 48000      1-6ch  (32 / 44.1 / 48 kHz only) <
   2. aac             8000-96000               1-8ch
 0. Back
```

选完之后，若当前采样率/声道数不合法会自动纠正，并打印改成了什么。

**② 参数校验**（命令行拒绝，菜单纠正）：

| 编码器 | 采样率 | 声道 |
| --- | --- | --- |
| `ac3` / `eac3` | **32000 / 44100 / 48000** | 1–6 |
| `mp2` | 22050 / 24000 / 32000 / 44100 / 48000 | 1–2 |
| `libmp3lame` / `mp3_mf` | 8000–48000 | 1–2 |
| `aac` | 8000–96000 | 1–8 |
| `wmav1` / `wmav2` | 8000–48000 | 1–2 |
| `libvorbis` / `vorbis` | 8000–48000 | 1–8 |
| `libopus` / `opus` | 8000 / 12000 / 16000 / 24000 / 48000 | 1–8 |
| `adpcm_ima_amv` | 22050 | 1 |
| `roq_dpcm` | 22050 | 1–2 |
| `flac` / `pcm_*` / `adpcm_ima_wav` | 不限 | 1–8 |

命令行**显式**给的采样率严格校验（写错就报）：

```
$ LiveArchiveEncoder.exe src.mp4 out.rm --encoder rv20 --audio-encoder ac3 --audio-rate 16000
ERROR: 'ac3' does not support 16000 Hz; supported sample rates: 32000, 44100, 48000
```

**时代预设**带来的采样率算建议值，会被就近纠正（16 kHz → 32 kHz）并说明原因
——预设是为"那个年代"写的，不是为容器强制的那个编码器写的。

**③ 容器匹配**：装不进容器的编码器直接拒绝，例如 `--encoder rv20
--audio-encoder libvorbis` 会报 `cannot be muxed into 'rm'`。白名单大致是：
`rm` 只有 ac3/aac，`ogg` 只有 vorbis/opus/flac，`matroska` 最宽。表里没建模
的编码器一律放行——宁可不检查，也不能误杀一个能用的编码器。

## 7.14 容器切换（v7.8）

"编码器 → 容器"不再是一对一写死：

| 编码器 | 可选容器（**加粗是默认**） |
| --- | --- |
| `mpeg1video` | **mpg**，avi |
| `mpeg2video` | **mpg**，avi |
| `mpeg4` | **avi**，mp4 |
| `libxvid` | **avi**，mp4 |
| `libx264` | **mp4**，avi，mkv |
| 其它 | 保持原来的单一容器 |

```bash
# mpeg1video 写进 AVI
LiveArchiveEncoder.exe src.mp4 out.avi --encoder mpeg1video --container avi
# libx264 写进 Matroska
LiveArchiveEncoder.exe src.mp4 out.mkv --encoder libx264 --container mkv
```

`--container` 认封装器名也认扩展名（`mkv` 和 `matroska` 都行）。菜单在
**参数设置 → 28**；换视频编码器时若新编码器写不了当前容器，会提示并回退。

输出扩展名必须和新容器一致，否则照旧报扩展名不匹配。选不了的组合（例如
`--encoder rv20 --container mkv`）**直接报错而不是静默回退**——静默回退会给
你一个没点过的扩展名。

默认容器一个都没动，所以 35 个编码器的扫描结果跟 v7.7 完全一致。

## 7.15 画面比例：16:9 与三种适配方式（v7.8）

分辨率预设现在 4:3 和 16:9 都有（菜单**参数设置 → 31**）：

| 4:3 | 16:9 |
| --- | --- |
| 160x120、256x192、320x240、384x288、512x384、640x480 | 160x90、256x144、320x180、384x216、512x288、640x360、854x480、1280x720 |

三种适配方式（`--fit`，菜单**参数设置 → 10**）：

| 模式 | 做法 | 什么时候用 |
| --- | --- | --- |
| `crop`（默认，v7.7 及以前的行为） | 裁到目标比例再缩放，填满画面、丢掉多的一边 | 老式录档的标准做法 |
| `fit` | 缩放到两边都放得下，剩下**补边**（`--pad-color` 定颜色） | **16:9 母带存进 4:3 画面**：保留完整画面，上下黑边 |
| `stretch` | 直接拉到目标尺寸，画面变形 | 老播放器经常这么干 |

```bash
# 16:9 源 -> 320x240 的 4:3 画面，上下黑边
LiveArchiveEncoder.exe wide.mp4 out.avi --encoder mpeg4 --width 320 --height 240 --fit fit
# 只想压成 16:9，就用 16:9 的预设尺寸
LiveArchiveEncoder.exe wide.mp4 out.mp4 --encoder libx264 --width 640 --height 360
```

`crop` 还能指定留哪一边（`--crop-anchor`，菜单第 29 项）：`center`（默认）/
`top` / `bottom` / `left` / `right`。16:9 → 4:3 裁的是左右，所以 `left` /
`right` 决定留哪边；反过来 4:3 → 16:9 裁的是上下。

对应滤镜链：

```
crop   : crop=min(iw\,ih*W/H):min(ih\,iw*H/W):<x>:<y>,fps=…,scale=W:H:flags=…,format=…
fit    : fps=…,scale=W:H:force_original_aspect_ratio=decrease:flags=…,pad=W:H:(ow-iw)/2:(oh-ih)/2:black,format=…
stretch: fps=…,scale=W:H:flags=…,format=…
```

`--no-crop` 保留为 `--fit stretch` 的旧写法。实测：640x360 源用
`--fit fit --width 320 --height 240` 得到的帧，顶部和底部 10 行亮度为 0（黑
边），中间 131，画面完整保留。

---

## 7.16 多流选择与字幕（v7.9）

### 流选择：`--video-stream` / `--audio-stream`

一个源可能有多条视频轨（VOB 的多角度）和多条音频轨（DVD 的多语言）。编号用
**ffprobe 打印的绝对流编号**，`--list-streams` 先列出来：

```text
$ LiveArchiveEncoder.exe --list-streams dvd.vob
streams in this source:
    0  data      dvd_nav_packet
    1  audio     dts
    2  video     mpeg2video 720x576
    3  audio     ac3
```

```powershell
# 要第 2 条流里的视频 + 第 3 条流的 AC3 音轨
LiveArchiveEncoder.exe dvd.vob out.avi --encoder mpeg4 --video-stream 2 --audio-stream 3
```

规则：

- 编号不存在、或者类型不符（给 `--video-stream` 一个音频流编号）都会在编码开始前
  被拒绝，并附上完整的流清单——**不会**静默编错流。
- 不给编号就是"各类型第一条"，与 v7.8 及之前完全一致。
- 选中的视频流的像素格式会驱动传输格式选择（第一条流的元数据不会被误用）。
- CUI 主菜单第 16 项 `Streams` 同样能选：先打印清单再逐个询问，留空＝第一条。
- 选择会写进配置文件，供下次交互会话沿用；CLI 不读取它作为默认。

### 字幕：`--subtitle` / `--subtitle-stream` + `--subtitle-mode`

```powershell
# 烧录外挂简中字幕（默认模式就是 burn；可重复给多份，全部渲染进画面）
LiveArchiveEncoder.exe src.mkv out.avi --encoder mpeg4 ^
    --subtitle "src.sc.ass" --subtitle "src.tc.ass"

# 用源内嵌的字幕轨（编号来自 --list-streams）
LiveArchiveEncoder.exe src.mkv out.avi --encoder mpeg4 --subtitle-stream 2

# 作为字幕轨封装（容器必须支持；编码器从 ffmpeg -encoders 里按容器挑）
LiveArchiveEncoder.exe src.mp4 out.mkv --encoder libx264 --container mkv ^
    --subtitle src.srt --subtitle-mode copy
```

规则：

- `burn`（默认）用 libass 的 `subtitles` 滤镜把文字渲染进画面，**渲染在缩放之后**
  （文字不会跟着缩放糊掉），并自动带上工具字体目录作 `fontsdir`——ASS 里写了
  "Microsoft YaHei" 而机器上没有时，会从 `fonts/` 目录兜底。
- `copy` 按容器挑编码器（实测过的默认：Matroska→ass、MP4→mov_text、AVI→xsub、
  MPEG-PS→dvdsub、MPEG-TS→dvbsub），也可以 `--subtitle-codec` 指定（必须在
  `ffmpeg -encoders` 里真的存在，否则报错并列出全部可用项）。rm/flv/asf/roq/amv
  没有字幕轨概念，会直接报错让你改用 burn。
- **位图字幕**（dvdsub/dvbsub/xsub 这类子图片）只能 copy 不能烧：libass 渲染的是
  文字，图片转文字是 OCR，FFmpeg 不做。
- 分离音频通路（`--audio-mode separate`）合并时会丢字幕轨，所以 copy 模式自动
  改走单进程并打 NOTE；burn 不受影响。
- 缺文件、两处来源同时给、编号越界等都在编码开始前报错（CLI 缺文件是 rc=2）。

### CUI 里的字幕（v7.9.1，菜单 17）

v7.9.0 的字幕只有命令行能用，菜单里看不见——v7.9.1 补上了。主菜单第 17 项
`Subtitles` 进去是四行：

```
 1. Mode          burn / copy / off（循环切换）
 2. Embedded     从源里列字幕轨让你选（先 ffprobe，再逐个问）
 3. Files        外挂文件，多个用 ; 分隔；输入 - 清空
 4. Codec        copy 模式用哪个字幕编码器（留空＝按容器自动）
 0. Back
```

行为约定：

- **文件与内嵌轨互斥**：选了内嵌轨就清掉外挂列表，反之亦然。两个同时给在
  CLI 里是错误，在菜单里不给你这个机会。
- 模式是 `off` 时主菜单摘要明确写"关闭"——不会出现"看着像开了、其实没烧"。
- 外挂文件当场校验：扩展名必须是 `.ass/.ssa/.srt/.vtt`，文件必须存在，
  否则不保存并提示原因。
- 内嵌流的选择走 ffprobe 的实际清单，编号不在清单里就不收。
- 设置写进配置文件，下次打开菜单还在；CLI 仍然**不读**配置里的字幕状态
  （和二次压缩一致的规则）。

---

## 8. 实用命令示例

```powershell
# RealVideo 20 录档味，默认 256x192 / 13fps / 200kbps 上限
LiveArchiveEncoder.exe src.mp4 out.rm --encoder rv20

# 只取 2 分 30 秒到 5 分整
LiveArchiveEncoder.exe src.mp4 out.rm --encoder rv20 --start-time 00:02:30 --end-time 00:05:00

# 低带宽感：把码率压到 80kbps，阶梯拉宽
LiveArchiveEncoder.exe src.mp4 out.rm --encoder rv20 --limit 80 --fps-levels 13,6,3,1

# MPEG-4 / AVI，320x240，bicubic 缩放，不裁 4:3
LiveArchiveEncoder.exe src.mp4 out.avi --encoder mpeg4 --width 320 --height 240 ^
    --no-crop --scale-algo bicubic
# H.264 对照用：CRF 23、 faster 预设（注意 --qv 对 libx264 是 CRF）
LiveArchiveEncoder.exe src.mp4 out.mp4 --encoder libx264 --qv 23 --x264-preset faster

# RoQ：尺寸必须 16 的倍数，音频会强制 22050Hz
LiveArchiveEncoder.exe src.mp4 out.roq --encoder roqvideo --width 256 --height 192

# 隔行扫描素材：先 yadif 反交错
LiveArchiveEncoder.exe src.mp4 out.avi --encoder mpeg4 --deinterlace yadif --deint-mode send_field

# 隔行扫描素材，只处理标记为 interlaced 的帧
LiveArchiveEncoder.exe src.mp4 out.avi --encoder mpeg4 --deinterlace bwdif --deint-marked-only

# 有 NVIDIA 显卡且驱动正常，想让解码走 GPU
LiveArchiveEncoder.exe src.mp4 out.rm --encoder rv20 --hwaccel cuda

# 不确定机器有什么，让工具自己试（按平台偏好顺序，第一个真能解出帧的就用它）
LiveArchiveEncoder.exe src.mp4 out.rm --encoder rv20 --hwaccel auto

# 没有显卡 / 不想看 CUDA 报错：默认就是关的，什么都不用加
LiveArchiveEncoder.exe src.mp4 out.rm --encoder rv20

# 脚本里跑，别进菜单也别刷屏
LiveArchiveEncoder.exe src.mp4 out.rm --encoder rv20 --no-cui --quiet

# 指定 FFmpeg（比如系统里有多个版本）
LiveArchiveEncoder.exe src.mp4 out.rm --encoder rv20 ^
    --ffmpeg D:\tools\ffmpeg\bin\ffmpeg.exe --ffprobe D:\tools\ffmpeg\bin\ffprobe.exe
```

Windows PowerShell 里用反引号 `` ` `` 换行，`cmd` 里用 `^`。

---

## 9. 输出怎么读

编码结束后会打印：

```
Done: D:\...\out.rm
Time range         : 00:00:00 - 00:00:05
Duration           : 5.00s
Source pix fmt     : yuv420p
Transport pix fmt  : yuv420p
Output resolution  : 256x192
Video average      : 216.5 kbps
Total average      : 248.5 kbps
Fresh frames       : 18
Repeated slots     : 21
```

* **Fresh frames** — 真正编码了新帧的槽数。
* **Repeated slots** — 缓冲里没新帧、复用了上一帧的槽数。
  两者之和 = 总输出槽数 ≈ `duration × fps`。
* **Video average / Total average** — 滚动窗口统计的平均码率（kbps）。
* **Transport pix fmt** — 管道里实际搬运的像素格式（可能和源不同，是为了适配
  编码器能力自动选的）。

`Fresh` / `Repeated` 每次跑都会有小幅差异，这是自适应控制器对实时抖动的正常响应，
不是 bug。想验证帧管线本身是否稳定，用 `--limit 99999` 关掉节流再跑，计数就会
变得完全确定。

---

## 10. 排错

| 报错 | 原因 | 怎么办 |
| --- | --- | --- |
| `xxx is not detected as a legacy/classic video encoder` | 编码器名拼错，或当前 FFmpeg 没编这个 | `--list-filters` + 菜单第 3 项看实际列表；换 BtbN GPL 构建 |
| `Output extension '.mp4' does not match selected muxer 'rm'` | 扩展名和容器不匹配 | 查第 5 节对照表改扩展名，或换编码器 |
| `roqvideo requires width and height to be divisible by 16` | RoQ 尺寸不合法 | 宽高调到 16 的倍数 |
| `roq_dpcm requires --audio-rate 22050` | RoQ 音频采样率不对 | 去掉 `--audio-rate`，或显式设 22050 |
| `recover must be > 0 and < limit` | 参数区间非法 | `--recover` 调到 0 和 `--limit` 之间，或直接省略 |
| `end time must be later than start time` | 时间区间非法/超出源时长 | 检查 `--start-time` `--end-time` |
| `unknown scale algorithm 'xxx'` | 缩放算法名不在当前 FFmpeg 里 | `--list-filters` 看支持的算法；别名：`nearest`/`point` → `neighbor`，`cubic` → `bicubic` |
| `FFmpeg filter 'yadif' is not available in this build` | FFmpeg 编译时没带这个滤镜 | 换完整构建，或用 `off` |
| `audio encoder not found: xxx` | 音频编码器不存在 | 去掉 `--audio-encoder` 让程序按容器自动选 |
| `ERROR: input not found` | 路径不对 | 注意路径含空格要加引号 |
| 一堆 CUDA/cuvid 警告 | 机器没 NVIDIA 或驱动不匹配 | 默认已关闭硬件解码；若手动开了 `--hwaccel cuda`，改回 `--hwaccel off` |
| 输出文件只有音轨 / 时长不对 | 老式 muxer 的坑（RoQ、RealMedia 都出现过） | 默认 `inline` 模式不会遇到；如果用 `--audio-mode separate`，工具内置了 3 帧预演 + 输出解码校验，会拦下并自动回退到单进程 |
| 字幕烧录后**中文是方块** | ASS 点名的字体不在 `fonts` 目录里，libass 挑了拉丁字体顶上 | 往 `%LOCALAPPDATA%\LiveArchiveEncoder\fonts\`（Linux `~/.local/share/LiveArchiveEncoder/fonts/`）放一个中文字体（msyh.ttc / simhei.ttf / Noto*CJK）；目录可用 `LAE_FONT_DIR` 改。见 §7.12 |
| `recompress` 报 `'-i' is not allowed here` / `looks like an output file name` | 参数段里写了输入或输出 | 参数段只写中间部分，`-i` 和输出路径由程序生成；见 §7.12 |
| `--parallel 4` 给了却"没生效" | 输入在网络驱动器上（UNC / `smb://` / 映射盘 / CIFS 挂载） | 这是故意回退成单遍：分片读同一个网络文件会让画质在中间塌一段。把源拷到本地再分片，或接受单遍；见 §7.8 |
| 远程源录到一半**画质突然变糊** | 网络抖动，实时节拍落后 → 自适应控制器误判码率太高而连续降档 | 同上，这是实时工具对线路的诚实反应；换稳定链路或降到本地源 |

---

## 11. 和 Python 版（v6.3.3）的关系

> **v7.6 起**：不再承诺与 v6.3.3 的输出一致——C# 版开始长自己的东西了
> （§7.12 的后处理管线）。原有行为**全部保留**，新功能一律需要显式开启，所以
> 不加任何开关时两边仍然等价；`compare_matrix` 从"等价性判据"降级为回归检查。

C# v7 是 v6.3.3 的**行为等价**移植：FFmpeg 命令行（包括 `-threads` 的位置）、
自适应状态机的阈值与迟滞、选帧规则、滚动码率算法、所有默认值和常量表，全部逐项
照搬。已验证：12 个编码器/参数矩阵结构字段全一致、11 条错误路径退出码与文案
一致、关掉节流后帧计数完全一致。

所以**同一条命令在 Python 版和 C# 版上应产出等价的结果**，可以直接对照着用。

C# 版在 v7.3 之外多出来的东西：`--parallel`（分片提速，见 §7.5）、配置
持久化（§7.6）、中英文（§7.6）、Shell 自动补全（§7.7）、SMB 读取优化（§7.8）、
`--help` / `--cpu-info` 两个辅助标志。这些都不改变默认行为：不加任何开关时，
命令行语义与 v6.3.3 一致。

### v7.4 新增

1. **SMB 读写**（§7.8）：修掉 `-stimeout` 这个让 UNC 输入必然失败的参数；新增
   写入侧韧性参数、写前可写探测、输出侧协议版本报告。
2. **FFmpeg 工具链**（§7.10）：四个发行版（BtbN / gyan.dev / avbuild / nonfree），
   支持切换、下载、删除；默认每日构建版；下载可选代理；有 `aria2c` 就多线程下载；
   安装目录 `%LOCALAPPDATA%\LiveArchiveEncoder\ffmpeg\<来源>\bin`。
3. **编码后播放**（§7.11）：`--play ffplay|external`，外部播放器可指定，留空走
   系统关联；默认 `off`。
4. **源码纳入 git 管理**：v7.3 基线 + 按改动逐步提交，方便回溯每一步。

### v7.4.1 新增

1. **引入 NUnit**（`tests/LiveArchiveEncoder.Tests`，当时 121 个用例，v7.6 已达
   214，v7.7 是 236，v7.8 已达 258，v7.8.1 是 280，v7.8.2 是 298）：纯逻辑单测，不依赖 FFmpeg 和网络，几秒跑完；Python harness
   继续负责
   "与 v6.3.3 是否等价"那一层。见 §12。
2. 首次跑就抓到并修掉了一个真 Bug：`note.*` / `run.*` 共 13 个词条只有英文没有
   中文，中文界面下会漏回英文并把对齐顶歪。现在有词条完整性测试盯着。

### v7.5 新增

1. **支持 Windows + Linux 双平台**（§1）。执行文件名、受管理 FFmpeg 目录、播放器
   调用、压缩包类型（新增 `.tar.xz`）、SMB 路径识别都按平台分支；`tools/build.sh`
   是 `tools\build.ps1` 的对应物。
2. **Linux 端的 SMB**（§7.8）：识别 CIFS 挂载点（读 `/proc/self/mountinfo`），
   显示协商到的 dialect；读写两侧都验证过。
3. **硬件解码改为可选且默认关闭**：`--hwaccel NAME`，`NAME` 可以是
   `ffmpeg -hwaccels` 里的任意方法（cuda / vaapi / qsv / d3d11va …），`auto` 让
   程序挑一个能用的。原来那个默认开 CUDA 的行为在无显卡机器（包括 CI 容器和
   Linux 虚拟机）上会直接崩在 `Cannot load libcuda.so.1`，所以默认值改成 `off`。
   启用前会**真的解一帧**验证，验证不过自动回退软件解码。
4. **去掉了 SMB 读取侧的 `-reconnect*`**（§7.8）：它属于 HTTP/FTP 协议，UNC 走
   的是 `file`，新 FFmpeg 构建会直接拒绝，导致共享上的源解出 0 帧。
5. **CI 双轨**：GitHub Actions 跑 windows + ubuntu 矩阵；Codeberg 用 Woodpecker
   （`.woodpecker.yaml`）做 restore + build + test。

### v7.6 新增

从 v7.6 起，C# 版**不再承诺与 Python v6.3.3 的输出一致**——原有行为全部保留，
新功能只在显式开启时生效，默认行为不变。

1. **后处理管线**（§7.12）：live 编码之后接一个独立阶段——**二次压缩**（模拟
   0 几年视频网站转码，你写 FFmpeg 参数段，程序负责 `-i` 和输出）。默认关闭。
   产物命名：`clip_live.rm` → `clip_live_web.mp4`。文字水印阶段在 v7.9.1 已移除
   （见 §7.12 顶部说明）。
2. **时代预设**（§7.12，`--era ID`）：五个年代一套一套的 live + 二次压缩参数，
   从 1997 年拨号时代的 RealVideo G2 到 2016 年的 720p H.264。数字有出处
   （RealSystem G2 官方手册、实测的土豆 FLV 文件、杂志里的网速报道），见
   [`docs/era-presets.md`](era-presets.md)。
3. **安全设计**：二次压缩的参数段逐 token 校验，`-i`、`-o`、末尾裸路径直接拒绝
   ——这些写法会让 FFmpeg 覆盖你的源文件。校验发生在 live 编码**之前**。
4. **CLI 语义不变**：`--recompress` 不读取配置文件里的状态，不给就没有；交互
   菜单里的设置会保存，菜单之间粘性保留。
5. **字体目录**（§7.12）：字幕烧录的字体放在程序自己的 `fonts` 目录（与受管理
   FFmpeg 同级），Windows 和 Linux 都不查系统字体。Linux 实测发现：最小化安装
   没有 `/usr/share/fonts`，点名微软雅黑的 ASS 在只找到拉丁字体时还会让中文
   字幕变成方块而编码照报成功——两种都是"错了不报错"。目录位置可用
   `LAE_FONT_DIR` 覆盖（旧名 `LAE_WATERMARK_FONT_DIR` 仍有效）。

### v7.7 新增

1. **修复：并行分片末尾越界。** 帧槽网格比媒体时间轴粗，最后一个分片会要一个
   超过片尾的时间（`end time 00:03:34.333 exceeds the source duration
   (00:03:34.280)`），整次编码直接失败。现在边界全部夹在片段终点内，
   最后一片精确对齐终点（§7.5）。规划逻辑抽成了 `ShardPlanner`，有单测覆盖。
2. **并行分片显示进度。** 7 个分片各打一行是没法看的，改成共享一行聚合进度
   （已完成片数 / 总帧槽 / 百分比 / 用时），每 0.2 秒刷新（§7.5）。
3. **升级到 .NET 11 / C# 15。** 两个工程 `TargetFramework` 改为 `net11.0`；
   Windows 用 `winget install Microsoft.DotNet.SDK.Preview`，Linux 用
   `mise use -g dotnet@11.0.100-rc.2.26504.105`（ci1/ci2 都已装好）。
   CI 的 `dotnet-version` 与 Woodpecker 镜像同步升到 11.0。
   > **v7.8.1 起回退为默认 `net10.0`**，并靠 `RollForward=Major` 让同一份产物同时
   > 兼容 .NET 10 与 .NET 11 运行时；`-p:LaeTfm=net11.0` 仍然可用，CI 两套都跑。
4. **Turbo 标记弃用。** `--turbo` 和菜单第 9 项功能完全不变，只是每次使用都会
   打印弃用警告、菜单里标注"（已弃用）"。老脚本不受影响（§7.5）。
5. **动态档位不能超过基础 FPS。** 阶梯只会往下压，写 `20` 而基础是 `15` 是笔误：
   菜单里拒绝并提示，命令行显式给了 `--fps-levels` 时报 rc=2（§6）。
6. **档位码率可调。** `--fps-levels` 支持 `FPS:降档码率:升档码率`，
   例如 `12:128:115,5:140:130`；菜单新增 **参数设置 → 27**（§6）。
   不写就是 v7.6 的老行为，默认值一点没变。

### v7.8 新增

1. **按编码器 + 容器扫描音频编码器。** 菜单新增"扫描音频编码器"，只列出当前
   容器**真的能复用的**音频编码器；显式选择时校验采样率/声道数（§7.13）。
2. **容器可切换。** mpeg1video / mpeg2video / mpeg4 / libxvid 可用 AVI，
   libx264 可用 MKV，命令行 `--container avi|mp4|mkv`，菜单 **参数 29**（§7.14）。
3. **画面比例与三种适配。** 分辨率预设区分 4:3 与 16:9；`--fit crop|fit|stretch`
   决定 16:9 画面进 4:3 帧时是裁掉、加黑边还是拉伸（§7.15）。

### v7.8.1 新增

1. **一个产物同时支持 .NET 10 与 .NET 11。** 默认目标框架回到 `net10.0`，发布时
   带 `RollForward=Major`：只有 .NET 11 运行时的机器也能启动，有 .NET 10 的机器
   继续用 .NET 10。显式 `-p:LaeTfm=net11.0` 可指定 .NET 11，CI 两套都跑
   （§1）。单测 280 条在两个目标框架下全部通过。
2. **引入 .NET 10 / C# 14 的语法与库特性**，行为不变：
   - **扩展成员（extension block）**：`Core/TextExtensions.cs` 提供
     `value.HasText` / `value.Or(fallback)`，替换了全项目 28 处
     `string.IsNullOrWhiteSpace(...)`；receiver 上的 `[NotNullWhen(true)]`
     让编译器照旧做空值流分析。
   - **`System.Threading.Lock`**：`OutputMonitor`、`ParallelEncoder` 进度器、
     `RealtimeEncoder` 诊断队列、`Toolchain` 管道捕获改用新的锁类型
     （更小的对象头、不会被人从外面 `Monitor.Enter`）。
   - **`FrozenDictionary` / `FrozenSet`**：`Constants` 里所有只读查表（默认码率、
     容器映射、像素格式、扩展名等）改为冻结集合——构建时多花一点，之后查询最快。
   - **`[GeneratedRegex]`**：28 条"是否老编码器"的正则合成一条源生成正则，
     JIT 与 NativeAOT 行为一致，且不再需要运行时编译。
3. **只构建 JIT 与 AOT。** 第三种产物 `publish\aot-avx2`
   （`-p:IlcInstructionSet=avx2`）被移除：指令集基线只影响本工具的 C# 侧，
     编码在 FFmpeg 里由它自己派发 SIMD，实测差异在噪声内（§1）。
4. **代码整理与归档。** 全项目逐个声明交叉核对引用，把零引用的成员移入
   `archive/retired/`（不删除，带退役版本和替代说明），删除 3 处无用赋值、
   2 组未使用参数（`Preflight.Video` 的 width/height、
   `Watermark.BuildFilter` 的 width）；新增 `Directory.Build.props` 统一
   属性、`AnalysisLevel=latest-recommended` 让构建自带分析器，现在零告警。

### v7.8.2 新增

1. **音频数字边界表**（§5）。以前只有采样率白名单、没有码率约束，且白名单本身
   不全：`mp2` 实际从 16000 Hz 起（表里写成 22050），`aac` 支持 96000 Hz（表里
   漏了），`libvorbis` 支持 12000/24000（漏了）。现在 `Core/AudioCatalog.cs`
   同时约束采样率与码率，**所有数字都是实测出来的**（拿工具链下载的 FFmpeg
   master 逐个编码器 × 采样率 × 码率探出来的）：
   - 码率下限/上限按编码器、必要时按采样率分带：`mp2` 在 44.1 kHz 起要 32 kbps
     且只认标准码率（36 不行、144 只在低采样率合法），`wmav1/wmav2` 最低 24，
     `libvorbis` 整条带随采样率缩放（8 kHz：8–40；22050：16–80；48 kHz：32–224），
     `libopus` 最高 256；
   - **容器级约束**：FLV 对 MP3 只接受 11025/22050/44100/48000，MP4 拒绝低于
     16 kHz 的 MP3（AVI/MKV/MOV/ASF 不受限）——这类失败以前只在 FFmpeg 跑到一半
     时才暴露。
2. **显式报错、预设纠正。** 命令行显式写的采样率/码率不合规 → 退出码 2 并列出
   合法值；`--era` 带来的值 → 自动就近纠正（dialup98 + FLV 的 16 kHz 改成
   11025，+ WMV 的 16 kbps 改成 24）并在日志说明。菜单 16（码率）也改成和
   17/18 一样的"纠正并告知"。
3. **拒绝实验性编码器。** FFmpeg 里不带 lib 前缀的 `vorbis` / `opus` 需要
   `-strict` 才会打开，选了必然失败；现在直接报错提示换 `libvorbis` / `libopus`，
   OGG 的默认音频列表也只剩这两个。单测 298 条（新增 18 条边界护栏）。

### v7.8.3 新增

1. **测试框架双换代**（§12）：单元测试从 NUnit 迁到 **TUnit 1.75.0**（原生
   AOT 友好、源码生成、无反射），e2e 六个独立脚本合并为 **pytest 9.1.1**
   套件（`tests/e2e/test_*.py` + 共享 `harness.py`）。用例数不变、判据不变，
   只是换引擎：`dotnet test --solution ...` 走 Microsoft.Testing.Platform，
   `pytest` 一把跑完 123 个 e2e 用例。迁移中顺修了 NUnit 时代遗留的集合
   断言按引用比较的问题（TUnit 的 `IsEqualTo` 对集合是引用相等，统一改
   `IsEquivalentTo`）。
2. **并行分片 + SMB 韧性修复取消。** v7.8.3 开发周期中原计划修复"并行分片时
   SMB 不稳定导致画面冻结"（解码器提前 EOF 时旧代码静默用上一帧填满槽位），
   实测定位到根因后决定**不修**：该静默行为是 option_smoke 既有断言的依赖项，
   修复会改变长年稳定的输出形态。根因记录留在代码注释里，留待 v8 评估。

### v7.9 新增

1. **视频流 / 音频流选择（目标 1）**：`--video-stream N` / `--audio-stream N`
   直接用 ffprobe 打印的**绝对流编号**（`--list-streams` 先列出），编号不存在或
   类型不符会在编码前被拒绝并列出全部流。CUI 主菜单新增一项（v7.9.0 里是第 17
   项，v7.9.1 删掉水印后是第 16 项）。VOB（DVD）
   这类多音轨源终于可以点名要哪条音轨了。真实素材验证：179MB VOB 经 SMB 选
   `--video-stream 2 --audio-stream 3` 编 00:00–00:30 片段，PSNR/零交叉率双重
   证明选的确实是目标流。
2. **字幕：读取、烧录、内嵌（目标 2）**：`--subtitle FILE`（外挂 .ass/.srt，可
   重复）与 `--subtitle-stream N`（内嵌轨）二选一；`--subtitle-mode burn`（默认，
   libass 渲染进画面，带 `fontsdir` 自动加载工具自带字体）或 `copy`（按容器从
   `ffmpeg -encoders` 挑字幕编码器封装成字幕轨）。位图字幕（dvdsub/dvbsub）只能
   copy 不能烧（转文字需要 OCR）。真实素材验证：Re:Zero S2 外挂简中 .ass 烧进
   256x192 归档产物，字幕清晰可读。
3. **Turbo 彻底移除（目标 3）**：`--turbo` → `unrecognized argument`，配置键被
   忽略，原实现归档于 `archive/retired/RetiredTurbo.cs`（见 §7.5）。
4. **字体相对路径修复（目标 4）**：水印字体不再必须写绝对路径——裸文件名会先到
   工具字体目录（`fonts/` 或 `LAE_WATERMARK_FONT_DIR`）里找；顺带修了
   simsun.ttc 首个 face 是单色 1bpp 被 drawtext 拒绝的问题（自动回退可用的
   中文字体并打 WARNING）。
5. **AVI 容器扩容（目标 5）**：h261/h263/h263p 加入 AVI 容器白名单；h261/h263
   从裸流（无音频）换成 AVI 后自动获得音频能力。
6. **HTTP/HTTPS 输入（目标 6，初步支持）**：输入可以是
   `https://archive.org/download/.../file.mp4` 这类直链。带 `-reconnect` 家族 +
   30s socket 超时；seek 依赖服务器支持 Range（archive.org/CDN 都支持）。只覆盖
   渐进式直链：不处理鉴权、Cookie、HLS/DASH。
7. **顺手修的两个老 bug**：`ffmpeg -filters` 的 flags 列宽是 2 字符（旧正则要求
   3 → 所有未标记滤镜从未被检出）；ffprobe 源超时 30s 不够——179MB VOB 实测要
   2m15s，放宽到 300s 并把 `--list-streams` 的超时变成干净报错。

### v7.9.1 变更

1. **水印功能移除（目标 1）**：静态文字水印整段退役——`--watermark` /
   `--watermark-position` / `--watermark-size` / `--watermark-color` /
   `--watermark-font` / `--no-watermark` 六个旗标、CUI 的第 15 项、配置里的
   `Watermark` 段、`_wm` 中间产物全部没有了。代码归档在
   `archive/retired/RetiredWatermark.cs`（含恢复说明），drawtext 时代那套字体
   挑选逻辑归档在 `RetiredFontPicking.cs`。**二次压缩保留**，`_web` 产物照旧。
2. **CUI 补上字幕（目标 2）**：主菜单第 17 项 `Subtitles`，可以选模式
   （burn / copy / off）、列内嵌字幕轨、指定外挂文件、换 copy 模式的编码器。
   文件与内嵌流**互斥**（选一个就清掉另一个），模式 `off` 时明确提示"不会带字幕"。
   以前只有 CLI 能用字幕，等于菜单用户没有这个功能。
3. **网络驱动器（目标 3）**：映射盘（`DriveType == Network`）现在也算远程路径：
   打印稳定性提示，并把并行分片强制成单遍（CLI / CUI 菜单 11 / 恢复配置三处
   都拦，`ParallelEncoder.BlockReason` 再兜一次）。见 §7.8。
4. **配置 ↔ 命令行互转（目标 4）**：`--emit-args[=FILE]` 把设置打印成一条等价
   命令行，`--save-config FILE` 把这次运行的解析结果写成配置文件，两者都不编码。
   见 §7.6。
5. **彻底删掉 `publish/aot-avx2`（目标 5）**：产物目录、CI 步骤、文档里的
   AVX2 产物说法一起清掉——指令集基线对 FFmpeg 子进程编码没有收益。
6. **测试**：单测 381（水印 11 条转归档、增补 CliArgs / 映射盘 / 字幕配置项），
   e2e 155（删 3 条水印用例，加 4 条配置转换，CUI 字幕菜单用例重写为三次独立
   调用）。
7. **e2e 提速（目标 7）**：新增 `tools/e2e.py`，把 155 条拆成"排他 25 条先独占
   跑 + 其余 130 条交给 xdist"，Windows 32 核实测 **794 s → 492 s**。排他规则
   用 marker 表达（`timing` / `shared_config`），`conftest.py` 会拒绝裸 `pytest -n`
   以免时序断言变成偶发失败。见 §12。

### v7.9.2 修复

**并行分片直接拼 flv 会把画面拼坏（v7.8.0 起潜伏，v7.9.2 修）。**

现象：243 秒 / 196 Mbit/s rawvideo 源、5 段分片、flv 容器，成品播到 **00:03:14
画面停住**，声音继续到结尾。

定位过程（都是可复现的硬数据）：

| 检查 | 结果 |
| --- | --- |
| 产物分流时长 | 视频 194.82s / 音频 238.6s，194.8 ≈ 第 4 段边界 |
| 5 个分片逐个解码 | 各 730 帧、**0 解码错误**，全长 48.6s |
| 两两拼接 | `0+1` `1+2` `2+3` 全 0 错误，**`3+4` 直接 5589 处错误** |
| 分片转 mkv 再拼 | 3651/3651 帧，**0 错误** |
| mkv 拼完 `-c copy` 转回 flv | 3651 帧、0 错误、243.54s |

结论：**分片没问题，`ffmpeg -f concat -c copy` 拼 flv 有问题**——从第 4 次拼接起
视频码流被写坏（`Bad picture start code`），而音频一路正常、把 format 时长撑到了
238.6s，于是原来的长度校验（容差 2%）恰好放行。

修复：

1. 分片改落 Matroska：`sNNN.mkv` → `concat` → `joined.mkv` → `-c copy` 目标容器。
   多一次 remux，只搬字节不解码。
2. 拼接后**按视频包数**复核：成品视频包数要对得上各段槽位总和（容差 2%），
   对不上直接报错并提示去掉 `--parallel`。
3. 排查开关：`LAE_KEEP_SHARDS=1` 保留分片目录并打印路径（默认仍然清掉）。

回归：单测 `ShardStagingTests`（4 条）锁住"分片必须落 matroska、编码器不变、
扩展名与音频表都认识 mkv"；e2e 新增 `parallel_flv_join_has_no_corrupt_frames`
（5 片 flv 拼接后整段解码零错误）。

> 20 秒测试素材复现不出这个 bug（5 片各 4 秒时拼接恰好没问题），所以真正的锁是
> 单测里那条"暂存容器必须是 matroska"，e2e 那条是整段可解码的守卫。

---

## 12. 自测

测试分两层，各管一段：

| 层 | 工具 | 管什么 | 怎么跑 |
| --- | --- | --- | --- |
| 单元 | **TUnit**（`tests/LiveArchiveEncoder.Tests`，v7.8.3 前是 NUnit） | 纯逻辑：SMB 参数、CLI 解析、配置往返、词条完整性、工具链元数据 | `dotnet test --solution LiveArchiveEncoder.V7.slnx` |
| 等价性回归 | **pytest** 套件（`tests/e2e/test_*.py`，v7.8.3 前是六个独立脚本） | 与 v6.3.3 的**产物是否等价**、错误路径、全编码器扫描、CUI 冒烟、时长漂移 | `pytest` |

### TUnit 单元测试（v7.4.1 起 NUnit，v7.8.3 起 TUnit）

```powershell
# Windows —— TUnit 跑在 Microsoft.Testing.Platform 上，用 --solution 位置参数
dotnet test --solution LiveArchiveEncoder.V7.slnx -c Release
# 第二个目标框架（.NET 11 运行时）同样要全绿
dotnet test --solution LiveArchiveEncoder.V7.slnx -c Release -p:LaeTfm=net11.0
```

```bash
# Linux —— 同一条命令；SDK 只装在 mise/asdf 里时给它一个包装脚本
DOTNET="$HOME/dot" dotnet test --solution LiveArchiveEncoder.V7.slnx -c Release   # 见 §1
```

不需要 FFmpeg、不需要网络、几秒跑完，覆盖：

- `SmbTests` — 远程路径识别、**读写两侧都不得再吐出 `-stimeout` / `-reconnect*`**
  （v7.3 / v7.5 的回归），写入侧只用 `-rw_timeout`、可写探测不留痕迹；
  v7.9.1 起还覆盖**映射网络驱动器**（本地盘不得误判为远程、UNC/`smb://` 恒为远程）
  与稳定性提示文案
- `FfmpegCatalogTests` — 各来源的 id / 默认值 / 谁能自动下载 / 压缩包类型，
  **以及清单不会跨平台混列**（Linux 上不得出现 `gyan`）
- `ToolchainTests` — 安装目录布局、**`path` 来源不许解析成受管理构建**、
  未知来源只报错不抛异常
- `HwaccelTests` — `off` / `auto` 归一化，未知方法名的处理
- `PlayerTests` — 播放模式解析与描述，缺文件/缺播放器都是"报告"而不是"崩"
- `CliParserTests` — `--ffmpeg-source` / `--ffmpeg-download`（裸用不吞下一个 flag）/
  `--ffmpeg-archive` / `--play` / `--proxy` 等新选项，以及老选项没被碰坏
- `CliArgsTests` — **配置 → 命令行**发射器：只发射非默认项、派生值不进阶梯、
  二次压缩参数按 shell 规则加引号、禁用的阶段不发射
- `ConfigStoreTests` — JSON 往返、`"path"` 不被规范化掉、损坏文件回退、
  分片数夹取；v7.9.1 起还覆盖字幕设置（外挂文件 / 内嵌流 / 模式 / 编码器）
  的往返与 `Active` 语义
- `LocalizationTests` — **中英文词条必须一一对应**（见下）、CJK 显示宽度补齐
- `PostArgsTests` — 二次压缩参数段的切分与校验：正常片段放行，`-i` / `-o` /
  末尾裸路径必须拒绝；引号内空格保留，**反斜杠不当转义符**
- `FontResolverTests` — `fonts` 目录 / `LAE_FONT_DIR` 环境变量契约（水印移除后
  只剩字幕烧录在用）
- `RecompressTests` — 参数向量必须以输出收尾、坏片段不生成命令、失败只报错不抛
- `EraPresetTests` — id 唯一、两层参数齐全、**预设自带的参数段能通过自家校验器**、
  时代在像素数与码率上单调递增
- `PostProcessingConfigTests` — 二次压缩设置往返持久化、**v1 配置文件仍能读出
  且阶段默认关闭**、水印段不复存在

> 词条完整性这一条刚建好就抓到了真东西：`note.*` 和 `run.*` 共 13 个词条只有英文，
> 中文界面下会漏回英文并把那一列的对齐顶歪。已补齐。

包：`TUnit` **1.75.0**（单个 NuGet 包含 Core + Engine + Assertions，源码生成、
无反射，NativeAOT 下也能跑）+ `Microsoft.NET.Test.Sdk` **18.x**，走
`nuget.config` 里的代理还原。`global.json` 里的 `test.runner:
Microsoft.Testing.Platform` 段负责把 `dotnet test` 切到 MTP 模式。

### 与 Python 基线的等价性回归（pytest）

六个独立脚本在 v7.8.3 合并成一个 pytest 套件：共享 plumbing 住在
`tests/e2e/harness.py`，公共 fixture 在 `conftest.py`，每个脚本对应一个
`test_*.py`（用例数、判据、环境变量全部不变）。装好依赖后**在仓库根目录**跑
`pytest` 即可（`pytest.ini` 的 `testpaths` 指向 `tests/e2e`）：

```powershell
# Windows
pip install -r tests\e2e\requirements.txt          # pytest==9.1.1 + pytest-xdist
python tools\e2e.py                                 # 全部 155 个用例，分两阶段跑
pytest                                               # 单进程串行，同上但慢一倍
pytest tests\e2e\test_feature_sweep.py              # 只跑一个模块
pytest tests\e2e\test_compare_matrix.py -k wmv2     # 只跑一条（替代旧 CM_ONLY）
CS_EXE=publish\aot\LiveArchiveEncoder.exe pytest     # 切换被测 exe（验 AOT 产物）
```

### 两阶段跑法（v7.9.1）

整套 155 条里，绝大部分只是"这个选项能不能产出一个结构正确的文件"，彼此独立；
但有 25 条不行，分两类，靠 marker 标出来：

| marker | 谁 | 为什么不能并行 |
| --- | --- | --- |
| `timing` | `compare_matrix`（12）、`ab_duration`（2） | 断言的是自适应码率控制器**选了什么帧**。机器一忙，控制器选的帧就变——并行 worker 不是让它变慢，而是让它测不准 |
| `shared_config` | `cui_smoke`（11） | 读写的是用户目录下真实的 `config.json`，而**每一次 CLI 运行都会在退出时写这个文件**，并行的 CLI 会插进它的两条断言中间 |

`tools/e2e.py` 就按这个分组跑两阶段：先把排他模块单独跑完（这段时间机器上不跑
别的），再把其余 130 条交给 xdist，一条用例一个 worker。

```powershell
python tools\e2e.py                 # 两阶段
python tools\e2e.py --phase serial   # 只跑排他的 25 条
python tools\e2e.py --workers 4      # 少用几个 worker
python tools\e2e.py -k era_flv       # 其余参数原样转给 pytest
python tools\e2e.py -- --maxfail 1   # `--` 之后也是 pytest 参数
```

实测（32 核，Windows，155 条）：

| 跑法 | 墙钟 |
| --- | --- |
| `pytest`（单进程） | 794 s |
| `python tools/e2e.py`（8 worker） | **492 s**（排他阶段 397 s + 并行阶段 95 s） |

提升有限是**结构性的**：排他的那 25 条本身就要 396 s（`ab_duration` 213 s、
`compare_matrix` 166 s、`cui_smoke` 11 s），而它们必须独占机器；其余 130 条
串行要 425 s，摊到 8 个 worker 只剩 92 s。想再快只能削覆盖，那是另一回事。

> `pytest -n auto` 直接跑整套会被 `conftest.py` 拒绝并给出一条明确的报错——宁可
> 拒跑，也不要让时序断言变成"偶发失败"。确实要并行时写
> `pytest -n 6 -m "not (timing or shared_config)"`。

```bash
# Linux —— 同几条命令，套件本身平台无关（可执行文件名差异由 harness 处理）
pip install -r tests/e2e/requirements.txt
pytest
```

三套产物（JIT / NativeAOT × 两个目标框架）都跑一遍才算过——NativeAOT 下反射型
API 会直接崩，JIT 上却完全正常，只验 JIT 会漏。`CS_EXE` 就是用来切产物的。

`compare_matrix` / `error_paths` / `feature_sweep` / `option_smoke` /
`ab_duration` 都需要 `tests/media/test_src.mp4`（20 秒 320x240 h264+aac 素材）、
PATH 里的 ffprobe，以及**Python 基线也要能用系统 FFmpeg**——Linux 上如果只装了
受管理构建，把它的 `bin` 目录临时加进 `PATH` 即可。

需要注意：`compare_matrix` 和 `ab_duration` 依赖实时节流下的帧选择，跑的时候
**别同时跑别的重活**——并行 worker 会扰动 fresh/repeat 计数和时长中位数（这是
自适应控制器对 CPU 争用的正常反应，不是 bug）。所以这两条（外加读写真实配置的
`cui_smoke`）被标成排他模块，`tools/e2e.py` 会先把它们单独跑完再并行跑其余
用例；直接用 `pytest -n auto` 跑整套会被 `conftest.py` 拒掉。

全部通过会打出（Windows / Linux 略有差异）：

```
dotnet test --solution LiveArchiveEncoder.V7.slnx                失败 0，通过 385
dotnet test --solution LiveArchiveEncoder.V7.slnx -p:LaeTfm=net11.0   同样 385
pytest  156 项（Windows 实测 154 通过 + 2 跳过）：
  option_smoke      58（39 组参数 + 18 项参数检查 + 1 条分片拼接回归）
  feature_sweep     35（33 通过 + 2 跳过：a64multi / a64multi5 只有 muxer 没有 demuxer）
  compare_matrix    12
  error_paths       11
  cui_smoke         11 个阶段（内含 40+ 项检查，Linux 多一条）
  stream_selection   9
  subtitles          7
  http_source        7
  config_conversion  4（配置 ↔ 命令行往返）
  ab_duration        2（mpeg1video / wmv2 各 4 变体 × 4 runs 的中位数对比）
```

v7.9.2 **只做了 Windows 验证**（用户在外地，目标 1）。Linux 的数字沿用 v7.9.1
实测：单测总数 380（379 通过 + 1 跳过，跳过的是 `PathMeansSystemFfmpeg`，需要
PATH 上的系统 FFmpeg；总数少的那一条是映射网络驱动器用例，Windows-only），
ci1（Ubuntu 26.10）与 ci2（Fedora 44）一致。v7.9.2 新增的 4 条单测是纯逻辑
（`ShardStagingTests`），跨平台成立但不作为 Linux 验收记录。

MPEG-PS 那两条（mpeg1video）在 Linux 上时长差得比 Windows 大——实测 0.167 s，正好
是 23.976 fps 下的 4 帧，而**帧数两边都是 120**。那是容器按字节到达时刻打戳的老
问题（见 `harness.py` 里 `DURATION_TOL` 的注释），不是丢帧；Windows 上同一用例只
差 ~0.08 s。容差按 Linux 的最坏值留了 6 帧余量。

`cui_smoke` 会**备份并恢复真实配置文件**（`%LOCALAPPDATA%` / `~/.local/share`
下的 `config.json`）——持久化检查读的就是它；跑之前不需要特殊准备，跑完之后
配置原样归还。

`option_smoke` 里的 `era_flv` 需要 `fonts` 目录里有字体（见 §7.12）；
`subtitles` 那 7 条也需要同样的字体目录（libass 没有中文字体会画成方块）。
另外 `compare_matrix` 现在只是**回归检查**——v7.6 起不再要求与 Python 版输出
一致，但不开新开关时两边仍然一致。

> 跑回归之前建议把配置里的 `FfmpegSource` 设成 `"path"`（或删掉受管理构建），
> 这样 C# 版和 Python 版用的是同一个系统 FFmpeg，对比才有意义。
