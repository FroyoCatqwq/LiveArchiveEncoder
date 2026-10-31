# LiveArchiveEncoder 8.0.0-m2.2

**仍然是里程碑存档点，不是给日常使用准备的产品版。**
日常使用请继续用 [v7.9.8.6](https://github.com/FroyoCatqwq/LiveArchiveEncoder/releases/tag/v7.9.8.6)，
`main` 上仍然是 v7。

M2 到这一版收尾：**预检回来了**，Linux 底线抬到 **glibc 2.34**，配置换成 TOML，
程序开始记运行历史，输出能说中英文，发布件只出依赖版。

## 这个版本能做什么

```
LiveArchiveEncoder setup                          # 检查 FFmpeg
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
- **中英双语**：`LAE_LANG=zh` 或 `settings.toml` 的 `[ui] language`。

## 这个版本没有的

- 插件宿主（M3）
- **FFmpeg 下载**：v7 有 `toolchain install`，v8 还没有。这一版要求机器上已经有 FFmpeg
  （`setup` 会告诉你有没有）。用 [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)
  的快照版；需要 nonfree 组件的用 [BnqDzj/FFmpeg-Builds-nonfree](https://github.com/BnqDzj/FFmpeg-Builds-nonfree)。
  **不要用发行版自带的 ffmpeg**：编码器表和解码器集合都不一样，行为不可比。
- 自更新、分片并行（后者明确不做）。

## 装哪个包

| 平台 | 文件 | 要求 |
| --- | --- | --- |
| Windows x64 | `LiveArchiveEncoder-8.0.0-m2.2-win-x64.zip` | 无（自包含单文件） |
| Linux x64 | `LiveArchiveEncoder-8.0.0-m2.2-linux-x64.tar.gz` | **glibc ≥ 2.34** |

单文件，解开就能跑，不需要装 .NET。Linux 包里的 SQLite 原生库是按 glibc 2.34 编译的，
所以在 AlmaLinux / Rocky / RHEL **8** 上会跑不起来（那是这一版有意放弃的范围）。
测试过的系统：Windows Server 2019、AlmaLinux 9.8。

```bash
tar -xzf LiveArchiveEncoder-8.0.0-m2.2-linux-x64.tar.gz
./LiveArchiveEncoder --version
```

## 这一版改了什么

**预检**。v7 有、v8 一度丢了。丢掉之后，"这个组合不行"的第一个信号是一段已经开录的录像，
报告只说"解码器一帧都没出来"。现在试跑和真跑走同一个 runner，验证的就是真命令。
`--hwaccel auto` 因此有了意义：硬件能不能用是机器的属性，只能靠试，试失败就改用软件解码。

**Linux 底线 2.34**。换来的不再被老发行版拖住依赖：SQLite 从 3.49.1（在
CVE-2025-6965 影响范围内）升到 **3.53.4**。Linux 侧的编译测试搬进 AlmaLinux 9 容器。

**单文件真的只有一个文件**。之前 `libe_sqlite3.so` 落在 exe 旁边，只拷 exe 就会在写历史时
静默降级；现在它被塞进单文件，打包脚本会断言目录里只有一个文件。

**重定向输出固定 UTF-8**。`> log.txt`、脚本抓 stdout、CI 日志，以前拿到的是机器 OEM 码页的
中文字节，UTF-8 读者只会看到乱码。

**报告不再把两件事说成一句**：阶梯动过才叫 `motion`，启动追帧丢的帧单独一行 `skipped`。

## 验证

单测 **312**、e2e **60**，零警告；AlmaLinux 9 容器内 **312/312**（BtbN FFmpeg 在 PATH 上，
两条真编码用例不再跳过）。GitHub Actions 在 `v8-milestone` 上两端（windows-latest / ubuntu-latest）
构建 + 单测通过。

两个包都解包真跑过：`--version`、`config path`、`history list`（这条要走 SQLite）。
