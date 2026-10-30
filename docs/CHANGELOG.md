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

## v8.0.0-m2.1（未发布）

M2 收尾的两件事：配置文件的格式定下来，以及程序终于开始记事。

### 配置改成 TOML（⚠ 行为变化）

`settings.json` → `settings.toml`，解析用 **Tomlyn 2.10.1**。JSON 只剩两个用途：读旧文件迁移，
和 v7 `config.json` 的转换。

为什么换（完整评估见 `deliverables/v8-config-format-decision.md`）：这份文件是**人写、机器偶尔改一行**，
四个候选里只有 TOML 同时做到注释能活、写错当场报、程序能只改那一行。

- **程序只改它拥有的那一段**：目前只有 `preset add/remove` 会写文件，它重写 `[[customPresets]]` 块，
  其余字节（注释、空行、表顺序）一个字都不动。
- **未知键是错误**：键集由 `SettingsDocument` 反射推导，不手写清单。`qualityy = 5` 会报出这个键，
  而不是静默用默认值——v7 的 `fpsLevesl` 就是这个坑。
- **迁移不删东西**：v7 的 `config.json` 复制成 `config.v7.backup.json` 后原地不动；v8 早前的
  `settings.json` 复制成 `settings.v8-backup.json` 后删除（留着会每次启动重复迁移）。
- `schemaVersion` 10 → 11。
- `lae config show --json` 改为 `--toml`，打印的就是存储格式，可以直接存成 `settings.toml`。

### 运行历史（SQLite）

每次真正跑起来的编码写一行到 `history.db`（与设置同目录），新增 `lae history list|path`。
记事实不记意图：时长、产物大小、平均码率、阶梯停在哪档、结果与失败原因。

- 失败的行**留空而不是填 0**；Ctrl+C 记 `cancelled` 不是 `failed`。
- 写不进去只在 stderr 打一行 `note:`，不把已完成的编码变成失败。
- 读取不创建数据库：没压过东西的机器上 `history list` 只说"还没有记录"。
- 连接**不开池**：一行写入不需要池，而池化句柄会让文件在进程空闲时仍被占用。

### 中英输出（I18Next.Net 2.0.0）

- 语言按 `LAE_LANG` → `settings.toml` 的 `[ui] language` → `en` 决定；`zh-CN`/`zh_CN`/`ZH`
  都归到 `zh`，不认识的语言落到英文而不是报错。
- 翻译资源**内嵌进程序集**（`lae.locales.{语言}.translation.json`），因为 v8 是自包含单文件发布，
  散落的 `locales/` 目录会是用户包里第一个丢的东西。
- 抽象在 `Lae.Core`（`IMessages` + `LanguageChoice`，纯逻辑可单测），实现在 `Lae.Media`
  （i18next + 资源读取），和 `ISettingsStore`／`TomlSettingsStore` 同一套分法。
- **本轮只完整翻译了 `lae history`**，其余命令仍是英文；剩余约 90 条字符串的清单和"怎么加一条"
  写在 [`v8-i18n.md`](v8-i18n.md)。先做一个命令是为了出问题时能分清是机制错了还是某条键错。

---

## v8.0.0-m2-canary（2026-10-31）

v8 重写的**存档点**，不是给日常使用准备的产品版。里程碑 M0（骨架）、M1（设置与规划）、
M2（实时泵、阶梯、字幕、二次压缩链）到此完成。日常使用请继续用 v7.9.8.6（`main`）。

### 里程碑 M2 完成

- **自适应帧率阶梯**接上实时泵：13/8/5/3 fps，落到下一档保持 0.2 s、爬回来保持 0.75 s。
  节流不减少槽位（那会缩短文件），而是把新帧换成重复帧。
- **码率读数改读 `-progress pipe:1`**。原来读正在写的文件长度，Windows 返回的是过期
  目录项，阶梯在 1017 → 0 → 1017 之间乱跳；编码器命令行加 `-flush_packets 1`，
  否则 AVI 复用器攒一到两秒才报一次。
