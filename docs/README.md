# 文档索引

**中文** · [English](README.en.md)

每份文档都有中英两版：`X.md` 是中文，`X.en.md` 是英文。每个文件第一行是语言切换。
历史型文档（`CHANGELOG*`）以中文为唯一权威全文，英文版是逐版摘要 —— 见
[语言约定](#语言约定)。

| 文档 | 内容 |
| --- | --- |
| [`usage.md`](usage.md) | 完整用户手册：CLI、CUI、编码器表、码控、并行、配置、补全、远程源、FFmpeg 工具链、播放、二次压缩链、时代预设、字幕、像素格式、自更新、排错、自测 |
| [`requirements.md`](requirements.md) | 操作系统 / CPU / 内存 / 磁盘 / 字体 / 权限 / 网络、实测环境、性能预期、不支持的做法 |
| [`era-presets.md`](era-presets.md) | 32 个预设（5 条 `generic/年份` + 13 个站点的 27 条 `site/year`）、证据分级（A/B/C）、q:v 与 CRF 标定、降档/升档窗口、旧 id 别名 |
| [`toolchain-builds.md`](toolchain-builds.md) | 六个 FFmpeg 来源（五个可自动下载）加统一 7-Zip、两端实测的编解码器差异 |
| [`architecture.md`](architecture.md) | 技术说明：架构与进程模型、核心机制（节拍/帧池/阶梯/码控/分片/后处理）、突出点与不足、v8（Rust）迁移设计 |
| [`v8-handover.md`](v8-handover.md) | v7 冻结基线：必须继承的行为契约、v7.9.9.5 / v7.9.9.6 审计结果、留给 v8 的取舍与黄金基准验收条件 |
| [`MAINTENANCE.md`](MAINTENANCE.md) | 目录职责、常用命令、改动后检查清单、文档命名与链接契约、发布流程、日志与诊断 |
| [`capability-matrix.md`](capability-matrix.md) | 容器 × 帧率 × 像素格式实测矩阵（v7.9.6 快照，已归档） |
| [`test-report-v7.md`](test-report-v7.md) | v7 移植验收测量报告（已归档，中文摘要） |
| [`CHANGELOG.md`](CHANGELOG.md) | v7.9.8 起逐版完整变更记录（**权威全文**） |
| [`CHANGELOG-archive.md`](CHANGELOG-archive.md) | v6.2.2 → v7.9.7 的完整变更记录（**权威全文**） |
| [`../archive/README.md`](../archive/README.md) | 退役代码的归档规则与当前清单 |

根目录的入口页：[`../README.md`](../README.md)（英文）· [`../README.zh.md`](../README.zh.md)（中文）。

## 语言约定

* **命名**：`X.md` 是中文，`X.en.md` 是英文。英文版里的相对链接一律指向 `*.en.md`，
  中文版保持原样 —— 否则读者点一次就掉回另一种语言。
* **切换**：每个文件第一行是 `[English](X.en.md) · **中文**`（或按该文件的主语言排列）。
* **历史型文档**：`CHANGELOG.md` / `CHANGELOG-archive.md` 的中文版是唯一权威全文，
  历史条目按原样保留、不重写；英文版给出逐版摘要与 `⚠ behaviour change` 标记。
  需要细节时从摘要里的链接回到中文原文。
* **归档型文档**（`capability-matrix`、`test-report-v7`）在开头显式标注 `> 已归档`，
  避免读者把历史读数当成现行规范。
* **例外**：根目录 `README.md` 以英文为主（它是仓库首页），中文版是 `README.zh.md`；
  其余所有文档都是 `X.md` / `X.en.md`。
* **校验**：`tools/check-slnx.py` 检查 `docs/*.md` 与 `docs/*.en.md` 成对存在
  （以及 `archive/README.md` 与根 `README.md`），只翻一半会让 CI 失败。
