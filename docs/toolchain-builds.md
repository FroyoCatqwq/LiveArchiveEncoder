# FFmpeg 工具链与 7-Zip 构建对照

> v7.9.8.13.1。所有资产信息于 **2026-11-07** 用 `gh api repos/<repo>/releases/latest`
> 与 HTTP HEAD 实测核对；编解码器清单用两端 `ffmpeg -hide_banner -encoders` 实测。
> 本文件只讲「装什么、彼此差在哪」，命令用法见 `docs/usage.md` §7.10。

## 1. 统一原则

| 项 | 规则 |
| --- | --- |
| FFmpeg 来源 | 5 个自动/手动源，按平台过滤；`btbn` 仍是默认 |
| 版本策略 | **一律 daily / rolling**；只有当日构建无法解析时才回退稳定版（`FfmpegDist.StableUrl`） |
| 解压器 | Windows 与 Linux **统一使用 `ip7z/7zip` 官方构建**，不再依赖系统预装 p7zip |
| 旧配置 | `avbuild`（SourceForge 手动）与 `gyan` 全部保留，旧 `config.json` 无需改动 |

`gyan` 与 `avbuild` 在 v7.9.8.11 曾被标为"只作兼容保留"，本版重新把 `gyan`
列为正式支持的 Windows 源；`avbuild` 仍因 SourceForge 的 Cloudflare 拦截保持手动。

## 2. 五个 FFmpeg 源