- **二次压缩链**：`--recompress` 可重复，每一趟读上一趟的产物，产物名从**录像**派生
  （`_web`、`_web2`），容器由片段里的编码器决定（VP9 自动落 `.webm`）。某趟失败
  只删自己的 part 文件，录像不动，退出码 1。`--recompress-only` 跳过采集直接跑链。
- **字幕**：`--subtitle-file`（烧）、`--subtitle-track`（拆内嵌轨再烧）、
  `--subtitle-copy`（把轨原样带进产物）。位图字幕（PGS/VobSub）不能烧，只能 copy；
  容器不支持字幕流（avi/flv/rm）在开跑前就拒。
- **分片并行明确不做**，`--parallel > 1` 现在是退出码 2 并说明原因：实时节奏本身就是
  墙钟，N 段各自按实时走总时长不变。理由和实测数字见 `docs/parallel-split-verification.md`。

### 本轮修掉的七个缺陷

1. **预设换了阶梯却不换阈值**：`preset add` 存的阈值是按帧率索引的，套用新阶梯时
   旧阈值留在原处，用户的 150 kbps 落点会挂到碰巧活下来的那一档上。命令行那条路
   早就拒绝这样做，预设是同一件事的另一扇门。
2. **设置文件的修复只做了一层**：`Normalised()` 只重建六个顶层成员，
   `"defaults": {"fpsLevels": null}` 会带着 null 阶梯穿过读取边界，在某个消费者里炸。
   现在递归，每个持有集合的记录自己负责。
3. **二次压缩片段只在最后一个 token 上查输出名**：`-c:v copy /tmp/x.mp4 -an` 能通过，
   FFmpeg 会多写一个计划外的文件。现在只有紧跟选项的裸词才算值。
4. **REPL 里按一次 Ctrl+C 就废掉整个会话**：取消源是单次的，之后每条命令一起步就是
   已取消状态，再按一次还会计成"第二次"直接强杀进程。现在每条命令一个新作用域。
5. **编码器不在容器表里时静默忽略 `--container`**：`--encoder qtrle --container avi`
   会得到一个 `.mov` 且一声不响。现在明说它只能写什么。
6. **`lae setup` 建议了一个不存在的命令**（`lae toolchain install`），还紧接着说
   "这个版本没有下载命令"。v7 犯过同一个错，注释里还写着别再犯。
7. **`--out` 的扩展名可以和产物的容器矛盾**：`--encoder flv --out clip.avi` 会把
   FLV 流写进一个叫 `.avi` 的文件并报成功。`ResolveContainer` 的注释明写"扩展名和
   内容矛盾是几年后才会发现的东西"，但另一扇门一直开着。现在两边一致才放行。

顺带修的：`lae config show` 有两个键各打印两次；`preset add --keep` 不冻结质量
（而其他字段都冻结）；`lae config migrate` 在写盘失败时仍然打印 "Wrote"。

### 架构

- `EncodeSession`（830 行、四个类型）按"读者在找什么"拆成五个文件：
  请求组装、依赖、编排、链、输出。`partial` 而非拆成独立类型，因为这些方法共享
  一次运行的状态。
- 新增**两条架构测试**：进程只能从 `ChildProcess` 起；宿主不许自己起线程。
  都是真实不变量，不是风格洁癖——v7 有四个 spawn 点，每个都能在管道写满时挂死。
- 新增 [`v8-architecture.md`](v8-architecture.md)：分层、数据流、三条硬规则、
  已知的架构债。

### 验证

单测 **270**，e2e **47**，零警告；lae-win（Server 2019）与 lae-linux（AlmaLinux 8.10）
都是 270/270。

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

## v7.9.7（2026-10-27）

