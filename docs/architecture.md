# LiveArchiveEncoder v7 技术说明

[English](architecture.en.md) · **中文**

版本锚点：**v7.9.9.4**（2026-11-08）。

这份文档写给三类人：要接手维护 v7 的人、要审计它为什么这样设计的人、以及要把它
重写成 v8（Rust）的人。它只描述现状，不提出改动，也不重复用户手册的内容 ——
参数怎么用看 [`usage.md`](usage.md)，版本历史看 [`CHANGELOG.md`](CHANGELOG.md)，
维护流程看 [`MAINTENANCE.md`](MAINTENANCE.md)。

> **v8 的方向已经定了**：一次彻底的、深入的、用 Rust 重写的重构。v7.9 从此进入
> 维护模式，只接受 bugfix / hotfix / 小功能，不再做结构性改动。本文第 8 节把
> “v8 应该保留什么、必须修掉什么、怎么迁”单独写成一节，其余章节描述的都是 v7
> 现状。

规模基线（`src` 排除 `bin`/`obj`）：83 个 C# 文件、约 29 500 行；单元测试 52 个
文件、约 9 600 行；e2e 23 个 Python 文件、约 4 400 行；文档 11 对双语文件。
换句话说，**规范文档的体量与产品代码处在同一数量级**，这是理解这个项目时必须先
接受的事实：它是一个“行为契约 + 测量记录”密度很高的工具，而不是一个只求跑通的
编码器前端。

---

## 1. 它是什么

### 1.1 一句话

把一份现代视频源，按“某年某站点会怎么处理它”的方式，重编码成那个年代的产物；
编码过程本身按**源的时间节奏**实时推进，而不是尽快跑完。

### 1.2 三条不可让渡的语义

1. **实时模拟（realtime pacing）**：解码端按 `1/fps` 的源节奏出帧，输出端每
   `1/fps` 消费一个槽位。总墙钟时间因此与素材时长绑定。这不是性能缺陷，是产品
   语义：自适应帧率控制器要按“节目时间”判断码率是否超限，只有实时推进时，墙钟
   时间才等于节目时间。
2. **默认行为等价**：不带任何新参数时，输出与 Python 参考实现 v6.3.3 一致。
   `reference/live_archive_encoder.py` **逐字保留**，既是法律意义上的出处，也是
   e2e 的对照基线。
3. **失败必须发生在长跑之前**：一次实时编码可能持续几十分钟，因此参数、能力、
   容器、色度、写权限、播放器占用、编码器/复用器组合，全部在第一个字节落盘之前
   验证。这条原则解释了代码里大量的“预检”与“提前拒绝”。

### 1.3 不是什么

* 不是编码器：所有像素处理都在 FFmpeg 子进程里，C#/Rust 一侧从不触碰像素。
* 不是批处理转码器（默认不是）：`--no-realtime` 是 v7.9.9.1 才加入的逃生口，
  它会改变帧选择与码控语义，见 §7.1。
* 不是滤镜/修复工具：文档明确写着 “this tool encodes, it does not damage”，
  故障艺术（glitch）是另一个项目。
* 不是跨平台 GUI：只有 CLI 与终端菜单（CUI）。

### 1.4 版本谱系

| 阶段 | 形态 | 关键事实 |
| --- | --- | --- |
| v6.2.2 → v6.3.3 | 单文件 Python | 只保留了 v6.3.3 的最终源码 |
| v7.0 → v7.5 | C# 逐行为移植 + 内部重写 | 帧池、异步管道、1 ms 定时器、类型化结果 |
| v7.6 → v7.9.6 | 长出产品能力 | 二次压缩、时代预设、字幕、流选择、HTTP 源、像素格式/色彩 |
| v7.9.7 → v7.9.9.4 | 史料修正 + 工程化 | 站点/年份重构、自更新、双语文档、结构化日志、`--no-realtime` |
| v8（计划） | Rust 重写 | 彻底重构；v7 转入维护 |

---

## 2. 进程与数据流

### 2.1 拓扑

本进程是**控制面**，FFmpeg 是**数据面**。单遍编码时是 2 个并发子进程；音频分离
模式先后起 3 个（视频与音频并发，最后再起一个复用进程）；分片模式是 `N × 2` 个
并发子进程，外加拼接与重封装各一次。

```
                         ┌──────────────────── 本进程（控制面） ────────────────────┐
  source.mp4 / URL / UNC │                                                          │
        │                │  ffprobe ──► SourceInfo / 时长 / 流清单                   │
        ▼                │      │                                                    │
 ┌──────────────┐        │      ▼                                                    │
 │ ffmpeg 解码  │ stdout │  能力档案（BuildProfile，磁盘缓存）                        │
 │ -f rawvideo  ├───────►│      │                                                    │
 └──────┬───────┘  raw   │      ▼                                                    │
        │ stderr         │  ArgsBuilder（纯函数）──► decCmd / encCmd                  │
        ▼                │      │                                                    │
  诊断行（环形 64 行）    │      ▼                                                    │
                         │  预检（3 帧 / 0.5 s 真链路）                               │
                         │      │                                                    │
                         │      ▼                                                    │
                         │  捕获线程 ──► FrameBufferPool ──► 槽循环（1/fps）          │
                         │      ▲                 │                  │               │
                         │      │                 └── 最新帧 ────────►│ stdin         │
                         │      │                                    ▼               │
                         │      │                            ┌──────────────┐        │
                         │      │        -progress (stderr)  │ ffmpeg 编码  │        │
                         │      └──── OutputMonitor ◄─────────┤ muxer        │        │
                         │             │                     └──────┬───────┘        │
                         │             ▼                            ▼                │
                         │      自适应 FPS 阶梯              clip.<tag>.part.<ext>    │
                         │             │                            │                │
                         │             └──────► cadence ────────────┘                │
                         │                                    Promote（成功才改名）   │
                         └──────────────────────────────────────────────────────────┘
```

### 2.2 一次单遍编码的时序

1. **解析**：`CliParser.Parse` 得到 `CliOptions`（带 `XxxExplicit` 标记）。
2. **本地动作优先**：`--help`、`--version`、`--cpu-info`、`--emit-args`、
   `--save-config`、字体导入/查询、`--ffmpeg-list`、`--ffmpeg-env`、
   `--ffmpeg-delete`、`--ffmpeg-download`、NNEDI3 权重、`--completion` 等，
   全部在 FFmpeg 发现之前返回 —— 因为“装一个 FFmpeg”这件事本身不能依赖已有
   FFmpeg（`Program.Run` 的顺序就是这条规则的实现）。
3. **发现**：`FFmpegDiscovery` 找到 ffmpeg/ffprobe；失败时如果 stdin 可交互，
   提供一次自动安装（`FirstRun`）。
4. **能力**：`BuildProfileStore.For` 读缓存，未命中才扫描一次（冷缓存要 4 次
   启动，之后 0 次），得到编码器、音频编码器、字幕编码器、滤镜、缩放算法；
   `EncoderCaps` 在首次选中某编码器时懒加载并回写缓存。
5. **解析运行计划**：时代预设 → 编码器（`--encoder` 优先）→ 容器（`--container`
   或输出扩展名反推）→ 分辨率/帧率/阶梯/音频参数 → 像素格式/色彩/抖动 →
   流选择 → 字幕计划 → 硬件解码探测。
6. **预检**：一条与真实运行同形、只跑 3 帧 / 0.5 秒的命令；失败则回退软件解码
   或直接报错。
