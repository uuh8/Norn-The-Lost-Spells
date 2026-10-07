# Task F9：火焰俯视薄片修复，实际检查

当前默认火焰保留正面软火舌、两个Draw、8192池及32—56m淡出。Splat改为Camera Facing Coverage + Fragment解析三维收尖Kernel，俯视不再将World Up的竖直面投影压成横条。没有新Buffer/Density Grid/Texture/Raymarch或Gameplay改动。它仍是Impostor与最近点近似，非完整体积积分或精确深度相交。

## 1. 实际前后对照

修复前正俯视：

![旧薄条](fire-overhead-before-overhead.png)

修复后正俯视：

![新横截面](fire-overhead-after-overhead.png)

修复后：[低角度](fire-overhead-after-near-low.png)、[高角度](fire-overhead-after-high.png)、[正俯视Roll](fire-overhead-after-overhead-roll.png)、[普通近景](fire-overhead-after-near-1.png)。修复前后各8个实拍及同名JSON保存Camera Position/Forward、实际Surface/Gas数量、模式与GC；不是只改相机位置后描述推测效果。

16张有效截图均非黑，两个Draw、无Surface Resources、Surface/Gas同时存在、ReadbackError=false、Peak GC0 B。修复前Roll截屏曾出现一次黑图，`black-attempt`文件作为失败诊断保留，未当作通过图片。移开旧LOD中心的截图是分支诊断，不是自然64m地形性能。

## 2. 真实Shader回归

[RED报告](fire-overhead-red.json)：既有三例通过，新增两个视角fixture失败。旧80°能量225.3878，约正面的17%；90°为0。

[GREEN报告](fire-overhead-green.json)：5/5。固定白色Orthographic正面保持54×103 Pixel、能量1297.3323，与旧值一致；年轻火舌90°为54×54/742.0777，Gas90°为32×32；90°Camera Roll0/45/90°稳定。每个角度指标保存在`fire-overhead-red/green-fire-view-*`。正面受控一致不代表所有Perspective画面逐Pixel完全一致。

最后生产Shader后，[集中回归](fire-overhead-concentrated.json) **243/243**，Failed/Skipped/Aborted0、SceneRestored=true。2026-10-03 23:07:06—23:07:42，Direct NUnit/Coroutine JSON，**不是官方Test Runner XML**。覆盖既有液体/反应、火源、生命周期、旧Mesh模式GPU测试、统一样式/范围和新视角；历史241只属于Task8。

## 3. 实际成本与限制

| 真实施法累计 | 有效帧 | GMU/页 | Editor P50/P95 |
|---|---:|---|---|
| 基线 | 136 | 6220/20 | 38.28/59.95ms |
| 5组水/火/毒/粘液 | 137 | 28264/34 | 34.01/58.45ms |
| 10组 | 139 | 45661/38 | 36.18/77.14ms |
| 15组 | 134 | 45203/50 | **44.92/77.32ms** |

各窗口TimeScale1、Fire/液体Renderer GC0 B、液体Overflow/Drop0、无CPU回退。真实反应会消耗GMU，不应假设数量单调增加。15组60帧整场GC均值1078/max3024 bytes，其他路径仍有分配。原始TSV及Profiler状态完整保留。

普通测试相机火源窗口，旧36.18/69.81ms，新38.27/70.69ms；动态粒子池、反应和Editor条件没有完全冻结，不能用它宣称严格A/B、性能完全不变或GPU提升。俯视现在确实绘制更多有效Pixel，Shader也增加向量算术；先clip空角落再读Depth Texture减少无用采样，没有新增Draw/交叉Quad。Native Gfx Jobs仍不支持可靠GPU计时，CPU Dispatch Marker不能当作GPU耗时。截图不混入性能窗口，俯视实拍也不冒充俯视稳定GPU压力测量。

**稳定60FPS仍未通过。** 正俯视/Roll退化已覆盖，完整体积积分/自遮挡、代理深度与墙体精确相交、Standalone、长期无限累积和可靠GPU计时仍有边界。

## 4. 场景与复现

当前P7 Edit Mode、Clean、TimeScale1、后台false，[只读Audit](p7-grid-cutover-audit.txt)0失败，[状态](grid-liquid-capture.json)。P7磁盘SHA256与[Task8](../2026-10-03-fire-unified-soft/README.md)相同。未保存/丢弃/重启/提交，没有SubAgent；所有临时相机/Interest/火写入随Stop撤销，测试恢复原根开关。

直接在P7施放火球，缓慢提高俯视角并旋转Yaw，火舌应连续变为饱满截面，来源位置和上升方向保持原样；检查墙边遮挡以及灭火/毒爆仍按原规则。无需额外Inspector配置。学习文档新增Task F9第43节，整体阶段继续更新中。

`manifest.json`保存当前源码和证据SHA256及场景一致性；`archive_fire_view.py`只写本目录，不覆盖旧Task8归档。
