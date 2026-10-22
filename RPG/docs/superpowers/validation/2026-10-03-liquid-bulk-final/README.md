# 饱满圆冠与GPU端点采样：当前验证记录（2026-10-03）

当前实现包含动态宽水团Dome、上下圆肩、0.7格卡通圆肩、明确显示体积倍率1.8、Stable水位豁免、连续满格柱根索引、0.5mm底壳Depth Bias、CPU有效Vertex索引，以及GPU插值端点的单快照宽核采样。粗Grid、Gameplay数量、密度与反应预算保持原规则。没有保存/丢弃/重启P7，没有提交或使用SubAgent。阶段仍更新中。

## 1. 最后版本与通过的范围

- [最后211项集中诊断](liquid-bulk-endpoint-211-green.json)：北京时间18:33:10—18:34:24，20 Fixture、211通过、0失败/跳过/中止、SceneRestored=true。加载Editor中Direct NUnit方法/Coroutine，含真实GPU与CPU输出；不是官方Test Runner XML或全项目测试。
- [正式P7接线Audit](p7-grid-cutover-audit.txt)：18:49:31，当前加载场景含未保存状态的检查，Failures=0。
- [结束状态](grid-liquid-capture.json)与[实拍恢复记录](visual-scene-restored.json)：P7、Edit Mode、SceneDirty=false、RunInBackground=false、TimeScale=1。
- [66个源码/配置Hash及四个Assembly时间](source-manifest.json)绑定最后代码。Geometry相关C#及默认资产在18:08的首轮211之前完成；之后仅HLSL端点性能分支改变了执行工作量，最后211重新确认输出。最后检查后只修正一处旧2cm Shader注释，执行数学未改。
- [首轮211](liquid-bulk-final-211-green.json)和[新外形签名未更新的210/1](liquid-bulk-final-211.json)保留为历史。最后签名3548606679经过真实体积/颜色/闭合检查后锁定；签名变化来自主动改外形，性能优化不能随意重置它。

## 2. 实际Unity画面与体积

以下PNG来自Unity，没有使用用户的生成参考图替代实拍。

| 场景 | 实拍 |
| --- | --- |
| 小水团，2048 GMU | [正常水色、圆冠与侧面](continuous-lab-bulk-tick30.png) |
| 平台落水，21420 GMU | [Tick 5](continuous-lab-platform-tick5.png)、[Tick 15](continuous-lab-platform-tick15.png)、[Tick 30](continuous-lab-platform-tick30.png) |
| 同量18432 GMU宽水团 | [地面](continuous-lab-wide-tick30.png)、[空中](continuous-lab-air-wide-tick30.png)、[CPU](continuous-lab-wide-cpu-tick30.png) |
| 同密度数量混色 | [混色](continuous-lab-same-tick30.png) |
| 不同密度、独立材料色 | [分层](continuous-lab-layered-tick30.png)、[毒液](continuous-lab-poison-tick30.png)、[粘液](continuous-lab-sticky-tick30.png) |
| 60°坡脚、8湖局部接触 | [坡脚](continuous-lab-join60-tick30.png)、[湖泊](continuous-lab-lake-contact-tick30.png) |
| 最后实际P7混合施法 | [正常游戏实拍](liquid-bulk-production-endpoint-game.png) |

[11类实拍汇总](visual-windows.json)均Quantity=Injected、SmallDrops=0、GC0B、Overflow=false。平台已有12917 GMU到地面；坡脚2210 GMU中789已到地面。8湖保持2105280 Stable GMU，加入510活动毒液后总量2105790。小场景不是长期场景规模保证。

[最后真实Mesh积分](continuous-volume-calibration.json)的12个场景均一个连通主体、开边0、独立球模板0。物理单位体积`0.5³/510 m³/GMU`；Crown 0.15的校准档误差在20%内，卡通档以物理体积乘明确显示倍率为目标、误差在25%内。没有放宽测试阈值。

- 4×4格×128 GMU：峰值约0.891m，实际Mesh体积约1.058m³。
- 12×12格×128 GMU：地面峰值0.670m；地面/空中体积8.858/9.202m³，差约3.9%，物理数量均18432 GMU。CPU体积8.755m³。
- [同量坡面体积](continuous-support-volume.json)：2040 GMU在平地、30°、60°与集中2×2场景分别1.055/1.047/1.027/1.223m³。
- [坡面接触检查](continuous-slope-contact.json)、[实际GPU时间连续性](continuous-slope-temporal.json)、[暖机固定工作量](frozen-presentation-signature.json)随最后211重新运行。

