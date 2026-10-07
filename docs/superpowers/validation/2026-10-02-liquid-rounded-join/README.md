# 共同坡脚、等量量感、卡通厚度与连发卡顿验证

> 本目录为11:36以前的194项版本与相应性能历史。后续冷支撑与累积元素CPU优化的最终206项、源码Hash和实际窗口见[2026-10-03最新验证](../2026-10-03-liquid-seam-performance/README.md)。本目录下“最终/没有再修改源码”只描述这份历史快照，不代表现在的工作区。

当前源码与结果截至2026-10-03（Asia/Shanghai）。本目录的文件名包含10月2日，是这批接缝任务的起始日期。所有JSON时间均为UTC；下表同时给出北京时间。旧167/185/190结果保留作历史，不代表当前源码。

## 1. 当前代码真正通过了什么

[最终集中诊断](final-liquid-regression-194.json)：2026-10-03 03:30:18—03:30:27 UTC，即北京时间11:30。19个明确Fixture，194/194通过，Failed/Skipped=0，Aborted=false，SceneRestored=true。包含实际GraphicsBuffer Readback、CPU几何、体积积分/闭合、双帧连续性、密度/颜色/身份/反应、资源生命周期与暖机分配，以及最后的GPU→CPU→下一CPU批次Stable裁剪交接。

这是保留已加载关卡的**Direct NUnit method invocation**，不是官方Unity Test Runner XML，也不是整个项目测试总数。它直接驱动对应Coroutine；相关Fixture不依赖官方Runner的UnitySetUp/LogAssert/OneTimeSetup。最终程序集时间及39份相关源码SHA256见[source-manifest.json](source-manifest.json)。运行后没有再修改生产C#、Compute或Shader。

必要的RED/GREEN分别保存：

- [预算与空格读取RED](performance-layout-red.json)：原输入1024/细页64不满足96页要求；空格仍解码旧运动位。
- [CPU调度RED](cpu-bounded-work-red.json)：单次Update直接完成一整页，不能在页内让出执行权。
- [CPU专项GREEN](cpu-bounded-work-green.json)：14/14；最终194项包含其后的空Halo早退，不拿这个中间批次代替最终结果。
- [Stable交接RED](cpu-stable-domain-red.json)：6通过/1失败；CPU首批构建中Stable裁剪被提前关闭。最终194项保留并通过新增断言。
- `joined-shape-red.json`及`before-steep-proxy-*`是中间失败/画面，保留用于解释排除过程。

## 2. 三项画面问题的证据与边界

### 接缝和圆珠

相接坡面/地面先合并连续覆盖率，再提取共同外壳；最终压缩、接地后重建几何Normal。足够厚的平地圆冠直接采样，陡坡圆肩继续保留厚代理，避免一个Cartesian Cube同时跨过顶底而折成条片。Owner Bounds包住代理厚度。所有液体走共同场，独立球模板数量为0。

修复前60°横条与本次正常材质实拍：

![陡坡厚圆冠误用直接采样时的横条](before-steep-proxy-continuous-lab-join60-tick30.png)

![最终60度坡脚共同外壳](continuous-lab-join60-tick30.png)

同一冻结快照还有[不透明诊断](continuous-lab-join60-opaque.png)和[原始Normal诊断](continuous-lab-join60-raw-normals.png)。纯色只用于排除光照，最终图保留原材质。近景接触带仍能看见少量折线、细小尖角；本轮没有声称全部接缝达到参考项目的美术细节。

[真实坡面输出](continuous-slope-contact.json)：可见顶面UnderTop=0；隐藏底壳最低约-19.84/-17.94mm，是最多2cm接地嵌入规则，不能说全体顶点都在Collider外。法线断言只统计实际坡面范围、远离12cm接触带且面积足够的朝上Triangle：30°294个、60°189个，ReversedTopNormals=0；不覆盖全部圆肩/退化小面。

[真实GPU时间连续性](continuous-slope-temporal.json)：最后194项批次中，25ms相位步进最大面积中心变化约23.19mm；Tick交界差约17.10微米。这是指定运动样本的连续性，不能替代任意复杂场景的逐帧检查。[60°同快照CPU/GPU节点](continuous-lab-join60-tick30.json)最大Field误差约2.34e-5。

### 等量量感和凸出厚度

不再对各格单独四次根膨胀。连接液量形成共同液团，横向面积约缩为s²，厚度约乘1/s²，再加有界离散重建补偿。Gameplay GMU、MaterialId及反应没有被Rendering改写。

[同量2040 GMU实际闭合Mesh](continuous-support-volume.json)，理论0.5m³：

| 支撑/分布 | Mesh体积m³ | 最大沿支撑Normal深度m |
|---|---:|---:|
| 平地4×4 | 0.597511 | 0.411233 |
| 30° 4×4 | 0.616590 | 0.356138 |
| 60° 4×4 | 0.619026 | 0.209026 |
| 平地2×2集中 | 0.622770 | 0.500100 |

