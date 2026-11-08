# v7 移植验收测量报告（中文摘要）

[English](test-report-v7.en.md) · **中文**

> **已归档。** 英文版 [`test-report-v7.en.md`](test-report-v7.en.md) 是当时的**现场记录**，
> 逐条保留原样（含当时的脚本名与代理故障），不重写、不翻译成中文正文。本页是中文
> 结论摘要，需要细节时点上面的英文原文。
>
> 报告中提到的 `compare_matrix.py`、`benchmark.ps1`、`tests/build.ps1` 等一次性脚本
> 已从仓库移除；现行的构建与发布命令见 [`MAINTENANCE.md`](MAINTENANCE.md)。

## 结论

| 检查项 | JIT 构建 | NativeAOT 构建 |
| --- | --- | --- |
| 12 例媒体矩阵（`compare_matrix.py`） | 12 / 12 | 12 / 12 |
| 11 例错误路径（`error_paths.py`） | 11 / 11 | 11 / 11 |

两项都以 `reference/live_archive_encoder.py` v6.3.3 为基准；把自适应状态机从链路里
拿掉后，两边的帧计数**逐槽一致**；CLI/CUI 语义一致。

## 逐节要点

**1. 构建状态**：JIT 与 AOT 都能出片；当时的 `LNK1181: cannot open input file
'advapi32.lib'` 通过显式提供 Windows SDK 导入库目录解决。

**2. 功能等价（12 例）**：同素材、同参数、两边各编一遍，ffprobe 比对
codec / 宽高 / pix_fmt / 帧率 / 时长 / 音频编码 / 流数 / 容器。**12/12 全等**，
包括 h261 吸附到 176x144、mpeg1video 吸附到 48000/1001、roqvideo 选
`roq_dpcm` 22.05 kHz、h261 只出视频流。

**帧选择**：9/12 例的 fresh/repeated 计数完全相同（libx264 `61/4`、mjpeg `24/41`）；
其余 3 例差 1–9 槽 —— 这是自适应控制器对「实测输出字节」的反应，不是移植缺陷。
同一份 Python 实现连跑三次本身就能差到 `37 / 67 / 64`。把控制器关掉
（`--limit 99999`）后两边都是 `fresh=64 repeated=1`。

**3. 真实素材**：`HK-MCT1-PART-encoded.avi`（320x240 h264+aac，377 s）取 60 s，
逐字段比对通过。

**4. 错误路径（11 例）**：非法参数、不存在的输入、容器不兼容、只读目标等，
两边的退出码与文案语义一致。

**5. CUI 冒烟**：菜单走查与按键路径一致。

**6. 性能**（20 s 素材、rv20、默认参数）：

| 运行 | 墙钟 | 控制器 CPU | 峰值内存 |
| --- | --- | --- | --- |
| Python v6.3.3 | 21.649 s | 0.188 s | 19.4 MB |
| C# v7（首版，自旋尾部） | 21.486 s | **1.406 s** | 65.9 MB |
| C# v7（睡眠定时器） | 21.523 s | 0.766 s | 65.7 MB |
| C# v7（运行时裁剪版） | 21.348 s | 0.641 s | 65.6 MB |

读法：**墙钟几乎不变**（受实时节流约束，不归控制器管）；**控制器 CPU 是固定启动
成本**（5 s 素材 0.656 s、20 s 素材 0.48–0.77 s，不随槽数增长），比 Python 多出的
~0.45 s 是 JIT + 运行时初始化；首版把每个槽的亚毫秒尾巴自旋掉、烧满一个核，
改成睡到截止点后降到 ~0.65 s 且输出节拍无可测变化；**内存更高**（~61 MB vs
~15 MB）是 .NET 运行时地板，帧池本身只有 8 × 72 KB。

**当时做不到的**：ReadyToRun / NativeAOT 需要 NuGet 运行时包，而 restore 在本地代理
后面失败（`NU1301: proxy tunnel request ... 502`），只应用了离线开关
（`InvariantGlobalization` / `UseSystemResourceKeys` / `MetadataUpdaterSupport=false` /
`HttpActivityPropagationSupport=false` / `DebuggerSupport=false` / `System.GC.Conserve=7`），
没有可测收益 —— 与「成本就是 JIT 本身」一致。代理修好后 AOT 重试成功（§8）。

**7. 分离音频管线**（`--audio-mode separate`）：视频单独编进暂存文件、另一个 FFmpeg
编音频、再 `-c copy` 合流。结论是**更慢且更不忠实**（多一次 remux、时间戳会漂），
所以**不做默认**，默认仍是 v6.3.3 的 `inline`。它保留的价值是给「老式 muxer 不接受
音轨」的情况兜底，并内置 3 帧预演 + 输出解码校验，失败自动回退单进程。

**8. NativeAOT**：出单片可执行文件、进程启动快约 50 ms；不改变控制器 CPU 总成本
（那部分分散在整次运行里，不是集中在启动）。AOT 二进制跑完了完整矩阵与错误路径。

**9. SIMD**：没有可开关的东西。编码发生在 FFmpeg 子进程里，由它运行时分发；
C# 控制器不接触像素，没有可向量化的循环。

**10. 修复记录**：`中断一次之后再也编不动了` —— v7 早期 Ctrl+C 后静态状态没复位，
第二次运行直接退出；现已改为每次运行重建取消源。

## 当时新增的三项（默认都不改变行为）

- **NativeAOT**：可用，全矩阵与错误路径全过。
- **`--audio-mode separate`**：可用但非默认（更慢、输出会偏）。
- **`--help` / `--cpu-info`**：补齐与 argparse 的最后差距。

## 与今天的关系

这份报告证明的是「默认参数下与 v6.3.3 等价」。之后的 v7.6–v7.9 新增了时代预设、
二次压缩、字幕、自更新等**默认关闭**的能力，等价性结论不变；现行行为的权威说明在
[`usage.md`](usage.md)。