7. **写前检查**：输出目录可写、目标文件未被占用、清理遗留的 `.part`。
8. **编码**：捕获线程 + 槽循环（§4.1–§4.4）。
9. **落位**：`.part` 非空 → `Promote` 改名；失败则 `Keep`（保留成品并给出路径）。
10. **后处理**：`PostPipeline` 的二次压缩链（可选）→ 报告 → 播放（可选）。

### 2.3 为什么是这个拓扑

* **像素不过托管堆**：C# 只搬运 `byte[]`，不做色彩转换、不做缩放，因此不需要
  SIMD 优化，也不受 GC 暂停影响画质。README 里 “SIMD 已经用到极限” 的结论就是
  这个拓扑的直接推论。
* **控制面必须能观测数据面**：`-progress` 走 stderr，帧走 stdout，两条流分离，
  互不阻塞。
* **子进程是隔离带**：FFmpeg 崩溃、挂死、写满管道，都有明确的回收路径
  （`Kill(entireProcessTree)`、切片等待、写帧 deadline）。

---

## 3. 代码组织

```
src/LiveArchiveEncoder/
  Program.cs          启动阶梯（本地动作 → 工具链 → 发现 → UI/CLI）
  Configuration/      Config / EncoderInfo / EncoderCaps / UserConfig / 常量表
  Core/               时间码、像素格式数学、校验、高精度计时、中断、音频目录、
                      滤镜参数、输出事务、跨模块小工具
  FFmpeg/             发现、工具链安装、能力探测与缓存、ffprobe、进程与管道 I/O
  Frames/             有界预分配帧池
  RateControl/        滚动码率监视器（由 -progress 喂）
  Encoding/           参数构建、预检、实时编码循环、音频路由、分片、流/字幕
  PostProcess/        二次压缩链、时代预设、片段词法校验、字体解析
  Remote/             SMB/UNC 参数、HTTP 源、链路测量
  Update/             自更新（两阶段）、版本比较、更新状态
  Localization/       en/zh 字符串表
  UI/                 手写 CLI 解析、CUI 菜单、选项目录、补全生成、报告、播放
  Diagnostics/        滚动文件日志、路径解析
```

### 3.1 依赖方向

`Program → UI → Encoding → {FFmpeg, Core, Configuration, Frames, RateControl}`
，`Encoding → PostProcess`（只在结果上做后处理），`PostProcess → Configuration`
（读 `Config.Recompress`）。没有环，但 `Configuration` 被所有层直接引用，
`Config` 同时充当 UI 的可变表单与编码层的输入 DTO —— 这是后面 §7.4 要说的结构债。

### 3.2 两个入口，一套内核

CUI（`Cui.Run`）与 CLI（`Cli.Run`）最终都构造同一个 `RealtimeOptions`，进入
`RealtimeEncoder.RunSplit`。差异只在“参数从哪来”：CUI 从 `Config` + 菜单，
CLI 从 `CliOptions` + `UserConfig` 合并。历史上大量 bug 来自这两条路径对同一个
字段的解释不一致（例如时代预设的音频编码器、`--era-native` 被解析后从不读取），
所以 v7.9 引入了 `MergeStoredPreferences` 与 `EraNativeOf` 这类“只此一处”的
收敛点。

---

## 4. 核心机制

### 4.1 实时节拍

两个节拍点，都只在 `RealTime == true` 时生效：

* **捕获线程**：每读满一帧，`nextDeadline += interval`；落后就**重置基准**
  （`nextDeadline = now`），不追赶。这与 Python 的语义一致：追赶会在一瞬间
  灌入多帧，改变自适应控制器的输入。
* **槽循环**：每输出一帧，`nextSlot += slotInterval`，同样“落后即重置”。

计时基元是 `Stopwatch.GetTimestamp()`（单调、无夏令时问题）。Windows 上默认
定时器粒度约 15.6 ms，对 13 fps（76.9 ms/槽）尚可，但对 30 fps（33 ms/槽）不够，
因此 `HighResolutionTimer.Enable()` 调 `timeBeginPeriod(1)`。它是**引用计数**的：
分片模式下每个 shard 都会 Enable，先结束的 shard 不能把粗粒度定时器还给还在跑的
shard。

`WaitUntil` 是 **sleep-based**，不是自旋：剩余 ≥1.5 ms 时 `Sleep(剩余-1)`，
0.5–1.5 ms 时 `Sleep(1)`，<0.5 ms 直接返回（睡过头比少睡更贵）。整个编码期间
CPU 占用因而接近零 —— README 测到的 “98.6% 时间在睡眠” 就是这个设计的结果，
也是分片并行能提速的原因：把睡眠时间叠加成真实工作量。

### 4.2 帧池：显式所有权，零稳态分配

`FrameBufferPool(capacity = buffer_frames, frameSize)` 预分配
`capacity + 2` 个 `byte[]`：

* `+2` 的理由：一帧被捕获线程持有，一帧被槽循环当作 `previous` 持有；
  两者都不能被 `Publish` 驱逐。
* `TryRent` / `Publish` / `TryTakeLatest` / `Release` 四个动作，一把
  `System.Threading.Lock` 保护。
* `Publish` 在满时驱逐最旧的一帧；`TryTakeLatest` 取最新一帧并释放其余全部，
  对应 Python 的 `frame = captured[-1]; captured.clear()`。
* `_borrowed[]` 位图记录“哪些槽真的借出去了”。v7.9.8.6 之前用
  `_freeCount >= _free.Length` 判重复释放，只在池全空时成立 —— 真正危险的那种
  重复释放反而漏过，同一个 buffer 会被发给两个写者，画面静默损坏。
* 释放动作在锁外执行：`System.Threading.Lock` 不可重入，而 `Release` 自己要
  拿锁。`TryTakeLatest` 只由单一线程调用，所以复用的 `_releases` 暂存数组安全。

为什么不用 `ArrayPool<byte>`：租来的数组在归还后可能被下一次租用覆盖，而这里
“上一帧”必须一直有效到下一帧写入完成。所有权必须显式，不能靠租借纪律。

### 4.3 自适应 FPS 阶梯

`EncoderMath.BuildLadder` 把 `--fps` 与 `--fps-levels` 变成 `FpsRung` 列表：

```csharp
readonly record struct FpsRung(double Fps, double DropAboveKbps, double RiseBelowKbps);
```

* 0 号梯级是基准帧率，`DropAbove = ∞`（没有更高的梯级可下降）。
* 阈值可以按 `FPS:drop:rise` 显式给出（v7.7），否则从 `limit` / `recover`
  推导（v7.6 及以前的行为）。
* 时代预设自带该年代的梯级（如 `bilibili/2012` 是 30→25→20→15），但只在用户
  既没写 `--fps` 也没写 `--fps-levels` 时生效。

控制律（槽循环内，每个槽一次）：

| 条件 | 动作 | 迟滞 |
| --- | --- | --- |
| `videoRate ≥ limit` 且目标梯级更高 | 下降一档 | 超限 ≥1.25 倍时 0.18 s，否则 0.30 s |
| 同上但目标更低 | 回升一档 | 0.80 s |
| 当前档 >0 且 `videoRate ≤ rung.RiseBelow` | 回升一档 | 0.75 s |
| 其他 | 清空计时器 | — |

输出节奏由 `cadence` 累积决定：`cadence += effectiveFps / baseFps`，≥1 时输出
新帧并减 1，否则重复上一帧。因此降帧不是“丢弃”，而是**重复**，输出容器的帧率
始终是 `--fps`，播放器看到的是卡顿而非时长变化。`fresh` / `repeated` 计数进入
结果报告。

特例：纯帧内编码器（MJPEG / `-q:v` 类）在 v7.9.8.11.1 之后走
`IntraQuality`：先标定量化器，再把可接受上限抬到实测基线的 1.15 倍，并让阶梯
只在超过上限 3 倍时才介入 —— 因为“丢帧不能让一张 JPEG 变小”，只会让画面抖。