最大/最小体积跨度约4.23%。坡面Normal深度与竖直高度不同，不能把两个数当成同一厚度方向。[九组校准/美术积分](continuous-volume-calibration.json)保留各自Expected/Actual：默认美术档4×4、128GMU/格峰高41.27cm，体积0.599664/理论0.501961m³；32GMU/格峰高24.09cm，体积0.138796/理论0.125490m³。校准GPU/CPU在20%容差内，美术档样本在25%内；全部记录OpenEdges=0、单一连通分量、球模板0。闭合积分包含隐藏底壳，不是任意运动形状的严格物理体积保证。

![2048GMU的小液团圆肩与侧壁](continuous-lab-bulk-tick30.png)

### 本次真实Play实拍

九类截图首先在北京时间10:55—10:56由独立临时Collider/Runtime/相机捕获；最后Stable交接修改后，11:35重新捕获lake-contact、thin-cpu，其余七类几何没有再改。PNG有同名JSON，全部来自Unity实拍。截图、GetData和诊断列表分配不作为FPS窗口。

| 场景 | 注入/保留GMU | 坡外地面GMU | 输出页 | 路径 | 最近Capture ms | Renderer GC B |
|---|---:|---:|---:|---|---:|---:|
| join30 | 2210 | 542 | 12 | GPU | 0.7832 | 0 |
| join60 | 2210 | 789 | 16 | GPU | 1.1620 | 0 |
| bulk | 2048 | — | 18 | GPU | 0.4411 | 0 |
| thin-cpu | 512 | — | 2 | CPU | 0.5518 | 0 |
| same（等密度1:3） | 7680 | — | 18 | GPU | 0.4700 | 0 |
| layered（默认密度） | 8160 | — | 18 | GPU | 0.5900 | 0 |
| poison | 8160 | — | 18 | GPU | 0.4200 | 0 |
| sticky | 8160 | — | 18 | GPU | 0.4563 | 0 |
| lake-contact（8湖及局部毒液） | 2105790 | — | 45 | GPU | 13.6566 | 0 |

各场景Quantity=Injected、Overflow=false、SmallDrops=0。lake-contact的Stable Quantity=2105280，其余510属于活动法术液体；只证明这组湖泊表示和交接，13.66ms是一次CPU快照，不是多湖60FPS。CPU低量图证明兼容输出完整，r2轮廓较方，不能用它宣称全部兼容画面与GPU美术一致。

[等密度混色](continuous-lab-same-tick30.png)、[毒浮水上](continuous-lab-layered-tick30.png)、[毒绿色](continuous-lab-poison-tick30.png)、[粘液橙色](continuous-lab-sticky-tick30.png)、[CPU外壳](continuous-lab-thin-cpu-tick30.png)、[8湖接触](continuous-lab-lake-contact-tick30.png)。

## 3. 四法术连发卡顿：诊断、修复与实测

机器：NVIDIA GeForce RTX 4070 Laptop GPU，Unity 6000.3.16f1 Editor，URP17.3。使用实际SpellCaster、四个既有Spell资产与临时WandLoadout；每5组=20个Projectile。没有改用户法杖资产。反应和运动使不同轮次数量/分布不同，表中的前后轮次**不构成同一负载的固定加速倍数**。

原图的RenderLoop约1203ms、GCWorker与Scripts同时很高，GPU显示No Value；仅凭截图不能归因于空中输运。本轮明确复现的瓶颈是GPU细页容量导致整体CPU回退，CPU每帧构造一整页宽核，继而拖慢整帧与显示。

GPU默认输入预留1024份会占用约56MiB，实际只有数十个输入Chunk；改成256输入、96个r2细页，Gameplay上限及128MiB硬预算保留。本机实际共享GPU分配127579776B=121.67MiB（包含CPU Owner域Buffer），布局测试包含2MiB域保留量。

CPU减少空格Half解码和Vector乘加；alpha=1不再计算完全无效的Previous端点；湿格数量加权姿态按快照固定容量缓存；颜色只读数量。CPU构建按Nodes→Vertices→SmoothA/B→Finish→Normals→Faces分段推进，目标约2ms/帧，每32个元素查时钟、最多8192个元素；全部批次完成后整体交换，保留旧完整结果。Mesh/native调用仍可能超过软预算。

最后修复了这种“保留旧结果”与Stable湖面的交接：裁剪区域跟随实际正在显示的完整快照。第一次CPU批次未完成时仍裁旧GPU域；已有CPU结果时保留旧CPU域，下一份CPU完整提交才上传新域。不会同时叠画更旧GPU与旧CPU外壳；回到GPU时清除CPU可见批次，避免后续回退复活过期表面。最后集中诊断和11:35实拍使用这份代码。

有效live窗口全部TimeScale=1、Playing=true、Samples≥128、唯一统一Renderer、Frame推进。JSON保存窗口帧时间；对应TSV保存最后60个Profiler帧。完整汇总见[33个窗口记录](performance-windows.json)。下列均是Editor数据：