第五个补丁版，主题是**预设的数据从"我记得"变成"页面上写着"**。三件事：把扁平
的预设表改成 `站点/年份`、每一条数字都回到当年的存档页面去核、以及把 H.264 从
"就是 x264"改成一个可以选的实现。

### 1. 预设改成 `站点/年份`

**问题**：旧表是两份平行清单——五个 `dialup98` 式的"时代"和一堆 `cctv03` 式的
"站点"，年份藏在后缀数字里。同一个站在不同年代换了形态时没法表达：优酷 2007 是
320x240 的 FLV、2011 是 640x360 的 H.264，旧表只能留一个，另一个叫 `youku11`，
名字里的 11 是硬凑的。

**改法**：一行 = 一个站在一个年份，id 就是 `site/year`：

```
--era bilibili/2012
--era youku/2007
--era generic/1998          # 通用年份，"那一年的一般水平"
```

- 菜单 14 变成两级：先列站点，选站点再列它有的年份。
- 通用时代（原 `dialup98`/`broadband03`/`flv07`/`mp4_10`/`hd14`）归到 `generic`
  这个伪站点下，语义不变。
- **旧 id 全部保留为别名**：`cctv03` → `cctv/2003`、`youku07` → `youku/2007`、
  `hd14` → `generic/2014`，共 32 条。用旧写法时 CLI 会打一行"已更名"，功能不变。

内置表现在是 32 行（5 个通用年份 + 27 个站点年份）。

### 2. 每一条数字都有存档页面

`docs/era-presets.md` 重写成一张带出处表的清单：每一行都附一个
`web.archive.org` 快照链接（或当年的官方说明），标明这个年份的这个站到底给的是
多少分辨率、多少码率、什么编码器。

能读到明确数字的，直接引页面上的原文（例如 YouTube 2007 的 320x240 与
`fmt=` 挡位、各站播放器的 `width/height`、`br`、`definition` 参数）。读不到的
（当年是 MMS/RTSP 流，没有被爬取保存），**在表里明确标成"按当年编码器包络估算"**，
不假装是实测。

### 3. H.264 换实现：`--h264 libx264|libopenh264`

**问题**：2010-2015 那批预设写的是 `libx264`，而当年的站点并不用 x264。x264 的
心理视觉优化会把 700 kbps 的 360p 做得比当年"干净"，于是产物一眼就是重制的。

**改法**：新增 `--h264`（菜单里是 `h`，三态：auto → libopenh264 → libx264 →
auto）。

- 2010 年及以后的 H.264 预设默认用 **OpenH264**：它是当年的参考实现，固定码率下
  的软糊感正是那个年代的样子。
- 直播层和二次压缩层一起换——抓的时候是 x264、交付的时候是 OpenH264 会描述两台
  不同的机器。
- OpenH264 没有 CRF，只有码率，所以预设的 `-b:v` 直接生效，不会漏出 `-crf`。
- 本项目的 BtbN GPL 快照**自带** `libopenh264`；如果换到某个没有它的构建，错误会
  指向 `--ffmpeg-download nonfree`（`BnqDzj/FFmpeg-Builds-nonfree` 那套）。
- `--recompress-codec libopenh264` 也能单独用了。

### 验证

- 单测 529 例 → 528 过 / 1 跳过（需要字体文件的用例），其中新增 6 条专测本次改动
  （别名全覆盖、`site/year` 形状、OpenH264 默认值、两种编码器的参数形态）。
  两台 CI 服务器各自独立跑过：`lae-linux` 528 例、`lae-win` 529 例，都 0 失败。
- e2e 195 例：两阶段全绿（第一阶段 27、第二阶段 167 过 / 2 跳过=无 `.a64` 解复用器）。
  里面用到旧 id 的用例（`--era flv07`、`--era youku07`、`--era bilibili09`）一条没改
  就通过了——别名表不是装饰。
