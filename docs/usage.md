# Live Archive Encoder v7 (.NET 10 / .NET 11 / C#) — 使用教程

面向使用者的操作手册。想了解迁移细节与验证数据，看 `README.md` 和
`docs/test-report-v7.md`；想了解**每个版本改了什么**，看
[`CHANGELOG.md`](CHANGELOG.md)。

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
AOT 版在 Ubuntu 22.04.5 上链接（glibc 2.35），**要求目标机的 glibc 不低于构建机**；
老发行版用 JIT 版更稳。两个版本都还需要 `PATH` 上有 FFmpeg/ffprobe，或让工具自己下一套（§7.10）。

完整的操作系统 / CPU / 内存 / 磁盘 / 字体 / 权限要求，以及实测环境与性能预期，见
[`docs/requirements.md`](requirements.md)。

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
| `--subtitle-font-dir` | 程序 `fonts` 目录 | **v7.9.5.1**：额外的字体目录（烧录字幕用） |
| `--subtitle-font` | 无 | **v7.9.5.1**：强制使用某个字体族，覆盖字幕文件自己的设置（SRT/VTT 本来就不指定） |
| `--font-import` | — | **v7.9.5.1**：把一个字体文件（或整个目录里的字体）拷进程序 `fonts` 目录后退出（见 §7.12） |
| `--list-subtitle-fonts` | — | **v7.9.5.1**：列出字幕文件要哪些字体、本机有没有、在哪，然后退出 |
| `--width` `--height` | `256` `192` | 输出分辨率（16:9 预设见 §7.15） |
| `--container` | 编码器默认 | 容器切换（见 §7.14） |
| `--fit` | `crop` | 画面适配：`crop` 裁切填满 / `fit` 完整画面＋补边 / `stretch` 拉伸变形（见 §7.15） |
| `--crop-anchor` | `center` | `crop` 时保留哪一边：`center` / `top` / `bottom` / `left` / `right` |
| `--pad-color` | `black` | `fit` 时补边的颜色，FFmpeg 颜色名或 `0xRRGGBB` |
| `--no-crop` | 默认裁剪 | 旧写法，等价于 `--fit stretch` |
| `--scale-algo` / `--scale-flags` | `neighbor` | 缩放核；`cubic` 是 `bicubic` 的别名 |
| `--pix-fmt` | `auto` | **v7.9.6**：输出像素格式（`pal8` / `rgb8` / `rgb555le` / `rgb565le` / `gray` / `rgb48le` / `yuv420p10le` …，见 §7.17）。`auto` ＝ 沿用源格式。请求要么被满足，要么 exit 2，不会静默降级 |
| `--list-pix-fmts` | — | **v7.9.6**：列出 86 种实测过的像素格式，并用 `OK`/`---` 标出当前编码器声明的那一列，然后退出 |
| `--color-range` | 跟随源 | **v7.9.6**：`tv` / `pc` / `mpeg` / `jpeg` / `unspecified` |
| `--colorspace` | 跟随源 | **v7.9.6**：色彩空间标记（`bt709` / `bt470bg` / `smpte170m` / `bt2020nc` …） |
| `--color-primaries` | 跟随源 | **v7.9.6**：原色标记（`bt709` / `bt470bg` / `bt2020` / `smpte431` …） |
| `--color-trc` | 跟随源 | **v7.9.6**：传输特性标记（`bt709` / `srgb` 写作 `iec61966-2-1` / `smpte2084`（PQ） / `arib-std-b67`（HLG） …） |
| `--dither` | `auto` | **v7.9.6**：降到低色深时的抖动（`auto` / `none` / `bayer` / `ordered` / `random` / `error_diffusion`），只在目标格式真的掉色时才发 |
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
| `--recompress` | 关 | 编码后按"网站转码"再压一遍（见 §7.12） |
| `--recompress-args` | 空 | 那一步的 FFmpeg 中间参数段；`-i` 和输出由程序给 |
| `--recompress-container` | 由编码器决定 | 二次压缩产物的容器扩展名 |
| `--recompress-codec` | 无 | **v7.9.5.1**：第二遍的视频编码器（libx265 / libvpx-vp9 / libsvtav1 / flv …），容器跟着它走 |
| `--recompress-quality` | 编码器默认 | **v7.9.5.1**：`-crf`（x264/x265/vp9/av1）或 `-q:v`（MPEG 家族） |
| `--recompress-audio-codec` | 按视频编码器推荐 | **v7.9.5.1**：`aac` / `libmp3lame` / `libopus` / `libvorbis` / `ac3` / `flac` / `copy` |
| `--recompress-audio-kbps` | 编码器默认 | **v7.9.5.1**：第二遍音频码率 |
| `--recompress-only` | 关 | **v7.9.5**：跳过直播编码，直接对已有文件做二次压缩（见 §7.12） |
| `--no-recompress` | — | 关掉二次压缩（优先于上面两个） |
| `--era ID` | 无 | **v7.6**：应用时代/站点预设（见 §7.12） |
| `--era-modern` | 关 | **v7.9.5.1**：预设的第二遍用 libx264 + AAC 的 mp4，而不是站点当年的编码器 |
| `--era-native` | 开 | **v7.9.5.1**：显式写"用站点自己的编码器"（默认行为，给脚本自证用） |
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
 5. Rescan encoders   重新扫一遍编码器（v7.9.5：同时重建能力表缓存）
 6. Audio encoder     扫描音频编码器：只列出"当前视频编码器 + 容器"装得下的（见 §7.13）
 7. Show FFmpeg filters
 8. Show configuration
 9. Start encoding
