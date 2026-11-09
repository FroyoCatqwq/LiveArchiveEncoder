# v8 交接说明（v7 冻结基线）

[English](v8-handover.en.md) · **中文**

基准版本：**v7.9.9.5**（2026-11-09）。

这份文档是 v7 与 v8 之间的交接点。它不重复架构说明（见
[`architecture.md`](architecture.md)），只回答四件事：**v7 现在冻结在什么状态、
哪些行为必须原样继承、哪些问题明确留给 v8、重写怎样才算验收通过**。

---

## 1. 状态

| | v7.9（当前） | v8（计划） |
| --- | --- | --- |
| 语言 / 运行时 | C# / .NET 10 + .NET 11（JIT、NativeAOT 三条发布形态） | Rust，彻底重写 |
| 目标 | bugfix / hotfix / 小功能 | 结构性重构 |
| 行为基线 | v6.3.3 Python 参考实现 + 本项目 e2e | 必须逐项与 v7.9.9.5 对齐 |
| 结构性改动 | **不再接受** | 全部在这里做 |

v7.9.9.5 做了一次全项目审计（83 个产品源文件、约 29.5k 行；52 个单测文件；
23 个 e2e 文件），修复了进程生命周期、取消、并发、三态设置与不可信输入方面的
遗留问题，并补了回归测试。完整清单见
[`CHANGELOG.md`](CHANGELOG.md) 的 v7.9.9.5 条目。

**为什么现在冻结**：v7 的很多约束（`-threads` 的位置、实时节拍、码控口径、
巨型 UI 类、全局状态）是 v6.3.3 等价性的一部分。在 v7 里改它们会让"默认输出
与参考实现一致"这个契约失去依据；在 v8 里改则必须先有黄金基准。

---

## 2. 冻结的行为契约（v8 必须继承）

以下每一条都有代码或 e2e 断言支撑。改任何一条都等于改产品定义，必须在基准里
显式登记，不能"顺手优化"。

### 2.1 时间与帧

1. 实时模式：捕获端每 `1/fps` 出一帧，输出端每 `1/fps` 消费一个槽；落后时
   **重置基准，不追赶**；总墙钟时间 ≈ 素材时长。
2. 降帧 = **重复上一帧**，不是丢帧；输出容器帧率恒为 `--fps`。
3. 三个计数 `slots` / `fresh` / `repeated` 进入结果报告，分片模式取各片之和。
4. 自适应阶梯：0 号梯级为基准帧率、`DropAbove=∞`；迟滞 0.18 / 0.30 / 0.75 /
   0.80 s；阈值倍率 1.25 / 1.10；`cadence += effectiveFps / baseFps`。
5. 码率观测：`-progress` 的 `total_size` / `out_time_us`；样本按**墙钟**时间淘汰，
   速率分母优先用**媒体时间**（见 §4.1，这是已知的口径不一致，v8 才能改）。
6. `--no-realtime` 把阶梯钉死一档、关闭两处节拍、捕获线程与槽循环一帧一槽。

### 2.2 命令与产物

7. **argv 顺序就是契约**：`-threads` 在裸视频输入之前；`-f rawvideo` →
   `pipe:0`；`-progress pipe:2`；`-flush_packets 1`；`-f MUXER -y <part>`。
   浮点用 `G6`（编码器参数）或 `R`（设置往返），全部 `InvariantCulture`。
8. 输出事务：写 `clip.<pid><rand>.part.<ext>`，成功后 `Promote`；目标被占用时
   重试 4 次再落 `name (2).ext`；已完成但改名失败时 `Keep` 并打印路径。
9. 二次压缩链：`_web` / `_web2` / `_web3`，每跳读上一跳产物，失败不碰实时产物。
10. 扩展名纠正、容器反推、SMB 网络源自动输出目录、URL 文件名清洗。
11. 退出码：`0` 成功 / `1` 运行失败 / `2` 用法错误 / `3` 有更新 /
    `130` 取消；`ERROR:`、`WARNING:` 前缀是 e2e 断言的一部分。
12. `--help` 文本、CUI 菜单编号与可打印字符串（en/zh 两张表键集合一致）。

### 2.3 配置与分发

13. `config.json` v6 可直接读；未知字段忽略；CLI 写回只改本次被告知的字段
    （`--no-realtime` 是 per-run，`--save-config` 是显式快照）。
14. "未设置 / 空字符串 / 有值" 三态：`GhProxy`、`H264Impl` 必须保留。
15. 预设是兜底不是覆盖；32 条内置 `site/year` 与 `generic/year` 行、旧 id 别名、
    用户 `CustomEras` 覆盖规则。
16. 发布四包：`LiveArchiveEncoder-v<版本>-win-x64.zip`、
    `-win-x64-self-contained.zip`、`-linux-x64.tar.gz`、
    `-linux-x64-self-contained.tar.gz`；每包 SHA-256；
    `self-contained` 后缀不可被自更新的 bare 后缀匹配到。