- OpenH264 端到端实测：`--era bilibili/2012` 输出 640x360 H.264，视频码率 694,738 bps
  （目标 700k），mp4 容器，AAC 96k 立体声。

### 产物

| 文件 | 大小 | 需要 | 构建机 |
| --- | --- | --- | --- |
| `LiveArchiveEncoder-v7.9.7-win-x64.zip` | 322,808 B | .NET 10 或 11 运行时 | 本地，JIT |
| `LiveArchiveEncoder-v7.9.7-win-x64-aot.zip` | 4,022,745 B（exe 9,261,568 B，62 秒） | 无 | Windows Server 2019 + VS2022 MSVC 14.44 |
| `LiveArchiveEncoder-v7.9.7-linux-x64.tar.gz` | 283,552 B | .NET 10 或 11 运行时 | AlmaLinux 8.10，JIT |
| `LiveArchiveEncoder-v7.9.7-linux-x64-aot.tar.gz` | 4,793,406 B（ELF 10,221,296 B，47 秒） | 无 | AlmaLinux 8.10 + copr clang 24 快照 |

Linux AOT 的 `objdump -T` 最高只要 `GLIBC_2.17`，比文档写的 2.28 下限还宽松；四个包
都在目标系统上跑过 `--help` / `--era`。

---

## v7.9.6.3（2026-10-25）

第四个补丁版，主题是**"第一次用"和"网上的素材"**：装不上 FFmpeg 的人、拿到 16:9 母版
却只能录 4:3 的人、素材在 NAS 上却不敢开并行的人。四项功能彼此独立，但都指向同一件事——
工具的默认行为不该替用户假设他的机器和环境。

### 1. 首次启动不再要求先装好 FFmpeg

**问题**：`live_archive_encoder.py` 起就要求 PATH 上有 FFmpeg，v7.7 加的下载功能藏在菜单
第 12 项里——而第一次运行的人根本进不去菜单：发现阶段就抛 `Cannot find ffmpeg; add
FFmpeg/bin to PATH` 退出了。下载能力存在，只是没人被问到。

**改法**：发现失败时，如果

- 没有显式给 `--ffmpeg` / `--ffprobe`（给了就是用户自己的选择，错了要报错），
- 也没有 `--ffmpeg-download`（那个是脚本路线，本来就会自己装），
- stdin/stdout 没有重定向（有人在键盘前，否则猜比失败更糟），

就列出本平台能自动下载的发行版，让用户挑一个装上，装完**继续这次运行**，并把选择的
来源写进配置文件，下次直接进编码器。清单只含能自动下载的源：让第一次用的人去手工找
`.7z` 不叫首次启动。

命令行路线不变，`--ffmpeg-download`（不带值＝默认 btbn）依旧是脚本该用的方式。

### 2. 时代预设可以切 4:3 / 16:9，而且只给编码器真支持的尺寸

**问题**：每个预设只带一个帧尺寸，于是 2007 年的站点预设永远只能出 4:3——可同一个
上传，等那个站有了宽屏播放器之后就变成 16:9 了。画幅是这个时代**显示器**的属性，不是
编码器的属性。

**改法**：新增 `--era-aspect source|4:3|16:9`（菜单里是时代子菜单的 `a.` 项，循环切换）。
规则是**保持采集宽度、只改高度**：352x264 → 352x198，1280x720 → 1280x960，320x240 →
320x180。保持宽度是因为宽度才是预设调好的横向细节，码率上限属于时代、不属于画幅，所以
不动。

"只给真支持的尺寸"是硬约束，不是提示：

- H.261 / H.263 只存在于 CIF 那五个尺寸（`Constants.SpecialSizes`），要 16:9 直接
  `<编码器> only exists at 128x96, 176x144, 352x288, 704x576, 1408x1152`，退出码 2；
- AMV 要 16 的倍数；rv10/rv20 上限 704x576；Sorenson Spark 上限 1280x720；其余要偶数。

