# 2026-10-02 液体修复验证索引

> 本目录记录L4.19历史185项及旧厚度。2026-10-03最终源码194项、共同坡脚、等量量感和四法术性能见[当前证据索引](../2026-10-02-liquid-rounded-join/README.md)。下文暂停性能的优先级已由用户新请求更新，不能用本目录结论代替当前版本。

本次收口范围是用户最新截图中的三项：去掉独立圆珠、让小量液体越过真实坡脚进入平地、让浅层有明显的卡通厚度。长时坡面与性能扫测按用户要求暂停。通用液体阶段仍标记为“更新中”；这里没有把历史167/167、旧172/178/180批次或无效性能窗口当作最终验收。

## 1. 当前代码实际通过了什么

| 检查 | 新鲜证据与结果 | 证据边界 |
|---|---|---|
| 集中回归 | [continuous-regression-185.json](continuous-regression-185.json)，2026-10-02 04:57:06—04:57:28 UTC（北京时间12:57）；185/185，Failed=0，Skipped=0，Aborted=false，SceneRestored=true | Unity 内直接执行19组NUnit Fixture的方法诊断，包含实际GPU Readback和最后的圆肩Normal修复；并非官方Unity TestRunner XML。 |
| 少量坡脚外流 | 30°/60° × 32/128GMU四用例，总量保留、坡外地面收到至少一半；修改前四例全部接收0，见[toe-thickness-red.json](toe-thickness-red.json) | 验证真实旋转BoxCollider与相接平地；复杂多平台/Terrain仍采用有界支撑近似。 |
| 真正几何体积与厚度 | [continuous-volume-calibration.json](continuous-volume-calibration.json)，9个GPU/CPU场景；闭合边缺口全部0、Drops=0；校准档指定样本体积误差均低于20% | 积分的是GPU Buffer/CPU Mesh顶点，未包含Vertex Shader的微小起伏；不是任意形状严格守恒。 |
| 空中相邻小量 | 16格空中小量共用一个闭合外壳，Components=1、Drops=0，体积误差+3.76% | 真正空间断开的液量仍可能呈现独立不规则液片。没有实现粒子PBF或严格Surface Tension力学。 |
| 坡面接触 | [continuous-slope-contact.json](continuous-slope-contact.json)，30°/60°实际GPU几何，统计顶点最低离面约+0.086/+0.050mm；Under=0、UnderTop=0 | 断言检查被统计的全部顶点，允许1mm数值容差；有限深度交叉约束不代表任意模型无穿插。 |
| 跨Tick连续性 | [continuous-slope-temporal.json](continuous-slope-temporal.json)，实际几何面积加权中心在边界相邻样本变化不超过2mm | 证明该边界没有离散跳格；不等于整机在所有负载下没有卡顿。 |
| CPU回退与生命周期 | 集中回归包含无Compute小量共同Mesh、GPU预算回退、禁用/重启Renderer、Palette三色传递、热路径分配检查 | 大范围CPU回退仍可能明显落后；下面的小场景GC不代表全游戏GC为0。 |
| 实际P7施法 | [p7-grid-real-cast.txt](p7-grid-real-cast.txt)，05:01:00 UTC，真实SpellCaster路径Emitted=3；临时克隆法杖，没有改用户Wand资产 | 施法链通畅不等于三个颜色已在全部实际关卡光照条件下通过美术验收。 |
| 当前P7配置审计 | [p7-grid-cutover-audit.txt](p7-grid-cutover-audit.txt)，05:04:19 UTC，Failures=0 | 只读检查加载的Dirty关卡；不会保存场景或重建用户地形。 |

最后的Normal修复有独立诊断链：[修改前纯色](contact-normal-before-opaque.png)外壳完整，[修改前Normal](contact-normal-before-normals.png)显示低侧壁朝下，[修改前光照](contact-normal-before-shaded.png)出现褐色三角齿；新增[RED报告](shoulder-normal-red.json)为184通过/1失败，低侧壁Normal.x错误为0。CPU/GPU按场梯度朝向保留外侧法线后，最终截图已更新。中间一次[185用例批次](normal-after-gpu-readback-failure.json)有GPU Readback.hasError失败；没有删除或跳过断言，[渲染专项10/10](normal-rendering-green.json)与最终185/185均通过。Readback偶发失败根因尚未单独定位，保留失败证据，不能称从未失败。

## 2. 最新Play截图与小场景数据

以下五个场景均使用临时对象、克隆Profile和独立Source；图像来自真实Unity Camera的1280×720 RenderTexture，没有后期重绘。Tick30为3秒模拟之后固定状态的截取，画面DisplayLag=0不能作为动态高负载延迟结论。JSON中的CaptureMs是Renderer采样/打包耗时的一次观测，不是完整帧耗时。

