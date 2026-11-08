# Live Archive Encoder v7（.NET 10 / .NET 11）

[English](README.md) · **中文**

面向经典/老式 FFmpeg 视频编码器的 CUI/CLI 前端。起点是 `live_archive_encoder.py`
v6.3.3 的行为等价移植（原文逐字保留在 `reference/`），内部按更少分配、更好的管道
调度重写。**v7.6 起不再只是移植**：实时编码之上增加了「视频站二次压缩」与按年代
分档的预设，默认全部关闭 —— 不给任何参数时，产物仍与 v6.3.3 完全一致。

## 文档

每份文档都有中英两版，索引见 **[`docs/README.md`](docs/README.md)**。

| 内容 | 中文 | English |
| --- | --- | --- |
| 完整用户手册 | [`docs/usage.md`](docs/usage.md) | [`docs/usage.en.md`](docs/usage.en.md) |
| 系统 / 硬件要求 | [`docs/requirements.md`](docs/requirements.md) | [`docs/requirements.en.md`](docs/requirements.en.md) |
| 时代预设与数字来源 | [`docs/era-presets.md`](docs/era-presets.md) | [`docs/era-presets.en.md`](docs/era-presets.en.md) |
| FFmpeg / 7-Zip 构建与编解码器差异 | [`docs/toolchain-builds.md`](docs/toolchain-builds.md) | [`docs/toolchain-builds.en.md`](docs/toolchain-builds.en.md) |
| 构建、测试、发布（维护者） | [`docs/MAINTENANCE.md`](docs/MAINTENANCE.md) | [`docs/MAINTENANCE.en.md`](docs/MAINTENANCE.en.md) |
| 版本历史（v7.9.8 起） | [`docs/CHANGELOG.md`](docs/CHANGELOG.md) | [`docs/CHANGELOG.en.md`](docs/CHANGELOG.en.md) |
| 版本历史（v6.2.2 → v7.9.7） | [`docs/CHANGELOG-archive.md`](docs/CHANGELOG-archive.md) | [`docs/CHANGELOG-archive.en.md`](docs/CHANGELOG-archive.en.md) |
| 容器 × 帧率 × 像素格式矩阵 | [`docs/capability-matrix.md`](docs/capability-matrix.md) | [`docs/capability-matrix.en.md`](docs/capability-matrix.en.md) |
| 移植验收测量报告 | [`docs/test-report-v7.md`](docs/test-report-v7.md) | [`docs/test-report-v7.en.md`](docs/test-report-v7.en.md) |
| 归档 / 退役代码 | [`archive/README.md`](archive/README.md) | [`archive/README.en.md`](archive/README.en.md) |

**要点**：本工具保留 v6.3.3 的实时模拟语义 —— 采集线程按素材节奏供帧，主循环
每 `1/fps` 秒出一个槽位。节流没有去掉，所以总墙钟时间仍与素材时长绑定。
需要不等时间时用 `--no-realtime`（直接压缩），它按机器速度编码、输出完全一致。

## 平台

| | Windows | Linux |
| --- | --- | --- |
| 状态 | 主平台 | v7.5 起支持 |
| 构建 | `tools\build.ps1`（JIT / AOT） | `tools/build.sh`（JIT，可选 `--aot`） |
| NativeAOT | 支持（需 MSVC + Windows SDK） | 实验性（需 `clang` + zlib 头文件） |
| SMB | UNC 路径（`\\host\share`） | CIFS 挂载点，需 `cifs-utils` |
| 播放器 | `ffplay` 或系统文件关联 | `ffplay` 或 `xdg-open` |
| 受管 FFmpeg | 支持 | 支持（`.tar.xz`，需 `xz-utils`） |

脚本需要知道的三处平台差异：

* 可执行文件在 Windows 上是 `LiveArchiveEncoder.exe`，其它平台无扩展名；
  pytest 套件通过 `EXE_NAME` 统一处理。
* 受管构建位于 Windows 的 `%LOCALAPPDATA%\LiveArchiveEncoder\ffmpeg\<id>\bin`
  与其它平台的 `~/.local/share/LiveArchiveEncoder/ffmpeg/<id>/bin`。
* FFmpeg 目录按平台过滤：`gyan.dev` 没有 Linux 构建，Linux 的菜单与补全不会列出它。

## 构建

Windows（PowerShell）：

```powershell
dotnet build src/LiveArchiveEncoder/LiveArchiveEncoder.csproj -c Release
tools\build.ps1 -Aot
# -> publish\jit\LiveArchiveEncoder.exe   （体积小；需要 .NET 10 / 11 运行时）
# -> publish\aot\LiveArchiveEncoder.exe   （单文件、无依赖；需 -Aot）
```

Linux / macOS：