校验在**参数解析阶段**完成，因为 FFmpeg 会接受命令行、然后在第一帧上失败——那是实时
编码跑了二十分钟之后才发现的错误。返回的尺寸一定过 `EraAspects.Support()`，单测里有
一条不变量守着这件事（每个内置预设 × 两种画幅 × 六种编码器）。

二次压缩也吃同一个尺寸：一次 16:9 的采集如果第二遍还写 352x264，说的就是两台机器。

### 3. 网络源（SMB / 网络驱动器 / HTTP）可以并行，条件是测出来的

**问题**：v7.9.1 起网络路径一律单程。理由是"N 个读取者共用一条时断时续的链路"，那对
一条拥塞的 Wi-Fi 是对的，对一个千兆 LAN 是错的——代价正好落在最需要分片的场景上
（NAS 里成堆的长录像）。

**改法**：把"假设"换成"测量"。判定逻辑在 `Encoder/ParallelEncoder.BlockReason`，测的是
**源本身**（`Remote/NetworkProbe`）：在文件里按 10%/37%/63%/90% 的位置各读 2 MiB，算中位
吞吐和抖动（默认 4 个采样，小文件减少）。要开并行得同时满足：

- 吞吐 ≥ 需求码率 × **2.5**（需求码率取容器 `bit_rate`，没有就用 体积/时长，再不行用输出
  上限——编码是按实时速度读源的，所以这个数就是链路必须持续供得上的量）；
- 逐采样吞吐的极差 < 中位数的 **30%**（抖动）；
- 首字节延迟 < **500 ms**。

HTTP(S) 源用 Range 请求测同一件事；不支持 Range 的服务器测不出来，于是不给并行——这是
"测不了就别冒险"，不是"默认关闭"。

**关于 iperf3**：`--iperf3 HOST[:PORT]` 会用 `iperf3 -c HOST -t 3 -J` 的
`sum_received.bits_per_second` 作为**链路参考**。它有两个前提：两端都装了 iperf3，且对端
对外开放了 5201/自定义端口；公共 iperf3 服务器只能当 WAN 参考，量不到你那台 NAS 的磁盘
和每连接限速。所以它永远不是默认路径，也永远替代不了上面那次读源测量。想单独看数字：
`--net-test PATH|URL`（只测量、不编码，可以对着生产共享跑）。

### 4. 补上 2001-2004 的 Windows Media 年份

时代表原来从 2005 开始，2003 年的录像没有落点。新增四个**站点**预设，走当年的 Windows
Media 而不是 Flash：

| id | 年份 | 尺寸 | 帧率 | 视频 | 音频 | 容器 |
| --- | --- | --- | --- | --- | --- | --- |
| `cctv03` | 2003-2006 | 320x240 | 15 | wmv2 ~250k | wmav2 32k 22.05k 单声道 | .wmv |
| `sina03` | 2002-2005 | 320x240 | 12 | wmv2 ~200k | wmav2 24k | .wmv |
| `sohu03` | 2003-2005 | 320x240 | 15 | wmv2 ~220k | wmav2 32k | .wmv |
| `netease03` | 2003-2006 | 176x144 (QCIF) | 12 | wmv2 ~120k | wmav2 24k | .wmv |

直播层也点明是 `wmv2`：2003 年的门户不会接收 H.264 上传，录成 H.264 再把第二遍写成
WMV8 描述的是两台机器。为此 `wmav2` 进了二次压缩的音频表（实测：wmv2 + wmav2 32k /
22.05kHz 单声道写进 .wmv，ffprobe 读回 `wmv2` / `wmav2` / `asf`）。

数字是**当年的典型值**，不是从某个存档文件实测出来的：MMS/RTSP 流不像 HTTP 托管的 FLV
那样被爬取保存，没有可指的样本。这些值落在 WMV8 自己的包络里，`docs/era-presets.md`
里写明了这一点。

