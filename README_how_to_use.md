# Live Archive Encoder v7 (.NET 10 / C#) — 使用教程

面向使用者的操作手册。想了解迁移细节与验证数据，看 `README.md` 和
`tests/TEST_REPORT_v7.md`。

> **一句话说明**：把一个现代视频，以"当年直播录档"的方式，实时节奏地重新编码成
> RV20 / RoQ / MPEG-1 / MPEG-4 / WMV2 等老式编码器格式，并带自适应码率控制。
> **画面丢帧、损坏、花屏等后处理不在本工具范围内**，那是同级的 `frame_damage.py`
> 的职责。

---

## 1. 安装与构建

### 依赖

| 需求 | 说明 |
| --- | --- |
| FFmpeg + ffprobe | 必须在 `PATH` 里，或用 `--ffmpeg` / `--ffprobe` 显式指定 |
| .NET 10 SDK | 仅**构建**时需要 |
| 运行 | JIT 版需要 .NET 10 运行时；NativeAOT 版什么都不需要 |

老式编码器（rv20、roqvideo、amv 等）只在完整构建的 FFmpeg 里才有。推荐
[BtbN GPL 构建](https://github.com/BtbN/FFmpeg-Builds/releases)；进交互式菜单时，
启动横幅会显示 `BtbN default` 和 `GPL build` 两行，可以直接确认当前用的是哪一套。

### 构建

```powershell
# 常规（JIT）构建
dotnet build src/LiveArchiveEncoder/LiveArchiveEncoder.csproj -c Release
# -> src\LiveArchiveEncoder\bin\Release\net10.0\LiveArchiveEncoder.exe

# 或者用构建脚本，顺便出 NativeAOT 单文件版
tests\build.ps1 -Aot
# -> publish\jit\LiveArchiveEncoder.exe       162 KB，需要 .NET 10 运行时
# -> publish\aot\LiveArchiveEncoder.exe       3.8 MB，单个文件、零依赖

# 三套全出（JIT + AOT + AOT-AVX2）
tests\build.ps1 -Aot -AotAvx2
# -> publish\aot-avx2\LiveArchiveEncoder.exe  3.9 MB，把指令集基线从 SSE2 抬到 AVX2
```

三套产物的差别只在 C# 侧（实测 `--cpu-info`）：

| 产物 | `Vector<byte>.Count` | Avx2 | Avx512F | 特点 |
| --- | --- | --- | --- | --- |
| `publish\jit` | 32 | ✅ | ✅ | JIT 运行时按 CPU 分派，指令集最全 |
| `publish\aot` | **16** | ❌ | ❌ | NativeAOT 默认按 SSE2 基线编译 |
| `publish\aot-avx2` | 32 | ✅ | ❌ | 显式 `-p:IlcInstructionSet=avx2` |

**别指望 AVX2 能加速编码。** 真正的编码发生在 FFmpeg 子进程里，FFmpeg 自己
运行时分派 SIMD，跟本项目的指令集基线无关。实测 turbo 20s（mpeg4，各 3 轮）：

| 产物 | turbo 20s 墙钟 | 进程启动（`--help`） |
| --- | --- | --- |
| `publish\jit` | 1.65 s | 0.336 s |
| `publish\aot` | 1.44 s | 0.268 s |
| `publish\aot-avx2` | 1.42 s | 0.278 s |

AOT 比 JIT 快约 0.22 s（省掉 JIT 预热），**AVX2 相对 AOT 只有 0.02 s，在噪声内**。
所以要最快就选 `publish\aot-avx2` + `--turbo`，但别期待 AVX2 本身带来什么。

NativeAOT 需要 Visual Studio 的 **Desktop development with C++** 工作负载（要用到
`link.exe`）。如果构建环境里 `vcvarsall.bat` 定位不到 Windows SDK，会报
`LNK1181: cannot open input file 'advapi32.lib'`，此时显式给它版本号：

```powershell
tests\build.ps1 -Aot -WindowsSdkLibVersion 10.0.26100.0
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
| `--audio-kbps` | `32` | 音频码率 |
| `--audio-channels` | `1` | 声道数 |
| `--audio-rate` | 跟随源 | 采样率（Hz） |
| `--width` `--height` | `256` `192` | 输出分辨率 |
| `--no-crop` | 默认裁剪 | 加上它表示**缩放**到目标尺寸，而不是 4:3 裁切 |
| `--scale-algo` / `--scale-flags` | `neighbor` | 缩放核；`cubic` 是 `bicubic` 的别名 |
| `--deinterlace` | `off` | `bwdif` / `yadif` / `w3fdif` |
| `--deint-mode` | `send_frame` | `send_frame` 或 `send_field` |
| `--deint-parity` | `auto` | `auto` / `tff` / `bff` |
| `--deint-marked-only` | 关 | 只反交错被标记为 interlaced 的帧 |
| `--x264-preset` | `medium` | 仅 libx264 |
| `--x264-tune` / `--x264-profile` / `--x264-level` | 空 | 仅 libx264，留空＝自动 |
| `--no-cuda` | 默认尝试 CUDA | 加上它就**不**传 `-hwaccel cuda` |
| `--ffmpeg` / `--ffprobe` | PATH 里找 | 显式指定可执行文件 |
| `--list-filters` | — | 打印当前 FFmpeg 支持的反交错滤镜和缩放算法后退出 |
| `--quiet` | 关 | 不打印进度横幅和结果摘要 |
| `--no-cui` | 关 | 无输入参数时不进菜单，直接报错退出（给脚本用） |
| `--audio-mode` | `inline` | `inline` / `separate` / `auto`，见第 7 节 |
| `--help` / `-h` | — | 打印用法后退出（退出码 0） |
| `--cpu-info` | — | 打印运行时检测到的指令集（诊断用） |
| `--turbo` | 关 | 去掉实时节奏，跑到解码/编码的极限速度（见 7.5 节） |
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
 5. Rescan encoders   重新扫一遍编码器
 6. Show FFmpeg filters
 7. Show configuration
 8. Start encoding
 9. Turbo mode        开关，默认关闭（见 §7.5）
10. Language          界面语言，en / zh
11. Parallel shards   并行分片数，1＝关闭（见 §7.5）
12. FFmpeg toolchain  切换/下载/删除 FFmpeg 构建（见 §7.10）
13. Playback after run 编码完成后用什么播放（见 §7.11）
 0. Exit
```

9 – 13 改完立即写进配置文件，下一次打开就是这次的值。

选编码器时会自动做三件事：把不支持的帧率吸附到合法值、把尺寸归一化到编码器
支持的尺寸、按容器挑一个默认音频编码器。AMV 和 RoQ 还会额外强制音频参数
（AMV 固定 22050 Hz IMA-ADPCM；RoQ 固定 `roq_dpcm` @ 22050 Hz 单/双声道），
并在屏幕上说明为什么。

**参数页**（编号 → 含义）：

```
 1. limit            2. recover          3. window           4. base FPS
 5. FPS levels       6. CRF / q:v        7. threads          8. capture buffer
 9. size            10. crop 4:3        11. scale algorithm 12. deinterlace
13. deint mode      14. field parity    15. deint marked only
16. audio bitrate   17. audio channels  18. audio sample rate
19. CUDA decode     20. audio encoder
21. x264 preset     22. x264 tune       23. x264 profile    24. x264 level
                    （21–24 仅在编码器是 libx264 时出现）
25. segment start   26. segment end
 0. Back
```

每一项都是"回车＝保留当前值"。10、15、19 是开关，选中即翻转。`26` 可以填
`end` / `none` / `eof` 表示"到源尾"。

选 `8` 之后会再打印一遍完整配置并问 `Start encoding? [Y/n]`，回车即开始。

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

## 7.5 提速：`--turbo` 与 `--parallel`

交互式菜单里也有这两个开关：主菜单第 9 项 `Turbo mode`、第 11 项
`Parallel shards`，改完立即写进配置（**默认都是关闭**，turbo 会改变产物，
不该默认打开）。命令行则用 `--turbo` / `--parallel N`（分片上限 8）。

### 前提：默认慢是**故意的**，不是性能问题

v7 按 1/fps 的墙钟节奏输出，用来模拟直播录档。实测 20 秒素材：

| 模式 | 墙钟 |
| --- | --- |
| 实时（默认） | 21.4 s |
| `--turbo` | **1.6 s（13×）** |

也就是说默认模式 96% 的时间在 sleep，CPU 只用了约 1.4%。
`--limit` 是码率上限（触发降 FPS），**不是速度开关**，调大它不会加速。

### `--turbo`

去掉输出循环和采集线程两处墙钟节流，改为「等采集线程真的交出下一帧」。
滞回判定的 0.18 / 0.30 / 0.75 / 0.80 秒从墙钟改为**媒体时间**，
否则每个窗口会多放行几十倍的帧，自适应阶梯永远不会稳定。

```powershell
# 20 秒素材，实时需 21s，turbo 约 1.6s
LiveArchiveEncoder.exe src.mp4 out.rm --encoder rv20 --turbo
```

**要注意的代价（必须知道，不是免费的）：**

1. **产物会变。** `pool.TryTakeLatest` 取的是「此刻已解码的最新帧」，
   fresh/repeat 比例本来就是墙钟竞态的结果；跑得快，采集线程供不上，
   repeat 会增多。想要和 v6.3.3 逐字节对齐的归档，别开 turbo。
2. **自适应码率控制在 turbo 下基本失效。** 反馈环依赖实时进度回传，
   全速跑时槽位循环在编码器追上来之前就结束了。等价于 `--limit 99999` 的行为。
3. 剩下的墙钟几乎全是**固定开销**（见下），不是编码。20s 和 1s 只差 0.1s。

### `--parallel N`

把时间范围切成 N 段并发编码，再用 `ffmpeg -f concat -c copy` 拼回去。

**只对白名单容器生效**，其余自动回退单段并在终端说明原因：

| 容器 | 是否可分段 | 实测 |
| --- | --- | --- |
| `avi` `mp4` `flv` `asf` `mpeg` | ✅ | 帧数守恒，时长误差 < 0.03s |
| `rm` | ❌ 回退 | 时长漂移 +0.031s，容器被重写而非拷贝 |
| `roq` | ❌ 回退 | **第二段整段丢失**（250 帧 → 125 帧，990KB → 222KB） |

另外每段不足 4 秒也回退——每段要额外付约 1.5s 的进程启动开销。

```powershell
# 只在 avi/mp4/flv/asf/mpeg 上真正生效
LiveArchiveEncoder.exe src.mp4 out.avi --encoder mpeg4 --turbo --parallel 4

# roq / rm 会打印 "Sharding skipped: ..." 后走单段，不会损坏产物
LiveArchiveEncoder.exe src.mp4 out.roq --encoder roqvideo --turbo --parallel 4
```

**实测结论：目前分段基本是净亏损。** 白名单内编码器的边际成本只有 0.06–0.26 s
（20s 素材），而每段要多付约 1.5 s 启动开销，所以 20s 源 `--parallel 4`
反而比单段 turbo 慢（2.33 s vs 1.85 s）。唯一边际成本够高、值得分段的是
**roqvideo（8.3 s）**，而 roq 恰好不能拼接。
接口留着，等固定开销压下去之后才有意义。

### 固定开销花在哪（turbo 之后的新瓶颈）

| 阶段 | 耗时 |
| --- | --- |
| .NET 进程启动 | 0.34 s（NativeAOT 可压到 ~0.05 s） |
| FFmpeg 能力探测（`-encoders` ×2、`-filters`、`-h filter=scale`、`-h encoder=X`） | ~0.9 s |
| ffprobe 探测 | ~0.25 s（已由 3 次合并为 1 次） |
| Preflight（Video + Audio） | ~0.2 s（已改为并发） |

想再快：用 `publish\aot\LiveArchiveEncoder.exe`（AOT 单文件），
配合 `--turbo` 就是目前最快的组合。

---

## 7.6 配置持久化与中英文

### 配置自动保存

交互式菜单（CUI）在**退出时、切换 turbo/语言/分片数时、每次开始编码前**都会把当前
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

### 中英文

```powershell
LiveArchiveEncoder.exe --lang zh          # 命令行用中文
LiveArchiveEncoder.exe --lang en          # 命令行用英文
```

CUI 主菜单第 10 项 `Language / 界面语言` 可直接切换，并写进配置。
接受 `en` / `zh` / `zh-CN` / `中文` 等写法。优先级：**命令行 `--lang` > 配置里存的
语言 > 默认英文**。

已翻译的范围：主菜单、参数页 26 项、编码器选择页、`Show configuration`、编码结果
摘要、进度横幅里的标签（PixFmt / Output size / Time range 之类）、SMB 提示。

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

## 7.8 远程资源（SMB 共享）

输入**和输出**都可以直接给 UNC 路径，正反斜杠都认：

```powershell
LiveArchiveEncoder.exe \\nas\share\record.mp4 out.rm --encoder rv20
LiveArchiveEncoder.exe //nas/share/record.mp4 out.rm --encoder rv20

# 直接写回共享目录
LiveArchiveEncoder.exe record.mp4 \\nas\share\out.rm --encoder rv20
```

### 读取侧

识别为 UNC 时会给 FFmpeg 加上网络韧性参数（`-rw_timeout`、`-reconnect 1`、
`-reconnect_streamed 1`、`-reconnect_delay_max 5`），避免网络抖动时整个管道挂死。
本地文件不会加这些参数。

> **v7.4 修正**：v7.3 的列表里有 `-stimeout`，但那是 **ffplay 的选项**，FFmpeg
> 根本不认识（`ffmpeg -h full` 里查不到）。任何 UNC 输入都会在一帧还没读到之前
> 直接失败：`Unrecognized option 'stimeout'. Error splitting the argument list`。
> 已移除。

### 写入侧

`-reconnect*` 系列在 `ffmpeg -h full` 里标记为 `D`（decoding only），只能用在
输入端，放到输出端会被拒。写入侧唯一可用的韧性选项是 **`-rw_timeout`**（它同时
覆盖读写），所以输出为 UNC 时只加这一个。

另外加了两个写侧保护：

- 编码前对输出目录做一次**可写探测**（建一个 0 字节标记文件再删掉）。实时节奏
  编码很慢，20 秒之后才发现共享是只读的太亏了。
- 输出为 UNC 时在 banner 里同样打印协商到的协议版本（`SMB output : ...`）。

### SMB 版本协商

**在 Windows 上版本协商是操作系统做的，应用程序无法直接指定。** 本程序能做的是
把实际协商结果查出来并显示，让"悄悄降级到 SMB1"这种事暴露出来：

```
SMB share       : \\nas\share -- SMB3 in use
SMB output      : \\nas\share -- SMB3 in use
```

- 协商到 SMB3 → `SMB3 in use`
- 只有 SMB2 → `SMB2 in use (server did not offer SMB3)`
- 落到 SMB1 → `SMB1 in use - obsolete and insecure; enable SMB2/3 on the server`（警告）

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
python tests\feature_sweep.py                                   # 默认 JIT
CS_EXE=publish\aot\LiveArchiveEncoder.exe python tests\feature_sweep.py
```

对当前 FFmpeg 探测到的**全部 35 个** legacy 编码器各跑 2 秒片段，检查退出码、
产物非空、包含视频流、时长合理，并留一份产物在 `tests\out_fsweep\`。

三种结果：`PASS` 正常；`WARN` 容器不写时长（amv / h263 / roq / h261 是容器本身的
行为，用纯 ffmpeg 生成的对照文件同样没有时长，不是本程序的问题）；`NOPROBE` 指
FFmpeg 只有 muxer、没有 demuxer（例如 a64），产物写出来了但 ffprobe 读不回来。
只有 `FAIL` 才是真问题。

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
%LOCALAPPDATA%\LiveArchiveEncoder\ffmpeg\<来源>\bin\ffmpeg.exe
```

多版本**并存**，切换是瞬时的；下载失败也不会破坏已经在用的那个。

### 四个来源

| id | 项目 | 构建 | 下载方式 |
| --- | --- | --- | --- |
| `btbn`（**默认**） | [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) | master 每日自动构建，win64 GPL | 自动（.zip） |
| `gyan` | [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) | git master full | 自动（.7z，需要 7-Zip 或 PATH 里的 `7z`） |
| `avbuild` | [avbuild（SourceForge）](https://sourceforge.net/projects/avbuild/files/windows-desktop/) | master | **手动**（见下） |
| `nonfree` | [BnqDzj/FFmpeg-Builds-nonfree](https://github.com/BnqDzj/FFmpeg-Builds-nonfree) | master 每日自动构建，win64 nonfree | 自动（.zip） |

默认来源是 `btbn` 的**每日构建版**（GitHub release 标签 `latest`，资产
`ffmpeg-master-latest-win64-gpl.zip`）。

### 命令行

```powershell
# 看看装了哪些
LiveArchiveEncoder.exe --ffmpeg-list

# 下载并安装（默认 btbn 的每日构建）
LiveArchiveEncoder.exe --ffmpeg-download                       # 等价于 --ffmpeg-download btbn
LiveArchiveEncoder.exe --ffmpeg-download nonfree --proxy http://127.0.0.1:10808

# 用哪个构建来编码
LiveArchiveEncoder.exe src.mp4 out.rm --encoder rv20 --ffmpeg-source btbn
LiveArchiveEncoder.exe src.mp4 out.rm --encoder rv20 --ffmpeg-source path   # 回到系统 FFmpeg

# 删除
LiveArchiveEncoder.exe --ffmpeg-delete gyan

# 手动来源（avbuild）：先自己下载，再从本地文件安装
LiveArchiveEncoder.exe --ffmpeg-download avbuild --ffmpeg-archive C:\Downloads\xxx.7z
```

`--proxy` 可以省略；省略时会复用配置文件里记住的代理。

### 下载细节

- **有 `aria2c` 就优先用它**，16 连接、断点续传、走代理；没有则退回 `HttpClient`
  单连接，同样支持代理。aria2c 失败也会自动退回 `HttpClient`。
- **会校验压缩包的 magic bytes**（`PK` / `7z¼¯'`）。有些站点（比如 SourceForge）
  会返回一个 HTML 拦截页，下载器照样会把它"下载成功"——没有这步你就会得到一个
  131 KB 的假压缩包，然后看到解压器报一堆莫名其妙的错。
- `.zip` 用内建的 `ZipFile` 解压；`.7z` 需要 PATH 里的 `7z` / `7za` / `7zz`，
  或装在 `C:\Program Files\7-Zip\7z.exe`。找不到会明确告诉你去装 7-Zip，
  或者改用 `.zip` 的 btbn / nonfree。
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
- 交互式菜单 `13` 可以设置播放方式和外部播放器路径（填 `-` 清除，回到系统关联）。
  设置成 ffplay / external 之后，每次编码结束会多问一句 `Play the result now? [Y/n]`。

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

# 机器没有 NVIDIA / 不想看 CUDA 警告
LiveArchiveEncoder.exe src.mp4 out.rm --encoder rv20 --no-cuda

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
| 一堆 CUDA/cuvid 警告 | 机器没 NVIDIA 或驱动不匹配 | 加 `--no-cuda` |
| 输出文件只有音轨 / 时长不对 | 老式 muxer 的坑（RoQ、RealMedia 都出现过） | 默认 `inline` 模式不会遇到；如果用 `--audio-mode separate`，工具内置了 3 帧预演 + 输出解码校验，会拦下并自动回退到单进程 |

---

## 11. 和 Python 版（v6.3.3）的关系

C# v7 是 v6.3.3 的**行为等价**移植：FFmpeg 命令行（包括 `-threads` 的位置）、
自适应状态机的阈值与迟滞、选帧规则、滚动码率算法、所有默认值和常量表，全部逐项
照搬。已验证：12 个编码器/参数矩阵结构字段全一致、11 条错误路径退出码与文案
一致、关掉节流后帧计数完全一致。

所以**同一条命令在 Python 版和 C# 版上应产出等价的结果**，可以直接对照着用。

C# 版在 v7.3 之外多出来的东西：`--turbo` / `--parallel`（提速，见 §7.5）、配置
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

1. **引入 NUnit**（`tests/LiveArchiveEncoder.Tests`，121 个用例）：纯逻辑单测，
   不依赖 FFmpeg 和网络，几秒跑完；Python harness 继续负责"与 v6.3.3 是否等价"
   那一层。见 §12。
2. 首次跑就抓到并修掉了一个真 Bug：`note.*` / `run.*` 共 13 个词条只有英文没有
   中文，中文界面下会漏回英文并把对齐顶歪。现在有词条完整性测试盯着。

---

## 12. 自测

测试分两层，各管一段：

| 层 | 工具 | 管什么 | 怎么跑 |
| --- | --- | --- | --- |
| 单元 | **NUnit**（`tests/LiveArchiveEncoder.Tests`） | 纯逻辑：SMB 参数、CLI 解析、配置往返、词条完整性、工具链元数据 | `dotnet test` |
| 等价性回归 | Python harness | 与 v6.3.3 的**产物是否等价**、错误路径、全编码器扫描 | `python tests\*.py` |

### NUnit 单元测试（v7.4.1 起）

```powershell
dotnet test tests\LiveArchiveEncoder.Tests
# 或指定配置
dotnet test tests\LiveArchiveEncoder.Tests -c Release
```

不需要 FFmpeg、不需要网络、几秒跑完，覆盖：

- `SmbTests` — UNC 识别、**`RemoteReadArgs` 不得再吐出 `-stimeout`**（v7.3 的回归）、
  写入侧只用 `-rw_timeout`、可写探测不留痕迹
- `FfmpegCatalogTests` — 四个来源的 id / 默认值 / 谁能自动下载 / 压缩包类型
- `ToolchainTests` — 安装目录布局、**`path` 来源不许解析成受管理构建**、
  未知来源只报错不抛异常
- `PlayerTests` — 播放模式解析与描述，缺文件/缺播放器都是"报告"而不是"崩"
- `CliParserTests` — `--ffmpeg-source` / `--ffmpeg-download`（裸用不吞下一个 flag）/
  `--ffmpeg-archive` / `--play` / `--proxy` 等新选项，以及老选项没被碰坏
- `ConfigStoreTests` — JSON 往返、`"path"` 不被规范化掉、损坏文件回退、
  分片数夹取
- `LocalizationTests` — **中英文词条必须一一对应**（见下）、CJK 显示宽度补齐

> 词条完整性这一条刚建好就抓到了真东西：`note.*` 和 `run.*` 共 13 个词条只有英文，
> 中文界面下会漏回英文并把那一列的对齐顶歪。已补齐。

包：`NUnit` **5.x** + `NUnit3TestAdapter` **6.x**（适配器包名没变，NUnit 4/5 都用它）
+ `Microsoft.NET.Test.Sdk` **18.x**，走 `nuget.config` 里的代理还原。

### 与 Python 基线的等价性回归

所有脚本都支持 `CS_EXE=<路径>` 切换被测 exe（用来验 AOT 产物）。三套产物都跑一遍
才算过——NativeAOT 下反射型 API 会直接崩，JIT 上却完全正常，只验 JIT 会漏。

```powershell
# 12 个编码器/参数矩阵，逐字段对比 Python 版与 C# 版
python tests\compare_matrix.py

# 11 条错误路径，对比退出码
python tests\error_paths.py

# 全编码器功能扫描：当前 FFmpeg 的每个 legacy 编码器跑 2 秒片段（见 §7.9）
python tests\feature_sweep.py

# 选项通路冒烟：turbo / 分片 / 分离音频 / 反交错 / 时间区间 …
python tests\option_smoke.py

# 交互式菜单冒烟：语言切换、turbo/分片开关、配置持久化、损坏配置容错
python tests\cui_smoke.py

# 拿 AOT 产物跑同一套回归
set CS_EXE=D:\...\publish\aot\LiveArchiveEncoder.exe
python tests\compare_matrix.py
```

`compare_matrix` / `error_paths` / `feature_sweep` / `option_smoke` 都需要
`tests\media\test_src.mp4`（20 秒 320x240 h264+aac 素材）和 PATH 里的 ffprobe。

需要注意：`compare_matrix` 依赖实时节流下的帧选择，跑的时候**别同时跑别的重活**，
否则 fresh/repeat 计数会被 CPU 争用扰动（这是自适应控制器的正常反应，不是 bug）。

全部通过会打出 `total=12 pass=12 fail=0` / `total=11 fail=0` /
`total=35 pass=35 warn=0 fail=0` / `total=25 pass=25 fail=0` /
`total=30 pass=30 fail=0`。

> 跑回归之前建议把配置里的 `FfmpegSource` 设成 `"path"`（或删掉受管理构建），
> 这样 C# 版和 Python 版用的是同一个系统 FFmpeg，对比才有意义。
