# 项目维护指南

写给继续维护 v7 的人：目录怎么分、命令怎么跑、改动后必须做哪些检查、发布怎么发。
面向使用者的说明在 [`usage.md`](usage.md)，版本历史在
[`CHANGELOG.md`](CHANGELOG.md) / [`CHANGELOG-archive.md`](CHANGELOG-archive.md)。

## 1. 目录职责

| 路径 | 放什么 | 不要放什么 |
| --- | --- | --- |
| `src/LiveArchiveEncoder/` | 全部产品代码，按 `Encoding` / `FFmpeg` / `UI` / `Configuration` / `Core` / `PostProcess` / `Update` / `Remote` / `RateControl` / `Frames` / `Localization` 分目录 | 生成物、一次性脚本 |
| `tests/LiveArchiveEncoder.Tests/` | TUnit 单元测试（纯逻辑、可离线） | 需要真实 FFmpeg 或计时的用例 |
| `tests/e2e/` | pytest 端到端（真实编码器、真实文件、CUI 交互） | 重复单元测试覆盖的纯函数 |
| `tools/` | 构建、e2e 编排、基准、清单校验脚本 | 产品逻辑 |
| `docs/` | 现行规范与历史归档 | 临时分析笔记 |
| `reference/` | v6.3.3 Python 参考实现（**逐字保留**，只用于对比） | 任何改写 |
| `archive/` | 已退役代码，明确标注 | 仍在使用的代码 |
| `publish/`、各 `bin/` `obj/` `TestResults/` | 生成物，`.gitignore` 已排除 | 提交到仓库 |

`LiveArchiveEncoder.V7.slnx` 的 Solution Items 是 IDE 清单，**只包含文件列表和两个
`.csproj` 引用**，不承载编译设置。新增文件后必须重新生成并校验，见 §3。

## 2. 常用命令

```bash
dotnet build LiveArchiveEncoder.V7.slnx -c Release
dotnet test --solution LiveArchiveEncoder.V7.slnx -c Release --no-build
dotnet build LiveArchiveEncoder.V7.slnx -c Release -p:LaeTfm=net11.0   # 双 TFM 验证
tools/build.ps1            # Windows: 生成 publish/jit 可执行文件
bash tools/build.sh        # Linux: 同上
python tools/check-slnx.py # 校验 IDE 清单没有漂移
python tools/e2e.py        # 全量 e2e（分两阶段，见 §4）
```

`--no-build` 前必须先 `build`；新增测试文件后尤其别忘了。目标框架由
`Directory.Build.props` 的 `LaeTfm` 控制（默认 `net10.0`），应用设了
`RollForward=Major`，所以一个 framework-dependent 产物在只装 .NET 11 的机器上也能启动。
`global.json` 只声明 MTP 测试运行器，**不锁定 SDK 版本**。

## 3. 改动后的检查清单

1. `dotnet build` 与 `dotnet test`（Windows + Linux 两端各一次）。
2. `python tools/check-slnx.py` —— 新增/删除任何 `.cs` / `.py` / `.md` / 脚本后，
   重新生成 `.slnx` 的 Solution Items 再校验，避免清单静默漂移。
3. 触及编码参数、预设数值或工具链 id 时，同步更新对应的硬编码单测断言和 e2e 期望值。
4. 触及本地化时，`Strings.cs` 的 en/zh 键集合与占位符数量必须一致（有单测校验）。
5. 触及 CLI 选项时，`CliOptions`（局部变量 / switch / 构造 / `Usage()`）与
   `OptionCatalog.All` 必须同步（有双向校验单测）。
6. 触及配置模型时，确认旧 `config.json` 仍能加载、`ConfigStore.Capture/Apply` 与
   `CliArgs.FromUserConfig` 的往返不丢字段。
7. 触及自更新时，用 `--update-check` 验证；产物命名必须与 `AssetSuffix()` 的三档
   （`-aot` / `-self-contained` / 无后缀）精确一致。

## 4. 测试分层与配置隔离

* **单元测试**：不依赖 FFmpeg、不依赖真实时钟，任何平台都应全绿。
* **e2e**：`tools/e2e.py` 分两阶段——`timing` 与 `shared_config` 两组**串行**，
  其余用 `pytest -n`。`tests/e2e/conftest.py` 里的防护栏会在 `-n` 与排他组同时出现时报错。
* **配置与字体目录全程隔离**：`harness.py` 把 `LAE_CONFIG_DIR` 指到套件私有目录，
  字体用 `LAE_FONT_DIR`。**任何测试都不得写开发者真实的 `%LOCALAPPDATA%` /
  `~/.local/share` 下的 `config.json`**。
* 需要真实 FFmpeg 的用例，Linux CI 靠 `apt install ffmpeg xz-utils`；完整 legacy
  编码器扫描仍在本机 / 双平台手动跑，因为发行版 FFmpeg 不带这些编码器。

## 5. 文档拆分约定

| 文件 | 范围 |
| --- | --- |
| `docs/CHANGELOG.md` | **v7.9.8 起**的现行版本历史。新条目加在标题块之后、第一个 `## v…` 之前 |
| `docs/CHANGELOG-archive.md` | **v7.9.7 及更早**（含 v6.2.2 → v6.3.3 移植基线）。只归档，不改写 |
| `docs/usage.md` | 现行功能说明；带版本前缀的段落是当时的说明，改行为时要同步 |
| `docs/era-presets.md` | 时代预设的史料依据、素材可信度分级与更正清单 |
| `docs/toolchain-builds.md` | FFmpeg / 7-Zip 构建来源对照与两端实测编解码器 |
| `docs/requirements.md` | 系统与硬件要求 |

拆分的判据是**版本号**，不是日期或行数。历史条目里的数字、链接与结论按原样保留，
阅读时注意其时代背景；需要纠正的是现行文档和代码注释。

## 6. 发布流程（每次改动后必做）

1. `Constants.Version` 改成目标版本号；`docs/CHANGELOG.md` 加条目。
2. 两端构建 + 测试（§2、§3）。
3. 生成 **4 个 JIT 包**——Windows / Linux × (framework-dependent / self-contained)：

   ```
   LiveArchiveEncoder-v<版本>-win-x64.zip
   LiveArchiveEncoder-v<版本>-win-x64-self-contained.zip
   LiveArchiveEncoder-v<版本>-linux-x64.tar.gz
   LiveArchiveEncoder-v<版本>-linux-x64-self-contained.tar.gz
   ```

   命名必须与 `SelfUpdate.AssetSuffix()` 完全一致，否则用户会被更新到错误的运行时形态。
4. `publish/release/notes-<版本>.md` 写说明，正文附四个包的 SHA-256。
5. `git add -A` → commit → `git -c http.proxy=http://127.0.0.1:10808 push origin main`
   （直连常被重置；用临时参数，不改 git 配置）。
6. `gh release create v<版本> <四个包> --notes-file … --target main`。
7. 验证：四个资产的 GitHub `digest` 与本地 SHA-256 一致、正式版、`--update-check`
   返回 0、tag 已同步、工作区干净。
8. **还原开发者真实配置文件**（若手动跑过未隔离的程序），并核对 hash 与开工前备份一致。
9. 清理工作目录里的一次性脚本与打包产物。

CI（`.github/workflows/ci.yml`）只做双平台 build + unit tests + 清单校验 +
net11 目标验证；e2e 因需要实时时钟与 legacy 编码器，不进 CI。
