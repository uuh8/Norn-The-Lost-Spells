# L4.25：用户卡顿录制分析与液体采集 CPU 优化

本轮使用用户提供的两份正常游戏卡顿 `.data`，确认显示准备存在明显 CPU 开销，完成同一 `Game.Rendering` 内的等价优化。没有降低厚度、显示细分、Shader、混色、密度层序或 Gameplay 反应，也没有扩大 GPU 预算。**固定输入 Capture 的优化已验证；正常游戏稳定 60 FPS、完整 GPU 成本和长期累积仍未验收。**

## 1. 两份原始记录能证明什么

原始文件保留在 `ProfilerCaptures/`，SHA256、长度及导入信息见 [统计报告](saved-capture-summary.json)。用户说明两份都来自正常游戏感觉卡顿的时刻；不假设前一份是少水、后一份是多水。两个文件的 Main Thread 原始时间戳有 **325 帧重合**，合计 875 个不同时间戳，不能当作独立的优化前后实验。

分析使用 Unity 6000.3.16f1 的 `ProfilerDriver.LoadProfile` 与 `RawFrameDataView`，在独立临时项目 `C:/Users/zhy/AppData/Local/Temp/NornProfilerOffline-20261004` 运行。该进程已经退出，没有为读取 `.data` 在用户项目 `Assets` 增加导出脚本，也没有暂停、退出或重启用户当时的游戏。随后观察到用户 Editor 已在 Edit Mode，才进行本轮源代码编译与诊断。

每份导入实际返回 **600 帧**，不将截图 UI 的 3132/3299 帧序号解释为本次已分析 3299 帧。第一份 218 帧、第二份 391 帧含液体或火焰 Draw marker，用于统计显示更新窗口；其他帧可能涉及焦点、说明界面、录制空档等，未擅自全部标为 Pause。P50 使用 median，P95 使用 nearest rank；完整逐帧索引见 [Main Thread 帧表](main-frames.tsv)。

| 统计范围 | 10:18:58 | 10:20:08 |
| --- | ---: | ---: |
| 导入帧 / 显示更新帧 | 600 / 218 | 600 / 391 |
| 显示更新帧 Main Thread P50 | 71.15 ms | 73.45 ms |
| 显示更新帧 Main Thread P95 | 128.61 ms | 138.34 ms |
| 显示更新帧最大值 | 315.93 ms | 1108.94 ms |
| Capture，均值/调用 | 23.49 ms | 28.46 ms |
| Crown，均值/调用 | 10.47 ms | 12.71 ms |
| PackCells，均值/调用 | 6.88 ms | 8.44 ms |
| ShapeBounds，均值/调用 | 4.25 ms | 5.22 ms |
| FluxSolver，均值/调用 | 2.05 ms | 2.64 ms |
| CpuBuildPage，均值/调用 | 2.36 ms | 3.01 ms |

这里的 Capture 是把已有 Gameplay 数量整理为显示输入，FluxSolver 才是搬运液量的 CPU 工作。液体肉眼静止不能推导出显示没有开销：同一个可见世界内还有新注入、反应、活动 Tick 或镜头变化，当前全局快照仍可能重新采集多个静止页；GPU Draw 与透明表面着色也继续执行。完全静止且版本、视野和动画状态不变时，原 Renderer 已复用几何。本轮优化减少真正发生采集时的无效空气格工作。

各子 marker 已包含在 Capture 中；不同线程与父子 scope 会重叠。禁止把表内全部值相加成一帧耗时。报告另列累计耗时/显示更新帧：两份 Capture 分别 16.91/21.33 ms，FluxSolver 为 1.46/1.97 ms；它们和“均值/调用”分母不同。

