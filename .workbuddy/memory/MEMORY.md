# 项目长期记忆

## 项目定位
live_archive_encoder：把现代视频转码成 RV20 / RoQ / MPEG-1 等**老式编码器**格式的
"直播录档模拟"工具。刻意按实时节奏运行（capture 线程按源帧率取帧，主循环按
1/fps 输出 slot），并带自适应码率控制（超 limit 就降 motion FPS）。
后处理（丢帧/损坏）由同级的 frame_damage.py 负责，不在本项目范围内。

## 版本
- v6.3.3 = `live_archive_encoder.py`（Python 3.13，权威参考实现）
- v7 = `src/LiveArchiveEncoder`（.NET 10 / C# 14），功能对等的移植版

## 迁移红线（改 v7 时必须同时改 v6.3.3 才允许的行为变更）
- FFmpeg 命令行参数顺序（包括 `-threads` 放在 rawvideo 输入之前）
- 自适应 FPS 状态机阈值：0.18 / 0.30 / 0.75 / 0.80 秒，ratio 1.00/1.25/1.60
- 选帧规则（cadence 累加、previous 帧复用、首帧必收）
- 滚动码率统计（window_bytes/window_media_us 增量维护，不重扫窗口）
- 所有默认值和常量表（Configuration/Constants.cs 是 v6.3.3 的逐项复制）

## 环境
- NuGet 代理 127.0.0.1:1418 会 502；可用的是 **127.0.0.1:10808**（已写进
  nuget.config）。有了代理后 R2O/AOT 都能装（ILCompiler 10.0.13 已缓存）。
- **AOT 链接失败的根因**：ILCompiler 的 `findvcvarsall.bat` → `vcvarsall.bat`
  取 `%LIB%`，而 vcvarsall 的 Windows SDK 探测走 reg.exe。构建沙箱里 reg.exe
  被拦截，LIB 就只剩 MSVC 两个目录 → `LNK1181: cannot open 'advapi32.lib'`。
  解法：用 `AdditionalNativeLibraryDirectories` 显式补
  `Windows Kits\10\Lib\<ver>\um\x64` 和 `ucrt\x64`（csproj 里可覆盖）。
- NativeAOT **默认按 SSE2 基线编译**（`Vector<byte>.Count=16`、
  `Avx2.IsSupported=False`）。要提升：`-p:IlcInstructionSet=avx2`，代价是挑 CPU。
- PowerShell 5.1 跑在 .NET Framework 上，`ProcessStartInfo.ArgumentList` 不存在，
  脚本里只能用 `.Arguments` 字符串。
- 自带 ffmpeg（BtbN GPL）在 PATH，35 个 legacy 编码器可用。

## 测试
- `python tests/compare_matrix.py` — 12 个编码器/参数矩阵，对比 ffprobe 结构字段
- `python tests/error_paths.py` — 11 个错误路径，对比退出码
- `python tests/feature_sweep.py` — 全部 35 个 legacy 编码器各跑 2 秒片段
- `python tests/option_smoke.py` — 18 条选项通路（turbo/parallel/audio-mode/deint/…）
- `python tests/cui_smoke.py` — 20 项交互界面断言（语言/持久化/容错）
- 全部支持 `CS_EXE=<path>` 环境变量切换被测 exe（用来验 AOT 产物）
- `python tests/ab_duration.py` — inline vs separate 的 duration 隔离实验
- `tests/build.ps1 [-Aot [-AotAvx2]]` — 构建 jit / aot / aot-avx2
- 判定标准：结构字段必须完全一致；fresh/repeated 允许抖动，但用
  `--limit 99999` 关掉节流后必须完全一致（这是帧管线等价的判据）
- **compare_matrix 依赖实时节流下的帧选择，跑的时候别并行跑别的重活**
- **任何改动都要三套产物各跑一遍**：NativeAOT 下反射型 API 会直接崩
  （`System.Text.Json` 反射序列化 → `JsonSerializerIsReflectionDisabled`），
  JIT 上却完全正常。只验 JIT 必漏。

### 全编码器扫描的三个"假失败"（别再查第二遍）
- **a64 / a64multi5**：FFmpeg 只有 muxer 没有 demuxer，ffprobe 必然报
  "Invalid data found when processing input" → NOPROBE，产物其实写好了。
- **amv / h263 / roq / h261**：容器不写时长。用纯 ffmpeg 生成的对照文件同样
  没有时长 → 容器固有行为。判断要按**扩展名**而不是 format_name
  （ffprobe 会把 amv 认成 avi）。

## 取消/中断（踩过的坑，改这块务必注意）
- 取消令牌必须**按操作发放**。`Interrupt.BeginOperation()` 会在返回前清掉上一次
  的取消状态。历史教训：一个 `static readonly` 的 CTS 被 Ctrl+C 取消后不重置，
  导致 CUI 会话里之后每次编码都在循环首行秒抛 `OperationCanceledException`。