### 4.4 码率观测

FFmpeg 以 `-progress pipe:2` 输出 `key=value` 行。`PipeReaders.ProgressReaderAsync`
只关心三组键：`total_size`、`out_time_us`、`progress`（值为 `end` 表示流结束），
其余行当作诊断进环形缓冲。

`OutputMonitor` 是一个容量 1024 的环形样本表：

* 每次 `AddProgress` 只在样本**进入/离开**窗口时增量更新 `windowBytes` /
  `windowMediaUs`，因此任何一次查询都是 O(1)，不是每次重扫窗口。
* 速率优先用**媒体时间**（`out_time_us` 差值）做分母；没有媒体时间时才退回墙钟。
* `total_size` 回退（复用器重写、文件被截断）时清空整个窗口并重新累积，而不是
  产生一个负增量。
* 诊断行另存 64 行，失败时把尾部拼进异常消息 —— 用户看到的 “encoder exited with
  code 1; …” 里那段 FFmpeg 原文就来自这里。

### 4.5 管道 I/O

* 解码器 stdout 是裸 `rawvideo`；编码器 stdin 是同一份字节。帧按 `frameSize`
  整块读写，不做逐行处理。
* 两路 stderr 必须**持续排空**：不排空，子进程写满管道就会阻塞，整个编码停住。
  因此 stderr 由 `PipeReader` + `SequenceReader<byte>` 异步按行消费（v7.9.9 从
  手写 `byte[]` + `BlockCopy` 换成了 `System.IO.Pipelines`）。
* 单行上限 256 KiB（`MaxLine`）。没有换行符的输出（某些 `\r` 进度流）也要被切成
  块消费，否则 `PipeReader` 会把整个前缀一直缓存在内存里。
* `onLine` 返回 `false` 只表示“别再投递”，**不表示停止读取**：停止读取会让子进程
  的 stderr 管道填满并死锁（v7.9.9.3 修的就是这个）。
* 写帧有 30 s deadline（`WriteFrame`）。同步 `Write` 会在编码器不排空时无限阻塞，
  期间 Ctrl+C 完全无效，`finally` 也到不了，最终留下两个孤儿 FFmpeg。
* 等待子进程退出用 250 ms 切片（`WaitForExitOrCancel`），让取消在子进程仍在运行
  时就被观察到；`Kill(entireProcessTree: true)` 保证不留孙进程。

### 4.6 命令构建与预检

`ArgsBuilder` 是一组**纯函数**：给定编码器、质量、上限、像素格式、色彩，返回
`List<string>`。它是唯一把“质量数字”翻译成 FFmpeg 标志的地方，因此也是三种质量
尺度（`Qv` / `Crf` / `Bitrate`）的交汇点。

顺序是语义的一部分，且被刻意保留成 v6.3.3 的样子：

```
ffmpeg -threads N -f rawvideo -pix_fmt F -s:v WxH -framerate FPS -i pipe:0
       [-i source] [-map ...] -c:v NAME <quality> [-r FPS -fps_mode cfr]
       <audio args> -t DUR -f MUXER -y out.part.ext
```

`-threads` 在**裸视频输入之前**，和 Python 完全一致。FFmpeg 的作用域规则意味着它
可能不会像用户期待的那样作用到输出编码器；这是已知约束（§7），改之前必须先按
编码器基准测试。

预检（`Preflight.Video`）跑的是**同一条命令的缩短版**：同样的滤镜、同样的像素
格式、同样的编码器参数、同样的音频编码器，只把时长压到 0.5 s、视频帧数压到 3。
这样失败的组合在几秒内暴露，而不是在一次实时长跑之后。硬件解码先由
`Hwaccel.Resolve` 真解一帧来验证，预检再失败则自动回退软件并重试一次。

### 4.7 能力探测与缓存

`BuildProfileStore` 的动机是**进程启动数**：v7.9.5 之前，一次 CLI 运行在解码第一
帧之前要启动 6 次 FFmpeg（两次 `-encoders`、`-filters`、`-h filter=scale`、
`-h encoder=NAME`、`-version`），Windows 上每次约 210 ms。

现在：

* 身份 = `路径 | 文件大小 | 最后写入时间` 的 SHA-256 前 16 位。不用版本号，因为
  问版本号本身就要启动一次进程。
* 一次 sweep 拿全：版本行、`-encoders`（视频/音频/字幕）、`-filters`、
  `-h filter=scale`；`EncoderCaps` 按编码器懒加载并回写。
* 内存缓存 + `%LOCALAPPDATA%\LiveArchiveEncoder\cache\caps-<id>.json`；文件坏了
  就当 miss，`LAE_NO_CAPS_CACHE=1` 强制重扫。
* `SchemaVersion` 在结构变化时 bump；旧缓存直接被忽略，而不是“字段缺失当 false”
  —— v7.9.8.8 加 `HasRcMode` 时正是这个陷阱。

### 4.8 分片并行（`--parallel N`）

```
source ──► ShardPlanner（整槽边界，钳住末尾）
             │
             ├─ shard 0 ──► 单遍实时编码 ──► s000.mkv
             ├─ shard 1 ──► 单遍实时编码 ──► s001.mkv
             └─ ...
                                   │
                     -f concat -c copy ──► joined.mkv
                                   │
                     校验（时长 + 视频包数）
                                   │
                     -c copy 重封装 ──► clip.part.<ext> ──► Promote
```

四道硬约束，全部是踩坑换来的：

1. **只有白名单容器可分片**（`avi` / `mp4` / `flv` / `asf` / `mpeg`）。RoQ 会在
   join 时丢掉第二个分片，RM 每次拼接漂移约 0.03 s。
2. **每个分片不短于 4 s**（`MinShardSeconds`）；否则两次进程启动的成本超过收益。
3. **分片一律以 Matroska 暂存**，join 之后再 `-c copy` 到目标容器。直接拼 FLV
   会产生“画面在中途停住、声音继续”的坏文件，而 FLV 不存每流时长，时长校验
   看不见它。
4. **调色板格式禁止分片**：Matroska 的 VfW 兼容头不带调色板，msrle 经一次
   `.mkv` 中转后整幅变成黑色（实测 YAVG 123 → 16）。

join 后做两道校验：容器时长与期望相差不超过 `max(0.5 s, 2%)`；视频包数与各分片
`Slots` 之和相差不超过 `max(4, 2%)`。任一不过就报“该格式不能安全拼接，请去掉
`--parallel`”，而不是交付一个静默损坏的文件。`LAE_KEEP_SHARDS=1` 保留暂存目录，
因为“画面 4/5 处停住”这种报告在分片被删掉之后无法诊断。

### 4.9 音频路由

`inline`（默认）把音频作为编码器的第二路输入，和视频一起复用；
`separate` 先编码纯视频与纯音频，再 `-c copy` 合并；`auto` 等同 separate，但在
复用器拒绝拆分时回退 inline。

拆分前先花 1–2 秒做 `CanSplitMux`：用 `lavfi` 合成 3 帧视频与 0.5 秒正弦波，
真正复用一次，再用 `-f null` 解码验证。原因是一些旧复用器（RoQ、RealMedia）
**返回 0 却丢掉整条视频流**，只看退出码会得到一个“成功”的音频文件。

文档明确写了结论：`separate` **不会更快**。视频段仍受实时节拍约束，音频段与
remux 是纯增成本；额外进程还会与捕获线程抢 CPU，改变自适应控制器的选帧结果。
它存在的意义是“A/V 对齐的可验证性”和某些容器必须分开写的场景。

