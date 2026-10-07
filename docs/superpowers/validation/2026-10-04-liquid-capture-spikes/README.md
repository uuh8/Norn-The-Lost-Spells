# L4.27：高 ms 峰值与液体显示采集优化

2026-10-04，阶段继续更新中。本轮降低了 CPU 的只读液体采集成本；正常游戏 FPS 尚未出现可靠改善，液体 Compute 重建仍是后续工作的主要瓶颈。没有调整真实液量、Density、反应、角色接触、显示厚度、细分精度、GPU 预算或火焰样式。

## 1. 用户截图实际选中了同一帧

三张截图都是 UI Frame 5389/5814。第一、二张展示相同的 UpdateScene 展开，第三张折叠了同一 Scope。原始缓存导出中对应 Raw Frame 5388：

| 主线程或子 Scope | Inclusive Time | 含义 |
| --- | ---: | --- |
| Main Thread | 95.5243 ms | 该帧约 10.47 FPS |
| UpdateScene | 85.2943 ms | 包含下面的等待和脚本工作 |
| WaitForLastPresentationAndUpdateTime | 38.0622 ms | 等待先前呈现/图形工作；子 WaitForGPU 38.0531 ms，不能再相加 |
| ScriptRunBehaviourLateUpdate | 35.4473 ms | 液体 Renderer 占 35.2372 ms |
| GridLiquid.Field.Capture | 28.8865 ms | 采集只读快照与塑形 |
| Capture 内 Crown / ShapeBounds / PackCells | 12.9526 / 6.5507 / 6.5302 ms | 保留厚圆冠、边界与两帧上传数据的 CPU 工作 |
| PageReuse / CpuBuildPage | 2.3099 / 2.0954 ms | Capture 外的页复用检查、CPU 回退构建 |
| Update / ElementWorldRuntime.Update | 9.7258 / 5.9662 ms | 求解和反应等 Gameplay 工作 |

父子 Scope 不能重复相加，Render Thread 与 Main Thread 也可能重叠。截图 GC Alloc 约 0.8 KB，没有证据表明这一帧主要由 GC Collect 导致。Physics FixedUpdate 约 0.70 ms，不能把大峰值归因于物理步数。GPU availability 含 NotSupportedWithNativeGfxJobs，因此本机 GPU Usage 的个别计数不能代替完整 GPU pass 时间。截图内存类别未完全展开，11.94 GB 不作液体或 VRAM 占用结论。

原缓存 5214–5813 共 600 个索引，其中 245 个有 Main Thread 样本；空白段不能直接解释成游戏暂停。117 次 Capture 平均 25.9125 ms，最大 37.1348 ms。已先保存用户原始缓存，再开始实验；原始文件在 [Backups 中](../../../../Backups/ElementPerformance/L427/user-capture-20261004-114323.data)，SHA/长度见 manifest。完整 TSV、选中帧全线程行及状态分别见 [user-all-before.tsv](user-all-before.tsv)、[user-selected-5389.json](user-selected-5389.json)、[user-all-before-state.txt](user-all-before-state.txt)。

## 2. 保留的 CPU 优化与严格对照

- Half 的 65536 种位模式由 Unity 原生转换一次生成只读 float 表，CPU 后续直接查询；共 256 KiB。BeforeSceneLoad 与 Frame 构造预热，避免第一帧第一次使用分配。GPU 的 16 字节 Packed Layout 和上传内容保持。
- PackCells 时收集 Previous 或 Current 任一帧有量的行，保留原线性顺序。圆冠、支撑列合并、塑形边界复用这些行；工作表满时完整扫描，不漏水。每个 phase 的旧完整参考仍保留其原有湿行列表，避免人为放慢参考实现。
- History 用每行一 bit 判定，替代“这一整页曾有变化”的粗筛；Previous-only 尾水仍进入插值。没有 Stable patch 的 phase 立即返回零；姿态缓存命中后不重复求数量。CopyFrom、GPU 域重排和 Capture 会按原生命周期清除缓存。

| 最终固定输入指标 | 原完整扫描参考 | 优化路径 | 结果边界 |
| --- | ---: | ---: | --- |
| 48 输入页、46080 GMU 的 Capture，六轮各八次平均 | 20.9936 ms | 10.4390 ms | 减少 50.28%；两种模式都使用精确 Half 表；不是游戏 FPS |
| Half 解码循环，九轮交替中位数 | Unity native 5.9113 ms | 查询表 2.1946 ms | 全部位模式 bit-exact；不是整帧提速比例 |

Previous/Current、Terrain、Owner Bounds 联合签名保留 3405202243，356 Owner，Overflow=false，暖机 Alloc=0。低容量 fallback 保留 4186262329、46080 GMU、144 Owner、原有 Overflow=true、Alloc=0；该案例没有可靠速度收益，不宣传所有场景减少 50%。所有初始化内存都固定分配，未加入逐帧数组、LINQ 或 Boxing。

