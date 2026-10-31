# 系统与硬件需求

> **本文描述的是 v7（`main`）。** v8 的底线不同：**Linux 要 glibc ≥ 2.34**
> （RHEL / AlmaLinux / Rocky 9、Debian 12、Ubuntu 22.04 起），Windows 仍是
> 10 1809 / Server 2019 起；v8 只发**依赖版自包含单文件**，不出 AOT。
> 依据见 `CHANGELOG.md` 的 `v8.0.0-m2.2` 一节。

一句话版本：**程序本身很轻，门槛在 FFmpeg。**

LiveArchiveEncoder 自己不碰像素：解码与编码全部由 FFmpeg 子进程完成，程序负责帧槽节流、码率控制、
分片与拼接。实测在 60 fps / 640x480 / 720 帧槽的运行里，编排进程自身只占 **单核 1.5%** 的 CPU
（0.22 s / 14.08 s 墙钟）。所以"能不能跑"取决于 FFmpeg 需要的 CPU 和内存，而不是这个 8 MB 的程序。

---

## 1. 操作系统

| 平台 | 支持情况 | 说明 |
| --- | --- | --- |
| Windows 10 1809+ / Windows Server 2019+ / Windows 11 | **支持**，x64 | 发布 `win-x64`（JIT）与 `win-x64-aot`（单文件）两种产物 |
| Linux x86_64 | **支持**，glibc ≥ **2.28** | JIT 产物吃 .NET 10 支持的全部发行版（AlmaLinux 8 / RHEL 8 起）；AOT 产物的下限等于构建机 |
| Linux ARM64 / macOS | 未发布产物 | 源码可编译，但没有任何验证，不承诺可用 |
| 32 位 Windows | 不支持 | 只发布 x64 |

> **v7.9.6.3 把下限从"Windows 11 / glibc 2.35"降到 .NET 10 自己支持的水平**：下限不再
> 由某一台开发机决定，而是由**测试机**决定，两台测试机就是下限——Windows Server 2019
> （10.0.17763）与 AlmaLinux 8.10（glibc 2.28）。两者都跑过完整单测。

Linux 的两种打包方式差别：

- `linux-x64-aot.tar.gz`：原生单文件，**不需要安装 .NET**，但它动态链接 glibc，
  所以目标机 glibc 必须 ≥ **构建机**的版本（v7.9.6.3 起构建机是 AlmaLinux 8.10 / glibc 2.28，
  因此该产物的下限就是 2.28）。更老的发行版走 JIT。
- `linux-x64.tar.gz`：普通 JIT 产物，需要目标机有 **.NET 10 运行时**（或 11；
  `RollForward=Major` 让它一份产物同时吃 10/11）。glibc 下限由 .NET 决定（2.28 起）。

Alpine / musl **不支持**：AOT 产物没有 musl 版本。

运行时会自检这两条（`Core/EnvCheck`）：Windows 比构建号、Linux 读
`gnu_get_libc_version`，低于下限只**警告**不拒绝——慢机器照样能压出正确的文件。

## 2. CPU

| 项 | 最低 | 推荐 |
| --- | --- | --- |
| 架构 | x86-64（SSE4.2） | 同上，AVX2 以上更佳 |
| 核心数 | **2** | 4–8 |

为什么是 2：`--parallel` 每个分片是一个独立 FFmpeg 进程，1 核机器上分片只会互相抢占，
程序会把分片数按网络盘/单核情况降为 1。分片数取"物理核数 − 1"通常最划算——留一个核给编排与 IO。

FFmpeg 自己的编码器也有要求：`libx264` 在中低端 CPU 上跑 640x480@25 没问题；
1280x720 以上或 `libx265` / AV1 编码建议 4 核以上，否则实时节流会拖不动。

## 3. 内存

| 项 | 最低 | 推荐 |
| --- | --- | --- |
| 可用内存 | **2 GB** | 8 GB |

一帧 1080p `yuv420p` 约 3.1 MB；程序保留 `--buffer-frames`（默认 6）帧的抓取缓冲，
再乘以并行分片数。4 分片 × 1080p × 6 帧 ≈ 75 MB 的裸帧，加上 FFmpeg 自身的参考帧/编码器缓冲，
千兆级别的余量是够的。真正吃内存的是 x265 / AV1 这类编码器，它们的 lookahead 可以到几百 MB。

## 4. 磁盘

| 项 | 数值 |
| --- | --- |
| 程序（JIT） | ≈ 0.25 MB 压缩包，解压约 0.6 MB |
| 程序（AOT 单文件） | ≈ 3.8 MB |
| FFmpeg 构建（完整 GPL 版） | ≈ **161 MB**（BtbN nightly，含全部编解码器） |
| 输出空间 | 至少 **输出体积的 3 倍** |

为什么要 3 倍：并行模式先把每个分片写成 `<名>.sNNN.mkv`，拼成 `joined.mkv`，再回封到目标容器——
峰值时源分片、拼接中间文件、成品同时在盘上。打开二次压缩则再多一份成品体积。
`LAE_KEEP_SHARDS=1` 会保留分片目录，调试时记得手动清。

网络盘（UNC / 映射盘 / CIFS）上的源码流式读取，但**输出不要放在网络盘上**：
分片写入 + 回封需要多次大文件重写，SMB 上会显著变慢。

## 5. 显示与终端