| 轮次/累计四法术组 | GMU | 页/路径 | 样本 | P50/P95 ms | 最近Capture ms | Display Lag ms |
|---|---:|---|---:|---:|---:|---:|
| 修改前有效窗口/15 | 58349 | 54/GPU | 132 | 27.28/58.76 | 21.27 | 100 |
| 增加页预算后/25 | 50550 | 88/GPU | 144 | 29.93/64.08 | 18.41 | 100 |
| 尚未分段CPU/30 | 49134 | 97/CPU | 128 | 113.49/444.12 | 22.45 | 4135 |
| 页内分段后/15 | 43274 | 50/GPU | 137 | 28.67/43.99 | 10.05 | 100 |
| 页内分段后/30 | 57429 | 97/CPU | 133 | 27.58/59.28 | 21.33 | 1578 |

[CPU分段前原始数据](pressure-final-valid-live-mixed-30.tsv)、[CPU分段后原始数据](pressure-cpu-sliced-valid-live-mixed-30.tsv)和各自summary：同名`GridLiquid.Field.CpuBuildPage`标记，旧单位是整页，新单位是本帧构建片段，**不能把平均调用比值解释为总构建速度比**。旧平均111.58ms/最坏452.15ms，新平均1.70ms/最坏4.73ms。最终97页窗口整体Main Thread GC metadata平均约510B/帧、最大1472B；Renderer累计GC=0B，整个游戏并非0B。

当前15组GPU窗口Capture标记平均10.91ms/次，FluxSolver平均5.27ms/次，FireReaction平均5.63ms/次；97页CPU窗口FluxSolver约9.25ms/次、FireReaction约4.87ms/次。Marker有嵌套、同帧多调用，不能简单相加当总帧时间。

同一102662GMU、60页冻结快照，强制每帧完整Compute的P50/P95约17.48/29.87ms；冻结Compute约16.61/21.81ms；隐藏Draw约16.79/22.87ms。TimeScale=0，这组只隔离渲染工作，MeasurementValid=false，不能当正常玩法FPS。GPU计时状态含`NotSupportedWithNativeGfxJobs`；虽有数值，不作为完整GPU执行时间认证。

排除的无效证据：`pressure-before-live-mixed-15.json`的63页回退/P95约298ms只有64帧，MeasurementValid=false。最早TSV拷贝发生在导出结束之前，部分文件仅12帧且截断，未用来计算最终Profiler结论。Exporter现在Flush全部内容再写完成标记，Python等待该标记后复制；本目录用于结论的TSV均60帧、IncompleteRows=0。

**仍有边界：** 超过96有效r2页或256输入Chunk会明确CPU回退。CPU分段降低整帧尖峰，代价是表面完成较慢；97页本窗口的显示落后约1.58秒，不是100ms，也未证明这个规模能够每Tick完成重建。P95约44/59ms仍未达到60FPS。Standalone Player、完整GPU时间、长时间更大负载没有本轮认证。天然湖应使用Stable表示，持续把整湖转换为活动液体不能靠这个预算无限扩展。

## 4. 当前关卡与如何复验

最后只读审计[Failures=0](p7-grid-cutover-audit.txt)，时间03:36:16 UTC（北京时间11:36）；仅加载P7，Edit Mode、Dirty=false、Playing/Paused=false、RunInBackground=false，唯一统一Renderer。[最终状态](grid-liquid-capture.json)、[临时画面关闭后的状态](visual-scene-restored.json)、[诊断前保存的十个根对象原状态](gate-saved-root-state.json)均保留。十个原根原状态全部true，最终Gate恢复成功。本轮沿用用户当前Clean P7，不能按早期Dirty/Untitled历史再次恢复。Profiler恢复进入本轮时用户已有的Enabled=true；没有强制关闭用户录制。

33个性能窗口采集于最后Stable裁剪交接修改之前，对应[source manifest](before-stable-domain-source-manifest.json)保留。最后只改了`GridLiquidFieldSurfaceRenderer.cs`、`GridLiquidFieldCpuSurface.cs`的完整结果交接及`GridLiquidFieldFrameTests.cs`断言；没有改变页内预算、采样数学、GPU布局或模拟。当前源码Hash与最后194项见[source-manifest.json](source-manifest.json)。没有把此前压力窗口改写成最后交接代码已重新压力采样的结论。

没有保存/丢弃/重启用户关卡，没有改原法杖，没有提交、工作树迁移或SubAgent。所有既有工作区修改保留。

制作与现场验收见[湖泊与地形制作指南第十节](../../../../Assets/_Project/Docs/湖泊与地形制作指南.md)。读取实现原因见[完整解析L4.20–L4.21](../../../../Assets/_Project/Docs/2026-09-20%20通用液体底层重构完整解析（更新中）.md)。复跑工具在`Tools/ElementPerformance/`，持久保存于Temp外，避免Unity清理临时脚本；运行前保持现有Editor，工具均不保存Scene。