### 4.10 输出事务

```
clip.mp4                     目标名（可能被播放器占用）
clip.<pid><rand>.part.mp4    真正写入的文件
clip.mp4                     成功后 rename 落位
clip (2).mp4                 目标被占用时的替补名
clip.<pid><rand>.part.mp4    失败/取消：删除；若已完成但改名失败：保留
```

* `.part` 名带进程号与随机段：固定 `clip.part.mp4` 可能**就是**用户手工恢复的
  成品，且在 Windows 上大小写不同的两个输出会共用一个 part 名。
* 写入前 `EnsureTargetAvailable`：以 `FileShare.None` 试开目标，失败则关闭“本程序
  为这个文件启动的播放器”（`ChildProcesses.KillAll(path)`，不是全杀），再试一次；
  仍失败就报“输出被占用”，而不是跑完一小时才发现。
* 同时清理**上一进程**留下的、未被锁住的 `.part`。用创建时间与进程启动时间比较，
  避免删掉并发编码正在写的文件（POSIX 上删除打开中的文件会成功，对方的输出会
  静默消失）。
* `Promote` 最多试 4 次（第 1–3 次失败后各等 250/500/750 ms），仍不行就落到第一个
  空闲的 `name (2).ext`。
  v7.9.8.13.2 曾把 Windows 的共享冲突误判为 ACL 拒绝而直接抛出，结果丢掉整段
  成品；现在两者都按可重试处理，真正的 ACL 问题由前置检查拦下。
* 所有下游（报告、`_web` 链、播放器）都跟随 `EncodeResult.PlacedPath`，而不是
  用户最初给的名字。

### 4.11 二次压缩链（`--recompress*`）

一条链是 N 个 FFmpeg 进程，第 N 跳读第 N−1 跳的产物：

```
clip_live.mp4 ──► clip_live_web.mp4 ──► clip_live_web2.flv ──► clip_live_web3.rm
```

* 命令两端由程序掌握：`ffmpeg -y -i <输入> <用户片段> <输出>`。用户只能写“中间”，
  `-i` 与输出路径不可覆盖。
* `PostArgs.Parse` 先按空白切分（尊重单双引号，反斜杠**不做**转义，否则 Windows
  路径会被吃掉），再逐 token 校验：空片段、`-i`、`-o`，以及**任何位置**上看起来
  像输出文件名的裸 token 都会被拒绝。不做 shell 转发，因此不会出现第二层解释。
  `Recompress.BuildArgv` 单独存在，所以“命令长什么样”可以在不启动 FFmpeg 的
  情况下断言。
* 失败策略：**永不碰实时产物**。失败的跳删除自己的 `.part` 并报告 `recompress 2/3`，
  前面的跳保留（它们都是过程的真实产物）。
* 媒体站点时代预设（§4.12）本质上就是“替用户写好这段片段”，所以二者的校验
  共用一套。

### 4.12 时代预设

内置 32 行：5 条 `generic/<年份>`（1998/2003/2007/2010/2014）加 13 个站点的
27 条 `site/year`（cctv、sina、sohu、netease、qq、tudou、youku、ku6、56、acfun、
bilibili、iqiyi、youtube）。

每一行同时描述两层：

* **live 层**：编码器、分辨率、帧率、梯级、码率上限、`q:v` 与 `CRF` 两个尺度、
  平均窗口、帧队列深度、去隔行器、音频编码器/码率/采样率/声道；
* **second 层**：站点二次转码的分辨率、帧率、码率、profile、音频参数、容器，
  以及 `H264Impl`（2010 年及以后默认 OpenH264，更早的行用 libx264）。

设计要点：

* **双尺度质量**。MPEG 的 `q:v 9` 与 x264 的 `crf 9` 不是一回事（实测同一画面
  375 kbps vs 935 kbps），所以每行都同时存 `LiveQv` 与 `LiveCrf`，切换编码器时
  不需要二次换算。换算公式 `crf = 7.5 + 1.17 × q:v` 只用于“用户手动改编码器”
  这种没有标定数据的情况。
* **预设是兜底，不是覆盖**：`--fps`、`--limit`、`--qv`、`--window`、
  `--deinterlace` 等显式参数永远优先。这条规则在 v7.9.3.1 之前被写反过
  （`era?.Fps ?? args.Fps`，而 `era.Fps` 非空，于是预设永远赢）。
* **数据可覆盖**：用户预设存在 `config.json` 的 `CustomEras`，同 id 覆盖内置行；
  旧 id（`cctv03`、`youku07`、`dialup98`…）通过 `Aliases` 解析并打印一行改名提示。
* **史料分级**：`docs/era-presets.md` 给每行标注证据等级（A/B/C）与出处；
  C 级是“落在该年代包络内的估计”，不是实测。

### 4.13 配置与状态

```
%LOCALAPPDATA%\LiveArchiveEncoder\        ~/.local/share/LiveArchiveEncoder/
  config.json                              交互式设置的快照（UserConfig v6）
  configs/                                 命名配置档（可整体保存/加载）
  cache/caps-<id>.json                     能力档案
  ffmpeg/<id>/bin/                         托管 FFmpeg
  fonts/                                   字幕字体（用户提供/导入）
  output/                                  网络源自动输出目录
  nnedi/                                   NNEDI3 权重与元数据
  logs/                                    滚动日志（LAE_LOG_DIR 可改）
```

* `UserConfig.Version` **只写不读**：所有字段都是可选的，旧文件缺字段就等于旧
  行为。这靠 `Normalise()` 里的补丁维持（例如 `Crop43: bool` → `FitMode: string`）。
* `ConfigStore` 写入是原子的（临时文件 + 替换），一次崩溃不会留下截断的配置把
  所有设置重置。
* CLI 运行**只改它被明确告知的字段**，其余原样写回（v7.9.8.6）。在此之前，一次
  `--encoder mpeg4` 会静默重置二次压缩链、字幕块、GitHub 前端、NNEDI3 权重路径、
  自动更新开关、工具链来源与播放器。
* “没有提到”和“明确设为空”是两回事：`GhProxy` 用 `null` / `""` 区分
  “未决定（可探测）”与“明确关闭（不要再问）”。
* 可重定向的三个根：`LAE_CONFIG_DIR`（`config.json`、`configs/`、`cache/`、
  `output/`）、`LAE_LOG_DIR`（日志）、`LAE_FONT_DIR`（字体；旧名
  `LAE_WATERMARK_FONT_DIR` 仍然识别）。托管 FFmpeg（`ffmpeg/`）与 NNEDI3 权重
  （`nnedi/`）固定在真实的数据根下，**不**随 `LAE_CONFIG_DIR` 移动 —— 这是有意的：
  换一个配置目录不应该让已安装的组件“消失”。e2e 通过 `LAE_CONFIG_DIR` +
  `LAE_FONT_DIR` 隔离自己写的那部分，绝不写开发者真实的 `config.json`。

### 4.14 CLI / CUI / 本地化

* **手写解析器**，不引入 `System.CommandLine`：为了与 argparse 的帮助文本、错误
  文本、退出码逐字对齐。代价是 1561 行的 `CliOptions` 与 1309 行的 `Cli`。
* **`OptionCatalog` 是补全的唯一事实源**（209 行）。v7.9.8.5 曾出现解析器认识
  112 种拼写、生成的补全脚本只认识 69 种的情况，于是现在有两个方向的测试：
  解析器源码里出现的选项必须在表里，表里的选项必须被解析器接受。