10. Language          界面语言，en / zh
11. Parallel shards   并行分片数，1＝关闭（见 §7.5）
12. FFmpeg toolchain  切换/下载/删除 FFmpeg 构建（见 §7.10）
13. Playback after run 编码完成后用什么播放（见 §7.11）
14. Era preset        时代/站点预设（5 个"时代" + 18 个"站点" + 自定义，见 §7.12）；
                      子菜单里 n/e/d 维护自定义预设、m 切"原生/现代"渲染
15. Recompress        二次压缩；1 开关、2 参数段、3 容器、4 只压一个已有文件、
                      5/6/7 第二遍的视频编码器 / 质量 / 音频编码器（见 §7.12）
16. Streams           视频/音频流选择（见 §7.16）
17. Subtitles         字幕：烧录 / 封装 / 关闭（见 §7.16）
18. Command line      把当前设置打印成一条等价命令行（见 §7.6）
19. Config files      命名配置档案：载入 / 另存 / 删除（见 §7.6）
20. Picture format    像素格式 / 色彩标记 / 抖动（见 §7.17）
 0. Exit
```

6 – 17 和 20 改完立即写进配置文件，下一次打开就是这次的值。18 是**只读**的（打印完就回菜单）；
19 载入档案后会把结果写回配置，和其它项一样。

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

参数页底部还会列一行当前**画面格式**（像素格式 + 已固定的色彩标记 + 抖动），
改它走主菜单 20（那里有格式清单可挑）。

每一项都是"回车＝保留当前值"。15、19 是开关，选中即翻转。`26` 可以填
`end` / `none` / `eof` 表示"到源尾"。16/17/18 会按音频编码器的能力**自动纠正**
（例如选了 ac3 却填 22050 Hz，会改成 32000 并说明原因；填 16 kbps 配 44.1 kHz 的
mp2 会改成 32），20 只允许填当前容器装得下的编码器。

选 `9` 之后会再打印一遍完整配置并问 `Start encoding? [Y/n]`，回车即开始。

---

## 5. 编码器 → 容器 → 默认音频 对照表

**输出扩展名必须和容器匹配**，否则直接报错退出（码 2）。下表是**默认**容器；
v7.8 起一部分编码器还能写别的容器（`--container` 指定，或者直接看输出扩展名），
完整清单和实测依据见 [`capability-matrix.md`](capability-matrix.md)。

| 编码器 | 默认容器 | 允许的扩展名 | 还能写（v7.9.5） | 默认音频 |
| --- | --- | --- | --- | --- |
| `rv10` `rv20` | rm | `.rm` `.rmvb` | — | `ac3` |
| `mpeg1video` `mpeg2video` | mpeg | `.mpg` `.mpeg` `.mpe` `.vob` | avi, mp4, mov, mkv | `mp2` |
| `mpeg4` `libxvid` | avi | `.avi` | mp4, mov, mkv | `libmp3lame` |
| `mjpeg` `h263p` | avi | `.avi` | mov, mkv, mp4（仅 mjpeg） | `libmp3lame` |
| `msrle` | avi | `.avi` | mov（**不能** mkv/asf：丢调色板） | `libmp3lame` |
| `wmv1` `wmv2` | asf | `.asf` `.wmv` | avi, mkv, mov | `wmav2` |
| `libx264` | mp4 | `.mp4` `.m4v` | avi, mkv, mov, flv | `aac` |
| `libtheora` | ogg | `.ogg` `.ogv` | mkv, avi | `libvorbis` |
| `flv` | flv | `.flv` | avi, mkv | `aac` |
| `flashsv` `flashsv2` | flv | `.flv` | avi/mkv（仅 flashsv） | `aac` |
| `qtrle` `rpza` `smc` `svq1` | mov | `.mov` `.qt` | avi/mkv（rpza、smc）；avi（svq1） | `aac` |
| `amv` | amv | `.amv` | — | `adpcm_ima_amv` |
| `roqvideo` | roq | `.roq` | — | `roq_dpcm` |
| `h261` | h261 | `.h261` | avi, mkv | **无音频** |
| `h263` | h263 | `.h263` | avi, mkv | **无音频** |
| `a64multi` `a64multi5` | a64 | `.a64` | avi, mkv | **无音频** |

### 几个会咬人的约束

* **RoQ**：宽高必须能被 16 整除。不满足会报
  `roqvideo requires width and height to be divisible by 16; got 250x190`。
  RoQ 文件只能写 `.roq`；帧率只接受整数（见下）。
* **`roq_dpcm`**：只接受 22050 Hz + 单/双声道。设了别的采样率会直接被拒：
  `roq_dpcm requires --audio-rate 22050 (or leave it unset for automatic correction)`。
* **AMV**：音频固定 22050 Hz IMA-ADPCM，而且 `22050 % fps` 必须整除，所以
  帧率真正可用的是 `5/10/14/15/18/21/25/30/50`（工具的表只列其中常见档位）。
* **MPEG-1**：帧率只能落在 `23.976 / 24 / 25 / 29.97 / 30 / 50 / 59.94 / 60`
  这八个真正的 MPEG-1 frame_rate_code 上，请求别的值会被吸附到最接近的一个。
  `ffmpeg -h encoder=mpeg1video` 里列的 `5/10/12/15` 是 libmpeg3 的"非官方"
  帧率，默认严格度下开编码器就会报
  `MPEG-1/2 does not support 15/1 fps` —— 所以那份列表不算数，工具按八个合法值
  校验并吸附（v7.9.4）。**MPEG-2** 有 frame rate extension，`5/10/12/15/75`
  之类都真的能编，因此照旧用 FFmpeg 声明的列表。
* **RealMedia（`rv10`/`rv20`）和 `roqvideo` 只接受整数帧率**（v7.9.5）：这族的
  文件头里 fps 是整数，`--fps 23.976` 会被写成 23.000，roq 更是直接拒编。
  现在三者都会四舍五入到最近的整数（23.976→24、29.97→30、59.94→60），报告的
  "FPS 支持"一栏对它们显示"只接受整数"。
* **原始 `.h261` / `.h263` / `.a64` 不存帧率**（v7.9.5）：裸码流里没有时间戳，
  15 fps 编出来的文件 ffprobe 一律读回 25 fps。画面没问题，工具会打印一条 note；
  要让帧率跟着文件走，用 `--container avi` 或 `.mkv`。
* **调色板编码器（`msrle`）只能单遍编码**：它的传输格式是 `pal8`，而 Matroska
  暂存（分片拼装用）会丢调色板，产物会变成纯黑。`--parallel` 会被自动跳过分片
  并在日志里说明（v7.9.4）。同理它**不能**写 `.mkv` / `.asf`（这两个容器同样丢
  调色板），只有 AVI 和 MOV 存得住。
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

| 阶段 | v7.9.4 | v7.9.5 |
| --- | --- | --- |
| .NET 进程启动 | 0.34 s（NativeAOT 可压到 ~0.05 s） | 同左 |
| FFmpeg 能力探测 | ~0.9 s（`-encoders` ×2、`-filters`、`-h filter=scale`、`-h encoder=X`、`-version`，共 6 次启动） | **首次 4 次，之后 0 次**（能力表落盘缓存，见下） |
| ffprobe 探测 | ~0.25 s（已由 3 次合并为 1 次） | 同左 |
| Preflight | ~0.2 s（Video + Audio 并发，但仍是 2 个进程） | ~0.1 s（**合并成 1 个进程**，且就是最终命令的缩样） |

一次 `ffmpeg` 启动约 **210 ms**（本机 10 次 `ffmpeg -version` 共 2.10 s）是这些数字
的来源。实测同一份 2 秒素材、同一个编码器：**改动前 10 次进程启动，改动后首次 8 次、
之后 3 次** —— 剩下的三次正好是预检、解码、编码。

### 能力表缓存（v7.9.5）

`ffmpeg -encoders` / `-filters` / `-h filter=scale` / `-h encoder=NAME` / `-version`
的答案只跟**这个二进制**有关，跟本次运行无关，所以第一次问完就存下来：

```
%LOCALAPPDATA%\LiveArchiveEncoder\cache\caps-<hash>.json      (Linux: ~/.local/share/…)
```

- `hash` = 二进制路径 + 大小 + mtime 的 SHA-256 前 16 位，换一个构建就是另一个文件，
  两套能力永远不会串用（这一点在 v7.9.4 的三个 bug 里是根因）；
- 一次 `-encoders` 同时喂视频 / 音频 / 字幕三个列表（以前是两次同样的命令）；
- **首次 4 次启动，之后 0 次**；每个编码器的 `-h encoder=NAME` 也是首次用到时才采、
  采完就存；
- 缓存坏了当没有：schema 版本不符、身份不匹配、JSON 损坏 → 重新扫一遍，不会让
  运行失败；
- CUI 菜单 **5**（重新扫描编码器）会强制重建；环境变量 `LAE_NO_CAPS_CACHE=1`
  对命令行同样生效（用来排查"我换了 ffmpeg 但工具还说旧能力"）；
- 整个 `cache/` 目录可以随手删。

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

### 在菜单里看命令行（v7.9.3，菜单 18）

菜单调好一套参数，想变成脚本又不想照着屏幕敲：

```
18. Command line       = （只读，打印完回菜单）
```

输出就是上面那个发射器生成的一行，和 `--emit-args` 完全同源：

```
命令行
  D:\rec\cam.mkv D:\rec\cam_live.flv --encoder flv --fps 15 --width 512 --height 288 ^
  --limit 384 --parallel 5 --recompress --recompress-args "-vf ""fps=23.976"" -c:v libx264 -crf 27"
  与菜单里这一模一样的一次运行——粘进脚本就能重跑。
