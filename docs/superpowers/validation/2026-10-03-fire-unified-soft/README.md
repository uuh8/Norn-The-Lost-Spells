# Task F8：统一柔软火舌，本轮实际证据

用户指定将近处也做成其更喜欢的远处软火舌，并让玩家附近不再出现形态分界。当前默认Profile已启用统一模式，保留原生命周期、颜色、运动、出生预算和Fire玩法。旧Mesh模式保留为关闭统一开关后的对照选项；[Task F7归档](../2026-10-03-fire-cohesion/README.md)未被覆盖。

## 1. 修复与真实回归

- 统一模式同时绕过Vertex的Near折叠与Fragment的互补Mask，取消8m Mesh/Splat形态切换。
- 32m内完整，32—56m以Smoothstep进行24m宽淡出；当前实际Draw Bounds半径65.2411m。没有扩大Gameplay活动/反应域。
- 原Shader实绘RED：[0/3](fire-soft-red.json)，Near Coverage=0、44m Alpha没有减半、旧Bounds13.79m；修改后同一断言[3/3 GREEN](fire-soft-green.json)。像素绘制使用生产Shader与真实GPU回读。
- 最终27个fixture [241/241](fire-soft-concentrated.json)，Failed/Skipped/Aborted均0、SceneRestored=true。2026-10-03 21:55:46—21:56:20，Direct NUnit/Coroutine JSON，**不是官方Test Runner XML**。
- 集中回归包括Splat像素、仍可选用的Mesh GPU闭壳/连通、生命周期、采样、只读Snapshot、持续火源与既有液体/反应。历史238不作为本轮最终代码通过证据。

## 2. 实际P7画面

![统一近景火舌](fire-soft-preview-near-1.png)

实际近景：[第一帧](fire-soft-preview-near-0.png)、[第二帧](fire-soft-preview-near-1.png)、[第三帧](fire-soft-preview-near-2.png)，[低角度](fire-soft-preview-near-low.png)，[移开旧LOD中心](fire-soft-preview-forced-far-lod.png)。最后一张只移动Rendering Interest24m，是显式分支诊断；不是自然64m地形穿行或远景性能证明。

五张均非黑，对应同名JSON读取真实Pool：Active4719—5388，Surface2715—2983，Gas2621—2889；UsesLocalSurface=false、HasSurfaceResources=false、DrawCalls=2、ReadbackError=false、Peak GC0 B。Triangles=0表示当前路径没有创建Mesh，不能解释成“Mesh渲染失败”。32/44/56m距离权重1/.5/0由实际Shader像素fixture验证。

近景固定池与旧版相同8192，生命周期仍1/0.3/1秒。程序化软轮廓、颜色、Depth Fade和上升保留，没有新增贴图/模型素材。

## 3. 资源与真实施法窗口

统一模式没有96³ Density/Accumulation或131072 Triangle资源，也没有对应六次Compute Dispatch/两次Mesh Draw。有效火焰Buffer载荷532496 bytes，约0.508MiB；省去的局部场/网格资源载荷约18.750MiB。数字根据实际容量/Stride计算，不包括Driver/Texture对齐成本，不能冒充显卡整机VRAM读数。

| 真实累计输入 | 有效样本 | GMU / 显示页 | Editor帧时间P50/P95 |
|---|---:|---|---|
| 基线 | 142 | 6220 / 20 | 37.09 / 55.58ms |
| 5组水/火/毒/粘液 | 154 | 26590 / 32 | 30.93 / 55.46ms |
| 10组 | 149 | 40722 / 36 | 34.43 / 56.78ms |
| 15组 | 131 | 57354 / 46 | 42.25 / 67.57ms |

JSON为`fire-soft-stress-baseline/live-mixed-5/live-mixed-10/live-mixed-15`，各自附60帧TSV、Profiler状态和summary。窗口TimeScale1、Fire/液体Renderer GC0 B、液体Overflow/Drop=0、无CPU回退。15组采样20次调用均值2.2420ms；整场GC平均517/max816 bytes来自其他路径。Shader Dispatch的CPU Marker不代表GPU执行时间；Native Gfx Jobs仍报告GPU计时不支持，部分Marker捕获次数不能代表完整Draw调度统计。截图/Readback不纳入正常帧窗口。

本次没有严格固定输入A/B，历史Task F7和本轮总体帧时间不能直接比较提升倍数。**稳定60FPS仍未通过**，透明Overdraw、复杂液体和其他场景路径仍需按真实数据判断。

## 4. Editor恢复、复现与边界

当前P7已回到Edit Mode、SceneDirty=false、TimeScale1、RunInBackground=false，[只读接线Audit](p7-grid-cutover-audit.txt)0失败，[最终状态](grid-liquid-capture.json)。未保存/丢弃/重启关卡、未提交、无SubAgent。临时相机/Interest替换/火写入随Stop撤销，测试保存并恢复原根对象状态；不要重复最早Untitled恢复动作。

直接进入P7 Play施放火球，观察近处/旋转/旧8m范围内是否保持同样软火舌。Profile的Unified Soft Flames已启用；对比旧Mesh时在Edit Mode取消它，再重新进入Play。运行中修改开关不会重新分配资源。距离参数默认32/56m，需保持终点位于World实际可见/Resident数据范围内。

Splat是Billboard近似，极限正俯视/任意Roll、Standalone Player、长期无限累积、稳定60FPS、可靠逐Kernel GPU时间仍未验收。学习文档新增Task F8第42节，Phase继续更新中。

`manifest.json`保存此版本源码SHA256、DLL时间、证据SHA256与原始窗口。可通过`python Tools/ElementPerformance/archive_soft_fire.py`在全部结果齐备且Scene恢复后重新核验；它只写本证据目录，不改历史归档或用户场景。