* **CUI**（3641 行）是纯终端菜单：主菜单 → 参数/编码器/音频/流/字幕/像素格式/
  二次压缩/时代预设/并行/播放/更新/语言/配置档。它写 `config.json`，CLI 读它，
  两条路径因此共享同一份“上次设置”。
* **本地化**：`L` 是两张 `Dictionary<string,string>`，键集合必须完全一致（有单测），
  占位符数量也必须一致。缺失键渲染为键名本身，让拼写错误大声失败而不是静默。
  FFmpeg 自己的诊断**故意不翻译**，否则无法搜索原文。
* 663 处 `Console.*` 是唯一的用户可见输出；日志不镜像到控制台（除了
  `--self-update-apply` 这个必须保持安静的进程）。

### 4.15 工具链与网络

* `FfmpegCatalog` 枚举 6 个来源（BtbN、nonfree、ytdlp、autobuild、gyan、avbuild），
  按平台过滤（gyan 与 autobuild 只有 Windows 构建），每个来源知道自己是否需要
  手动下载、用什么压缩格式、装到哪个目录。
* 下载 → 校验（能拿到摘要时）→ 解压（7-Zip 或 `tar.xz`）→ 写 `build.json`
  → 把 `bin` 发布到当前 shell 的 `PATH`（`ToolchainPath.Publish`）。
  `--ffmpeg-env bash` 打印一行 `export PATH=…`，给“已经开着的终端”用。
* `--gh-proxy` 用于 GitHub 直连不可达的环境（实测 raw.githubusercontent.com
  20 秒 0 字节，同 URL 经前端 206 + 正确 `Content-Range`）。三态：未设置 = 探测
  5 秒后决定；设置 = 使用；`--no-gh-proxy` = 记住“不再探测”。
* `NetworkProbe` 读源本身来测吞吐/抖动/首字节延迟，判断能否分片；`--iperf3`
  只作为参考，因为 iperf3 看不到 NAS 的磁盘与单连接限制。
* `Smb` 负责 UNC 参数（`-rw_timeout`、读写差异、不可达提示）；`HttpSource`
  负责 `-reconnect` 家族与 30 秒 socket 超时，并明确不支持认证/Cookie/HLS。

### 4.16 自更新

两阶段，因为 Windows 不允许替换正在映射为映像的文件：

1. 旧进程：检查 feed → 选本机形态的资产 → 下载 → 校验 `sha256:` →
   解包到 `<程序目录>/.update-<tag>/` → 运行新二进制的 `--version` 证明它可执行
   → 启动它并带 `--self-update-apply` → 退出。
2. 新进程（从 staging 运行）：等父进程退出 → 复制自身到安装目录 → 可选重启。

规则：只接受 https（loopback http 供本地测试）、永不降级、永不触碰 FFmpeg、
只接受与 `AssetSuffix()` 完全匹配的资产名（`-win-x64.zip` /
`-win-x64-self-contained.zip` / `-win-x64-aot.zip` 三档）。没有代码签名，没有
自动回滚，也没有保留 `.old` 备份 —— 这是明确的设计取舍（见 §7.14）。

### 4.17 诊断

* Serilog 滚动文件，代码配置（无 XML/JSON），因此能在 Release 的裁剪开关下工作，
  也对 NativeAOT 友好。
* 一次运行一份关联日志：`process.exit`、`ffmpeg.discovered`、`output.promoted`
  等事件带结构化字段；`LAE_LOG_LEVEL` 控制级别，`LAE_LOG_MIRROR` 决定是否镜像
  到控制台。
* `ffmpeg.stderr` 采用**采样**写入（`AppLog.Sampled`），避免每小时几十万行把
  日志盘写满。
* 用户可见文本仍然全部走 `Console`，日志是补充而不是替代 —— e2e 断言的是
  `Console` 的原文。

### 4.18 跨平台与发布形态

| 维度 | Windows | Linux |
| --- | --- | --- |
| 可执行文件 | `LiveArchiveEncoder.exe` | `LiveArchiveEncoder`（无扩展名） |
| 数据目录 | `%LOCALAPPDATA%\LiveArchiveEncoder` | `~/.local/share/LiveArchiveEncoder` |
| 网络路径 | UNC `\\host\share` | CIFS 挂载点 |
| 播放 | `ffplay` / 文件关联 | `ffplay` / `xdg-open` |
| 自更新 | 同一套 | 同一套 |
| NativeAOT | 需要 MSVC + Windows SDK | 实验性（glibc 绑定） |

三种产物形态：

1. **framework-dependent JIT**：`net10.0` + `RollForward=Major`，因此同一个包在
   只装 .NET 11 的机器上也能启动；CI 用 `-p:LaeTfm=net11.0` 真编译一遍验证。
2. **self-contained JIT**：自带运行时，无 .NET 依赖，但保留 JIT。
3. **NativeAOT**：单文件、启动最快、无运行时；编码速度不变（编码在 FFmpeg 里）。

发布时按 `MAINTENANCE.md` 出四个 JIT 包（Win/Linux × framework-dependent /
self-contained），每个都带 SHA-256；AOT 作为可选形态保留代码路径。自更新通过
`IsSelfContained()`（找 `coreclr.dll` / `libcoreclr.so`）与
`RuntimeFeature.IsDynamicCodeSupported` 判定自己该拿哪一种。

---

## 5. 质量保障

### 5.1 单元测试（TUnit，52 文件 / 9.6k 行）

只测“不需要真实编码”的纯逻辑：

* SMB 参数、FFmpeg 目录表、工具链布局、播放器参数、CLI 解析、配置往返、
  硬件解码归一化、本地化键集合；
* 二次压缩片段词法与其拒绝规则、时代预设一致性、设置 ↔ 命令行双向转换；
* 选项表与解析器的双向校验、补全生成；
* 在 NativeAOT 敏感的 API 上运行，因此“只能在反射下工作”的代码会在单测阶段
  暴露，而不是在发布产物里崩。

`InternalsVisibleTo` 只开给测试工程，说明作者清楚哪些是实现细节。

### 5.2 e2e（pytest，23 文件 / 4.4k 行）

| 模块 | 用例 | 检查什么 |
| --- | --- | --- |
| `test_compare_matrix.py` | 12 | 编码器/参数矩阵，ffprobe 结构与 v6.3.3 对齐 |
| `test_error_paths.py` | 11 | 失败路径与退出码 |
| `test_feature_sweep.py` | 35 | 每个 legacy 编码器，2 秒素材 |
| `test_option_smoke.py` | 63 | 选项管道，含二次压缩与时代预设 |
| `test_cui_smoke.py` | 12 | 交互菜单断言（40+ 检查） |
| `test_stream_selection.py` | 9 | 绝对流索引 + 时间裁剪 |
| `test_subtitles.py` | 7 | 烧录/复制/关闭、外挂与内嵌、坏请求 |
| `test_http_source.py` | 7 | HTTP(S) 输入、超时与失败 |
| `test_config_conversion.py` | 4 | 设置文件 ↔ 命令行往返 |
| `test_ab_duration.py` | 2 | 时长中位数对比 |

分两阶段跑：`timing` 与 `shared_config` 两组**串行独占**（前者对墙钟敏感，后者
会写真实设置文件），其余用 `pytest -n`。`conftest.py` 里的护栏拒绝“`-n` 与排他组
同时出现”，因为那会静默把计时断言变成 flaky。全量在 32 核机器上约 492 秒
（串行 794 秒）。

### 5.3 仓库门禁

* `tools/check-slnx.py`：`.slnx` 清单与工作树双向一致；`docs/*.md` 与
  `docs/*.en.md` 必须成对；根 `README.md` ↔ `README.zh.md` 必须成对。