```

另外这一行会在**正式编码前的确认提示之前**自动打印一次——看到配置摘要的同时就能
看到等价命令，觉得对就回车，觉得该留档就复制走。

> 引号是 Windows shell 的写法（`"…""…"…"` 在 cmd 与 PowerShell 里都表示内层引号），
> 直接粘进这两个 shell 可用；bash 里需要自己改成单引号。

### 多个配置档案（v7.9.3，菜单 19）

`config.json` 是"当前这份"，`configs/` 目录下则是可以随手切换的**命名档案**：

```
%LOCALAPPDATA%\LiveArchiveEncoder\config.json          ← 当前设置（退出时自动写）
%LOCALAPPDATA%\LiveArchiveEncoder\configs\*.json        ← 命名档案（菜单 19 管理）
```

菜单 19 进去先列出档案（按文件名排序，带修改时间），然后：

| 输入 | 作用 |
| --- | --- |
| `1`..`N` | 载入第 N 份，立刻应用到当前设置（随后写回 `config.json`） |
| `s` | 把**当前设置**另存为一个新档案（输入名字即可，自动补 `.json`） |
| `p` | 按**完整路径**载入一份档案（用于 `--save-config` 生成在别处的文件） |
| `d` | 删除档案（会先问 `[y/N]`） |
| `0` | 返回主菜单 |

细节：

- 载入走的是和启动恢复同一套 `Apply`：**输入文件不存在就跳过、当前 FFmpeg 没有
  这个编码器就跳过**，不会因为一份旧档案把状态搞坏。