| 场景 | JSON / PNG | 液量 / 注入 | 外部地面接收 | CPU回退 | CaptureMs | Renderer GC |
|---|---|---:|---:|---|---:|---:|
| 厚水团bulk | [数据](continuous-lab-bulk-tick30.json) / [画面](continuous-lab-bulk-tick30.png) | 2048/2048 | — | 否 | 0.4981 | 0B |
| 圆肩blob | [数据](continuous-lab-blob-tick30.json) / [画面](continuous-lab-blob-tick30.png) | 3060/3060 | — | 否 | 0.5684 | 0B |
| 小量CPU | [数据](continuous-lab-thin-cpu-tick30.json) / [画面](continuous-lab-thin-cpu-tick30.png) | 512/512 | — | 是 | 0.3894 | 0B |
| 30°坡脚 | [数据](continuous-lab-toe30-tick30.json) / [画面](continuous-lab-toe30-tick30.png) | 128/128 | 119 | 否 | 0.4643 | 0B |
| 60°坡脚 | [数据](continuous-lab-toe60-tick30.json) / [画面](continuous-lab-toe60-tick30.png) | 128/128 | 121 | 否 | 0.2875 | 0B |

设备：NVIDIA GeForce RTX 4070 Laptop GPU。五例SmallDrops均0。浅水4×4样本在0.5m粗格、默认Cartoon Crown Strength=0.8下，每格32/128GMU实际顶点最大高度分别25.02/33.86cm；校准档0.15分别3.62/14.44cm。美术档显示体积分别约理论GMU体积的7.08/2.58倍，实际填槽Quantity保持不变，满格与深水水位不使用这个膨胀。Vertex起伏仅朝上、最大额外3cm，12cm接地范围内衰减到0。

![最新真实Unity圆肩液团](continuous-lab-blob-tick30.png)

当前主体和侧壁已经明显增厚，轮廓的r2粗采样仍能在近景看到少量折线和局部接触边缘细节；未宣称达到Unity_Slime的粒子细节或软体弹性行为。

## 3. 性能数据怎样解读

最新真实P7施法后原始快照见[p7-runtime-final.json](p7-runtime-final.json)，05:03:14 UTC：26个连续表面页、GPU路径、Capture=3.7751ms、DisplayLag=100ms、Renderer GC=0B。当前Editor P7最近300帧窗口P50=22.36ms、P95=40.99ms、Max=56.82ms，TimeScale=1、MeasurementValid=true，9个Resident Chunk、90个Active Cell、11020GMU；这组Editor结果没有达到60FPS目标，仍不能替代Standalone Player、多湖泊与大面积活动液体的压力验收。Capture仅为采样/打包时间，不能与P50相等或推导完整GPU耗时。此前04:27的24页一次观测仍保留为[p7-runtime-observation.txt](p7-runtime-observation.txt)转录，仅作历史记录。

容量压力记录[continuous-p7-capacity-pressure.json](continuous-p7-capacity-pressure.json)与[Profiler TSV](continuous-p7-capacity-pressure-profiler.tsv)是02:50 UTC、此次三项修复前的历史窗口：60620GMU、31个Resident Chunk、852个Active Cell、68个表面页，GPU资源133921096B接近128MiB上限，转CPU r2后Capture约16.55ms、DisplayLag约1581ms。只有62帧且MeasurementValid=false；P50/P95/Max不得用作最终帧率验收，也不能与当前24页的一次观测计算加速倍数。

历史Profiler里Capture均值14.73ms、Pack5.95ms、Crown8.03ms，Flux6.15ms、Fire4.76ms；这些可能存在嵌套与一帧多Tick，不能直接相加为每帧总成本。Native Graphics Jobs环境未取得有效完整GPU计时，Dispatch的CPU提交时间不代表GPU运行时间。大量活动页的CPU回退延迟、全游戏零GC、60FPS尚未验收；长时压力优化留待后续恢复，当前没有提高GPU预算。

已实现的反应优化有单独断言：256个远处Fire样本在没有液体接触时，World活动查询从5376次降至0，实际液体接触的反应仍保留。它不能替代相同负载的Profiler对照。

## 4. 关卡保护与阅读入口

最初恢复见[unity-restoration-2026-10-01.txt](unity-restoration-2026-10-01.txt)。最新原生Editor核对见[unity-final-restoration.txt](unity-final-restoration.txt)，05:04:26 UTC：只加载P7，P7为Active/Dirty，原十个根对象全部激活，Playing/Paused/Compiling/Updating均false，ProfilerEnabled/ProfilerGPU/RunInBackground均false；临时后台运行恢复到本轮开始前实测的false。临时验证室已关闭，没有保存、丢弃、重启用户关卡，没有提交。

初学者学习解释见`Assets/_Project/Docs/2026-09-20 通用液体底层重构完整解析（更新中）.md`的L4.19；制作、注水、同密度混色、不同密度叠层和开口验证见`Assets/_Project/Docs/湖泊与地形制作指南.md`。实现执行记录见`docs/superpowers/plans/2026-10-01 液体连续输运执行记录.md`。后续执行前先读交接文件顶部的新状态，不要再执行旧的Untitled恢复步骤。

`source-hashes.json`记录本次收口时相关源码的SHA-256，供后续区分源码漂移；它不是构建产物签名，也不替代回归报告。