- 交互界面是纯文本菜单，只需要一个能读 stdin 的标准终端：Windows Terminal / cmd / PowerShell，
  Linux 任意 xterm 兼容终端。
- 中文界面按**显示宽度**补齐（全角字符按 2 列算），窗口建议 ≥ 80 列。
- 不需要图形环境；`--play` 用 `ffplay` 或系统关联程序时才需要。

## 6. 字体（烧录字幕）

字幕烧录用 libass，字体只从**程序自己的 `fonts` 目录**取（与 `ffmpeg/` 同级）：

| 平台 | 目录 |
| --- | --- |
| Windows | `%LOCALAPPDATA%\LiveArchiveEncoder\fonts\` |
| Linux | `~/.local/share/LiveArchiveEncoder/fonts/` |
| 覆盖 | 环境变量 `LAE_FONT_DIR` |

- 中文字幕必须放一个中文字体（`msyh.ttc` ≈ 19 MB、`simhei.ttf` ≈ 9.7 MB、`simsun.ttc` ≈ 18 MB）。
- 不放也不会崩：libass 会回退到系统字体，缺中文字体时表现为**方块**。
  `--list-subtitle-fonts --subtitle x.ass` 会先告诉你这个 ASS 要哪些字体、本机有没有。
- 导入：`--font-import <字体文件或目录>`，或菜单 `17 → 5`。

## 7. 权限与目录

程序会写这些位置：

| 用途 | 位置 |
| --- | --- |
| 设置文件 | `%LOCALAPPDATA%\LiveArchiveEncoder\config.json`（`LAE_CONFIG_DIR` 可改） |
| 能力缓存 | `<设置目录>/cache/caps-<hash>.json`（可随时删） |
| 命名字段档案 | `<设置目录>/configs/*.json` |
| 托管 FFmpeg | `%LOCALAPPDATA%\LiveArchiveEncoder\ffmpeg\<源>\` |
| 字体 | 同上，`fonts\` |
| 输出与分片 | 输出文件所在目录 |

没有管理员权限要求（除非把 FFmpeg 装到系统目录）。

## 8. 网络

| 场景 | 要求 |
| --- | --- |
| HTTP(S) 输入 | FFmpeg 构建需带 `http` 协议（标准 BtbN/Gyan 构建都有） |
| SMB / UNC / 映射盘 | 由操作系统挂载；程序只做路径判定，并给读/写/探测都加 `-rw_timeout 30000000` |
| 下载 FFmpeg | 需要访问 GitHub / gyan.dev / SourceForge；`--proxy` 可指定代理 |
| 下载 NNEDI3 权重（`--nnedi-update`） | 只需访问 GitHub（`sekrit-twc/znedi3`），约 13.5 MB。大陆线路直连 `raw.githubusercontent.com` 实测**连上后不给数据**，用 `--gh-proxy` 或让它自动选路 |
| GitHub 下载前沿（`--gh-proxy`） | 需要能访问前沿本身（默认 `gh-proxy.com`）；不设置代理也能用，这是它存在的目的 |
| 程序自更新（`--update` / 启动检查） | 需要访问 `api.github.com` 的 releases 接口和 release 资产所在主机；同样认 `--proxy` 与 `--gh-proxy`。启动检查有 3 秒预算、24 小时缓存，失败静默。发布页返回 401/403/404 时可用 `LAE_UPDATE_TOKEN`（只从环境变量读，不落盘） |

网络源**强制单遍编码**：N 个分片各自打开同一个共享连接，是这个项目里最容易挂死的形态。

## 9. 实测环境

| 机器 | 配置 | 结果 |
| --- | --- | --- |
| Windows 11 | 16 线程 | 单测 475 × {net10.0, net11.0}；e2e 全量 |
| Ubuntu 22.04.5（cia1） | **2 核**，glibc 2.35 | 单测 474（映射盘用例 Windows-only）× 双 TFM；e2e 全量，两阶段 321 s + 209 s |

2 核能跑完全部测试，包括 4 分片编码——所以"最低配置"不是纸面数字。

## 10. 性能预期（同一个 20 秒素材）

| 做法 | 墙钟 |
| --- | --- |
| 单遍、实时节流（默认，模拟直播节奏） | ≈ 21 s（= 素材长度 + 1.2 s 固定开销） |
| 4 分片、2 核 | ≈ 1/3 素材长度 |
| 纯 `ffmpeg` 一次性转码（无节流） | **0.489 s** |

前两行是设计：这个工具模拟的是"当时的机器边录边压"。如果你要的是最快出片，
用 `--recompress-only`（纯转码，不节流）。把节流本身变成可选（`--pace fast`）是 v8 的既定项。

固定开销 ≈ 2 s：.NET 启动 + 一次 ffprobe + 一次预检。能力探测在 v7.9.5 之后按 FFmpeg 二进制
落盘缓存，热启动不再起进程。

## 11. 不支持的做法

- 32 位进程、ARM、musl、macOS 发布包；
- 把输出写进只读目录或系统目录（会正常报错，不会静默丢文件）；
- 用 `--parallel` 读网络盘（会被强制改成 1，并说明原因）；
- 制造画面损坏、花屏、马赛克这类"损伤"效果——本工具只负责编码，那些效果属于
  另一个独立项目，`--help`、报告和菜单里都不再声称由某个本地脚本处理。