### 5. 最低系统要求降到 .NET 10 支持的下限，并实测

文档原来写的是 Windows 11 / 较新的发行版。改成测试机本身的水平：

| | 最低 | 依据 |
| --- | --- | --- |
| Windows | Windows Server 2019 / Windows 10 1809（10.0.17763） | 测试机 `auto-ci-1`；.NET 10 的 Windows 下限 |
| Linux | glibc 2.28（AlmaLinux 8 / RHEL 8） | 测试机 `test-ci-2`；.NET 10 支持的 RHEL 8 系 |
| 运行时 | .NET 10 或 .NET 11（JIT 产物 `RollForward=Major`） | 单一框架依赖产物 |

`Core/EnvCheck` 现在也会检查这两项并给出提示（仍然只警告、不拒绝）：Windows 比构建
号、Linux 读 `gnu_get_libc_version`（拿不到就是 musl 或静态运行时，不猜）。AOT 产物按
发布机的 glibc 链接，所以更老的发行版该用 JIT 产物——文档里一直这么说，现在运行时会
提醒。

CI 也随之调整：两台自建机（Windows Server 2019 + AlmaLinux 8）各跑一遍单测，Windows
端能出 NativeAOT（VS2022 + MSVC），Linux 端用 copr 的 clang 快照工具链出 AOT。

### 6. e2e 不再依赖控制台代码页

`tests/e2e/harness.py` 用 UTF-8 解码子进程输出，而 .NET 在 Windows 上按**控制台代码页**
写 stdout。简体中文的 Windows 是 936，于是中文菜单整片变成 U+FFFD，`test_cui_smoke`
里两条断言（"已恢复上次配置"、"报告显示 avi 容器"）变成假失败——产品没问题，测试读错码。

改成：先按 UTF-8 严格解码，失败再按 `GetConsoleOutputCP()` 拿到的代码页解码，最后才是
替换字符兜底。于是 936、65001、UTF-8 的 Linux 三种控制台都读得对，不用再 `chcp 65001`。

### 产物与验证

四个产物，两头各一份 JIT/AOT：

| 产物 | 大小 | 需要 | 构建机 |
| --- | --- | --- | --- |
| `…-win-x64.zip` | 318 KB | .NET 10 或 11 运行时 | Windows Server 2019 |
| `…-win-x64-aot.zip` | 4.0 MB | 无（单 exe） | Windows Server 2019 + VS2022 / MSVC 14.44 |
| `…-linux-x64.tar.gz` | 279 KB | .NET 10 或 11 运行时 | AlmaLinux 8.10 |
| `…-linux-x64-aot.tar.gz` | 4.8 MB | 无（单文件） | AlmaLinux 8.10 + clang 24 快照 |

AOT 产物是在**最低支持系统**上编的，不是在新机器上编完再声称兼容：Windows 侧
9,193,472 字节的 exe 在 Server 2019 上编成并当场 `--help` 通过；Linux 侧 10,171,232
字节，`objdump -T` 里最高的 glibc 符号是 `GLIBC_2.17`（构建机是 2.28，符号需求反而更低）。

- 单测：521 例 → 520 过 / 1 跳过（跳过的是需要字体文件的用例）。
- e2e：195 例 → 193 过 / 2 跳过（跳过的是 FFmpeg 没有 `.a64` 解复用器）。两个阶段
  （独占计时 + 8 路并行）都绿，`test_cui_smoke` 在 cp936 控制台下不再假失败。

---

## v7.9.6.2（2026-10-24）

第二个补丁版：**站点预设也要改第 4 项参数（直播层）。**

### 问题

`--era youku07` 这类**站点**预设只填了两样东西：直播层的编码器、二次压缩的参数。
直播层的帧尺寸、帧率、挡位、码率上限、缩放核、音频参数一概不动，于是：