- 名字只保留最后一段并过滤非法字符：输入 `../../evil` 存的是 `configs/evil.json`，
  **不会写到目录外**。
- **读不懂的文件会被拒绝，一个字段都不动**：档案是完整快照，一段坏 JSON 若按
  "全部缺省"应用，等于把当前设置清成默认值（v7.9.3.1 之前就是这样，还报载入成功）。
- 档案只是设置快照，不含产物；载入后仍需自己按 9 开跑。
- 想把配置整体搬到别处（便携 U 盘、CI 沙箱）：设 `LAE_CONFIG_DIR` 指向目标目录，
  `config.json` 与 `configs/` 都会跟着走。

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

| id | 年代 | live 层 | live 帧率挡位（v7.9.6.1） | 二次压缩 |
| --- | --- | --- | --- | --- |
| `dialup98` | 1997–2000 拨号 | rv20 160x120@10，上限 45 kbps | 10 → 8 → 5 → 3 | crf 34，160x120@10，音频 16k 单声道 |
| `broadband03` | 2001–2004 ADSL 初期 | rv20 320x240@15，上限 225 kbps | 15 → 12 → 10 → 8 → 5 | crf 31，320x240@15 |
| `flv07` | 2005–2008 视频网站 | flv 352x264@15，上限 300 kbps | 15 → 12 → 10 → 8 → 5 | crf 28，352x264@15，音频 22.05k 单声道 |
| `mp4_10` | 2009–2012 | libx264 640x480@25，上限 900 kbps | 25 → 20 → 15 → 10 | crf 25，640x480@25 |
| `hd14` | 2013–2016 | libx264 1280x720@30，上限 2500 kbps | 30 → 25 → 20 → 15 | crf 23，720p@30，立体声 96k |

**挡位也是"当年"的一部分**（v7.9.6.1）：只设基准帧率会让 25 fps 的预设沿用
13/8/5/3，第一次降档就砍掉一半帧率。现在预设自带当年那套挡位（依据见
[`era-presets.md`](era-presets.md) 的"动态帧率挡位"）；显式 `--fps` 或
`--fps-levels` 仍然优先，预设是兜底不是覆盖。站点预设**不设**挡位——它们只管
"网站怎么转码"，采集怎么做是你的事。

#### 站点预设（v7.9.5；v7.9.5.1 按站点指定编码器）

2005–2009 每个门户的转码档位都不一样，所以这一段拆成**站点**预设。它们
填**两层**——直播层的采集参数（尺寸 / 帧率 / 挡位 / 码率上限 / 缩放 / 音频）和
二次压缩层（分辨率 / 帧率 / 码率 / 音频 / 容器），并**指名那个站点当年用的
编码器**——320 kbps 的 H.264 和 320 kbps 的 Sorenson Spark 看起来完全是两回事，
只说码率等于只说了一半。

> **v7.9.6.2 起**站点预设也改菜单第 4 项参数。以前它只改第二遍和编码器，直播层
> 保持你的设置，结果 `--era youku07` 录的是 640x480@25 而第二遍写的是
> 352x264@15。现在 `--era youku07` 就是 352x264@15、320 kbps、15/12/10/8/5 挡位、
> mp3 22.05 kHz 单声道。显式参数仍然优先。

**编码器怎么用**（v7.9.5.1）：

- 预设的编码器同时是**直播层的兜底**：`--era bilibili09`（没给 `--encoder`）
  就用 flv1 编码直播那一遍；`--encoder` 显式给了就按你的来；
- 第二遍默认用站点自己的编码器（`.flv` 里 flv1、`.webm` 里 VP9）；
  加 `--era-modern` 改成 libx264 + AAC 的 mp4 观看版；
- CUI 菜单 14 的 `m` 切换这个偏好，会记在配置里。

| id | 站点 / 年代 | 第二遍（分辨率@fps，码率） | 编码器 → 容器 | 直播层兜底 |
| --- | --- | --- | --- | --- |
| `sina06` | 新浪播客 2005–2008 | 320x240@12，200 kbps | flv1 → `.flv` | flv |
| `sohu06` | 搜狐视频 2006–2009 | 320x240@15，240 kbps | flv1 → `.flv` | flv |
| `qq06` | 腾讯 / QQ 视频 2006–2009 | 320x240@**25**，260 kbps | flv1 → `.flv` | flv |
| `tudou06` | 土豆网 2005–2009 | 352x264@12，295 kbps（实测） | flv1 → `.flv` | flv |
| `youku07` | 优酷 2007–2010 | 352x264@15，320 kbps | flv1 → `.flv` | flv |
| `ku6_07` | 酷6网 2007–2010 | 320x240@15，240 kbps | flv1 → `.flv` | flv |
| `56_07` | 56网 2006–2010 | 320x240@15，220 kbps | flv1 → `.flv` | flv |
| `cctv06` | 央视网 2006–2009 | 384x288@25，350 kbps | flv1 → `.flv` | flv |
| `acfun08` | AcFun 2008–2011 | 320x240@15，260 kbps | flv1 → `.flv` | flv |
| `bilibili09` | 哔哩哔哩 2009–2012 | 384x288@24，320 kbps | flv1 → `.flv` | flv |
| `youtube07` | YouTube 2006–2008 | 320x240@15，200 kbps | flv1 → `.flv` | flv |
| `bilibili11` | 哔哩哔哩 2011–2013 | 640x360@30，700 kbps | H.264 → `.mp4` | libx264 |
| `youku11` | 优酷 2011–2013 | 640x360@25，800 kbps | H.264 → `.mp4` | libx264 |
| `iqiyi11` | 爱奇艺 2011–2014 | 640x360@25，800 kbps | H.264 → `.mp4` | libx264 |
| `cctv12` | 央视网 / CNTV 2010–2013 | 640x360@25，700 kbps | H.264 → `.mp4` | libx264 |
| `youtube11` | YouTube 2011–2013 | 640x360@30，700 kbps | H.264 → `.mp4` | libx264 |
| `bilibili14` | 哔哩哔哩 2014–2016 | 1280x720@30，1800 kbps | H.264 → `.mp4` | libx264 |
| `youtube15` | YouTube 2014–2016 | 1280x720@30，1500 kbps | VP9 + Opus → `.webm` | libx264 |