- 取消的唯一来源是 Ctrl+C（`Core/Interrupt.cs`）。`Execute()` 内部的
  `CreateLinkedTokenSource` 只用于停止 diagnostics 读线程，linked CTS 是**单向**
  的，不会反向取消父令牌——别误以为是它导致假中断。

## 性能（v7 提速，2026-10-17 实测）
- **默认慢是故意的**：输出循环 (`slotInterval=1/fps` + `WaitUntil`) 和
  **采集线程**各有一处墙钟节流，两处都得解。96% 墙钟在 sleep，
  20s 素材真实计算量只占 1.4%。`--limit` 是码率上限，**不是速度开关**。
- `--turbo`（默认关）：解节流 + `pool.WaitForFrame()` 数据驱动等待 +
  滞回时钟改媒体时间 `slots/o.Fps`。实测 20s：21.42s → 1.62s（**13×**）。
  代价：产物会变（TryTakeLatest 是墙钟竞态）、自适应码率控制基本失效。
- `--parallel N`：只给白名单 `ConcatSafeFormats=[avi,mp4,flv,asf,mpeg]`；
  **roq 拼接会整段丢第二段**（250帧→125帧），**rm 时长漂移**。按整数 slot
  分段（按秒会凑不齐）。实测目前净亏损：白名单内边际成本仅 0.06–0.26s，
  而每段要付 ~1.5s 启动开销。唯一值得分段的 roqvideo(8.3s) 恰好不能拼接。
- **turbo 之后的新瓶颈是固定开销 ~1.5s**：.NET 启动 0.34s（AOT 可压到 0.05s）
  + 能力探测 ~0.9s + ffprobe + preflight。已做：3 次 ffprobe 合并为
  `Probe.Source()`、preflight 并发、encoder 探测并发。

- **AVX2 变体对编码速度无用**（实测 AOT 1.44s vs AOT-AVX2 1.42s，噪声内）：
  编码发生在 FFmpeg 子进程，FFmpeg 自己运行时分派 SIMD。AOT 的真实收益只是
  省掉 JIT 预热的 ~0.22s（启动 0.336s → 0.268s）。
- **AOT 构建时沙箱会拦 `reg.exe`**（vcvarsall 的 SDK 探测），但 csproj 里
  `AdditionalNativeLibraryDirectories` 已显式补 um/ucrt 库路径，链接照样成功。
  看到 "PROGRAM BLOCKED BY SECURITY POLICY ... reg.exe" **不代表构建失败**，
  认 "Generating native code" + 输出目录即可。
- `tests\build.ps1 -Aot -AotAvx2` 一键出三套（jit / aot / aot-avx2）。
  NativeAOT 默认按 SSE2 基线编译（Vector=16、Avx2=False），
  `-p:IlcInstructionSet=avx2` 才抬到 Vector=32。

## 音频通路（重要结论）
`--audio-mode` 默认必须是 **inline**（= v6.3.3）。`separate` 已实现且可用，
但实测：①慢约 0.7s（视频段被墙钟钉死，音频+重封装是纯增量）；②并行的音频
进程抢 CPU，mpeg1video 的 fresh 从 56 掉到 32、duration 在 4.901/4.984 之间跳；
③wmv2 帧完全相同但 duration 从 5.046 变 5.015（`-c copy` 重封装改写 ASF
duration 字段）。音频进程降到 BelowNormal 也消除不了扰动。

## v7.3 用户可见行为（改这块前先想清楚）
- **语言优先级**：`--lang` > 配置里的 Language > 默认 en。CLI 没给 `--lang` 时
  **不**覆盖已存语言；给了就写回配置（显式偏好应当记住）。
- **CLI ↔ 配置**：CLI 每次运行都把参数**写回**配置（之后打开菜单就是上次 CLI 的
  设置），但 CLI **从不**把配置读进来当默认值——脚本参数必须可预期。
- **配置恢复的校验**：输入文件不存在→忽略、编码器在当前 FFmpeg 里找不到→忽略、
  分片数夹到 1..8、JSON 损坏→退回默认值且不阻止启动。
- **`-b:a` 只对有码率控制的编码器输出**：`EncoderMath.AudioIgnoresBitrate()`
  挡掉 `adpcm_*` / `pcm_*` / `roq_dpcm`（FFmpeg 会静默忽略，写了只会误导）。
- **SMB**：UNC → ffmpeg 加 `-rw_timeout`/`-stimeout`/`-reconnect*`（本地文件不加），
  ffprobe 只加 `-rw_timeout`（`ffprobe -h` 确认它只认这一个）。
  **版本协商由 OS 决定，应用选不了**，只能把协商结果显示出来（SMB1 给警告）。
