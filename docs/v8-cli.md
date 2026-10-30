# v8 命令行

> 这份文档只讲 **v8**（`v8-milestone` 分支）。v7 的用法见 `docs/usage.md`，两者不兼容，
> 而且 v8 没有交互式菜单——一切从命令行来，`lae repl` 是唯一带补全的入口。

程序名在 Windows 上是 `LiveArchiveEncoder.exe`，其余平台是 `LiveArchiveEncoder`；
下文一律写作 `lae`。

## 退出码

| 码 | 含义 |
| --- | --- |
| 0 | 成功 |
| 1 | 本机或素材做不到（找不到 FFmpeg、源读不出来、编码器中途死了、产物为空） |
| 2 | 请求本身不成立（参数错、编码器不存在、容器写不了、功能还没接） |
| 3 | 有更新版本（自更新查询用，暂未接线） |
| 130 | 被 Ctrl+C 打断（子进程已收尾、part 文件已清理） |

`stdout` 是答案（计划、路径、清单），`stderr` 是旁白（`note:`、`ERROR:`、进度）。
把 `encode --dry-run` 重定向到文件，得到的正好是两条可以直接粘进 shell 的命令行。

## 命令

### `lae encode <input>`

一次编码：一个源、一个产物、一趟实时节奏。

```
lae encode clip.mkv --out clip.avi --encoder mpeg4 --quality 8 --limit 200
```

| 选项 | 说明 |
| --- | --- |
| `--out` | 产物路径。不写就放在源旁边，扩展名取**实际用的容器**（不是猜的） |
| `--encoder` | 视频编码器名。换编码器时质量数会跟着换刻度（`q:v 8` → `crf 17`） |
| `--container` | 容器扩展名；不写就用该编码器自己的 |
| `--from` / `--to` | 片段起止（秒） |
| `--quality` | 质量，**在当前编码器的刻度上**：mpeg 家族 2..31，x264/VP9/AV1 0..51 |
| `--size` | `宽x高`，两种大小写的 x 都行 |
| `--preset` | 套用一个自己存的预设，见 `lae preset` |
| `--limit` | 码率上限（kbps）。超过就**降帧**，不抬上限。`0` 表示关掉自适应 |
| `--recover` | 回升阈值，默认上限的 75%；写大了会被夹住并说明 |
| `--fps-levels` | 阶梯，`13,8,5,3` 或逐档阈值 `13,8:150:110,5` |
| `--subtitle-file` | 烧录这个 `.srt`/`.ass`，可重复 |
| `--subtitle-box` | 字幕排版用的画幅：`source`、`4:3`、`16:9` |
| `--subtitle-font` | 强制字体族，盖过字幕文件自己写的 |
| `--fonts-dir` | 额外字体目录（默认先找 FFmpeg 同级的 `fonts/`） |
| `--dry-run` | 只打印两条命令行，不开进程 |

### `lae preset`

v8 **不自带任何预设**。v7 的 32 行（5 个年代 + 27 个站点/年份）全部去掉了：那些数字
对某一个站点在某一年成立，换个站点就不成立，而程序没法告诉用户哪一行可信。预设是用户
自己写下来、起名字、随时能改的东西。

```
lae preset add portal-2006 --encoder mpeg4 --size 320x240 --limit 150 --quality 8
lae preset list
lae preset show portal-2006
lae preset remove portal-2006
lae encode in.mp4 --preset portal-2006 --limit 120     # 命令行永远赢
```

`--keep` 会把当前默认值全部写进预设（可复现），不加就只记你写的那几个字段（跟着默认走）。

### `lae config`

```
lae config path      # 设置文件在哪
lae config show      # 每个字段一行，值以运行期为准
lae config migrate   # 读 v7 的 config.json，写 settings.json，留 config.v7.backup.json
```

`LAE_CONFIG_DIR` 可以指定别的目录（便携安装、e2e 测试都靠它）。v7 的文件**从不删除**。

### `lae setup`

诊断：找到没有、在哪、能不能跑。退出码 1 表示缺东西。它**只诊断，不下载**。

### `lae repl`

带高亮和补全的交互提示。补全候选来自同一棵命令树，所以 shell 补全脚本和这里的菜单
不会各说各话。`stdin`/`stdout` 被重定向时直接以 2 退出，不会挂着等输入。

## 自适应阶梯

v7 的行为被完整保留：**帧率是会掉的**。码率顶到上限就降一档（`13 → 8 → 5 → 3`），
掉到回升阈值以下再爬回来；一档一档走，不会一次跌到底。

实现方式是**槽照写、把新帧换成重复帧**：容器帧率和时长都不变，变的只是"动的帧数"。
所以降帧后的文件仍然是 `13 fps`、时长精确不变，码率下来。

读数来自编码器的 `-progress pipe:1`，分母是**节目时间**而不是墙钟——这样两台机器
快慢不同，档位决策也一样。

## 还没有接的

诚实列一下，这些都会**明确报错**（退出码 2），不会静默忽略：

- 二次压缩链（`RecompressChain`）
- 内嵌字幕轨的烧录（`--subtitle-file` 那种外部文件可以用）
- 字幕 copy 模式（另起一条字幕流）
- 分片并行（`Parallel`，设置里还是 1）
- 插件宿主（`PluginRegistration` 已在设置里定义）
- 自更新（退出码 3 的路径）
