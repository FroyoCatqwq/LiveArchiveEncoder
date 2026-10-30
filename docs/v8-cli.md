# v8 命令行

> 这份文档只讲 **v8**（`v8-milestone` 分支）。v7 的用法见 `docs/usage.md`，两者不兼容，
> 而且 v8 没有交互式菜单——一切从命令行来，`lae repl` 是唯一带补全的入口。

程序名在 Windows 上是 `LiveArchiveEncoder.exe`，其余平台是 `LiveArchiveEncoder`；
下文一律写作 `lae`。

## 退出码

| 码 | 含义 |
| --- | --- |
| 0 | 成功 |
| 1 | 本机或素材做不到（找不到 FFmpeg、源读不出来、编码器中途死了、产物为空、二次压缩的某一趟失败） |
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
| `--out` | 产物路径。不写就放在源旁边，扩展名取**实际用的容器**（不是猜的）。写了就必须和容器一致，`--encoder flv --out x.avi` 会被拒 |
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
| `--subtitle-track` | 烧录**容器里**的那条字幕轨，值是 ffprobe 的绝对流序号 |
| `--subtitle-copy` | 把那条字幕轨**原样带进产物**，不烧。可重复 |
| `--subtitle-box` | 字幕排版用的画幅：`source`、`4:3`、`16:9` |
| `--subtitle-font` | 强制字体族，盖过字幕文件自己写的 |
| `--fonts-dir` | 额外字体目录（默认先找 FFmpeg 同级的 `fonts/`） |
| `--recompress` | 一段 FFmpeg 参数，等于给产物再压一趟。**可重复**，写几次就是几趟 |
| `--recompress-container` | 某一趟的容器，按位置与 `--recompress` 配对；不写就由那趟的编码器决定 |
| `--dry-run` | 只打印命令行，不开进程 |

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
lae config show --toml   # 按存储格式打印整份文档，可以直接存成 settings.toml
lae config migrate   # 把旧文件（v7 的 config.json、v8 早前的 settings.json）转成 settings.toml
```

`LAE_CONFIG_DIR` 可以指定别的目录（便携安装、e2e 测试都靠它）。旧文件**从不删除**：
v7 的 `config.json` 原地不动（v7 可能仍是用户手上唯一的构建），v8 早前的 `settings.json`
连同备份 `settings.v8-backup.json` 一起留着，只是不再读它。

### 配置文件是 TOML

```toml
# 那年的播放器缓冲只有几十秒，200 kbps 是能连续播的上限
[defaults]
quality = 9
limitKbps = 200.0
fpsLevels = [15, 12, 8, 5]

# 老站点的味道：320x240、4:3 的方框
[defaults.subtitles]
boxAspect = "4:3"     # 必须带引号：裸写 4:3 是语法错误，不是"243"

[[customPresets]]
id = "portal-2006"
label = "2006 门户"
encoder = "mpeg4"
liveQv = 6
```

四条规则，都来自踩过的坑：

1. **没写的键取默认值。** 两行配置也是一个合法文件，升级不需要迁移脚本补新键。
2. **未知键是错误，不是忽略。** `qualityy = 5` 会让程序拒绝读取并说出这个键的名字——
   JSON 时代拼错会被静默丢弃，用户以为改生效了。
3. **程序只改它拥有的那一段。** 目前程序会写文件只有两条路：`preset add/remove`
   （重写 `[[customPresets]]` 块）和迁移（生成整份文件）。其余字节——注释、空行、
   表格顺序——一个字都不动。所以你手写的注释不会因为加了预设而消失。
4. **预设必须是 `[[customPresets]]` 块**，不能写成 `customPresets = [...]` 单值。
   单值没法就地替换，追加一个块又会产生重复定义，程序会明确拒绝而不是写出一个读不回来的文件。

`--out` 那类参数不受影响：配置只影响默认值，命令行永远优先。

### `lae setup`

诊断：找到没有、在哪、能不能跑。退出码 1 表示缺东西。它**只诊断，不下载**。

### `lae history`

跑完就忘是 v7 的老毛病：一行统计打完，什么都没留下。现在每次真正跑起来的编码会写一行到
`history.db`（和设置同一个目录，SQLite）。

```
lae history list           # 最近 20 次，倒序
lae history list --limit 5 # 只看最近 5 次
lae history path           # 数据库在哪
```

```
   id  started          result     encoder/container  kbps  in → out
    3  2026-10-31 01:40  ok         mpeg4/avi           214  tape.mkv → tape_live.avi
    2  2026-10-31 01:12  cancelled  mpeg4/avi             -  tape.mkv → -
    1  2026-10-30 23:55  failed     libx264/mp4           -  tape.mkv → tape_live.mp4