- 直播录的是你原来的 640x480@25 母版，第二遍却声称是 352x264@15 —— 两个阶段描述
  的是两台不同的机器；
- CUI 菜单第 4 项参数显示的是**两边都不像**的数字（母版的尺寸 + 站点预设的编码器），
  用户看到的是一份不存在的配置。

v7.9.5.1 的原始理由是"网站说什么不代表你怎么采集"。但这个工具本来就是**用当年的
参数录一遍**：选 2007 年的站，录出来的东西该是 2007 年的样子，而不是"拿 2007 年的
站转码一份 2016 年的母版"。采集侧和投放侧一样属于那个年代。

### 改法

站点预设现在两层都填，取自同一行数据：帧尺寸/帧率/码率上限 = 该站当年的转码档，
挡位按基准帧率给当年的阶梯，缩放核 2000 年代 `bicubic`、2010 年代 `lanczos`，
音频跟着该站的码率/声道/采样率，FLV 时代的站点名 `libmp3lame`。

```
# 现在是 352x264@15、320 kbps、15/12/10/8/5 挡位、mp3 22.05 kHz 单声道
LiveArchiveEncoder in.mkv out.avi --era youku07

# 显式参数仍然优先：这里录 320x240@20、上限 500
LiveArchiveEncoder in.mkv out.avi --era youku07 --width 320 --height 240 --fps 20 --limit 500
```

| 预设 | 直播层 | 挡位 |
| --- | --- | --- |
| `sina06` / `tudou06` | 320x240@12 / 352x264@12 | 12 → 10 → 8 → 5 |
| `sohu06` / `youku07` / `ku6_07` / `56_07` / `acfun08` / `youtube07` | 320x240 或 352x264 @15 | 15 → 12 → 10 → 8 → 5 |
| `qq06` / `cctv06` | 320x240@25 / 384x288@25 | 25 → 20 → 15 → 10 |
| `bilibili09` | 384x288@24 | 24 → 20 → 15 → 10 |
| `bilibili11` / `youku11` / `iqiyi11` / `cctv12` / `youtube11` | 640x360@25 或 @30 | 25/30 → 20 → 15 → 10 |
| `bilibili14` / `youtube15` | 1280x720@30 | 30 → 25 → 20 → 15 |

### 顺带修掉的一处不一致

`--audio-encoder` 之外的**预设音频编码器**以前在 CLI 里被忽略（只有 CUI 认）：
`EraPreset.AudioEncoder` 写在预设里，CLI 却永远按容器自动挑。FLV 时代的站现在点名
`libmp3lame`，两条路径终于一致（显式 `--audio-encoder` 仍然优先，构建里没有该编码器
时按容器回退）。

### 升级提示（行为变化）

- 以前 `--era <站点>` 不改直播层，现在会改：尺寸、帧率、挡位、码率上限、缩放核、
  音频参数都按预设来。想要旧行为，把这几项显式写出来即可（预设是兜底）。
- 自定义站点预设也支持带直播层了：`config.json` 里给了 `Width`/`Height` 就有，
  没给就仍只填第二遍（老文件行为不变）。

### 验证

| 项 | v7.9.6.1 | v7.9.6.2 |
|---|---|---|
| 单元测试（Windows） | 512 × {net10.0, net11.0} | **512 × {net10.0, net11.0}**（断言改写，数量不变） |
| 单元测试（Linux cia1） | 511 = 510 + 1 跳过 | **511 = 510 + 1 跳过** × 双 TFM |
| e2e 收集 | 194 = 192 + 2 跳过 | **195 = 193 + 2 跳过**，两平台一致（Windows 425s+141s，cia1 316s+236s） |

新增 e2e `test_an_explicit_flag_still_beats_a_site_preset`；`test_a_site_preset_only_
reshapes_the_second_pass` 改为 `test_a_site_preset_reshapes_both_passes`（断言直播
产物是 352x264，同时显式 `--encoder` 仍然优先）。

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
