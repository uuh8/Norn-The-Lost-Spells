# L4.26：累积物质的显示成本、CPU稀疏查询与GPU页复用

当前优化已落地；整体液体阶段保持更新中。最终代码29 Fixture、**260/260**，2026-10-04T09:13:22.6420169Z—2026-10-04T09:14:13.0365033Z，Failed/Skipped/Aborted为0、SceneRestored=true。为Direct NUnit方法/Coroutine JSON，包含真实GPU Readback、CPU Mesh和日志检查，**非官方Test Runner XML/全项目测试**。最终P7正常累积仍未稳定60FPS。

## 1. 用户录制实际说明了什么

两份13:56:58/13:57:11文件各600帧，共1200独立主线程时间戳，无重叠。用户明确正常运行、没有暂停；没有前后A/B条件。主线程P50/P95为16.90/27.21与16.68/21.57ms。第一份每帧约2.70ms的CpuBuildPage，第二份已无构建；都有Draw，导出范围没有Capture/求解/反应标记。不能套用旧录制的23ms采集成本，也不能凭缺少模拟标记指称用户暂停。

[saved-capture-summary.json](saved-capture-summary.json)、[逐帧主线程统计](main-frames.tsv)保留分析结果与原文件长度/SHA。两份原始.data仍在ProfilerCaptures，归档时再次校验SHA。详细样本TSV曾在Temp中，重启后丢失；本目录保留的summary/main frames不是那份完整样本导出。GPU状态NotSupportedWithNativeGfxJobs，不采用零散GPU counter或WaitForGPU作为完整GPU时间。

## 2. 最后保留的改动

- CPU：每个粗查询中心缓存175邻格中两帧任一有数量的位置及相位存在性，保留y/z/x顺序、负坐标Floor和Previous-only尾水；完整扫描可作同输入参考。
- GPU：湿输入逐位比较及Terrain/依赖Halo/Bounds/槽号/r2-r3失效检查。非零速度仍插值，严格静止页统一Current相位。所有Kernel共用页工作标记，保留未重建页的几何、Counter和Indirect Args。
- 空气：仅GPU上传Terrain副本用+Infinity标记两帧均空的行，避免各材料Packed解码；源数据与Gameplay不改。
- 保留数量、Density、反应、接触状态、粘度、曲面分辨率和材质风格。128MiB硬预算不变，P7实际131774080B；CPU侧固定影子缓存约10MiB。
- 工具：临时CPU profiling后恢复原设置、有效窗口检查、根恢复记录持久化、结果立即归档。没有自动提交或使用SubAgent。

## 3. 最终固定输入对照

[CPU原始交替记录](l426-cpu-work.json)、[GPU原始交替记录](l426-gpu-work.json)、[GPU几何逐项比较](l426-gpu-equivalence.jsonl)、[最终260报告](final-260-concentrated.json)、[Manifest/源码SHA](manifest.json)。

| 测量 | 完整参考均值 | 最终优化均值 | 降低 | 结果约束 |
| --- | ---: | ---: | ---: | --- |
| CPU完成整套Mesh | 250.97ms | 115.58ms | 53.94% | 1536GMU、1036Vertex、960Index、签名1960475504、GC0 |
| GPU提交+完成等待 | 9.30ms | 6.21ms | 33.29% | 1568GMU、16页，优化每次重建8页 |
| GPU全页+空气快跳 | 9.30ms | 8.49ms | — | 区分空气快跳与页复用 |

均为暖机后同输入交替三轮，保留波动，不换数量/相位序列。CPU是完成整套分帧构建，GPU是含Readback fence的Wall Time；**均不等于游戏帧时间或完整GPU timestamp**。最终五组GPU位置/Normal/Swell误差0，材料Half权重、有效性与Args一致。带相同Packed端点但非零速度的页也实际产生相位形变。

## 4. 最后正常P7窗口

临时无敌只用于本工具新进入的诊断Play，防止死亡暂停污染帧时间；[保护记录](benchmark-player-protection.txt)与[画面](l426-final-restored-p7-game.png)留档。TimeScale=1、CPU profiling、GPU Usage关闭；全部窗口MeasurementValid=true，Renderer/火焰GC0，无全域CPU回退。