* GitHub Actions：Windows + Linux 双平台 build + 单测 + 清单校验 + net11 目标
  再 build/test 一遍。
* Codeberg 侧有 Woodpecker（Linux）与 Forgejo Actions（含 framework-dependent
  的 win/linux 发布）。
* e2e **故意不进 CI**：需要带 legacy 编码器的 FFmpeg、安静机器、真实时钟。

### 5.4 文档

11 对双语文档（其中两对是 CHANGELOG）+ 归档说明。语言约定是硬规则：`X.md` 中文、
`X.en.md` 英文，相对链接不得跨语言，历史型文档以中文为唯一权威全文。文档不是
“附带说明”，而是行为契约的一部分 —— 很多注释直接引用 `docs/usage.md §7.17`
这样的章节号。

---

## 6. 突出点

1. **移植方法论可复现**。参考实现逐字保留，e2e 直接与 v6.3.3 对比，“无参数时
   字节一致”是可执行的断言，而不是口头承诺。这让后来所有新功能都能用
   “默认关闭、显式开启”的方式安全叠加。
2. **数据面/控制面边界干净**。C# 不碰像素，因此优化目标极其明确：分配次数、
   管道吞吐、计时精度；也让“SIMD 已到极限”这类结论可以被验证而不是猜测。
3. **帧路径零稳态分配**。预分配 buffer + 借用位图 + 最新帧语义，`+2` 槽的设计
   把“捕获中的帧”和“上一帧”都排除在驱逐之外，不会有帧被写者覆盖。
4. **计时工程到位**。`timeBeginPeriod(1)` 引用计数、sleep-based 等待、落后即
   重置基准而不追赶，这三条保证了长跑的时间轴稳定，也保证了 CPU 几乎不空转。
5. **观测直接驱动控制**。`-progress` 流式解析 + 环形窗口 O(1) 聚合 + 媒体时间
   优先的码率，控制律的输入是真实字节数与真实节目时间，不是估算。
6. **“失败要趁早”贯穿全链路**。参数、能力、扩展名、调色板、容器、写权限、
   播放器占用、真链路预检、音频可拆分性、NNEDI3 权重，全部前置。
7. **能力探测缓存**。6 次启动 → 首次 4 次 → 之后 0 次；身份用 size+mtime 而不是
   版本号，避免“为了省启动而启动”。
8. **分片宁可不用，也不交付坏文件**。白名单 + 最短分片 + Matroska 暂存 +
   时长/包数双校验 + 失败回退；`LAE_KEEP_SHARDS` 保留现场。
9. **产物事务性**。`.part` + `Promote` + `Keep/Discard` + `(2)` 兜底 + 启动前锁
   检测，三重保证：不覆盖源、不丢成品、不把半成品当成品。
10. **安全边界具体**。二次压缩片段词法化并拒绝路径、URL 文件名消毒、扩展名纠正
    不覆盖既有文件、自更新只接受 https。
11. **单一事实源是持续工程**。选项目录 ↔ 解析器双向测试、补全由表生成、
    en/zh 键集合测试、`LaeTfm` 双 TFM CI、`.slnx` 与双语门禁。
12. **可运维性**。结构化滚动日志 + 运行关联 ID、`--emit-args` / `--save-config`
    往返、`--net-test`、`--cpu-info`、`--ffmpeg-env`、`--update-check` 的
    0/3/2 退出码。
13. **诚实的技术写作**。几乎每个不显然的常量都带测量日期、对照组与结论，甚至
    记录“曾经写反过”。这让后来者能判断某个数字是实测还是估计 —— 大多数项目
    做不到这一点。
14. **发布工程完整**。四个包 + SHA-256 + 自更新资产三档匹配 + 三套 CI + 双语
    文档门禁，工具链本身是可交付的。

---

## 7. 不足与风险

按性质分四类：设计取舍（不是 bug，但会限制用途）、结构债、工程债、运维风险。

### 7.1 设计取舍：墙钟绑定

默认模式下总耗时 = 素材时长。这对“复原当年观感”是必要的，对“批量转码一个
档案库”是硬伤。`--no-realtime`（v7.9.9.1）是逃生口，但它**改变了语义**：码率
窗口不再对应节目秒，于是调用方把阶梯钉死为一档；帧选择也从“按时间取最新帧”
变成“一帧一槽”。因此“快”和“像当年”不可兼得，而项目没有提供“两种模式的
可复现对照基准”。

### 7.2 结构债：巨型文件

| 文件 | 行数 | 问题 |
| --- | --- | --- |
| `UI/Cui.cs` | 3641 | 菜单流程、参数编辑、报告、更新、播放混在一起 |
| `Localization/Strings.cs` | 1634 | 两张全量字符串表 |
| `Encoding/RealtimeEncoder.cs` | 1562 | 准备、预检、建造、执行、拆分、校验全在一个类 |
| `UI/CliOptions.cs` | 1561 | 字段 + Explicit 标记 + 解析状态 |
| `UI/Cli.cs` | 1309 | 参数归一化、预设合并、报告、后处理编排 |
| `Update/SelfUpdate.cs` | 1171 | HTTP、解包、校验、进程替换 |

单文件超过 1000 行之后，改动的影响面只能靠人记忆。`RealtimeEncoder.Execute`
尤其典型：它同时管理两个进程、两条 stderr、一个捕获线程、一个槽循环、一个
阶梯状态机与一套取消路径。

### 7.3 结构债：全局可变状态

`EraPresets.CustomRows`、`BuildProfileStore.Memory`、`L` 的当前语言、
`ConfigStore` 的直接读盘、`AppLog` 的单例、`HighResolutionTimer` 的静态计数 ——
这些让“同一进程内跑两份不同配置”变成不可能，也让单测必须串行或依赖目录隔离。
对一个 CLI 工具尚可接受，对库化/服务化是硬阻碍。

### 7.4 结构债：四套相似模型

`CliOptions`（含 `XxxExplicit`）→ `Config`（UI 表单）→ `RealtimeOptions`
（编码输入）→ `UserConfig`（持久化），字段高度重叠，靠手写映射与 `with` 传递。
`with` 解决了“新增字段忘记复制”，但没有解决“优先级规则散落”：

```csharp
double fps = args.FpsExplicit ? args.Fps : era?.Fps ?? args.Fps;
int    w   = args.WidthExplicit ? args.Width : eraSize?.W ?? era?.Width ?? args.Width;
```

这类表达式出现几十次，每一处都是“CLI 与 CUI 不一致”bug 的潜在来源 ——
历史上确实反复发生过（`--era-native` 被解析后从不读取、预设音频编码器在菜单里
生效但 CLI 里被忽略）。

### 7.5 工程债：异常即控制流

`ArgumentException` = 用法错误（退出 2）、`InvalidOperationException` =
FFmpeg 失败 / 配置冲突 / 子进程异常退出 / 内部不一致，全部混在一起；退出码只有
0 / 1 / 2 / 3 / 130 几档。脚本无法区分“源不可达”“编码器不支持”“磁盘满”。
错误消息虽然具体，但不可编程消费。

### 7.6 工程债：没有端口抽象

`Execute` 直接 `Process.Start`，`Probe` 直接启动 ffprobe。没有 `IFrameSource` /
`IEncoderSink` / `IClock` / `IProcessRunner`，因此编码主循环**无法单测**，只能靠
真实 e2e。这直接导致测试金字塔倒置：e2e 4.4k 行、13 分钟、计时敏感、不能进 CI。

### 7.7 工程债：启发性常量散落

