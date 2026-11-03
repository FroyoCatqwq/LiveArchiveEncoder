# LiveArchiveEncoder v7.9.8.7

v7 的补丁版。两件事，都是"以前根本用不了"：**检查更新永远是失败的**，**PGS 内封字幕不能烧**。

> ⚠ **从 v7.9.8.6 升级要手动下载一次。** 那一版里的仓库常量还是已封账号，它自己看不到
> 这一版；下载解压覆盖之后，`--check-update` 恢复正常。

## 1. 检查更新

`SelfUpdate.Repo` 写的是 `fr0yo0v0/LiveArchiveEncoder`——仓库原先所在的账号。那个账号被封，
API 对它下面的一切返回 404，所以每次检查都拿不到 JSON。仓库现在在
`FroyoCatqwq/LiveArchiveEncoder`，常量改过去了。

## 2. PGS / 图形字幕能烧了

`Constants.BitmapSubtitleCodecs` 里**没有 `hdmv_pgs_subtitle`**。PGS 轨因此没被认成图形字幕，
规划器按文本字幕去拆包（`-c:s ass`），FFmpeg 拒绝，用户看到的是：

```
ERROR: could not extract subtitle stream 2 (hdmv_pgs_subtitle) from the source;
use --subtitle-mode copy to carry it instead, or --subtitle with a file
```

听起来像文件坏了，其实是这个构建不认识的编码。补上之后，图形字幕走 overlay：

```
[0:v][0:2]overlay=format=auto:eof_action=pass[vmain];
[vmain]<scale/crop/fps chain>[vout]
```

- **不拆包**：`[0:<index>]` 直接进 filter graph，少一次进程、少一个 30 MB 的 `.sup`
  临时文件；只读共享目录上也不会因为写不出来而在编码前失败。
- **overlay 在缩放链最前面**：字幕图与源视频同尺寸，先按原始分辨率合成，再让整幅画面走
  scale/crop/fps——字幕跟着同一个变换走，位置不用第二套偏移去对。
- `-ss` 起点时字幕跟着源一起 seek，时间戳天然对齐。

实测 `[晚街与灯][Re Zero 4th - 04]`（hdmv_pgs_subtitle ×2，简日/繁日），256x192 输出，
顶部歌词与底部对白位置、时间都正确。

## 装哪个包

| 平台 | 文件 | 要求 |
| --- | --- | --- |
| Windows x64 | `LiveArchiveEncoder-v7.9.8.7-win-x64.zip` | **.NET 10 运行时** |
| Linux x64 | `LiveArchiveEncoder-v7.9.8.7-linux-x64.tar.gz` | **.NET 10 运行时** |

这一版只出依赖版（JIT）；AOT 产物这次没有构建（本机缺少出 Windows AOT 的工具链）。

## 校验

| 文件 | 字节 | SHA256 |
| --- | --- | --- |
| `LiveArchiveEncoder-v7.9.8.7-win-x64.zip` | 369,208 | `a6793ecbee0e3b0323751ca344b6608e4b3c5473cc98754d2529817f8d3aa4c0` |
| `LiveArchiveEncoder-v7.9.8.7-linux-x64.tar.gz` | 328,969 | `3556c2551c74fefc9e7f1421afaecf4a1c9fea65154083f8d4e89c3bf44c9c80` |

## 这一版怎么验的

单测 **618**（617 通过 / 1 跳过）、零警告。

- 单测 `Plan_BurnsBitmapSubtitlesWithOverlay`：bitmap 轨不再抛异常，`BitmapStream` 是索引，
  `BurnInto` 生成 `[0:v][0:1]overlay=...` 且在 scale 之前。
- 真素材：v7 从 `00:01:00` 起跑 32 秒并中断，从 `.part` 抽帧，顶部歌词与底部对白都在；
  从 `00:23:30` 跑完整 10 秒，退出码 0。