最早宽水团RED、最初尝试以及上下圆肩中间版本在相邻`2026-10-03-liquid-bulk-before`、`...-attempt`、`...-rounded-air`目录保留。本目录`before-endpoint`保存GPU端点优化之前的11类实拍；最后只重拍bulk/platform/wide/air-wide/wide-cpu，其他6类仍是同一几何数学的端点优化前记录，不能说11类均在18:34以后重拍。

## 3. 正常推进的性能与瓶颈诊断

硬件NVIDIA GeForce RTX 4070 Laptop GPU，Unity Editor / D3D12。最后窗口TimeScale=1、MeasurementValid=true，开局说明关闭，走真实SpellCaster，每组水/火/毒/粘液。每窗口至少128正常帧，并保留独立完整60帧原始Profiler。

| 最后正常窗口 | 样本 | GMU | 活动格 / 页 | P50 / P95 ms | CPU回退 |
| --- | ---: | ---: | ---: | ---: | --- |
| baseline，含原有液体 | 130 | 6220 | 76 / 20 | 39.08 / 56.69 | 否 |
| 5组 | 143 | 28014 | 259 / 32 | 36.31 / 55.07 | 否 |
| 10组 | 129 | 46755 | 435 / 38 | 39.14 / 58.99 | 否 |
| 15组 | 132 | 60278 | 562 / 48 | 42.37 / 67.96 | 否 |

[全部正常窗口](performance-windows.json)包含端点优化前/后的8份报告。最后版本文件名前缀为`liquid-bulk-production-endpoint`，之前为`liquid-bulk-production`。两轮玩法总量、活动格、页数不同，不能把前15组249.76/264.68ms与后15组42.37/67.96ms写成严格等量A/B倍数。旧206项的30组33.61/63.30ms也不代表当前厚圆冠结果。

[最后15组原始Profiler](liquid-bulk-production-endpoint-live-mixed-15.tsv)及[摘要](liquid-bulk-production-endpoint-live-mixed-15.summary.json)：60完整帧、IncompleteRows=0；Capture均值10.16ms/次、4.57ms/frame，Crown 4.68ms/次、2.10ms/frame，Flux 4.88ms/次、2.19ms/frame，FireReaction 1.93ms/次、0.87ms/frame。Marker存在嵌套，不能相加作总CPU时间。Renderer热路径0B，整帧平均约517B、最大816B；尚未稳定60FPS。

严重卡顿时[优化前原始15组](liquid-bulk-production-live-mixed-15.tsv)显示Render Thread的`D3D12.WaitForGPU`约243.94ms/frame，主线程呈现等待约192.31ms/frame。固定26276 GMU/36页并暂停时钟的诊断文件为`liquid-bulk-isolation-{full,frozen,hidden,full-repeat}`：强制重建的P50约145.53/157.32ms，停Compute约16.49ms，隐藏液体约16.54ms。它们标记`DiagnosticFrozen=true`，仅定位Compute瓶颈，不列入正常玩法FPS。

修复在HLSL `Field`：Alpha为0/1时，直接采唯一参与的Previous/Current；中间相位保持双宽核插值。省去`lerp`的无用端点计算，没有减少采样节点、细分率、色层、更新或反应预算。真正GPU时间仍受`NotSupportedWithNativeGfxJobs`限制，不能用Dispatch CPU时间或状态里单个GPU值当作完整GPU耗时。

CPU兼容输出最后实拍为270帧、累计934.86ms工作、11275.28ms显示等待，体积8.754889m³；优化前是280帧、1128.37ms工作、63164ms等待。主场景GPU阻塞会减少每秒能推进的CPU分片数；两者不能混为CPU计算时长。GPU预算128MiB、实际主Renderer约121.67MiB。超出GPU有效页/输入容量时仍完整CPU回退，当前兼容等待不适合大量活动液体。

## 4. 剩余限制与制作方法

局部平台溢流仍可见折面，很宽且每格液量极少的水团仍呈较扁比例；尚未完全达到生成参考图的所有曲面。全程稳定60FPS、长期更大活动区、Standalone Player及完整GPU计时没有通过。不得以删液体、减少Gameplay采样、降低色层或球形实例换取“通过”。Shader还有寄存器/动态数组warning；本轮加载代码和实际GPU检查通过，不表示这些性能警告消失。

湖泊继续使用闭合非Trigger Collider湖盆、Trigger Authoring Bounds与Stable数量表示；局部出口/接触才展开活动区域。细节及厚度验证见[湖泊与地形制作指南第十节第5、9小节](../../../../Assets/_Project/Docs/湖泊与地形制作指南.md)，原理、脚本职责及Debug路径见[完整解析L4.23](../../../../Assets/_Project/Docs/2026-09-20%20通用液体底层重构完整解析（更新中）.md)。