```

几条刻意的选择：

- **一次运行为一行**，记的是事实而不是意图：跑了多久、产物多大、平均码率、阶梯停在哪一档。
- **失败的行不填 0，而是留空。** `0 kbps / 0 帧` 读起来像"成功编码了一段空内容"，比"它死了"
  严重得多。失败原因单独一列，用的就是用户在屏幕上看到的那句话。
- **Ctrl+C 记成 `cancelled`，不是 `failed`。** 中断不是错误，退出码 130 也不变。
- **写不进去不算失败。** 磁盘满了、数据库被别的东西锁住，只会在 stderr 打一行 `note:`，
  编码本身照常成功——已经压完的文件不该因为记不住而变成"失败"。
- **`history list` 不会创建数据库。** 从没压过东西的机器上，它只说"还没有记录"。
- **不记配置里已有的东西**（预设内容、默认值）。历史记的是发生过什么，预设记的是想要什么；
  混在一起会变成两个都是二手的。

数据库结构在 `src/Lae.Media/History/SqliteHistoryStore.cs` 里，`PRAGMA user_version = 1` 留给
以后加列时判断。连接**不开连接池**：一次只写一行，而池化的句柄会让文件在进程空闲时仍被占用，
Windows 上足够让用户的备份工具跳过它。

### 语言

输出有中英两套，按这个顺序决定：

```
LAE_LANG  →  settings.toml 里的 [ui] language  →  en
```

```toml
[ui]
language = "zh"     # 或 "en"
```

`zh-CN`、`zh_CN`、`ZH` 都认；不认识的语言落到英文，不会报错。机制、翻译进度和"怎么加一条新字符串"
在 [`v8-i18n.md`](v8-i18n.md)。目前完整翻译的是 `lae history`，其余命令这一轮仍是英文。

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

## 字幕

字幕烧在**解码侧**：在原始帧进管道之前画进画面，所以编码器命令行一个字都不变——有字幕
和没字幕的两次运行，差别只在帧的内容里。这不是实现细节，是两条命令行能当计划看的理由。

```
lae encode tape.mkv --subtitle-file chi.srt --subtitle-box 4:3
lae encode tape.mkv --subtitle-track 2            # 烧容器里第 2 条轨
lae encode tape.mkv --container mkv --subtitle-copy 2 --subtitle-copy 3
```

**烧录还是带走，不是同一个文件的两种偏好**：

| | 烧录 | 带走（copy） |
| --- | --- | --- |
| 需要什么样的轨 | **文本**轨（srt/ass/mov_text/webvtt） | 任何轨，包括位图 |
| 产物里有什么 | 画面上多了一层字，字幕流没了 | 多了一条字幕流，画面不变 |
| 能进哪些容器 | 跟烧录无关 | mkv 原样、mp4/mov 转 `mov_text`、webm 转 `webvtt`；avi/flv/mpg/rm/asf **没有字幕复用器** |
| 时间码 | 被重新渲染（`setpts` 那对） | 原始 cue 时间不动 |

**位图字幕（PGS/VobSub）只能带走，不能烧**。`hdmv_pgs_subtitle` 是"文字的图片"，
libass 没有字可排；FFmpeg 也没有把位图轨烧进画面的滤镜。典型场景就是 WEB-DL 的
`简繁日内封PGS`——它就是两条 PGS 轨，没有任何文本轨：

```
$ ffprobe -show_entries stream=index,codec_type,codec_name -of csv=p=0 in.mkv
0,h264,video,1920,1080
1,aac,audio,2
2,hdmv_pgs_subtitle,subtitle,1920,1080
3,hdmv_pgs_subtitle,subtitle,1920,1080