0.18 / 0.30 / 0.75 / 0.80 / 1.10 / 1.25 / 3.0（控制律）、30 s（写帧超时）、
4 s（最短分片）、256 KiB（行上限）、1024（样本容量）、64（诊断行）…… 分散在
多个文件里，没有集中参数表；运行时只在 verbose 模式打印一行“方向”，没有记录
“为什么在这一槽做这个决定”。调参只能靠读代码 + 猜。

### 7.8 工程债：只有日志，没有指标

没有结构化事件流（槽、阶梯变化、丢帧、探测耗时、子进程启动数），没有
metrics/tracing。长期回归只能靠 e2e 的通过/失败，无法回答“这次改动让平均阶梯
位置下降了多少”。

### 7.9 工程债：本地化与输出形态

`Dictionary<string,string>` + `{0}` 占位，无法处理复数、终端宽度、颜色、机器
可读输出。全部 663 处 `Console.*` 直写意味着：没有 `--json`，脚本只能解析人类
文本或依赖退出码。

### 7.10 平台偏向

Windows 是主平台：`winmm` P/Invoke（有平台判断，但没有抽象）、SMB/UNC 语义、
`File.Move` 共享冲突处理、AOT 链接 MSVC/SDK。Linux 的 AOT 被明确标注为实验性
（glibc 版本绑定），计时精度在 Linux 上依赖 `Sleep` 的实际粒度，没有单独验证。

### 7.11 对 FFmpeg 行为的依赖是“注释级契约”

大量结论（`-threads` 的作用域、MJPEG 的全范围要求、mpeg1video 的可用帧率、
Matroska 丢失调色板、FLV 拼接损坏）都以注释 + 测量日期记录，但它们**不会自动
回归**。FFmpeg 升级后只能靠 e2e 或用户报告发现问题。

### 7.12 配置无显式迁移器

`UserConfig.Version` 只写不读；旧文件靠“缺省值 = 旧行为”兼容。六个 schema 版本
下来这仍然奏效，但字段语义变化（`Crop43` → `FitMode`）只能靠 `Normalise()` 里的
补丁。字段越多，补丁越难证明完备。

### 7.13 并行是进程级暴力并行

`Parallel.For` 一次拉起 N 个 shard，每个 shard 两个 FFmpeg + 一个捕获线程；
没有 CPU/IO 配额、没有背压、没有“机器正在跑别的任务”的降级。实时节拍因此对
系统负载敏感（文档也承认计时用例必须在安静机器上跑）。

### 7.14 自更新的信任模型与回滚

只校验 release JSON 里的 `sha256`（信任 GitHub、TLS 与 feed 本身），没有代码
签名；替换失败时旧二进制仍在，但没有自动回滚，也没有 `.old` 备份；`--self-update-apply`
依赖平台的文件锁语义。对一个 GPL 小工具是合理取舍，但对“企业内分发”不够。

### 7.15 文档体量 ≈ 代码体量

11 对双语文档（其中两对是 CHANGELOG），规范与实现同步完全靠人。`check-slnx.py` 只校验
“文件存在 / 成对”，不校验内容是否过期。文档越详细，过期条目越难被发现。

### 7.16 注释考古化

大量注释以 `v7.9.8.13.2:` 开头讲当年的 bug。优点是决策可追溯，缺点是读新代码
必须先理解旧 bug；`archive/retired/` 保留退役代码同样增加搜索噪音。

---

## 8. 面向 v8（Rust）的迁移设计

v8 是彻底重写，因此这一节不讨论“怎么在 v7 上打补丁”，只讨论“哪些东西必须
被继承、哪些必须被重新设计”。

### 8.1 必须继承的契约（不可在重写中丢失）

1. **无参数时的输出等价**。以 v7.9.9.4 为黄金基准，逐字节对齐 FFmpeg argv
   （包括参数顺序、浮点格式 `G6`、`-threads` 的位置）。
2. **实时语义**：捕获端 `1/fps`、输出端 `1/fps`、落后重置不追赶、降帧用重复
   而非丢帧、`fresh/repeated/slots` 三个计数。
3. **自适应阶梯的阈值与迟滞**（0.18/0.30/0.75/0.80/1.25/1.10/3.0），以及
   “预设是兜底不是覆盖”的优先级。
4. **失败前置清单**：能力、扩展名、调色板/容器冲突、可写性、目标占用、真链路
   预检、音频可拆分性、NNEDI3 权重。
5. **产物事务**：`.part` 命名规则、`Promote`/`Keep`/`Discard`、`(2)` 兜底、
   跟随 `PlacedPath`。
6. **退出码与文本契约**：0/1/2/3/130 的含义，`ERROR:` / `WARNING:` 前缀，
   以及 e2e 断言的原文。
7. **配置兼容**：`config.json` v6 必须能直接读；未知字段忽略；写入只改被告知
   的字段；“未设置”与“设为空”的三态语义。

### 8.2 建议的 crate 划分

```
lae-cli         clap 定义、子命令、补全；由同一份定义生成 help/completion
lae-core        domain 类型、时间码、质量尺度、校验、Plan（唯一的运行计划）
lae-ffmpeg      spawn、管道、-progress 解析、能力探测与缓存、ffprobe
lae-pipeline    实时/非实时两种 Mode、帧池、阶梯、分片、输出事务
lae-post        二次压缩链、时代预设、字幕/字体
lae-net         HTTP/SMB、工具链下载、镜像探测、自更新
lae-config      serde schema + 显式 migrate 链 + 配置档
lae-i18n        字符串表 + 终端渲染（宽度/颜色/复数）
```

关键是把 v7 的“四套模型”合并成**一个 `Plan`**：解析阶段只产出 `Plan`，编码阶段
只读 `Plan`，CLI/CUI/未来 GUI 都是 `Plan` 的构造器。优先级规则集中在
`Plan::resolve` 一个函数里，而不是散落在几十个 `?:` 表达式中。

### 8.3 机制映射

| v7（C#） | v8（Rust）建议 | 注意 |
| --- | --- | --- |
| `Process` + 重定向流 | `tokio::process::Command` + `AsyncRead` | Windows 上仍需 `CREATE_NO_WINDOW` 等价物 |
| `PipeReader` + `SequenceReader` | `tokio_util::codec::LinesCodec` 或 `bytes::BytesMut` 手工切行 | 保留 256 KiB 行上限与“停止投递但继续排空”的语义 |
| `FrameBufferPool` | `Vec<Box<[u8]>>` + 借用位图 + `parking_lot::Mutex`，或 `rtrb` 环 | 保持 `+2` 槽与“最新帧优先”语义 |
| `Stopwatch` + `HighResolutionTimer` | `std::time::Instant` + `tokio::time::sleep_until`；Windows 用 `CreateWaitableTimerEx` 高精度定时器，Linux 用 `timerfd`/`clock_nanosleep` | 不要用 `thread::sleep` 承担 33 ms 级节拍 |
| `OutputMonitor` | 环形缓冲 + 增量聚合，同上 | 媒体时间优先于墙钟 |
| `BuildProfileStore`（JSON） | `serde` + 同一套 identity（size+mtime 哈希） | 加 schema_version 与损坏即 miss |
| 手写 CLI 解析 | `clap` derive + 自定义 `value_parser` | 用 `try_parse` 复刻 argparse 的退出码 2 与错误文本风格 |
| 手写补全生成 | `clap_complete` | 表与解析器天然同源，消灭 69/112 那类漂移 |
| `L` 字典 | `rust-i18n` / `fluent` + `anstream` | 终端宽度与颜色可以顺便解决 |
| `Serilog` | `tracing` + `tracing-appender` | span 按 run/slot 分层，天然带关联 |
| 异常控制流 | `thiserror` 枚举 + `ExitCode` 映射 | 每类错误一个 variant，脚本可区分 |
| `Process.Start` 直接调用 | `trait ProcessRunner` + `FakeRunner` | 编码主循环第一次变得可单测 |

