# L4.24：累积液体可见性与远处休眠，实际检查

本轮处理新水已经造成Wet却长期不可见的问题。保留粗Grid、密度/数量/反应与卡通厚度；显示提前排除远处/Frustum外来源，GPU资源优先用于近处和最近命中，超过细页预算的区域使用独立CPU缓存。Gameplay沿已有活动域暂停/恢复，远处量完整保存。当前128MiB硬预算内实际GPU分配125.67MiB；还有CPU快照/缓存内存，不能把GPU预算说成全部内存。

## 1. 根因与新旧回归

旧实现把不可见远页也打包，超过256输入或96个r2输出页便整体转CPU，完整构建期间保留旧外壳，新命中水等待。[有效RED](liquid-visibility-red.json)0/2，两个真实Renderer用例都在CpuFallback=true处失败。更早的测试配置失败未当作行为RED。

只做Frustum筛选仍无法解决镜头内真正超预算。当前保留96页GPU持续更新、完整可见CPU快照及实时GPU域裁剪。最近注入4m范围优先，30秒后到期，不改变Gameplay量。镜头转动使休眠水重新选页；CPU完整缓存保留以后可能降为CPU的原GPU页。

[休眠容量RED](liquid-dormant-capacity-red.json)13通过/1失败：满休眠列表会持续触发全Store扫描。现在等待活动域变化再有界扫描，未列入休眠队列的数量仍存Store。返回唤醒8格、1600 GMU与暖机GC0均纳入最终回归。

[最后集中检查](liquid-visibility-final-concentrated.json)：2026-10-04 **01:35:04—01:35:24 Asia/Shanghai**，28个Fixture **252/252**，Failed/Skipped0、Aborted=false、SceneRestored=true。Direct NUnit方法与Coroutine JSON，含真正GPU Readback/CPU Mesh，**不是官方Unity Test Runner XML或全项目测试**。旧243是Task F9，早期21/29/25项是中间版本，均没有替代本次252项。

## 2. 受控GPU输出与Capture成本

| 检查 | 当前真实结果 |
| --- | --- |
| 镜头内大量液体超预算 | 241个可见Owner，GPU96页、CPU145页；保持GPU工作，没有整体CPU切换。 |
| 新命中256 GMU水 | 42个有效Vertex、216个Index；总量15616 GMU保持。 |
| 相同120远页+1近页，完整/筛选Capture | 输入121→1页；暖机10次平均40.87005→0.62815ms，GC0B，共同查询点Field数值一致、总15616 GMU不变。 |
| 转头且没有新的世界写入 | 镜头180°后选页重新生成；前后水各200 GMU，总400 GMU保持。 |
| 域外液体返回 | 400 GMU案例远水保存、返回后继续向邻格流动；满列表案例8格全部唤醒。 |

[GPU几何](liquid-visible-impact-gpu.json)、[固定Capture对照](liquid-visible-capture-ab.json)均在最终集中Gate内生成。Capture对照只有显示采集方法；不能写成整个游戏获得65倍FPS。241页样本没有等待145个CPU页全部完成，它验证近处实际GPU输出和预算分配，不能当作全部CPU页及时可见证明。

## 3. 最后P7正常窗口

最后源代码后的五个正常窗口均Playing=true、TimeScale1、MeasurementValid=true；每个窗口至少128样本，另有完整60帧Profiler TSV。截图在窗口结束后生成，没有把截图/Readback放进性能窗口。

| 当前负载 | 有效帧 | GMU | Resident / GPU页 / Culled页 | Editor P50/P95 |
| --- | ---: | ---: | --- | --- |
| 基线 | 130 | 6220 | 5 / 16 / 1 | 28.71 / 45.99ms |
| 加120远处液体Chunk | 151 | 36940 | 125 / 16 / 121 | 28.58 / 43.43ms |
| 再施法5组水/火/毒/粘液 | 135 | 45743 | 127 / 20 / 121 | 38.92 / 55.94ms |
| 累计10组 | 135 | 53490 | 128 / 20 / 121 | 39.94 / 59.42ms |
| 累计15组 | 135 | 60710 | 129 / 20 / 121 | 43.45 / 77.09ms |