$ lae encode in.mkv --container mkv --subtitle-copy 2 --subtitle-copy 3
note: carrying subtitle tracks 2, 3 as copy into mkv
```

**内嵌轨要先拆出来**（仅烧录时）。libass 吃的是文件名，没有任何参数能让它读一条流，
所以 `--subtitle-track` 会先把那条轨解成一个 `.ass` 放在产物旁边，烧完（或失败）再删掉。
试运行不拆——它承诺的是两条命令行加零个进程，而拆轨是一次会写文件的进程。copy 模式
不需要拆：它本来就是从第二个输入直接 map。

**时间轴跟素材走**：`--from` 会让解码器把时间轴归零，而字幕文件（和拆出来的 `.ass`）
还是原始时间，所以滤镜链两头各加一次 `setpts` 把时间顶上去再抬回来。少了后半段，整条
编码会被重新计时。拆轨时**不能**加 `-ss`，加了就等于把要平移的量抵掉。copy 模式不受
影响：第二条输入的 `-ss` 会同样作用在字幕包上。

## 二次压缩链

录完之后再压一趟，是"上传到当年的视频站"这件事的机械版本。一份录像可能被发布不止
一次（先 2004 年的站，再后来接替它的那个），所以这是一**条链**，不是一次：

```
lae encode tape.avi --out tape_live.avi \
  --recompress "-c:v libx264 -crf 32" \
  --recompress "-c:v mjpeg -q:v 4"
```

得到三份东西，全留着：

| 文件 | 是什么 |
| --- | --- |
| `tape_live.avi` | 录像本身，**任何一趟都不会碰它** |
| `tape_live_web.mp4` | 第一趟：h264 |
| `tape_live_web2.avi` | 第二趟：读第一趟的产物，mjpeg |

几条硬规则：

- **名字从录像派生，不从上一趟派生**。第二趟叫 `_web2` 而不是 `_web_web`——后者说不清
  楚它是链条里的第几环。
- **容器跟编码器走**。`-c:v libvpx-vp9` 会落在 `.webm` 里；留给 FFmpeg 自己猜的话，
  扩展名就得在编码器确定之前先写死，这正是 `_web.mp4` 里塞进 VP9、几秒后在复用器里
  报错的那条路。不想让它决定，就用 `--recompress-container` 明说。
- **`-i` 和末尾的裸路径会被拒**。输入是上一趟的产物，输出路径由程序生成；一段里带
  `-i` 就是第二条输入，末尾写个 `tape.mp4` 就是第二个输出。两者都在**开跑之前**以
  退出码 2 拒掉——不会让你先录二十分钟再听拒绝。
- **某一趟失败，录像还在**。那一趟的 part 文件删掉，链条停在原地，已经写出来的几趟
  都留着，退出码是 1 并指出是哪一趟。半条链不算成功：脚本读到 0 会去发布不该发布的
  东西。
- **链只从命令行来**，设置文件里存的链永远不会自动参与一次运行（v7.9.8.6 就是在这里
  出的错：普通一次编码偷偷把自己又压了一遍）。

`--dry-run` 会把每一趟的命令行也打出来，因为一份只显示采集、藏起三趟转码的计划，不是
这次运行的计划。

**只跑一条链**：`--recompress-only` 跳过采集，直接对已存在的文件跑链条。调参数时不用
每次都重录一遍：

```
lae encode tape_live.avi --recompress "-c:v libx264 -crf 32" --recompress-only
```

它读的就是命令行里那个文件，产物仍然是 `tape_live_web.mp4`，输入文件**一个字节都不动**
（端到端测试逐字节比过）。

## 分片并行：不做

v8 没有 `--parallel`，设置文件里写了大于 1 会在**开跑之前**以退出码 2 拒掉，并说清楚
原因。这不是"还没做"，是**做了没用**：

实时节奏本身就是墙钟。把 20 分钟按 4 段并行，每段仍然要按实时走 5 分钟，四段一起也就
是 5 分钟——和一段走 20 分钟里同时进行的四分之一一样。要真提速只有一个办法：**拿掉
节奏**，而节奏正是这个程序的全部意义（让编码器的码率控制像 2004 年那台服务器一样行为）。

项目 2026-10-17 还实测过一遍（`docs/parallel-split-verification.md`）：白名单里所有
编码器的边际成本只有 0.06–0.26 s，而每段要额外付约 1.5 s 的启动与探测开销；20 s 素材
`--parallel 4` 是 2.33 s，比单段的 1.85 s **还慢**。唯一边际成本够高、值得分段的是
roqvideo，而 roq 恰恰是拼接会丢数据的那个容器。

## 还没有接的

诚实列一下，这些都会**明确报错**（退出码 2），不会静默忽略：

- 插件宿主（`PluginRegistration` 已在设置里定义）
- 自更新（退出码 3 的路径）

M2 到此收齐：实时节奏、自适应阶梯、字幕（烧录 / 内嵌轨 / copy）、二次压缩链、
`--recompress-only`。分片并行是**结论性不做**，理由见上一节。