两份记录所有 Main Thread 帧的 GPU 状态都包含 `NotSupportedWithNativeGfxJobs`。截图 1.559 ms 不能用作完整 GPU 时间；Render Thread 大量 `GfxDeviceD3D12.WaitForGPU` 能证明等待，却不能单独区分宽核 Compute、透明 overdraw、驱动同步或 Profiler 查询影响。D3D12 Task 截图还有 ReadValue，导出的部分 native marker 无名称，不能仅凭名称推定它就是全部根因。[Unity 6.3 GPU Usage 文档](https://docs.unity3d.com/6000.3/Documentation/Manual/ProfilerGPU.html)说明 Graphics Jobs 下不支持该 GPU profiling；[CPU marker 文档](https://docs.unity3d.com/6000.3/Documentation/Manual/profiler-markers.html)说明相关等待的解释边界。没有把 CPU Dispatch 的约 0.06 ms 写成 GPU 执行时间。

## 2. 本轮等价优化与固定输入结果

修改 `GridLiquidFieldFrame.cs` 的三个位置：

1. 圆冠塑形第一次检查数量时，把湿行地址按原线性顺序写入初始化预分配的 `int[]`。后续连通、质心、面积、圆冠与姿态遍历湿行；不反复扫描空气，也不清零永远不被读取的空气 Body。保留 Union 和浮点累加顺序。固定工作表满时回到原有完整遍历，不能截掉多余液体。
2. 生成包围盒先看 packed 整数数量；空气直接跳过，再解码湿行的 Half 姿态。包围盒公式与有效输入相同。
3. 将已有完整 History changed-cell 列表映射成每页 `bool`。只有该页没有 History、没有 Previous/Current Stable Patch，且本行数量全为零，才跳过逐格 Gameplay/History 查询。当前空格中仍可能存在 Previous 水，Stable 虚拟表面也可能没有 Dynamic 数量，两者都必须保留。

工作数组仅在初始化创建；湿行表最多 131072 个 int（载荷上限约 512 KiB），另加每输入页一个 bool。CPU snapshot-only 副本使用空数组，未机械复制塑形工作区。原 GPU 128 MiB 预算、r2/r3、1.8 显示倍率、Palette 和模拟参数均保持。

受控输入是同一 Collider、同一 Profile 参数、同一注入顺序：48 个分散的 8³ Chunk、192 个湿格，三材料合计 46080 GMU，含接地与空中输入。先暖机，六轮每轮八次 Capture。原实现和最终实现源文件、数据、对照 patch 均归档；计时只记录，不用容易受系统负载影响的毫秒阈值判测试失败。

| 相同输入对照 | 原实现均值 | 最终实现均值 | 说明 |
| --- | ---: | ---: | --- |
| 48 个稀疏 Chunk | 29.2753 ms | 18.0368 ms | 减少 **38.39%**；48 输入页、356 Owner、Overflow=false |
| 极小容量，走完整塑形遍历 | 27.3366 ms | 22.1149 ms | 减少 19.10%；原实现也有 Owner 容量告警，前后均 144 Owner、Overflow=true |
| 原有较密集的混合/空中小场景 | 2.2217 ms | 2.2136 ms | 约 0.37%，视为接近不变，不宣称普遍 38% 加速 |

三组签名前后均相同：`3405202243`、`4186262329`、`3548606679`；上传 Previous/Current、地形和新场景 Owner Bounds 纳入核对。各组 Gameplay 总量与查询校验保持，暖机 Capture 分配均 **0 B**。对照原始轮次见 [manifest](manifest.json) 的 Comparisons，不能把这三个 CPU 方法结果换算成整个游戏的 FPS 提升。

首次低容量测试误用正常容量签名；随后恢复原实现确认每个容量的输出。该极小配置还将 Owner 上限压到 144，原实现同样 Overflow，首次错误地要求它为 false。修正测试前提后，保留每个配置的独立原始签名及原有 Overflow 告警。中间失败 [optimized focused](user-profiler-sparse-final-focused.json)、[original configurations](user-profiler-sparse-original-configurations.json)保留；它们不是已定位的生产行为 RED，也没有通过改容量或删除数量断言掩盖问题。

## 3. 最后集中检查与场景保留

[最终集中诊断](user-profiler-final-255-concentrated.json)：2026-10-04 **11:30:46—11:31:36 Asia/Shanghai**，28 个 Fixture，**255/255**，Failed/Skipped=0、Aborted=false、SceneRestored=true。直接 NUnit 方法与 Coroutine JSON，包含真实 GPU Readback / CPU Mesh；**不是官方 Unity Test Runner XML、全项目测试或正常 P7 帧率验证**。新增两个容量案例及 Previous-only 清空案例；密度、混色、坡脚、厚度、Stable 接触、可见性、新命中优先、休眠恢复、火焰视角等集中范围保留。

初次默认脚本仍引用旧 185 项索引，本轮得到 [216/216 子集](user-profiler-final-concentrated.json)，缺少后续可见性和火焰 Fixture。已把工具默认索引改为最新 L4.24 的 28 组，才运行最后 255 项；没有把两个批次相加。最后集中运行也再次输出相同签名、数量和 GC0，计时独立存为 `final-gate-*`，不覆盖上表固定对照。

11:34 只读 [P7 Audit](p7-grid-cutover-audit.txt) **Failures=0**；[最终 Editor 状态](final-editor-state.json)为 P7、Edit Mode/Clean、TimeScale=1、后台=false，临时场景已恢复。P7 磁盘 SHA256 与本轮开始相同：`bdc44a9b3529128a85843aa842eb83970faeaffe322ab4774baeb4014c860b83`。原根状态由持久记录恢复，没有猜测激活；无自动保存、丢弃、重启、commit 或 SubAgent。此前 L4.24 曾观察到的场景变化仍保留为历史，本轮“未变”只针对自己的起止基线。

## 4. 下一次实际游戏采样

在正常 Play、感觉卡顿的相同视角，打开 Profiler：只启用 **CPU Usage / Rendering**，关闭 **GPU Usage / Deep Profile / Call Stacks**；记录约 5 秒后停止并保存 `.data`。保持同一 Game View 分辨率，不改变法杖、质量档、Graphics Jobs 或 Graphics API。先停止施法看稳定阶段，再在同一处施放数组并再次录制；注明有没有移动镜头、打开说明/编辑界面。这样才能核对新代码下 CPU Capture 是否下降，以及关闭不受支持的 GPU 采样后等待是否仍在。

关闭 GPU Usage 只是减少诊断干扰的一步，不声称它就是全部卡顿原因。真正 GPU pass 成本仍需支持的环境或独立 Development Player 工具验证，本轮未更改项目设置或构建/重启用户关卡。GPU per-page 缓存、透明 overdraw、可见 CPU 缓存完成延迟、墙体 Occlusion、长时累积和 Standalone 60 FPS 仍保留边界。

复现离线统计：先按归档 `OfflineProfilerExport.cs.txt` 与 `offline-export-config.json` 在**独立**临时 Unity 项目导出，执行方法 `NornProfilerOffline.OfflineProfilerExport.Run`；然后从项目根运行：

```powershell
python Tools/ElementPerformance/analyze_saved_profiler.py Temp/ElementPerformance/user-profiler-20261004 docs/superpowers/validation/2026-10-04-liquid-profiler
```

导出原 TSV 在本机 `Temp/ElementPerformance/user-profiler-20261004/`，原 `.data` 保留用户提供位置；本索引不重复复制近 100 MiB 的 TSV。统计工具只读取文件，不操作 Unity 场景。Archive 工具在复制前核对最终 gate、生产源码、原始源码和场景 Hash，避免把临时还原版本误当交付版本。
