# LiveArchiveEncoder 8.0.0-m2.3-canary

**仍然是里程碑存档点，不是给日常使用准备的产品版。**
日常使用请继续用 [v7.9.8.6](https://github.com/FroyoCatqwq/LiveArchiveEncoder/releases/tag/v7.9.8.6)，
`main` 上仍然是 v7。

M2 到这一版真正收齐：**程序能自己下载 FFmpeg**，`nnedi` 去隔行滤镜补完，
计划被拒时的每句话都会说中文，还有三个藏在底层、不会自己报错的缺陷被修掉。

## 这个版本能做什么

```
LiveArchiveEncoder toolchain install              # 下 BtbN 的 FFmpeg 快照
LiveArchiveEncoder toolchain use btbn             # 让 encode 用这一份
LiveArchiveEncoder toolchain nnedi                # 下 nnedi 去隔行要的权重
LiveArchiveEncoder setup                          # 检查 FFmpeg 和 ffprobe
LiveArchiveEncoder encode in.mkv --dry-run        # 打印两条 FFmpeg 命令行
LiveArchiveEncoder encode in.mkv --to 600 --out clip.avi
LiveArchiveEncoder encode in.mkv --subtitle-copy 4  # 把 PGS 轨原样带进 mkv
LiveArchiveEncoder encode in.mkv --recompress "-c:v libx264 -crf 28"
LiveArchiveEncoder history list                   # 跑过什么，多快，停在哪档
```

- **实时节奏**：解码和编码是两个 FFmpeg，中间由程序按 `1/fps` 喂帧。
- **自适应帧率阶梯**：默认 13 > 8 > 5 > 3 fps，超上限掉档——不是少写帧，是把新帧换成重复帧。
- **预检**：每次开录前先拿同一条命令跑三帧。编码器不吃这个像素格式、复用器装不下这个编码、
  这台机器的硬件解码不可用——都在这三帧里露出来，一句话，一秒。
- **字幕**：烧外部字幕、烧容器里的文本轨、或把位图轨原样带走。
- **二次压缩链**：录完依次重压，每趟读上一趟的产物。
- **中英双语**：`LAE_LANG=zh` 或 `settings.toml` 的 `[ui] language`。现在连"设置文件读不出来"、
  "从 v7 迁移时丢了哪几项"、"下载被文件系统拒绝"都是中文。
- **自带 FFmpeg 下载**：只认 [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) 的快照
  和 [BnqDzj/FFmpeg-Builds-nonfree](https://github.com/BnqDzj/FFmpeg-Builds-nonfree)。
  **不要用发行版自带的 ffmpeg**：编码器表和解码器集合都不一样，行为不可比。

## 这一版修掉的

三个都不报错、只是"结果不对"的那种：

- **`-progress` 的 `out_time_ms` 被当成毫秒读**，实际是微秒（实测 2 秒的片子两个字段都是
  `2000000`）。码率因此差一千倍，而 2000 秒看着像一个正常的时长。
- **输出目录不接受程序自己起的临时名时，预检替整条命令背锅**：只允许精确产物名的归档挂载、
  按扩展名白名单的共享盘都会拒掉 `.lae-preflight-<guid>.avi`，报出来却是"三帧试跑失败"。
  现在先探一次可写性，被拒就挪到临时目录并说明"muxer 没在目标文件系统上验证过"。
- **`preset add` 从第二个起永久丢失、`preset remove` 静默无效**（两条命令都打印成功）。
  外科式 TOML 回写把预设块边界判错了，数组表里每个预设都是一行 `[[customPresets]]`。

## 这个版本没有的

- **插件宿主（M3）**：`chain` 编排、`--json-events` 都还没有。
- **AOT 发布**：这一版仍然是依赖打包（自包含单文件），没有原生 AOT。
- 自更新、分片并行（后者明确不做，见 `docs/parallel-split-verification.md`）。
- 交互式菜单（M4）；`lae repl` 是唯一带补全的入口。

## 装哪个包

| 平台 | 文件 | 要求 |
| --- | --- | --- |
| Windows x64 | `LiveArchiveEncoder-8.0.0-m2.3-canary-win-x64.zip` | 无（自包含，不用装 .NET） |
| Linux x64 | `LiveArchiveEncoder-8.0.0-m2.3-canary-linux-x64.tar.gz` | **glibc ≥ 2.34** |

Linux 底线是 **glibc 2.34**：RHEL / AlmaLinux / Rocky **9**、Debian **12**、Ubuntu **22.04**
及以上。AlmaLinux 8.10 跑不动（捆绑的 SQLite 原生库是按 2.34 编的），不是配置问题。

两个包都解包真跑过：`--version`、`toolchain list`、`history list`。

## 校验

```bash
sha256sum LiveArchiveEncoder-8.0.0-m2.3-canary-linux-x64.tar.gz
certutil -hashfile LiveArchiveEncoder-8.0.0-m2.3-canary-win-x64.zip SHA256
```

与 Release 页面列出的值对照。Windows 包里的 exe 是自包含单文件，解压后只有一个可执行文件
（打包脚本会断言这一点——早先的版本会漏一个 `libe_sqlite3.so` 在旁边，只拷 exe 就在写历史时
静默降级）。