远处120Chunk增加30720 GMU且完整保留，GPU页数仍16；登记休眠19→139，原场景活跃格27→37，没有新增120个持续活跃来源。近处模拟与反应本来在变化，不能把这组动态FPS当作完全冻结A/B。最后15组显示筛掉121来源、20页GPU无回退/溢出；前一轮视角/分布不同的50页结果在Temp，不混成当前20页结果。实际反应会改变Quantity，不能凭施法次数假设每次保留相同量。

15组原始Profiler的`GridLiquid.Field.Capture`均值6.417ms/次、累计2.994ms/frame，`GridLiquid.FluxSolver`1.886ms/次、0.880ms/frame。这是CPU Marker，不是完整GPU执行时间。基线/加120远Chunk的Capture均值2.612/2.750ms，新增远页不再参与逐格塑形。所有五个窗口液体Renderer累计GC0 B、Fire Peak GC0 B；15组整帧GC metadata均值809B、最大17391B，其它路径仍分配。基线整帧也有一帧较大分配，原始数据照实保留。

**稳定60FPS尚未通过。** Native Graphics Jobs状态含`NotSupportedWithNativeGfxJobs`，单个GPUFrame或CPU Dispatch耗时不能代表可靠GPU成本。Standalone Player、长时间累积和更密集的可见CPU缓存仍未认证。

最后P7画面：

![当前正常累积施法](liquid-visibility-final-live-game.png)

## 4. 几何复拍、场景与剩余边界

最后源码后重新捕获60°坡脚与8湖接触：坡脚2210 GMU、接收地面789 GMU、独立小球0；8湖Stable2105280 GMU加510活动毒液，总2105790完整。两者Renderer GC0B，没有整体CPU回退；[坡脚](continuous-lab-join60-tick30.png)仍有少量粗折面，不能据此宣称所有复杂地形美术已经验收。[8湖接触](continuous-lab-lake-contact-tick30.png)是无正式场景Lighting的功能lab，不作为最终材质颜色标准。

Frustum带8m预备范围、采样Halo和32—56m连续淡出；还有相邻输入与已出生火焰生命周期的有限工作。镜头外Gameplay活动来源继续原反应/角色接触。域外液体暂停期间不补算历史流动，返回继续；满列表登记数也不等于所有域外湿格总数。墙体Occlusion Culling未实现，视野内墙后来源可能参与显示准备。

GPU细页以上仍存在CPU缓存首次完成/更新延迟与额外CPU内存。CPU约2ms分段软预算保持，近处新水获得GPU输出不等于所有远CPU页即时完成；Hybrid Display Lag与GPU Display Lag分别记录。Frame容量真正溢出或无Compute仍走完整CPU参考路径，不能保证无限累积零成本。

01:46只读[P7接线Audit](p7-grid-cutover-audit.txt)0失败，[最终状态](liquid-visibility-final-scene.json)为当前P7 Edit Mode/Clean/TimeScale1/后台false。归档发现P7磁盘最后修改为01:34:02，SHA256已与Task F9不同；修改来源未确认。本任务未调用SaveScene或写入关卡文件，不回滚当前文件。最终01:35回归、01:36性能和01:41实拍都发生在这次修改后；manifest保存两个Hash和最后修改时间，不能声称整轮场景字节未变。所有临时lab/Play已退出，根对象恢复使用持久记录，无猜测激活。未自动保存、丢弃、重启、提交或使用SubAgent。

学习文档L4.24解释Frustum/AABB、Halo、Current快照与Gameplay活动域；《湖泊与地形制作指南》第十一节给出参数、建湖、离开/返回与累积施法验收步骤。`manifest.json`保存当前源码与证据SHA256，归档工具不覆盖之前轮次的文件。
