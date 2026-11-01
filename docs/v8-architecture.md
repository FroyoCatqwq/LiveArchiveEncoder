# v8 架构说明

> 状态：M2.3 之后、M3 之前（2026-11-01，tag `M2.3-Canary`）。
> 面向读者：要改这个程序的人，以及三个月后回来的人（很可能是同一个人）。

## 1. 一句话

把"决定"和"执行"彻底分开：**所有决策都是纯函数，只有一处代码能启动进程。**
v7 把这两件事写在一个 465 行的方法里，所以任何判断都只能通过"真的跑一遍编码"来验证。

## 2. 六个程序集，一个方向

```
Lae.Cli  ──►  Lae.App  ──►  Lae.Plugins
                 │              │
                 ├──►  Lae.Encoding  ──►  Lae.Media  ──►  Lae.Core
                 └──────────────────────────┘
```

| 程序集 | 里面有什么 | 不许有什么 |
| --- | --- | --- |
| `Lae.Core` | 规划、换算、阶梯（`FpsLadder`）、字幕链、设置模型、预设 | 任何项目引用；`Console`/`File`/`Directory`/`Process`/`HttpClient`/`Environment.Exit`；`Thread`/`Stopwatch`/`Timer`/`CancellationTokenSource` |
| `Lae.Media` | FFmpeg 进程、管道、part 文件、设置文件、工具链探测 | 对 `Lae.Encoding` 的引用（方向反了） |
| `Lae.Encoding` | 一次实时编码的生命周期、帧泵与节拍（`FramePump`/`FrameChannel`/`FineTimer`）、二次压缩链、通用的 post 链执行器（`PostChainRun`） | 对 `Lae.App` 的引用；`FramePump` 之外再起线程 |
| `Lae.Plugins` | 插件宿主（M3） | 对 `Lae.Encoding` 的引用 |
| `Lae.App` | 命令树、会话编排、终端抽象、事件流、REPL | 启动进程（必须走 `ChildProcess`）；自己起线程 |
| `Lae.Cli` | `Program.cs`，52 行 | 任何业务逻辑 |

`Lae.Media` 和 `Lae.Plugins` 是兄弟，互不引用：媒体处理不该知道插件存在，插件由宿主编排。

`Path` 在 Core 里是允许的——它是纯字符串运算，禁掉只会把路径拼接推给每个调用方。

**阶梯留在 Core，泵搬走了**，这条线值得记住：`FpsLadder` 是"码率到了阈值该降到几帧"的规则，纯函数、可按毫秒断言，
而且 `EncodePlan.BuildLadder()` 要在规划阶段就把它算出来；`FramePump`/`FrameChannel`/`FineTimer` 是"怎么按时钟把帧
递过去"的机制，一个线程、一堆 `Stopwatch` 读数、平台定时器。前者是决定，后者是执行——v8 的全部结构就是这条分界。

## 3. 三条硬规则，都有测试守着

`tests/Lae.Tests.Architecture` 读的是**源码文本**，不是反射。反射证明编译器产出了什么，读源码证明人写了什么，而且报错能指到行。

1. **Core 是纯的**（`CoreStaysPure`）：出现 `File`/`Console`/`Process` 就红；出现 `Thread`/`Stopwatch`/`Timer`/`CancellationTokenSource`/`SemaphoreSlim` 也红。连注释里写这些裸词都会触发，这是有意的——注释里的 `File exists` 也是未来代码的诱饵。
2. **进程只从 `ChildProcess` 起**（`ProcessesAreOnlyStartedThroughChildProcess`）：类型是唯一会抽干 stderr、按树杀进程的地方。第二个 spawn 点就是第二个会因管道写满而挂死、或在 Ctrl+C 后留下两个 FFmpeg 的地方。v7 有四个。
3. **只有流水线起线程**（`OnlyThePipelineStartsThreads`）：唯一配得上第二个线程的是泵的生产者（读解码器管道），它在 `Lae.Encoding/Live/FramePump`。扫描覆盖 `Lae.App`、`Lae.Cli`、`Lae.Encoding`，只放行 `FramePump.cs` 一个文件——泵搬进 Encoding 之后，这条规则顺手把它的邻居也看住了。

## 4. 一次 `lae encode` 的生命周期