### 8.4 v8 必须顺手修掉的 v7 缺陷

1. **两种 Mode 并列**：`Realtime` 与 `Batch` 都做一等公民，各自有独立的码控
   语义与验收基准；不要让 `--no-realtime` 继续当“钉死阶梯”的补丁。
2. **单一 Plan**：消灭四套模型与 `XxxExplicit` 标志的散落优先级。
3. **错误分类**：至少区分 `Usage` / `SourceUnavailable` / `EncoderUnsupported` /
   `OutputBusy` / `Io` / `Internal`，并映射到稳定退出码。
4. **并发配额**：分片与音频并行共享一个 `Semaphore`，并按 CPU/IO 预算限制；
   实时模式下对系统负载做检测与降级。
5. **可观测性**：每个槽一个 `tracing` 事件（可选），阶梯变化、丢帧、探测耗时
   都有结构化字段；为长期回归提供数据集而不只是日志。
6. **显式配置迁移**：`migrate_v5_to_v6()` 这样的函数链 + 黄金文件测试，取代
   “缺省即旧行为”。
7. **机器可读输出**：`--json` 事件流（NDJSON），脚本不必解析人类文本。
8. **真正的跨平台计时层**：Windows / Linux 各自的定时器实现放在一个 trait 后面，
   有基准测试证明精度。

### 8.5 迁移路线（建议）

1. **黄金基准先行**：用 v7.9.9.4 录制一套 argv 快照 + 产物指纹（容器结构、
   时长、包数、关键帧数、像素格式、码率区间），覆盖全部编码器 × 预设 × 选项
   组合。没有这套基准，重写无法验收。
2. **先写 `lae-core` + `lae-ffmpeg`**：Plan、能力探测、argv 构建。用快照测试
   对齐到字节级。
3. **再写 `lae-pipeline` 的 Batch 模式**（无实时节拍，最容易测），跑通全部
   e2e；然后把 Realtime 节拍作为独立策略加进来。
4. **平移后处理与预设**：它们本质是数据 + argv，风险最低，可以先用数据文件
   驱动，再逐步代码化。
5. **最后迁 UI 与自更新**：CLI 用 `clap`，CUI 用 `dialoguer`/`inquire`（或先
   只保留 CLI）。
6. **并行运行期**：v7 与 v8 在 CI 中跑同一套 e2e，差异必须解释，不能“先合并
   再说”。

### 8.6 风险清单

* **argv 顺序与浮点格式**：FFmpeg 对参数位置敏感，`G6` 与 Rust 默认的浮点
  格式化不同（`13` vs `13.000000`），必须显式复刻。
* **Windows 文件锁语义**：`.part`/`Promote`/`(2)` 的整套逻辑依赖“共享冲突”与
  “ACL 拒绝”都以 `ERROR_ACCESS_DENIED`/`ERROR_SHARING_VIOLATION` 出现，Rust 侧
  需要按 `io::ErrorKind` 重新判定。
* **UNC 与长路径**：`\\?\` 前缀、SMB 超时、`rw_timeout` 的行为要逐项验证。
* **AOT/glibc**：Rust 静态链接 musl 与 FFmpeg 动态链接的交互需要单独设计发布
  形态，别把 v7 的“三形态”直接照搬。
* **自更新替换**：Windows 仍然不能替换运行中的映像；两阶段方案要保留，但可以
  用 `self-replace` 之类的 crate 降低复杂度。
* **文档与契约**：v8 必须同时继承 §8.1 的契约清单，否则“重写”会变成“另一个
  工具”，e2e 也就失去意义。

---

## 9. 附录

### 9.1 关键文件索引

| 想理解什么 | 从哪里读 |
| --- | --- |
| 启动顺序与分派 | `src/LiveArchiveEncoder/Program.cs` |
| 一次编码的全过程 | `Encoding/RealtimeEncoder.cs`（`RunSingle` → `Execute`） |
| 帧的所有权 | `Frames/FrameBufferPool.cs` |
| 码率观测 | `RateControl/OutputMonitor.cs`、`FFmpeg/PipeReaders.cs` |
| 参数拼接 | `Encoding/ArgsBuilder.cs`、`Encoding/Preflight.cs` |
| 能力探测与缓存 | `FFmpeg/BuildProfileStore.cs` |
| 分片与拼接 | `Encoding/ParallelEncoder.cs`、`Encoding/ShardPlanner.cs` |
| 产物落位 | `Core/OutputFile.cs` |
| 二次压缩 | `PostProcess/Recompress.cs`、`PostProcess/PostArgs.cs` |
| 时代预设 | `PostProcess/EraPreset.cs`、`docs/era-presets.md` |
| 配置读写 | `Configuration/UserConfig.cs` |
| CLI 与补全 | `UI/CliOptions.cs`、`UI/OptionCatalog.cs`、`UI/Completion.cs` |
| 自更新 | `Update/SelfUpdate.cs` |
| 日志 | `Diagnostics/AppLog.cs`、`Diagnostics/LaePaths.cs` |

### 9.2 关键常量

| 常量 | 值 | 位置 | 含义 |
| --- | --- | --- | --- |
| `WriteDeadlineSeconds` | 30 | `RealtimeEncoder` | 一帧写不进编码器的上限 |
| `MinShardSeconds` | 4.0 | `Constants` | 短于此不值得分片 |
| `ConcatSafeFormats` | avi/mp4/flv/asf/mpeg | `Constants` | 可分片的容器白名单 |
| `SampleCapacity` | 1024 | `OutputMonitor` | 码率窗口样本上限 |
| `MaxLine` | 256 KiB | `PipeReaders` | 单条 stderr 行上限 |
| `DefaultFps` / `DefaultFpsLevels` | 13 / 13,12,10,8,5 | `Constants` | 默认帧率与梯级 |
| `SchemaVersion` | 4 | `BuildProfileStore` | 能力缓存结构版本 |
| `CurrentVersion` | 6 | `UserConfig` | 设置文件结构版本（只写不读） |

### 9.3 环境变量

| 变量 | 作用 |
| --- | --- |
| `LAE_CONFIG_DIR` | 重定向配置/缓存/字体/输出根目录 |
| `LAE_LOG_DIR` / `LAE_LOG_LEVEL` / `LAE_LOG_MIRROR` | 日志目录、级别、是否镜像到控制台 |
| `LAE_FONT_DIR` | 字幕字体目录 |
| `LAE_NO_CAPS_CACHE` | 强制重扫 FFmpeg 能力 |
| `LAE_KEEP_SHARDS` | 分片模式保留暂存目录 |
| `LAE_LOG=0` | 完全关闭文件日志 |
| `LAE_UPDATE_FEED` | 覆盖更新 feed |
| `LAE_UPDATE_TOKEN` | 给需要认证的 feed 加 Bearer（从不落盘、从不入日志） |
| `LAE_WATERMARK_FONT_DIR` | 旧版字体目录变量，仍被识别 |

### 9.4 术语

* **槽（slot）**：输出时间轴上的一个 `1/fps` 位置。一个槽要么写新帧，要么重复
  上一帧。
* **梯级（rung）**：自适应帧率阶梯的一档，带下降阈值与回升阈值。
* **能力档案（build profile）**：某个 FFmpeg 二进制的编码器/滤镜/算法清单，
  按二进制身份缓存。
* **时代预设（era preset）**：`site/year` 或 `generic/year` 的一行数据，同时
  描述采集层与站点二次转码层。
* **实时模拟**：按源的时间节奏推进编码，而不是尽快跑完。