17. 自更新按 `AssetSuffix()` 三档（bare / `-self-contained` / `-aot`）选资产，
    只接受 https（loopback http 供测试）、校验 `sha256:`、运行新二进制的
    `--version`、两阶段替换、永不降级。

---

## 3. 本次审计做了什么

| 阶段 | 内容 | 结果 |
| --- | --- | --- |
| 静态审查 | 两个独立只读审查（编码核心/管道、UI/CLI/配置/更新/网络），逐条要求文件行号与触发条件 | 45 条可证实问题 + 6 条待确认 |
| 运行检查 | `--version` / `--help` / `--cpu-info`、无输入与互斥开关、代理位置参数、四种 shell 补全、CUI 进入退出、真实 direct 编码（901 294 B 产物）、`--save-config` → `--emit-args` 往返 | 全部符合预期 |
| 单元测试 | TUnit，双 TFM（net10.0 / net11.0） | 734 / 734，0 警告 |
| e2e | `tools/e2e.py` 两阶段（timing / shared_config 串行，其余 xdist） | 242 passed / 2 skipped |
| 门禁 | `tools/check-slnx.py`：清单漂移 + 双语成对 | 通过 |

修复重点（详见 CHANGELOG）：**取消令牌贯通到所有短命子进程**、**子进程退出后管道
读取有界**、**能力档案并发保护**、**三态设置不再被压平**、**不可信 feed / URL 名
落地前清洗**、**临时目录与 shell 启动文件的原子写**。

---

## 4. 明确留给 v8 的问题

这些在 v7.9.9.5 里**没有改**。原因分三类：会破坏等价性、需要结构性改动、或
缺少可验证的判定依据。重写时必须逐条给出"改 / 不改"的结论。

### 4.1 会改变输出字节的

| 项 | 现状 | 为什么留给 v8 |
| --- | --- | --- |
| `OutputMonitor` 口径 | 样本按墙钟淘汰，速率分母用媒体时间；首个无媒体增量的样本仍计入字节 | 改了会改变阶梯降档时机，进而改变帧选择与产物。v7 的黄金基准不存在，改了无法证明"比原来对" |
| `-threads` 位置 | 在裸视频输入之前，与 v6.3.3 相同 | FFmpeg 作用域使它可能不作用于输出编码器；要先按编码器做基准 |
| `--audio-mode separate` | 不比 inline 快，且会改变选帧 | 是 v6.3.3 语义的一部分；是否保留为一种模式应由 v8 重新定义 |
| intra-only 量化器标定 | 实测基线 ×1.15、超过上限 3 倍才启用阶梯 | 常数来自一次测量；v8 应把它变成有基准的实验 |

### 4.2 需要结构性改动的

| 项 | 现状 | v8 应做 |
| --- | --- | --- |
| CUI 与 CLI 编码选项 | 无 input 且带编码选项时**明确报错**（v7.9.9.5 起），因为 CUI 从 `config.json` 重建状态、看不到 `CliOptions` | 让两条前端共享一个 `Plan` 构造器，CUI 能真正接受 CLI 预置值 |
| 巨型文件 | `Cui.cs` 3641、`RealtimeEncoder.cs` 1562、`CliOptions.cs` 1561、`Cli.cs` 1309 行 | 按 crate / 模块拆，编码主循环变成可单测的状态机 |
| 全局可变状态 | `EraPresets.CustomRows`、`BuildProfileStore.Memory`、`L` 当前语言、`AppLog` 单例、高精度定时器静态计数 | 全部改为显式传入的上下文，允许一个进程内多份配置 |
| 四套模型 | `CliOptions` → `Config` → `RealtimeOptions` → `UserConfig`，优先级规则散落在几十个 `?:` | 一个 `Plan` + 一个 `resolve` 函数 |
| 异常即控制流 | `InvalidOperationException` 同时表示 FFmpeg 失败、配置冲突、子进程异常；退出码只有 5 档 | `thiserror` 类型化错误 + 稳定退出码映射 + `--json` NDJSON 事件流 |
| 无端口抽象 | `Execute` 直接 `Process.Start`，主循环只能靠真实 e2e 覆盖 | `trait ProcessRunner / FrameSource / Clock`，fake 实现可单测 |
| 配置迁移 | 只加了一次性 `LadderMigrated` 标记，没有显式迁移链 | `schema_version` + `migrate_v5_to_v6()` + 黄金文件测试 |
| 并行配额 | `Parallel.For` 一次拉起 N 个 shard（每个 2 个 FFmpeg + 1 捕获线程），无背压 | 共享 `Semaphore`，按 CPU/IO 预算限流，实时模式检测负载并降级 |
| 观察性 | 只有文本日志与 stderr 采样，无 metrics/tracing | `tracing` span（run/slot/shard）+ 结构化指标，支撑长期回归 |
| 本地化 | `Dictionary` + `{0}`，663 处 `Console.*` 直写 | 统一终端层（宽度/颜色/复数/机器可读输出） |