| id | 项目 | Win | Linux | release 标签 | 本次实测资产 | 归档 |
| --- | --- | --- | --- | --- | --- | --- |
| `btbn` | [btbn/ffmpeg-builds](https://github.com/btbn/ffmpeg-builds/releases) | ✅ | ✅ | `latest`（2026-11-06 13:11 UTC 重建） | `ffmpeg-master-latest-win64-gpl.zip` / `...linux64-gpl.tar.xz` | `.zip` / `.tar.xz` |
| `nonfree` | [BnqDzj/FFmpeg-Builds-nonfree](https://github.com/BnqDzj/FFmpeg-Builds-nonfree/releases) | ✅ | ✅ | `latest`（2026-11-06 19:20 UTC） | `...win64-nonfree.zip` / `...linux64-nonfree.tar.xz` | `.zip` / `.tar.xz` |
| `ytdlp` | [yt-dlp/FFmpeg-Builds](https://github.com/yt-dlp/FFmpeg-Builds/releases) | ✅ | ✅ | `latest`（2026-11-06 20:29 UTC） | `ffmpeg-master-latest-win64-gpl.zip` / `...linux64-gpl.tar.xz` | `.zip` / `.tar.xz` |
| `autobuild` | [AnimMouse/ffmpeg-autobuild](https://github.com/AnimMouse/ffmpeg-autobuild/releases) | ✅ | ❌ | **每次 commit 一个 tag**，如 `2026-11-07-00-17-e196e57-89ebf7b` | `ffmpeg-<hash1>-<hash2>-win64-nonfree.7z` | `.7z` |
| `gyan` | [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) | ✅ | ❌ | 无固定 tag；`ffmpeg-git-full.7z` 是 git master rolling | HEAD 实测 200，74,442,465 B，Last-Modified 2026-11-05 10:23 UTC | `.7z` |
| `avbuild` | SourceForge | ✅ | ✅ | 手动目录 | 需浏览器下载后用 `--ffmpeg-archive` | `.7z` |

**关于 AnimMouse**：用户常以为它有 Linux 版，但本次翻查该仓库最近 30 个 release，
**每一个都只有 `win32` / `win64` 两个 nonfree 资产**，没有 Linux。因此 `autobuild`
只在 Windows 出现；其 tag 与资产名含提交哈希，不能写成固定 URL，本版改为安装时
读取 `releases/latest` 的 `assets[]` 并按正则 `win64.*nonfree.*\.7z$` 选取。

**稳定版回退（v7.9.8.13.2 校正）**：固定 URL 的四个源（`btbn` / `nonfree` / `ytdlp` /
`gyan`）本身就是 rolling 文件，没有更稳定的替代品，因此不设 `StableUrl`。只有走动态
feed 的 `autobuild` 需要一个回退：现指向一个已验证的固定 commit 资产（tag
`2026-11-07-00-17-e196e57-89ebf7b`）。`releases/latest` 读不到、JSON 畸形或无资产匹配时
才用它。此前该字段全空，"daily 优先、无 daily 用稳定版" 只是一句注释——属于已修的
死代码，见本版变更记录。

## 3. 编解码器差异

### 3.1 两端实测（`ffmpeg -hide_banner -encoders`）

| 编码器 | Windows `nonfree` N-128412 | Linux `btbn` GPL N-128532 | 用途 |
| --- | --- | --- | --- |
| `rv10` / `rv20` | ✅ | ✅ | RealVideo 1.0 / 2.0，`.rm` 时代预设 |
| `roqvideo` | ✅ | ✅ | id RoQ |
| `wmv1` / `wmv2` / `wmav2` | ✅ | ✅ | 2003–2005 门户 `.asf` / `.wmv` |
| `amv` / `adpcm_ima_amv` | ✅ | ✅ | MP4 播放器时代 |
| `mjpeg` / `huffyuv` / `ffvhuff` | ✅ | ✅ | intra-only / 无损 |
| `mpeg1video` / `mpeg2video` / `mpeg4` / `libxvid` | ✅ | ✅ | 通用老式编码 |
| `flv` (Sorenson H.263) | ✅ | ✅ | 2006–2009 中文视频站 FLV |
| `libmp3lame` / `ac3` / `real_144` | ✅ | ✅ | 同期音频 |
| `libopenh264` / `libx264` / `libx264rgb` | ✅ | ✅ | H.264 两条实现 |
| `libx265` / `libvpx` / `libvpx-vp9` | ✅ | ✅ | 现代交付 |
| **`libfdk_aac`** | **✅** | **❌** | nonfree 独有；GDP/许可证差异的典型例子 |
| `libx264_qsv` / `mjpeg_qsv` / `*_vaapi` | ✅（Windows MF/QSV 变体） | 部分 | 硬件加速，按机器而异 |

> 表中 ✅/❌ 是**这两台实测机器**的结果，不是各项目的承诺。跨版本、跨构建参数
> 都可能变化，换构建后请用下面的命令自行核对。

### 3.2 构建定位差异

| 源 | 典型构建配置 | 适合谁 |
| --- | --- | --- |
| `btbn` GPL | `--enable-gpl` + libx264/libx265/libvpx 等，另出 LGPL / shared 变体 | 默认；要 GPL 编码器且能接受 GPL 分发 |
| `btbn` 稳定分支 | 同一 release 里的 `n8.1` / `n9.0` 资产 | 需要可复现版本号时 |
| `nonfree` | 在 btbn 基础上加 `--enable-nonfree`（实测有 `libfdk_aac`） | 需要 nonfree 音频编码器；**二进制再分发需自行确认许可** |
| `ytdlp` | 面向 yt-dlp 下载后处理的 GPL 构建，资产覆盖 win32/winarm64/linuxarm64 | 与 yt-dlp 生态保持一致；体积/库集合与 btbn 不同 |
| `autobuild` | 每次 FFmpeg commit 自动构建的 nonfree 版，仅 Windows | 想紧跟上游、复现某个 commit 的行为 |
| `gyan` | `full` 版（第三方库最全），Windows x64，`.7z` | Windows 上要"什么都在"的一包式构建 |
| `avbuild` | SourceForge 手工发布 | 旧配置兼容；需手动下载 |

### 3.3 自行核对

```bash
ffmpeg -version                       # 版本、配置行、编译时间
ffmpeg -hide_banner -buildconf        # 完整 configure 开关（是否 --enable-nonfree）
ffmpeg -hide_banner -encoders | grep -E 'rv10|rv20|roqvideo|wmv2|libfdk_aac|flv'
ffmpeg -hide_banner -decoders | grep -E 'rv40|cook|vp6f'
```

本项目依赖的 legacy 编码器（rv10/rv20/roqvideo/wmv2/amv/mjpeg）在上述两个实测构建里
**都齐全**；真正需要挑构建的场景是 `libfdk_aac` 之类的 nonfree 编码器，以及硬件加速变体。

## 4. 7-Zip：两端统一的解压器

| 平台 | 官方资产（release `26.05`, 2026-10-21） | 落地 | 获取方式 |
| --- | --- | --- | --- |
| Windows | `7z2605-x64.exe`（另有 `.msi`、`7zr.exe`、arm/arm64） | `<数据目录>/7zip/7z.exe` | 下载后按官方 FAQ 的 `/S`（静默）+ `/D=<dir>`（目标，大小写敏感）安装，无需管理员权限 |
| Linux | `7z2605-linux-x64.tar.xz`（另有 x86/arm/arm64/mac） | `<数据目录>/7zip/7zz` | `tar -xJf` 解包 + `chmod +x` |

`7zz` 是完整的 7-Zip CLI，实测包内还带 `7zzs` 与 `MANUAL/`。查找顺序是
**系统 PATH（`7z` / `7zz` / `7za`）→ Windows `Program Files\7-Zip` → 我们装的那份**；
只有三者都没有才联网下载。因此正常机器不会触发下载。

用途不止字体包：`.zip` 字体包、`.7z` / `.rar`（含 Rar5）字体包，以及 `gyan` /
`avbuild` / `autobuild` 的 `.7z` 归档，全部走同一份 `SevenZip.Ensure()`。
Linux 不再出现 "install p7zip by hand" 的死路；`.zip` 仍保留 .NET 内置读取器作为
最后兜底。

## 5. 选择建议

- **默认/无特殊需求** → `btbn`（两端一致、daily、GPL）。
- **要 nonfree 编码器（fdk-aac 等）** → `nonfree`；注意再分发许可。
- **跟 yt-dlp 一起用 / 要 ARM 资产** → `ytdlp`。
- **Windows 上想要库最全** → `gyan`；**想追某个 commit** → `autobuild`。
- **老配置里写着 `avbuild`** → 继续可用，只是仍需手动下载归档。

换源后建议用 `LAE_NO_CAPS_CACHE=1` 强制重扫能力缓存，因为编码器集合可能不同。