为什么 `qq06` 是 25 fps：腾讯当年的播放器是那批站点里唯一把帧率拉上去的，
同样 320x240 / 260 kbps，帧率翻倍——画面更顺，单帧更糊，这就是 QQ 播客的样子。
`cctv06` 的 25 fps 是另一个原因：PAL 广播电视素材，4:3、384x288。

```bash
# 直播层还是你选的 mpeg4/AVI，只有第二遍变成优酷 2007 档（flv1）
LiveArchiveEncoder.exe src.mp4 out.avi --encoder mpeg4 --era youku07 --recompress --era-modern

# 完全按 2009 年的 B 站来：直播编码器也换成 flv1，产物是 .flv
LiveArchiveEncoder.exe src.mp4 out.flv --era bilibili09 --recompress
```

依据见 [`era-presets.md`](era-presets.md#站点预设v795)。

#### 自定义预设（v7.9.5.1；v7.9.6.1 加直播帧率挡位）

内置表只读，**你自己的预存放配置里**，升级不会和本地修改打架。同名 id 会覆盖内置行
（这是"改一改优酷 2007"的方式），其余混在列表里（行首带 `*`）。

- CUI：菜单 14 → `n` 新建 / `e` 编辑 / `d` 删除；向导逐项问（id、类型、年份、
  第二遍尺寸 / 帧率 / 码率 / 编码器 / 容器、一句话描述；类型选 1 时再问直播层）。
- 存储：`config.json` 的 `CustomEras` 数组；缺字段、坏编码器名、写不通的容器组合
  都会在读取时被纠正成可用的值，而不是让程序启动失败。
- CLI 里 `--era <你的id>` 直接可用（参数解析不再只认内置表）。

`--era` 不碰你显式给的 `--encoder` / `--fps` / `--width` / `--height` / `--limit` /
`--scale-algo` / `--audio-kbps` / `--audio-channels`：预设是兜底，不是覆盖。
（v7.9.3.1 之前这条只在文档里成立——代码里预设会盖掉显式值，`--era flv07 --width 320`
实际按 352 编码；现在是真的了。）

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
`--recompress-container EXT`（默认由编码器决定）、`--recompress-codec NAME`、
`--recompress-quality N`、`--recompress-audio-codec NAME`、`--recompress-audio-kbps N`、
`--no-recompress`。

#### 第二遍的编码器（v7.9.5.1）

不想手写片段时，直接点名编码器；**容器跟着编码器走**，不会被硬塞进 mp4：

```bash
# HEVC + Opus，容器自动 .mkv
LiveArchiveEncoder.exe clip.mkv --recompress-only --recompress-codec libx265 --recompress-audio-codec libopus

# VP9 + Opus，容器自动 .webm（以前会被写进 mp4，然后播不了）
LiveArchiveEncoder.exe clip.mkv --recompress-only --recompress-codec libvpx-vp9

# 质量：x264/x265/vp9/av1 是 -crf，MPEG 家族是 -q:v
LiveArchiveEncoder.exe clip.mkv --recompress-only --recompress-codec libx265 --recompress-quality 30
```

| 视频编码器 | 可写容器（第一个是默认） | 质量参数 |
| --- | --- | --- |
| `libx264` | mp4, mkv, mov, avi, flv | `-crf` |
| `libx265` | mp4, mkv, mov（带 `-tag:v hvc1`） | `-crf` |
| `libvpx-vp9` | webm, mkv | `-crf` + `-b:v 0` |
| `libvpx`（VP8） | webm, mkv | `-crf` + `-b:v 1M` |
| `libaom-av1` / `libsvtav1` | mkv, webm, mp4 | `-crf` |
| `flv`（Sorenson Spark） | flv | `-q:v` |
| `mpeg4` / `mpeg2video` / `mpeg1video` / `mjpeg` / `wmv2` / `h263` | 各自的容器 | `-q:v` |

音频：`aac` / `libmp3lame` / `libopus` / `libvorbis` / `ac3` / `flac` / `copy`。
`libopus` 与 `libvorbis` 只接受 webm / mkv / ogg；`copy` 原样搬运音轨。

三条硬规则：

1. **写不通的组合在开跑前就报错**（退出码 2），而不是二十秒后由 FFmpeg 抛一句
   muxer 消息：`libvpx-vp9` + `mp4` 会被拒，并把可用容器列出来；
2. `-c:v` 写在 `--recompress-args` 里也照样识别：`-c:v libvpx-vp9` 不指定容器时，
   `_web` 的扩展名会自动变成 `.webm`；
3. 编码器**不在当前 FFmpeg 构建里**时直接说清楚
   （`this FFmpeg build has no 'x' encoder`），不会跑到一半才失败。

### 只跑二次压缩：`--recompress-only`（v7.9.5）

第二阶段本来只存在于一场直播编码之后，于是"把这个文件过一遍网站的转码"必须先
重新录一遍。`--recompress-only` 把这一层拆出来：**没有直播编码、没有编码器探测、
没有分片、没有预检**，输入 → 片段 → 输出，跑完退出。

```powershell
# 用站点预设（输出 clip_web.mp4，默认就在输入旁边）
LiveArchiveEncoder.exe clip.mkv --recompress-only --era youku07

# 自己给参数和容器
LiveArchiveEncoder.exe clip.mkv --recompress-only --recompress-container flv ^
  --recompress-args "-vf scale=320:240,fps=15 -c:v libx264 -profile:v baseline -b:v 240k"

# 自己给输出路径
LiveArchiveEncoder.exe clip.mkv D:\out\youku_version.mp4 --recompress-only --era youku07
```

- 输出默认是 `<输入名>_web.<容器>`，和直播产物同一套命名；
- `-i` 和输出仍然由程序给，片段仍走同一套逐 token 校验；
- `--no-recompress` 依旧优先，所以"关掉这一阶段"的脚本不会被 `--recompress-only`
  重新打开；
- 只处理本地文件（URL 请先下载），输入必须存在，输出不能等于输入。

本机跑一次 2 秒片段只要一次 FFmpeg 启动 —— 这是它和"重新录一遍"的区别。

CUI 里在菜单 **15 → 4**（"直接二次压缩一个文件"）。

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

#### 字体检查、导入与替代（v7.9.5.1）

ASS 字幕在 `[V4+ Styles]` 的 `Fontname` 列里点名它要的字体（也可能把字体直接嵌在
`[Fonts]` 段里）。程序现在会读这些名字——直接从字体文件的 `name` 表里读，所以
`msyh.ttc` 会同时报出 "Microsoft YaHei" 和 "微软雅黑"，"宋体" 也能对上 `simsun.ttc`。

```bash
# 这个字幕要哪些字体？本机有没有？在哪个目录？
LiveArchiveEncoder.exe --list-subtitle-fonts --subtitle movie.ass

# 把字体拷进程序自己的 fonts 目录（文件或目录都行）
LiveArchiveEncoder.exe --font-import "C:\Windows\Fonts\msyh.ttc"
LiveArchiveEncoder.exe --font-import D:\fonts\cjk
```

处理规则：

| 情况 | 行为 |
| --- | --- |
| 字体在 `fonts` 目录里 | 直接用（"随程序走"） |
| 只在系统字体里 | 仍能渲染，但换台机器可能变成方块——`--list-subtitle-fonts` 会标出来 |
| 谁都没有 | 烧录**照常进行**，libass 用回退字体；同时打一行 `NOTE` 说明缺哪个 |
| SRT / VTT（不指定字体） | 用 `--subtitle-font 家族名` 自己选，或不动（libass 默认） |

`--subtitle-font` 会被写成 `subtitles=...:force_style='FontName=…'`，覆盖整个文件里
所有样式的字体——这是"字体缺失时先让它能看"的应急开关，不是逐样式编辑器。

CUI 在菜单 **17 → 5**：列清单 / 导入 / 设替代字体。

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

## 7.14 容器切换（v7.8；v7.9.5 按实测重写）

"编码器 → 容器"不再是一对一写死：

| 编码器 | 可选容器（**加粗是默认**） |
| --- | --- |
| `mpeg1video` `mpeg2video` | **mpg**，avi，mp4，mov，mkv |
| `mpeg4` `libxvid` | **avi**，mp4，mov，mkv |
| `libx264` | **mp4**，avi，mkv，mov，flv |
| `libtheora` | **ogv**，mkv，avi |
| `flv` | **flv**，avi，mkv |
| `mjpeg` | **avi**，mkv，mov，mp4 |
| `msrle` | **avi**，mov（**不能** mkv / asf，见下） |
| `h261` `h263` | **h261/.h263**，avi，mkv |
| `h263p` | **avi**，mkv |
| `flashsv` | **flv**，avi，mkv |
| `cinepak` `asv1` `asv2` `cljr` `snow` `zmbv` `msmpeg4v2` `msvideo1` | **avi**，mkv |
| `rv10` `rv20` `roqvideo` `amv` `flashsv2` `qtrle` `smc` `svq1` `rpza` `wmv1` `wmv2` `a64multi` | 保持原来的单一容器（部分多一个 avi/mkv，见 §5） |

v7.9.5 的这张表不是"看起来合理"手写的：每一对都用
`tools/capability_sweep.py` 量过三道 —— muxer 接受、`ffprobe` 用编码器自己的
名字读回来、抽帧确认画面还在。完整矩阵（含"能存但没人这么用"的组合）在
[`capability-matrix.md`](capability-matrix.md)。

只过第一道的组合全部**没有**放进来，因为它们会给你一个坏文件：
`rv20 → avi`（存成 rawvideo）、`mpeg4 → rm`（tag 被改成 rv20）、
`qtrle → avi`（同上）、`msrle → mkv/asf`（调色板丢失，画面纯黑）。

```bash
# mpeg1video 写进 AVI：两条都行，第二条不用再点名容器
LiveArchiveEncoder.exe src.mp4 out.avi --encoder mpeg1video --container avi
LiveArchiveEncoder.exe src.mp4 out.avi --encoder mpeg1video
# libx264 写进 Matroska / FLV
LiveArchiveEncoder.exe src.mp4 out.mkv --encoder libx264 --container mkv
LiveArchiveEncoder.exe src.mp4 out.flv --encoder libx264 --container flv
```

`--container` 认封装器名也认扩展名（`mkv` 和 `matroska` 都行）。菜单在
**参数设置 → 28**；换视频编码器时若新编码器写不了当前容器，会提示并回退。

**不给 `--container` 时，输出扩展名就是容器请求**（v7.9.4）：上表里任何一格
都只要把输出名写成对应的扩展名即可，CLI 与 CUI 会打印一句
`NOTE: mpeg1video writes avi for this output; the '.avi' extension selected it.`
`--container` 显式给了就按它来，扩展名对不上照样报错；扩展名不属于该编码器任何
容器时（`--encoder rv20` + `out.mkv`）也照旧报错，不做静默兜底。

选不了的组合（例如 `--encoder rv20 --container mkv`）**直接报错而不是静默回退**
——静默回退会给你一个没点过的扩展名。

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

## 7.17 输出像素格式与色彩（v7.9.6）

v7.9.5 以前，管道里的像素格式是**推导**出来的：源是什么就用什么，源不行就取同
色度家族里编码器接受的第一个。这满足"不出错"，但不满足"我要 16 位色"或"我要 256 色
索引"这类要求——而这恰恰是老录像归档里最常被问到的。

### `--pix-fmt FORMAT`

```
# 256 色索引画（msrle 只吃 pal8，这里显式写出来）
LiveArchiveEncoder in.mkv out.avi --encoder msrle --pix-fmt pal8 --dither bayer

# 16 位色（5:6:5）
LiveArchiveEncoder in.mkv out.avi --encoder zmbv --pix-fmt rgb565le

# 10 位 4:2:0，HEVC 归档
LiveArchiveEncoder in.mkv out.mkv --encoder libx264 --pix-fmt yuv420p10le

# 灰度
LiveArchiveEncoder in.mkv out.avi --encoder cinepak --pix-fmt gray
```

三个规则：

1. **请求要么被满足，要么被拒绝。** 编码器声明里没有这个格式就 exit 2，并把编码器
   真正支持的格式列出来——不会"好心"降级。静默降级是这一版最不想保留的行为：
   `I asked for 10-bit and got 8-bit` 只有数位数的人才会发现。
2. **写出的格式会被核对。** 编码结束后用 ffprobe 读回真实 `pix_fmt`，和请求不一致
   就打一行 note。实测例子：`-c:v libx264 -pix_fmt gray` 的产物是 `yuvj420p`——
   编码器把 gray 当输入接受，编码时却按 4:2:0 写。
3. **调色板格式不能进丢调色板的容器。** `pal8` 只允许 AVI / MOV。Matroska 和 ASF
   把未知 VfW 编码器存成不带调色板的 BITMAPINFOHEADER，同一个片段在 AVI 里 YAVG 123、
   在 mkv 里 YAVG 16（纯黑）。这条以前只在分片时拦，现在单遍也拦。

表里只有**实测过**的格式：LE 变体（`yuv420p10le` 等）在表内，BE 变体（`yuv420p10be`）
不在——不是不支持，而是"每帧字节数没量过就不猜"。写成表外的名字会 exit 2 并列出认识的
格式，不会按名字去推断帧长。

`--pix-fmt` 只管**直播那一遍**。二次压缩是用户自己写参数段的，要保格式就在
`--recompress-args` 里写 `-pix_fmt`。

`--list-pix-fmts` 会按家族列出全部 86 种格式，`OK` 表示当前编码器声明支持：

```
$ LiveArchiveEncoder --list-pix-fmts --encoder msrle
Verified pixel formats (raw video transport)
Encoder msrle declares: pal8

  pal8
   OK  pal8         8-bit palette, 1 B/px + 1 KB palette
```

### 帧长是量出来的，不是算出来的

编码器读的是裸 `rawvideo` 管道，一帧就是 `宽 × 高 × 每像素字节数`（调色板格式再加
1024 字节）。这个数字错一个字节，画面就会一帧一帧地横向滑走——v7.9.4 的 msrle
黑屏就是这么来的。

所以 `Core/PixelFormatCatalog.cs` 里的表全部由 `tools/pixfmt_sweep.py` 实测生成：
86 种格式，每种都真的写 3 帧 64×48 裸视频再除以帧数。v7.9.6 用它抓到两个潜伏错误：

* `yuv440p`（4:4:0）被写成 1.5 B/px，实测是 **2.0**（色度全宽半高）；
* 它的可整除约束被当成 4:2:0 的 2×2，实际只要高为偶数。

历史版本从来没有选中过 4:4:0，所以"每帧少读一半"这个错误一直躺着。位打包的
`monob`/`monow` 也按行字节对齐算（65 像素宽 = 9 字节/行）。

### 色彩标记：`--color-range` / `--colorspace` / `--color-primaries` / `--color-trc`

这四个是**写入流头的标记**，不转换画面。不填就沿用源；填了就覆盖那一项。

| 值 | 说明 |
| --- | --- |
| `tv` / `pc` | 有限范围（16–235）/ 全范围（0–255） |
| `bt709` / `bt470bg` / `smpte170m` / `bt2020nc` / `bt2020c` | 常见的色彩空间（矩阵） |
| `bt709` / `bt470bg` / `bt2020` / `smpte431` / `smpte432` / `jedec-p22` | 原色 |
| `bt709` / `iec61966-2-1`（sRGB）/ `smpte2084`（PQ）/ `arib-std-b67`（HLG） / `linear` | 传输特性 |

两个实测事实：

* **libx264 收下 `-color_primaries`/`-color_trc` 但不写**。同样的值走
  `-x264-params colorprim=…:transfer=…:colmatrix=…` 才进码流（实测：前者产物
  `primaries=unknown`，后者 `smpte2084/bt2020`）。程序对 libx264 两个都发，
  并把 FFmpeg 与 x264 拼写不同的四个值自动翻译（`gamma22→bt470m`、
  `gamma28→bt470bg`、`log→log100`、`log_sqrt→log316`）。
* **别的老编码器根本没地方存原色/传输特性**。mpeg4 / mpeg2video 的码流里没有这两个
  字段，Matroska 的 `Colour` 元素也只落 Range 和矩阵。这是 1990 年代码流的容量问题，
  不是程序偷懒——写进去的只有容器能承载的部分。

### `--dither`：降到低色深时的抖动

`auto`（默认）不加任何参数，交给 swscale 自己挑；`none` 关掉；`bayer` / `ordered` /
`random` / `error_diffusion` 走 `-sws_dither`。

**只在目标格式真的会掉色时才发这个参数**：`pal8`、`rgb8`（3:3:2）、`rgb555`/`rgb565`、
`monob`/`monow`、8 位 `gray`。`yuv420p` 上这个参数什么也不做，发了只会让日志说谎。

实测（64×48 渐变源，4 帧）：四种模式产出的字节各不相同，`auto` 也不等于 `none`，
所以默认值确实在抖动，不是关着的。

### 菜单

主菜单 **20「画面格式与色彩」**：像素格式（按编号选，或直接打名字）、四项色彩标记、
抖动、格式清单。换了编码器之后，如果它吃不消当前格式，程序会清掉并说一句，而不是
留到预检才失败。

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
| `pal8 frames cannot be stored in 'matroska'` | 调色板格式只能进 AVI / MOV：Matroska/ASF 存未知 VfW 编码器时不带调色板，画面会是纯黑 | 改用 `.avi` / `.mov`，或换一个不需要调色板的像素格式（`--pix-fmt yuv420p`）；见 §7.17 |
| `encoder 'xxx' cannot take yuv420p10le` | 该编码器不声明这个像素格式 | `--list-pix-fmts --encoder xxx` 看它到底吃哪些；老编码器多数只吃 8 位 |
| 请求了 `gray` 但文件报 `yuvj420p` | 编码器把 gray 当输入接受、编码时按 4:2:0 写（libx264 实测如此） | 这是 note 不是错误；要真的灰度就改用 `cinepak`/`qtrle` 这类原生 gray 编码器 |
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

### 逐版变更记录去哪了

**搬到 [`CHANGELOG.md`](CHANGELOG.md) 了**：从 Python 6.2.2 到当前版本，每版一节，
写明"改了什么、为什么、会不会打动产物"。本文件只讲**怎么用**，不再混入版本历史
——两者放一起时，找参数的人要先翻过十几个版本。

v6.3.3 基线的已知缺陷（AMV 0 字节、默认开 CUDA、SMB 两个错误选项、音频只有采样率
白名单、字体不落地、字幕滤镜检不出）在 CHANGELOG 的 v6.3.3 一节里列了表，附修在
哪一版——这些是**有意偏离**，不是"行为差异"。

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
- `EraPresetTests` — id 唯一、两层参数齐全、**两种渲染（原生/现代）的参数段都能通过
  自家校验器**、时代在像素数与码率上单调递增
- `SitePresetTests`（v7.9.5.1）— 站点必须指名编码器与容器、FLV 时代的编码器就是 flv1、
  `qq06` 的 25 fps、原生/现代只差编码器
- `RecompressCodecTests`（v7.9.5.1）— 编码器目录的容器约束（VP9+Opus 不能进 mp4）、
  容器由编码器对推导、质量参数按编码器选 `-crf`/`-q:v`、码率渲染丢掉 `-b:v 0`
- `CustomEraTests`（v7.9.5.1）— 自定义预设的 JSON 往返、id 归一化、坏编码器/坏容器纠正、
  同名覆盖内置行
- `SubtitleFontTests`（v7.9.5.1）— ASS 样式表按列名定位、SRT 不指定字体、中英族名对照、
  同族不同 face、`force_style` 拼装、字体文件 `name` 表解析（没有字体时自动跳过）
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
dotnet test --solution LiveArchiveEncoder.V7.slnx                失败 0，通过 475
dotnet test --solution LiveArchiveEncoder.V7.slnx -p:LaeTfm=net11.0   同样 475
pytest  186 项（Windows 实测 184 通过 + 2 跳过）：
  option_smoke      83（48 组参数 + 23 项参数检查 + 12 条专项回归：分片拼接、pal8 非黑、
                     AVI 帧率头、整数帧率、站点预设、站点自带编码器、独立二次压缩、
                     能力缓存、原生编码器落盘、编码器推容器、容器冲突拒绝）
  feature_sweep     35（33 通过 + 2 跳过：a64multi / a64multi5 只有 muxer 没有 demuxer）
  compare_matrix    12
  error_paths       11
  cui_smoke         13 个阶段（内含 40+ 项检查，Linux 多一条）
  subtitles         10（含 v7.9.5.1 的字体清单 / 强制字体 / 缺失字体提示）
  stream_selection   9
  http_source        7
  config_conversion  4（配置 ↔ 命令行往返）
  ab_duration        2（mpeg1video / wmv2 各 4 变体 × 4 runs 的中位数对比）
```

Linux（cia1，Ubuntu 22.04.5 / glibc 2.35）上是 **474 = 473 通过 + 1 跳过 × 两个目标框架**
（少的 1 条是映射网络驱动器用例，Windows-only；跳过的 1 条是"读一个真实字体"，
服务器上没装字体），e2e 同样 186 项 = 184 通过 + 2 跳过（2 核机器上两阶段
317 s + 224 s）。

v7.9.4 的数字：单测 Windows 424 × 两个目标框架、Linux 423 = 422 + 1 跳过，
e2e 167（165 通过 + 2 跳过）。

v7.9.3.1 的数字：单测 408 × 两个目标框架、e2e 162（160 通过 + 2 跳过）。
其中 Linux 侧没有重跑（ci1/ci2 仍是 v7.9.1 的读数），下面这段说明它差在哪。

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
