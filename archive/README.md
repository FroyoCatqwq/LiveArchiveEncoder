# 归档（archive）

[English](README.en.md) · **中文**

退役的代码放在这里，而不是删掉。

这个目录下的任何东西都**不属于**任何项目：没有 `.csproj` 会 glob 它，所以这里的东西
不参与编译、裁剪或发布。它的存在是为了让某个决定在以后还能带着当时的上下文被重新
读到。

## 什么该放进来

* 已经没有调用点、但意图值得保留的成员（一张表、一条规则、一个对着 FFmpeg 验证过的
  公式）。
* 被更好的实现取代、但「为什么替代者等价」值得留档的旧实现。

## 什么不该放进来

* 活代码里的死分支 —— 那些就地删除。
* 仍被 `src/`、`tests/` 或 `docs/` 引用的东西。
* 构建产物、测试素材或日志文件。

## 当前内容

| 文件 | 退役版本 | 原因 |
| --- | --- | --- |
| `retired/RetiredConstants.cs` | v7.8.1 | 无引用的查表与辅助：`Constants.DefaultFpsLevels`、`Constants.BtbnGplReleaseUrl`、`Constants.LegacyEncoderPatterns`（已由一个 source-generated 正则取代）、`EncoderMath.FallbackTransportFormat()`、`AudioCatalog.Names` / `IsKnown` / `KnownFor`、`CliOptions.EffectiveRecover`。 |
| `retired/RetiredTurbo.cs` | v7.9 | Turbo 模式：`RealtimeOptions.Turbo`、三个槽循环分支加 `FrameBufferPool.WaitForFrame`/`Close`、`--turbo` 的 CLI 接线与两种语言各七个本地化键。弃用两个版本后直接移除 —— 它和本工具存在的意义（模拟当年的直播录制）相冲突。 |
| `retired/RetiredHelpers.cs` | v7.9.3.1 | 全量声明/引用扫描找到的无引用重复辅助：`Probe.HasAudio` 与 `Probe.Stream`（都被单次运行的 `Probe.Source` 取代）、`SubtitleTrack.FormatMode`、传输格式偏好表的最后一份副本，以及 v7.7 的布尔「裁 4:3」滤镜链。 |
| `retired/RetiredWatermark.cs` | v7.9.1 | 静态文字水印阶段（`WatermarkSettings`、参数构建与菜单文案）。按用户要求移除；二次压缩成为唯一后处理阶段。 |
| `retired/RetiredFontPicking.cs` | v7.9.1 | drawtext 时代的字体选择器（`Find` / `PickFrom` / `SeedFromSystemFonts`、`ToFilterPath`、`InstallHint`）。水印是它唯一的调用方；字幕字体现在来自 `SubtitleFonts`。 |

每一条都带着「在哪个版本退役」与「被什么取代」，所以恢复它是复制粘贴，而不是考古。
