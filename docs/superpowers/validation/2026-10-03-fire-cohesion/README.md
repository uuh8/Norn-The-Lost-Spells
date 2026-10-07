# 2026-10-03 火团连通、统一外观与采样优化证据

本轮完成相邻火源共享连续主体、旧活跃Parcel不被512预算排除、Tetra封闭拓扑/法线、Surface/Gas生命周期、近远景颜色与火舌轮廓，以及火源采样的Chunk批量只读优化。Gameplay火格、伤害、Water灭火、Poison毒爆继续沿用原流程；已有液体/角色/关卡修改保留。**本轮代码与集中回归通过；整体元素世界阶段仍在更新中，稳定60FPS没有通过。**

## 当前源码验证

- [最终集中报告](fire-liquid-concentrated-verified.json)：北京时间20:47:15—20:47:52，26个fixture、**238 Passed / 0 Failed / 0 Skipped**，Aborted=false、SceneRestored=true。直接执行NUnit方法和GPU Coroutine，**不是官方Unity Test Runner XML**。
- 真实GPU断言：Slot700在1024池仍有Surface；外壳焊接后每条Edge恰有两个邻面且Winding外向；0.7m间距一个主体、2.6m间距两个主体；生产8192池可保存最大发射率下完整2.3秒生命周期。
- Chunk Snapshot三例：512值与原查询相同、副本修改不回写World、非法Buffer拒绝/未知Chunk不创建、warm后100次复制GC0；Persistent Fire Source十例及原液体/反应覆盖同批执行。
- [P7只读接线](p7-grid-cutover-audit.txt)：22:04、Failures=0。
- [最终Editor状态](grid-liquid-capture.json)：P7 Edit Mode、SceneDirty=false、TimeScale1、RunInBackground=false。[根对象原状态](gate-saved-root-state.json)由诊断开始前持久化；恢复使用这些实际状态，没有猜测激活所有对象，没有保存/丢弃/重启。
- [源码与证据SHA256](source-manifest.json)记录最终文件、DLL时间与报告Hash。当前报告覆盖最终Alpha1主体与Chunk复制；旧211/25不能替代它。

## 最终实际画面

用真实Gameplay写入入口创建8个0.6m间隔Fire来源；来源位置见[输入](fire-verified-final-row-input.json)。三次Near、低角度和强制Far均检查了实际非黑像素，图片/Readback在性能窗口外。

![最终近景](fire-verified-final-near-1.png)

![最终低角度](fire-verified-final-near-low.png)

![强制Far LOD分支诊断](fire-verified-final-forced-far-lod.png)

Far图只移动Rendering Interest以强制使用备用Splat，**不是自然玩家距离或远景FPS测量**。不同LOD使用近似几何，共享颜色和火舌方向；Far仍是软投影火舌。

| 画面 | Active | Surface | Gas | Triangle | Overflow / Readback Error | Renderer Peak GC |
|---|---:|---:|---:|---:|---|---:|
| Near0 | 4829 | 2802 | 2659 | 12740 | 0 / false | 0 B |
| Near1 | 5348 | 2957 | 2851 | 12916 | 0 / false | 0 B |
| Near2 | 5081 | 2636 | 2984 | 12030 | 0 / false | 0 B |
| Low | 4511 | 2476 | 2675 | 12322 | 0 / false | 0 B |
| Forced Far | 5071 | 3285 | 2777 | 3520 | 0 / false | 0 B |

Surface和Gas在交叉期重叠，不能相加当作Active。Far移开Density窗口后仍可能包含其他来源，不能以剩余Triangle判断目标火焰仍走Near。完整记录见[视觉窗口](visual-windows.json)。完整Near的Alpha1消除背景透出条带；LOD交叉区继续使用透明近似。

## 实际性能与测量边界

最后生产P7，真实四法术一组（Water/Fire/Poison/Sticky），每次追加5组，TimeScale1、无暂停。采集窗口与完整60帧Raw Profiler分别保存，Screenshot/Readback不在性能窗口内。基线已有持续来源和液体，不是空场；不同帧率/反应/摄像机构图会影响分布，非单调P50不能解释为新增元素免费。

| 累计追加组 | 样本 | 液体GMU | 页数 | P50 | P95 | Fire CollectSeeds均值/次 |
|---|---:|---:|---:|---:|---:|---:|
| 基线 | 132 | 6220 | 20 | 35.81ms | 51.36ms | 2.12ms |
| 5 | 133 | 27003 | 32 | 32.16ms | 51.42ms | 2.35ms |
| 10 | 132 | 41330 | 38 | 33.28ms | 54.05ms | 2.31ms |
| 15 | 136 | 56694 | 48 | 40.01ms | 63.11ms | 2.32ms |

