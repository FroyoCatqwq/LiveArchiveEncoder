# v8 架构说明

> 状态：M2 完成时的快照（2026-10-31，tag `M2-Canary`）。
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
| `Lae.Core` | 规划、换算、阶梯、字幕链、设置模型、预设 | 任何项目引用；`Console`/`File`/`Directory`/`Process`/`HttpClient`/`Environment.Exit` |
| `Lae.Media` | FFmpeg 进程、管道、part 文件、设置文件、工具链探测 | 对 `Lae.Encoding` 的引用（方向反了） |
| `Lae.Encoding` | 一次实时编码的生命周期、二次压缩链 | 对 `Lae.App` 的引用 |
| `Lae.Plugins` | 插件宿主（M3） | 对 `Lae.Encoding` 的引用 |
| `Lae.App` | 命令树、会话编排、终端抽象、REPL | 启动进程（必须走 `ChildProcess`）；自己起线程 |
| `Lae.Cli` | `Program.cs`，52 行 | 任何业务逻辑 |

`Lae.Media` 和 `Lae.Plugins` 是兄弟，互不引用：媒体处理不该知道插件存在，插件由宿主编排。

`Path` 在 Core 里是允许的——它是纯字符串运算，禁掉只会把路径拼接推给每个调用方。

## 3. 三条硬规则，都有测试守着

`tests/Lae.Tests.Architecture` 读的是**源码文本**，不是反射。反射证明编译器产出了什么，读源码证明人写了什么，而且报错能指到行。

1. **Core 是纯的**（`CoreStaysPure`）：出现 `File`/`Console`/`Process` 就红。连注释里写这些裸词都会触发，这是有意的——注释里的 `File exists` 也是未来代码的诱饵。
2. **进程只从 `ChildProcess` 起**（`ProcessesAreOnlyStartedThroughChildProcess`）：类型是唯一会抽干 stderr、按树杀进程的地方。第二个 spawn 点就是第二个会因管道写满而挂死、或在 Ctrl+C 后留下两个 FFmpeg 的地方。v7 有四个。
3. **宿主不起线程**（`OnlyThePipelineStartsThreads`）：唯一配得上第二个线程的是泵的生产者（读解码器管道），它在 `Lae.Core/Live/FramePump`。

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
              └─ RunChain            RecompressRun：一趟一个子进程
```

三个"纯"是重点：`BuildRequest`、`EncodePlan.Create`、`RecompressPlan.Create` 都不碰磁盘、不抛异常，所有错误进 `Problems`，由 CLI 变成一句 `ERROR:` 和退出码 2。这就是为什么坏参数在开跑前几毫秒就能被拒，而不是录完二十分钟之后。

**退出码是契约**：0 完成、2 请求不成立、1 这台机器或这个素材做不到、3 有新版本、130 中断。

## 5. 泵：唯一需要并发的部分

`FramePump.Run` 两个角色：

- **生产者**（`Capture` 线程）：按 `1/fps` 读解码器管道，塞进 `FrameChannel`（有界 FIFO，慢了丢最旧）。
- **消费者**（调用线程）：按绝对截止时间 `baseline + k*interval` 写编码器。相对截止会累积，v8 第一次真实编码就把 5.04 s 跑成了 5.88 s。

阶梯的节流不减少槽位——那会缩短文件——而是**把新帧换成重复帧**，`cadence` 计数器决定哪一槽取新画面。码率读数来自 `-progress pipe:1`，分母是节目时间而不是墙钟（墙钟含机器被别人占用的时间，会让阶梯在负载高的机器上永不下降）。

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
| `LegacyV7Converter` 仍在 Core | 它是纯的（只做 JSON → 记录的映射），但只服务一次迁移 | v8.0.0 之后可以移到 `Lae.Media`，和 `FileSettingsStore` 一起 |
| 表现层没有 Spectre.Console | 决策时选了它，实际一直在用 `ITerminal` 的裸文本 | 等 REPL 需要真正的表格/进度条时再接 |

## 9. 和 v7 的对照

| | v7 | v8 |
| --- | --- | --- |
| 决策 | 混在 `RealtimeEncoder.Execute`（465 行） | `EncodePlan.Create`（纯函数，可按行断言） |
| 设置 | `Config` + `UserConfig` + 手写 `Capture`/`Apply` | 一个 `SettingsDocument`，全 `init`，`with` 覆盖 |
| 时代预设 | 32 行内置表，改不动 | 零内置；用户自己 `preset add` |
| 界面 | CUI 菜单 + 命令行两套 | 只有命令行 + REPL |
| 进程 | 四处 spawn | 一处 |
| 测试 | 618 个，绝大多数是集成 | 270 单测 + 47 e2e，单测跑 7 秒 |