最终源码、每文件 SHA、本轮增量 patch 和固定输入记录见 [manifest.json](manifest.json)、[l427-sparse-final.json](l427-sparse-final.json)、[l427-dense-final.json](l427-dense-final.json)、[l427-half-final.json](l427-half-final.json)。RED、GREEN 和中间误差均留档，不只保留通过结果。

## 3. 正常游戏与 GPU 隔离结果

正常窗口均 TimeScale=1、MeasurementValid=true；临时诊断 Play 只为防止死亡暂停启用玩家保护，退出后恢复，不改变生产玩法。GPU 预算实际 131774080 字节，Renderer/火焰路径暖机 GC=0，以下两次均无完整 CPU fallback：

| 正常四法术 20 组窗口 | 总量 / GPU 页 / 活动格 | P50 / P95 |
| --- | --- | --- |
| 原代码 l427-before | 43850 GMU / 26 / 369 | 35.7990 / 51.6755 ms |
| CPU 优化后 l427-after | 43390 GMU / 26 / 206 | 37.7879 / 54.6990 ms |

不同反应、分布和时序不构成严格 A/B；上述结果**没有证明整体 FPS 改善**。随后 l427-isolation 正常运行更慢：baseline 98.7878/165.2924 ms，20 组 211.8759/242.0386 ms。它的视域、数量与前轮不同，保留全部原始记录；不能把这轮变化归为已确认的 GPU 回归、VRAM 泄漏或某个唯一原因，也不能隐去慢窗口。

随后冻结同一快照，25430 GMU、26 GPU 页，TimeScale=0，明确作为隔离实验：

| 冻结诊断模式 | P50 / P95 | 用途 |
| --- | --- | --- |
| 强制全部 Compute 重建 | 311.1516 / 340.8147 ms | 放大重建成本，超过正常生产重建范围 |
| 冻结 Compute，继续 Draw | 16.5298 / 22.6926 ms | 已缓存的液体、火焰绘制仍在 |
| 隐藏液体 Draw | 16.6303 / 23.0654 ms | 对照液体绘制成本 |
| 再次强制全部重建 | 324.1037 / 359.7990 ms | 重复确认 |
| 隐藏液体和火焰 Draw | 16.2169 / 23.4216 ms | 进一步对照绘制 |
| 恢复火焰，液体仍隐藏 | 16.9060 / 23.5286 ms | 检查火焰恢复后的边界 |

该快照主要成本随液体 Compute 重建出现，单纯显示已有曲面远轻于每次重建。冻结诊断不包含正常 Gameplay，不是正常游戏 FPS，更不是完整 GPU timestamp。本轮不靠隐藏显示或冻结 Gameplay 提高验收数字。后续优先分析宽核场值、最终 Normal/颜色采样以及重建依赖，不能简单降低美术精度。

曾试 32-thread group 降低高寄存器 kernel 的调度压力；6 项检查中 3 项失败（一次 Coroutine timeout，两次 Half 颜色等价失败），没有得到可信净收益。实验已全部撤回，生产 Compute、Renderer、原 GPU 性能测试和 Sampler 的 SHA 与本轮开始一致。保留 rejected-thread-group 文件和 [失败报告](l427-thread-probe.json)，没有放宽比较精度来通过。编译器寄存器警告仅为线索，未证明唯一根因。

## 4. 最终验证与交付状态

2026-10-04 20:17:55–20:18:51（Asia/Shanghai），最终代码 29 Fixture、**261/261 通过**，Failed/Skipped=0、Aborted=false、SceneRestored=true。包括真实 GPU Readback、CPU Mesh、坡脚/Stable/分层、History、可见性、休眠与火焰正视/俯视检查。这是直接 NUnit 方法/Coroutine 诊断，见 [l427-final-after-probe.json](l427-final-after-probe.json)，不是官方 TestRunner XML 或全项目测试。

最后五个 GPU 对照的 Position/Normal/Swell 最大误差均为 0；Half 颜色和 Draw Args 在测试中严格比较。P7 只读 Audit 0 失败；最终 EditMode/Clean、TimeScale=1、后台 false、十根原状态恢复，场景 SHA 保留 bdc44a9b3529128a85843aa842eb83970faeaffe322ab4774baeb4014c860b83。本轮没有保存、丢弃或重开关卡，没有 Commit 或 SubAgent。

本轮实现可复核的 CPU 减负，GPU 重建及真实游戏累积卡顿仍未完全解决。阶段、Standalone 60 FPS、完整 GPU 时间和长期压力继续标为未验收。学习解释见主文档 L4.27 和《湖泊与地形制作指南》第十四节。