### 4.3 缺少验证依据的

| 项 | 说明 |
| --- | --- |
| 7-Zip NSIS `/D="dir"` | 官方 FAQ 与 NSIS 文档对引号的要求互相矛盾，本机没有可复现的失败；v8 用 crate 解压替代安装器即可绕开 |
| `Toolchain.CopyInto` | 逐文件 `File.Copy` 非事务，中途失败会留下新旧混合的 `bin/`；v8 应先解到临时目录再整体切换 |
| `Probe.Source` 选第一条 video 流 | 若第一条是 attached_pic 且无 `width`，可能抛 `KeyNotFoundException`；需要构造素材确认 |
| `ShardPlanner` 浮点尾片 | 提前 `break` 时把尾片 `End` 钉到 `rangeEnd` 但 `Slots` 不变；需要浮点边界用例 |
| 自更新的信任模型 | 只校验 release JSON 的 `sha256`，无代码签名、无自动回滚、无 `.old`；企业分发场景要重新设计 |
| Linux NativeAOT / glibc | AOT 产物绑定构建机 glibc；v8 的静态链接与 FFmpeg 动态链接如何组合需要单独设计 |
| 预发布版本比较 | `VersionTag.Parse` 有意丢弃 `-rc1` / `+build` 后缀（单测固定此行为），v7.9.9.5 只在 `Install` 层拒绝安装；v8 应实现真正的 SemVer 预发布优先级 |

---

## 5. 重写的前置条件：黄金基准

**没有这套基准，v8 无法验收。** 建议在写第一个 crate 之前完成：

1. **argv 快照**：用 v7.9.9.5 录制每个编码器 × 预设 × 选项组合的
   `decCmd` / `encCmd` / `preflight` / `recompress` 命令字（含顺序与浮点文本）。
   建议以 `insta` 或 JSON 快照保存，作为 v8 的第一组测试。
2. **产物指纹**：容器结构（`ffprobe` 的 stream/format 字段）、时长、视频包数、
   关键帧数、像素格式、平均码率区间、`fresh`/`repeated`/`slots`。
   不做字节比对（编码器版本会变），只比结构与统计。
3. **CLI 契约快照**：`--help` 全文、退出码表、`ERROR:` / `WARNING:` 前缀、
   `--emit-args` 往返、四种 shell 的补全脚本。
4. **配置快照**：v6 `config.json` 的读入/写回、三态字段、旧 id 别名、损坏文件
   行为。
5. **并行期**：v7 与 v8 在同一套 e2e 上跑，差异必须逐条解释；不允许用
   "新实现更好"跳过差异。

`architecture.md` §8 给出了 crate 划分与 C#→Rust 机制映射，可直接作为起点，但
以上基准必须先于它。

---

## 6. 验收标准（建议）

* 黄金基准 100% 一致，或每一处差异都有变更记录与理由。
* 单测覆盖编码主循环（v7 做不到，v8 必须做到），e2e 仍全绿。
* 两种模式（Realtime / Batch）各自有独立基准，不再靠 `--no-realtime` 补丁。
* 取消链路有自动化测试：Ctrl+C / 超时 / 子进程挂死 / 管道不关闭四种场景，
  验证进程树、临时目录、`.part` 文件与退出码。
* 发布四包的命名、SHA-256、`AssetSuffix()` 匹配与 `--update-check` 全链路验证。
* 文档：v8 自己的架构说明 + 与本文件的差异表。

---

## 7. 附：本次新增/修改的关键文件

| 用途 | 文件 |
| --- | --- |
| 全局启动、退出码、更新互斥、无输入拒绝 | `src/LiveArchiveEncoder/Program.cs` |
| 子进程取消与管道收尾 | `FFmpeg/ProcessRunner.cs` |
| 编码主循环、分片、字幕计划所有权 | `Encoding/RealtimeEncoder.cs`、`ParallelEncoder.cs`、`ShardPlanner.cs` |
| 能力档案并发 | `FFmpeg/BuildProfileStore.cs` |
| 设置三态与快照 | `Configuration/UserConfig.cs`、`UI/Cli.cs`、`UI/CliArgs.cs`、`UI/CliOptions.cs` |
| 补全单一事实源 | `UI/OptionCatalog.cs`、`UI/Completion.cs` |
| 更新安全 | `Update/SelfUpdate.cs`、`Update/UpdateState.cs` |
| 工具链与 shell 配置 | `FFmpeg/Toolchain.cs`、`ToolchainPath.cs`、`SevenZip.cs` |
| 回归测试 | `tests/LiveArchiveEncoder.Tests/V7995Tests.cs`、`tests/e2e/test_realmedia.py` |

规程与发布流程见 [`MAINTENANCE.md`](MAINTENANCE.md)；完整修复记录见
[`CHANGELOG.md`](CHANGELOG.md)；架构与不足点见 [`architecture.md`](architecture.md)。