```
命令行 ──► EncodeCommand（只解析选项）
              │
              ▼
        EncodeSession.Run
              ├─ BuildRequest        纯：设置 → 预设 → 命令行，三层覆盖
              ├─ ProbeToolchain      找 FFmpeg/ffprobe
              ├─ ProbeSource         ffprobe → SourceInfo
              ├─ PrepareTrack        内嵌字幕轨：拆成临时文件（dry-run 不拆）
              ├─ ListEncoders        ffmpeg -encoders → 目录
              ├─ EncodePlan.Create   纯：两条命令行 + 问题清单，**从不抛**
              ├─ RecompressPlan.Create 纯：每一趟的输入、输出、容器
              ├─ LiveEncode.Run      两个子进程 + 泵
              └─ RunChain            PostChainRun：一趟一个 hop，FFmpeg 只是一类
```

三个"纯"是重点：`BuildRequest`、`EncodePlan.Create`、`RecompressPlan.Create` 都不碰磁盘、不抛异常，所有错误进 `Problems`，由 CLI 变成一句 `ERROR:` 和退出码 2。这就是为什么坏参数在开跑前几毫秒就能被拒，而不是录完二十分钟之后。

**退出码是契约**：0 完成、2 请求不成立、1 这台机器或这个素材做不到、3 有新版本、130 中断。

**输出也是一条流**：整个过程只往 `IEventSink` 里说话（`Stage` / `Progress` / `Warn` / `Done`），
终端渲染器把它排成今天的样子。见 §9。

## 5. 泵：唯一需要并发的部分

`Lae.Encoding/Live/FramePump.Run` 两个角色：

- **生产者**（`Capture` 线程）：按 `1/fps` 读解码器管道，塞进 `FrameChannel`（有界 FIFO，慢了丢最旧）。
- **消费者**（调用线程）：按绝对截止时间 `baseline + k*interval` 写编码器。相对截止会累积，v8 第一次真实编码就把 5.04 s 跑成了 5.88 s。

阶梯的节流不减少槽位——那会缩短文件——而是**把新帧换成重复帧**，`cadence` 计数器决定哪一槽取新画面。码率读数来自 `-progress pipe:1`，分母是节目时间而不是墙钟（墙钟含机器被别人占用的时间，会让阶梯在负载高的机器上永不下降）。

**开文件那一下不算节拍**。生产者发布第一帧后就停下，等消费者写完第一帧再放行（`Capture.Release` /
`WaitForRelease`），节拍基准从放行时刻重算。不这么做，复用器写文件头的几百毫秒里生产者会把六帧深的
队列塞满并覆盖最旧的——13 fps 的素材每次丢 3~8 帧，而报告把它们记成"阶梯跳过的"。放行之后仍是绝对
截止时间；等待期间只是解码器那头不再被读，这是任何一次慢消费都会给的背压。

丢帧分在两个数上：`PumpStats.DroppedAtStart`（编码器还没接住第一帧）与 `DroppedWhileRunning`
（跑起来之后：阶梯主动跳过，或消费者跟不上）。报告里 `motion` 行只报后者，前者单独一行 `startup`。

泵和它的两部分邻居（`FrameChannel` 帧握手、`FineTimer` 1 ms 定时与睡/旋混合）一起住 `Lae.Encoding/Live`；
阶梯的规则 `FpsLadder` 留在 `Lae.Core/Live`，因为它要在规划阶段就算出来（`EncodePlan.BuildLadder`），
而且它不需要时钟。

## 6. 落盘规则

- 产物先写 `name.part.ext`，校验非空后才 `File.Move` 到正式名。
- 起进程前先 `PartFile.Discard(part)`，否则上一次被外部杀掉的残留会让 FFmpeg 在内部 `exit -17` 死掉。
- 设置文件整文件原子写：临时文件写完 `Flush(flushToDisk)` 再 move。

## 7. 已经拆掉的

`EncodeSession` 原来是 830 行、一个文件四个类型。M2 收尾时按"读者在找什么"拆成五个：

| 文件 | 职责 |
| --- | --- |
| `EncodeOverrides.cs` | 命令行的形状，与 System.CommandLine 无关 |
| `EncodeDependencies.cs` | 会话对外部世界的五个请求（探测、探测源、列编码器、拆轨、跑链） |
| `EncodeSession.cs` | 一次运行的编排：工具链、源、拆轨、执行 |
| `EncodeSession.Request.cs` | 设置 → 预设 → 命令行 的三层覆盖 |
| `EncodeSession.Chain.cs` | 二次压缩链的两个入口（随采集跑 / 只跑链） |
| `EncodeSession.Report.cs` | 所有输出：计划、链、结果、命令行引用 |

