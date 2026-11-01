# LiveArchiveEncoder 8.0.0-m2.4-canary

**仍然是里程碑存档点，不是给日常使用准备的产品版。**
日常使用请继续用 [v7.9.8.6](https://github.com/FroyoCatqwq/LiveArchiveEncoder/releases/tag/v7.9.8.6)，
`main` 上仍然是 v7。

**这一版没有新命令。** 它做的是 M3 之前必须落地的地基，外加四条一直躺在审查清单里的缺陷。
如果你只想看"用户能感知到什么变了"，那是两件：**开录时不再丢前几帧**，以及报告里的丢帧
终于分得清是"启动丢的"还是"阶梯丢的"。

## 能感知到的

- **开录不再丢 3~8 帧**。编码器的第一次写入要开复用器、写文件头，几十到几百毫秒；这期间
  生产者一直按 `1/fps` 往六帧深的队列里塞，塞满就覆盖最旧的。现在生产者发布第一帧后会等
  消费者写出第一帧再起节拍，而且**节拍基准从放行时刻算起**——沿用旧基准会把等待时间当成
  "错过的槽"，生产者转头加速追赶，正好把它刚被叫停的事做一遍。
- **丢帧归因**：`PumpStats` 拆成启动丢帧与运行时丢帧。`motion` 行只报运行时丢帧；阶梯动过
  而启动仍有丢帧时，多一行 `startup`（启动丢帧现在是回归信号，所以不能无声）。**没有阶梯时
  那行 `skipped` 的数字和文案一字未改。**
- **`--version` 变成 `8.0.0-m2.4-canary`**。

实测：13 fps 静态板、39 槽、mpeg4 —— 新帧 26、重复 13、阶梯 13→8→5 fps、运行时丢 11 帧、
**启动丢帧 0**。同一素材在 m2.3 上是 3~8 帧启动丢，而且被记成"阶梯跳过的"。

## 这一版修掉的

三个都不会自己报错、只是"状态读错了"的：

- **`ChildProcess.ExitCode` 在子进程还活着时抛异常**：`Process.ExitCode` 的规则是没退出就抛
  `InvalidOperationException`，原来的封装直接透传，全靠调用点先问 `HasExited` 兜着——兜漏一处
  就是一条异常。现在未退出返回 −1。同时退出码读一次就记住：原来每次读都 `Refresh()`，泵每槽
  读两次，30 fps 下是每秒六十次重读进程表。
- **`PipeLines.Dispose` 同步等异步**：`Task.Wait(2s)` 写在 `Dispose` 里。控制台程序没事，放进
  任何有同步上下文的宿主就是死锁的经典形状。顺带确认那个等待本来也救不了诊断完整性——调用方
  在 `Dispose` 之前就读完了。
- **`EncodePlan.Create` 不抛的承诺没有测试**：代码在 `764c5bd` 就修好了，但没有任何东西钉着它。
  现在补上了。

## 内部结构（不影响命令行，影响以后每一次改动）

- **插件阶段定稿三个**：`post-encode` / `between-passes` / `after-chain`。`pre-encode` 被删掉——
  直播采集的源是流，不是文件，没有"编码前把成品交给插件"这种时刻，那个 stage 永远跑不到。
- **二次压缩链的执行器不再认识 FFmpeg**：part 文件、Promote、每趟超时、取消、失败编号这套
  逻辑抽成 `PostHop` + `PostChainRun`。`RecompressRun` 退化成适配器，错误串逐字节相同。
  插件 hop 此后只要实现"从 input 写 part"。
- **输出收敛成一条事件流**：`IEventSink`（Stage / Progress / Warn / Done）。终端渲染器让它
  和以前一模一样，屏幕上一字节没变；`--json-events` 以后只是第二个渲染器，不是第二套打印。
- **泵从 `Lae.Core` 搬进 `Lae.Encoding`**：Core 里装着全程序唯一的那个线程，而架构测试原本
  只禁 `Console`/`File`/`Process`，"Core 里每条规则都能无 fixture 测"对它并不成立。搬的是
  **执行**（泵、帧通道、精密时钟），留的是**决定**（`FpsLadder`）。Core 从 7,680 行降到约
  6,950，Encoding 从 625 涨到约 1,470。规矩同步收紧：`CoreStaysPure` 增加
  `Thread`/`Stopwatch`/`Timer`/`CancellationTokenSource`/`SemaphoreSlim`，
  `OnlyThePipelineStartsThreads` 的扫描范围加上 `Lae.Encoding`。

## 这个版本没有的

- **插件宿主（M3）**：`Lae.Plugins` 仍是一个空项目。`chain` 编排、`--json-events`、`--json`
  都还没有。
- **AOT 发布**：仍然是依赖打包（自包含单文件），没有原生 AOT。
- 自更新、分片并行（后者明确不做，见 `docs/parallel-split-verification.md`）。
- 交互式菜单（M4）；`lae repl` 是唯一带补全的入口。

## 装哪个包

| 平台 | 文件 | 要求 |
| --- | --- | --- |
| Windows x64 | `LiveArchiveEncoder-8.0.0-m2.4-canary-win-x64.zip` | 无（自包含，不用装 .NET） |
| Linux x64 | `LiveArchiveEncoder-8.0.0-m2.4-canary-linux-x64.tar.gz` | **glibc ≥ 2.34** |

Linux 底线是 **glibc 2.34**：RHEL / AlmaLinux / Rocky **9**、Debian **12**、Ubuntu **22.04**
及以上。AlmaLinux 8.10 跑不动（捆绑的 SQLite 原生库是按 2.34 编的），不是配置问题。

## 校验

```bash
sha256sum LiveArchiveEncoder-8.0.0-m2.4-canary-linux-x64.tar.gz
certutil -hashfile LiveArchiveEncoder-8.0.0-m2.4-canary-win-x64.zip SHA256
```

与 Release 页面列出的值对照。Windows 包里的 exe 是自包含单文件，解压后只有一个可执行文件
（打包脚本会断言这一点）。