| 窗口 | 帧数 | GMU | GPU页 | 活动格 | P50 ms | P95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| baseline | 144 | 6220 | 16 | 27 | 18.77 | 38.70 |
| live-mixed-5 | 140 | 17094 | 20 | 140 | 22.45 | 36.37 |
| live-mixed-10 | 139 | 26386 | 26 | 190 | 29.68 | 49.38 |
| live-mixed-15 | 146 | 34275 | 26 | 142 | 37.01 | 53.53 |
| live-mixed-20 | 132 | 42844 | 26 | 240 | 38.43 | 56.07 |

五份对应JSON/60帧原始TSV及Profiler状态均以l426-final-restored-p7开头。38.43ms中位帧约26FPS，**稳定60FPS未通过**。各窗口数量、反应及活动状态不同，不能宣称固定实验降低比例等于真实FPS提升比例。主线程Capture/求解/火液反应与Render Thread工作仍存在。父子Inclusive Scope和不同线程时间不能相加当作一帧纯CPU工作。

同一源码较早两次有效自然窗口也保留：l426-final-p7为强开GPU Usage的历史诊断，l426-cpu-only-p7为改为CPU-only后的窗口。关闭GPU模块没有消除累积成本，未认定它是主要根因。[Unity API](https://docs.unity3d.com/cn/6000.0/ScriptReference/Profiling.ProfilerArea.GPU.html)提醒GPU profiling成本较高；[手册](https://docs.unity3d.com/cn/2023.2/Manual/ProfilerGPU.html)说明Graphics Jobs支持限制。没有改项目Graphics Jobs/API。

## 5. 撤回与中间检查

GPU邻列Mask实验通过260项和当前全量几何对照，但固定样本没有可靠净收益，已从生产代码撤回；[实验报告](l426-rejected-columns-concentrated.json)和[实验计时](l426-rejected-columns-gpu-work.json)保留。跨不同轮次还有Editor/硬件波动，没有把Shader寄存器压力推定为已证明的唯一原因。

恢复后第一专项为4/5，CPU参考错误撤掉Uniform Height/Stable等快照语义，[报告](l426-recovered-focused.json)保留；改成仅禁用候选遍历后通过。邻列首次专项的第一个Coroutine超时60秒，其他4项通过，[报告](l426-column-focused.json)保留；后来完整260通过，仍不能把首次超时当作功能通过。实验撤回后重新跑最终260，之后正常Play、退出、Audit。原先对称速度相位测试、早期RED和部分早期计时在Temp重启清理中丢失，未补造文件或将其加入最终通过计数。

## 6. 原生崩溃与P7恢复

16:00 Editor原生退出。[去除无关信息的日志摘录](native-crash-excerpt.txt)有显存不足提示与D3D12 unrecoverable GPU device error，具体触发点未确定。用户明确允许重开恢复后继续，接续已新开的Editor实例，未另开重复实例。[恢复时状态](recovered-editor-state.json)和[最终状态](final-editor-state.json)显示十根原对象全部激活、Edit Mode/Clean/TimeScale1/后台false；[最终只读Audit](p7-grid-cutover-audit.txt)0失败。

原场景SHA256为bdc44a9b3529128a85843aa842eb83970faeaffe322ab4774baeb4014c860b83，最终相同，未保存/丢弃关卡。恢复记录改放Assets外Backups/ElementPerformance，含名字/场景/开关/Process ID；Instance ID仅同一进程使用，跨进程不猜测恢复。测试每例请求PlayerLoop处理Render Thread提交/延迟释放，此措施未被宣称修复原生设备错误。

Temp重启丢失了部分早期源码备份、原始导出和中间报告。最终使用重新恢复后的测量；Frame上轮源码有持久档案，保留previous-final和patch，其余来源不将更早Git版本冒充本轮起点。

## 7. 学习、湖泊制作和限制

[学习文档L4.26](../../../../Assets/_Project/Docs/2026-09-20%20通用液体底层重构完整解析（更新中）.md)、[湖泊指南第十三节](../../../../Assets/_Project/Docs/湖泊与地形制作指南.md)解释数据生命周期、World Space/粗格转换、缓存依赖、Alpha、Indirect Draw、调度与取舍。

天然大湖保持Stable，真实Collider和数量决定湖盆/水位，局部注入/出口/反应才激活；新增近处命中、转头返回、坡脚、同/异Density和数量验收按指南操作。大量同时可见的活动曲面、CPU overflow构建延迟、透明绘制、复杂地形、长期累积、Standalone稳定60FPS和完整GPU计时仍有限制，整体计划未标完成。