`partial class` 而不是拆成独立类型，因为这些方法之间共享的是**一次运行的状态**，拆开会变成一堆要互相传参的小类。文件边界在这里是给读者的，不是给编译器的。

## 8. 已知的架构债

诚实列一下，都记在这里，不假装不存在。

| 债 | 现状 | 何时还 |
| --- | --- | --- |
| `Constants.cs` 25 KB 一个静态类 | 容器、音频、像素格式、颜色、滤镜全在里面 | v8.1；按域拆成 `ContainerCatalog`/`ColorCatalog`/`FilterCatalog` |
| `EncodePlan.Create` 仍有 170 行 | 已经抽出了 `BuildDecoderArgs`/`BuildEncoderArgs`，决策段还是长 | 下次动编码参数时顺手拆 |
| `EncodeDependencies` 是五个 `Func` 的袋子 | 测试要填四个才能测一个 | 等 M3 插件需要注入更多东西时再考虑接口化 |
| `SubtitleDefaults.Codec` 没有消费者 | 容器→编码器的表已经决定了 `-c:s` | M3 前删掉，或让 `--subtitle-codec` 接上 |
| 事件流只有终端一个渲染器 | `IEventSink` 的第二个消费者（JSONL）还没写 | M3，和 `--json-events` 一起 |
| `LegacyV7Converter` 仍在 Core | 它是纯的（只做 JSON → 记录的映射），但只服务一次迁移 | v8.0.0 之后可以移到 `Lae.Media`，和 `FileSettingsStore` 一起 |
| 表现层没有 Spectre.Console | 决策时选了它，实际一直在用 `ITerminal` 的裸文本 | 等 REPL 需要真正的表格/进度条时再接 |

## 9. 一条事件流，两个渲染器

编码期间发生的事只有四件，都在 `Lae.App/Ui/IEventSink` 上：

| 事件 | 谁发 | 终端怎么渲染 | JSONL 会怎么渲染 |
| --- | --- | --- | --- |
| `Stage(name)` | 会话 | **不打印**（这些阶段报告里本来就有） | `{"t":"stage","name":"capture"}` |
| `Progress(done,total)` | 泵的调用方 | 就地重写一行（节流：交互 0.25 s，重定向 5 s 一行） | `{"t":"progress",…}` |
| `Warn(text)` | 计划、预检、链条 | `note: …` | `{"t":"warn","message":…}` |
| `Done(summary,artifacts,elapsed)` | 会话 | 擦掉进度行，打印 `frames written` | `{"t":"done","artifacts":[…],"seconds":…}` |

两条规矩：

1. **sink 由 `EncodeSession.Run` 的最后一个可选参数传入；传了就替换终端渲染器，不是叠加。**
   `--json-events` 因此是第二个渲染器，而不是第二套 `ErrorLine`。想要两者都有，写一个组合 sink。
   传进来的 sink 归调用方释放，会话只释放自己造的那个。
2. **渲染器管措辞，会话管事实。** `note:` 前缀在渲染器里拼（`encode.note.prefixed`），中文才能
   把前缀放到自己的语序上；会话只交出"有一条 warning，内容是……"。

阶段名与插件位置共用 `Lae.Core/Plugins/PluginStages`：`between-passes` 在"链条第几趟之间"和
"插件声明自己能在哪插"里是同一个时刻，两套拼写就是两台机器开始对不上话的第一步。
`preflight`、`capture` 是这两个之外仅有的阶段名（`EncodeSession.Phases`）。

**没有 `pre-encode` 这个阶段**：直播采集的源是流，不是文件，没有成品可以在编码前交给
插件。协议里写它，只会得到一个永远跑不到的 stage——M2.3 之前的注释里就写着它。

## 10. 和 v7 的对照

| | v7 | v8 |
| --- | --- | --- |
| 决策 | 混在 `RealtimeEncoder.Execute`（465 行） | `EncodePlan.Create`（纯函数，可按行断言） |
| 设置 | `Config` + `UserConfig` + 手写 `Capture`/`Apply` | 一个 `SettingsDocument`，全 `init`，`with` 覆盖 |
| 时代预设 | 32 行内置表，改不动 | 零内置；用户自己 `preset add` |
| 界面 | CUI 菜单 + 命令行两套 | 只有命令行 + REPL |
| 进程 | 四处 spawn | 一处 |
| 测试 | 618 个，绝大多数是集成 | 270 单测 + 47 e2e，单测跑 7 秒 |