```bash
tools/build.sh
# -> publish/jit/LiveArchiveEncoder
# NativeAOT 需先装 clang 与 zlib 头文件
```

默认 JIT 产物目标是 `net10.0` 且带 `RollForward=Major`，同一个包在只装了 .NET 11
的机器上也能启动。要显式按 .NET 11 编译：`tools/build.sh --tfm net11.0`。

## 测试

```powershell
dotnet test --solution LiveArchiveEncoder.V7.slnx -c Release     # 单元测试（TUnit）
python tools\e2e.py                                             # 端到端（分阶段跑）
```

* `tests/e2e/` 是 pytest 套件：默认二进制是 `publish/jit/`，可用 `CS_EXE` 指向 AOT 产物。
  它全程使用私有 `LAE_CONFIG_DIR` / `LAE_LOG_DIR`，不会碰你真实的 `config.json`。
* `timing` 与 `shared_config` 两组必须单独跑（实测节拍与共享配置），`tools/e2e.py`
  已经这样排序；直接 `pytest -n` 会被 `conftest.py` 拒绝。
* 端到端细节、分组理由与产物说明见 [`docs/usage.md`](docs/usage.md) §12。

## 布局

```
src/LiveArchiveEncoder/
  Configuration/  配置模型、编码器信息、常量表
  Core/           时间码、像素格式数学、校验、高精度计时、Ctrl+C、音频目录
  Diagnostics/    运行日志（Serilog + MEL）、数据根路径
  Encoding/       实时编码器、预检、参数构建、音频/字幕轨、分片、并行
  FFmpeg/         工具链下载与解压、7-Zip、能力探测、进程与管道
  PostProcess/    二次压缩、时代预设、字体包、字幕发现、fragment 编辑器
  RateControl/    码率与帧率阶梯控制
  Remote/         HTTP / SMB 源
  Update/         自更新
  UI/             CLI 解析、CUI 菜单、补全、报告、播放
archive/retired/  退役成员（不参与编译，逐个注明退役版本与替代物）
reference/        v6.3.3 Python 基线，逐字保留，仅用于对比
tools/            构建、e2e 驱动、清单校验、能力探测
tests/e2e/        端到端套件；tests/media/ 是生成的测试素材
```

## 与 v6.3.3 的差异

默认行为等价：FFmpeg 命令行（包括 `-threads` 的位置）、自适应帧率状态机、选帧规则、
滚动码率计算与全部默认值都保持原样。变化在水面之下 —— 预分配帧缓冲取代
`deque`、三个异步管道读取代三个阻塞线程、1 ms 计时器取代 15.6 ms `sleep`、
类型化结果取代 dict。

完整对照表与本次重写有意修复的六个基线缺陷（AMV 产出 0 字节、CUDA 默认开启、
两个错误的 SMB 选项、音频限制只查采样率、字体从不解析）见
[`docs/CHANGELOG-archive.md`](docs/CHANGELOG-archive.md) 的「v7.0 → v7.2」与「v6.3.3」。

## 已知约束

* **墙钟时间受素材时长约束** —— 这是实时模拟的设计，不是性能缺陷。要去掉等待用
  `--no-realtime`（输出一致，只是不等时间）。
* **`-threads` 在 raw-video 输入之前**，与 v6.3.3 完全一致；FFmpeg 的作用域规则可能
  使它到不了输出编码器，为了保持行为没有改动。
* **SIMD 已用满，无法再打开更多** —— 编码发生在 FFmpeg 子进程里，由它运行时分发；
  C# 控制器从不接触像素。v7.8.1 起不再产出 `-p:IlcInstructionSet=avx2` 的 AOT flavour
  （实测无收益，且只能在 AVX2 CPU 上启动）。
* **AOT 是打包手段，不是提速手段** —— 它产出单文件并缩短启动时间，编码本身不变。
* **退役代码归档而非删除** —— `archive/retired/` 保留无人调用的成员，各自注明退役版本
  与替代物；`archive/` 下不参与编译。
* **硬件解码默认关闭** —— `ffmpeg -hwaccels` 列出的只是「编译进来」的方法，没有 GPU
  或驱动时 `--hwaccel cuda` 会在第一帧失败；`--hwaccel auto` 会逐候选真解一帧再决定。
* **Linux NativeAOT 是实验性的** —— ILCompiler 需要链接各发行版布局不同的系统库，
  在 Linux 上优先用 JIT 构建。

与 v6.3.3 的实测对比见 [`docs/test-report-v7.md`](docs/test-report-v7.md)。

## 许可证

GPL-3.0，见 [`LICENSE`](LICENSE)。C# 移植继承 `reference/live_archive_encoder.py`
的许可证；默认下载的 FFmpeg 发行版（BtbN）是 GPL 构建。