- [最后窗口索引](performance-windows.json)；`fire-verified-stress-*.json/.tsv/.summary.json/-state.txt`为最后运行。所有窗口Fire Peak GC=0、Liquid Renderer GC=0，液体Overflow/Drop=0且走GPU，没有CPU回退。
- 修改前相似5组的[采样分拆](fire-seed-diagnosis-live-mixed.summary.json)：CollectSeeds平均20.2697ms、SeedUpload20.3198ms。最终5组分别为2.3508/2.3910ms，上传/提交约0.04ms，主要改善来自每Chunk一次Dictionary查找+Array.Copy。优化前27029 GMU/95个Fire扫描Chunk，最终27003 GMU、扫描Chunk数量接近；这是实际相似负载观察，**不是逐帧相同输入A/B的严格倍数**。
- 最后15组Liquid Field Capture平均9.5705ms/次、Flux Solver4.2745ms/次、Fire CollectSeeds2.3163ms/次。Marker有嵌套，不能把父Marker与子Marker相加；同一Editor Profiler帧可有多次PlayerLoop，报告保留实际调用数。
- Fire/液体热路径的Managed Allocation为0，整场其他路径仍有分配。最后15组原始60帧导出汇总约1760 B/帧、最大33178 B；[上下文](fire-verified-stress-gc-context.json)只保存相邻Sample名称，**不包含父调用树，不能凭它断言具体责任对象**。没有把整场GC写成0。
- Render Thread `RenderLoop`仍大，但包含调度/等待和其他画面工作，不能直接当作Fire Shader GPU耗时。`GpuFire.ReconstructSurface` Marker只测CPU提交。`-state.txt`显示`NotSupportedWithNativeGfxJobs`，某些GPU Frame值为0ns或部分值，均不能作为完整GPU时间。
- 累加Buffer额外3.375MiB，8192池比2048多0.375MiB；没有以减少粒子寿命、采样预算、Gameplay量或可见火源数量换性能。Triangle与Density仍有固定上限。

## 保留的失败与对照

- [原始实际GPU RED](fire-surface-red.json)：0/3；旧Slot丢失、闭壳与近邻连通均失败。
- [第一阶段GREEN](fire-surface-green2.json)：25/25；只覆盖当时版本，最终以238报告为准。
- [Snapshot RED](fire-chunk-copy-red.json)：0/3，接口未实现。
- [工具上下文失败](fire-focused-current-green.json)：43通过/3失败，`LogAssert`没有LogScope，且`[Test]+[TestCase]`多执行一个无参数调用。工具已使用官方LogScope验证日志、正确枚举显式TestCase；没有改断言或忽略Error。
- `fire-layer-diagnosis-surface-only.png/.json`与`surface-opaque.png/.json`：关闭所有Splat后条带仍在，Alpha1完整覆盖后消失。最大实际Mesh Edge约0.29m、Material Depth Queue2999/Color3000，帮助排除长Triangle或Splat来源。它们是诊断对照，非最终美术截图。
- `fire-depth-tail-near-0.png`和`near-1.png`是**全黑失败截图**，不算验收；最终helper已检测黑图并重试。
- `fire-visual-before-*`为用户起始Play状态的历史表现/性能，工作量与最终窗口不同；`fire-chunk-optimized-*`为完整覆盖修正前的采样优化中间版，不当作最终视觉或全帧性能。

## 制作与复查

保持原P7唯一GPU Fire Renderer和原Legacy禁用状态。普通法术沿用Projectile写入，持续环境火源使用`ElementWorldPersistentFireSource`。邻近两来源检查底部合成一个主体、上部仍起伏并飘散；隔开来源检查中间留空；水灭火/毒液毒爆检查原Gameplay反应。

默认Profile的Cohesion0.6m、纵向1.9、Surface/Gas 1/0.3/1秒、Capacity8192。改变出生速度/总寿命必须重新核算 `ceil(MaxSeeds×Lifetime/SpawnInterval)`；Visual配置在Start形成Snapshot，退出Play后改Profile再重进。学习原理、坐标/Shader/CPU所有权与每个文件责任见[完整解析Task F7第41节](../../../../Assets/_Project/Docs/2026-08-09%20GPU%20PBF流体模拟完整解析.md)。

尚未验收：严格60FPS、Standalone Player、长时间无限累积、可靠的完整GPU逐Kernel时间。有限Voxel贴近会有细分轮廓，8m边缘的透明交叉是近似；竖直Billboard的极限正俯视/任意Roll未覆盖。本轮未保存关卡、未提交、未使用SubAgent。
