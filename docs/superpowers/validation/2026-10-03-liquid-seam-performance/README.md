# 坡脚抖动与累积元素性能验证（2026-10-03）

本轮接续已批准方案，保留现有工作区修改、粗 Grid、数量账本、密度、厚度和反应规则。当前结果优先于上一目录的194项历史结果。下文时间为北京时间，JSON使用UTC。

## 1. 最终源码与通过范围

[最终集中诊断](final-liquid-regression-206.json)在15:25:09—15:25:46完成：**20个Fixture、206/206通过**，Failed/Skipped=0，Aborted=false，SceneRestored=true。包括原液体矩阵，以及本轮的冷支撑预算、缓存满/失效、Chunk移槽/复用、固定上传签名、毒爆槽位顺序和稀疏火格采样顺序检查。四个相关程序集均在请求前确认比源码新；最终源码Hash见[source-manifest.json](source-manifest.json)。

这是带关卡恢复保护的 **Direct NUnit method invocation**，包含Coroutine、GraphicsBuffer读取及真实CPU/GPU几何断言；不是官方Unity Test Runner XML，也不是整个项目的全部测试。没有保存、丢弃或重启P7，没有提交或使用SubAgent。

## 2. 坡脚抖动的已证实来源

原来的地形支撑缓存每Tick最多允许128次Collider查询。Rendering与Solver共用它；冷液体格超过预算后，显示会先采用水平粗格地板，下Tick预算恢复后又换成真实坡面。高度与Normal会跳变，坡脚尤其明显。

- [原版RED](seam-cold-support-red.json)：真实45°Collider上256个静态湿格、32768GMU。数量不变，只恢复采样预算，最大支撑高度变化 **0.646446705m**，8通过/1失败，场景恢复成功。
- [修复后支撑数据](slope-cold-support.json)：相同测试高度变化=0、Normal变化=0、Fallback增量=0。显示查询没有消耗Gameplay的128次预算，也没有填入Gameplay缓存；相关断言已纳入最终206项。
- [先前假设排除](seam-snapshot-before.json)：Current成为下一Tick的Previous时，8轮提交数量、支撑、姿态及81个坡脚采样点均保持一致。该检查在修复前已通过，原Temp文件名带red，不属于失败证据。

修复让显示使用独立的只读支撑缓存，使用同样的Raycast、Layer筛选、Collider高度/Normal，不改变输运速度、反应剂量或数量。静态地形通知同时使两个缓存失效。缓存满时循环复用最早插入的槽位，避免新探索地点永久重复Raycast；[满容量RED](seam-presentation-cache-recycle-red.json)为26通过/1失败，最终206项通过满容量及移动地板检查。

P7缓存容量为65536；新增循环索引数组固定约0.75MiB，Dictionary也在初始化预留，运行中不扩容。它增加启动期内存，换取稳定支撑和可复用查询；不是无限缓存或减少采样精度。

本轮还读取真实GPU运动Mesh：[时间连续性](continuous-slope-temporal.json)保留每个相位的面积中心与Tick接续数据。这是指定30°运动样本，不能代表任意复杂地形的每个顶点。近景坡脚仍能看到粗重建留下的少量折线和尖角，不能把支撑跳变消除解释为所有美术接缝已经完美。

## 3. 保留画面和Gameplay的CPU优化

### 固定输入对比

固定64格、三材料、负坐标、跨Chunk和空中输入，总量8022GMU，输入4页、输出8个Owner。A/B使用相同支撑修复，只关闭/开启这两项CPU优化：

1. 同Chunk的22邻格用线性地址直接访问，跨Chunk继续原FloorDiv/Dictionary；邻居及Union-Find遍历顺序保持一致。
2. 数量加权的行姿态复用于圆冠质心、最后偏移及Bounds。形变完成后清除原始缓存，再缓存最终姿态，保留Half解码/舍入与材料数量。

存储层另复用最近访问的Chunk对象，实时读取数量；Resident标记防止被回收的对象以default坐标误命中。负坐标、移槽、原点与远处反复复用、跨格转移及材料身份已经检查。

| 固定工作量 | 优化关闭 | 优化开启 | 范围 |
|---|---:|---:|---|
| 192次暖机Capture平均 | 2.3494ms | 1.6712ms | 同输入快照，约减少28.9% |
| 589824次数量读取平均 | 239.71ns | 85.49ns | 同查询序列，约减少64.3% |
| 上传签名 | 1543324119 | 1543324119 | 双相位全部上传位、输入页及Owner数量 |
| 数量累加校验 | 24643584 | 24643584 | Gameplay实时查询结果一致 |
| 暖机GC Alloc | 0B | 0B | 这两个受测热路径 |

[关闭优化数据](seam-fixed-ab-off.json)、[开启优化数据](seam-fixed-ab-on.json)、[关闭优化10项检查](seam-fixed-ab-off-gate.json)。源码切换使用finally恢复原始字节，随后重新编译与集中回归；两种临时源码及Hash保存在`seam-fixed-ab/`。上述计时早于最后的支撑缓存循环替换，受测暖机输入没有缓存淘汰；最后206项也保留相同签名与0B断言。它不是整游戏FPS提升百分比。

### 毒爆反应的预留空槽扫描

真实Profiler中`GridLiquid.FireReaction`约5ms，源码进一步发现接触去重会反复扫描65536个预留槽。现在只扫描最高使用位置之前的槽位，仍优先复用最低空槽；Tick结束仅收回尾部空洞，不交换中间条目。

因此同Tick输出顺序、destination满时停止、预热、局部冷却、范围合并、移动毒液的最新数量读取均保留。新增检查在4槽与65536槽两种容量下运行相同过程，原版[6项基线](seam-reaction-reserved-before.json)和最终206项均通过。最后还保持旧PBF使用同一表的接触对去重规则。

[65536槽基线](toxic-reserved-before-65536.json)与[优化后](toxic-reserved-scan-65536.json)：3072次固定无燃料/无匹配接触查询，平均315693ns→87.08ns，GC0B。这个极稀疏查询证明预留尾部不再被扫描，不能当成整场反应或FPS的加速倍数；实际大量活跃反应仍按使用槽位数增长。

仅改这张表后，真实窗口FireReaction仍约5.50ms，说明它不是该Marker的全部主因。Marker还包含火格采集：原版先为每个空格做三次除法/取余、构造坐标，再调用ToIndex读回同一线性位置。最终改为直接读取当前Cell数组，仅确认为Fire时还原坐标；每次采集重新取得Current Buffer引用，兼容Double Buffer交换。

没有改变256格采样预算、排序、Amount和游标。原版[17项采集基线](seam-fire-collection-before.json)与最终206项均通过；12轮跨负坐标、页边界和游标绕回的48份输出签名固定为1675499582，GC0B，见[原序列](sparse-fire-sample-sequence-before.json)和[最终序列](sparse-fire-sample-sequence.json)。新Marker分别记录CollectFire与ProcessContacts，帮助下一次定位而不靠猜测。最终实际耗时见本节末尾采样结果。

## 4. 实际Play、Profiler和限制

九类临时Play实拍均保持Quantity=Injected、Overflow=false、独立球模板0、Renderer GC0。缓存循环替换后复拍join60、thin-cpu和lake-contact；最后反应/采集修改保持顺序和数量，最终206项包含实际几何/体积与全部原反应检查。图片为Unity实拍，不把截图/Readback期间当作FPS采样。

| 场景 | 注入/保留GMU | 接收地面GMU | 页/路径 |
|---|---:|---:|---|
| join30 | 2210 | 542 | 12/GPU |
| join60 | 2210 | 789 | 16/GPU |
| bulk | 2048 | — | 18/GPU |
| thin-cpu | 512 | — | 2/CPU |
| same，等密度混色 | 7680 | — | 18/GPU |
| layered，默认密度 | 8160 | — | 18/GPU |
| poison | 8160 | — | 18/GPU |
| sticky | 8160 | — | 18/GPU |
| lake-contact，8湖与局部接触 | 2105790 | — | 45/GPU |

湖泊Stable量2105280GMU，另外510属于活动毒液。30°/60°接收数量与上一轮相同；[闭合Mesh等量体积](continuous-support-volume.json)也保留0.59751/0.61659/0.61903/0.62277m³，最大/最小跨度约4.23%。美术外壳体积与理论量有容差、含最多2cm隐藏底壳，不是任意运动状态的严格物理积分守恒。

[60°坡脚](continuous-lab-join60-tick30.png)、[圆冠厚度](continuous-lab-bulk-tick30.png)、[默认毒浮水上](continuous-lab-layered-tick30.png)、[同密度混色](continuous-lab-same-tick30.png)、[8湖接触](continuous-lab-lake-contact-tick30.png)、[CPU小量](continuous-lab-thin-cpu-tick30.png)。

所有性能窗口使用RTX4070 Laptop、Unity6000.3.16f1 Editor、真实SpellCaster四法术，每组4个Projectile。TimeScale=1、Samples≥128、MeasurementValid=true，临时法杖与后台设置在结束后恢复。[完整窗口表](performance-windows.json)保留数量、活动格、页面、P50/P95、显示延迟与Renderer GC。反应/运动造成不同轮次分布差异，不能用同一施法次数计算固定加速倍数。

| 采样版本/累计组数 | GMU | 页/路径 | 帧数 | P50/P95 ms | 显示延迟ms |
|---|---:|---|---:|---:|---:|
| 本轮优化前/30 | 103256 | 60/GPU | 152 | 29.15/69.31 | 100 |
| 寻址/圆冠优化后，循环缓存前/30 | 61476 | 50/GPU | 139 | 24.19/48.96 | 100 |
| 寻址/圆冠优化后，循环缓存前/35 | 68371 | 98/CPU | 146 | 26.27/59.68 | 1360 |
| 循环缓存后，反应空槽优化前/5 | 25147 | 32/GPU | 147 | 25.17/45.10 | 100 |
| 反应空槽优化后、采集优化前/30 | 71838 | 68/GPU | 149 | 28.16/56.99 | 100 |
| 反应空槽优化后、采集优化前/40 | 65332 | 98/CPU | 147 | 23.33/46.91 | 3506 |
| 最终采集优化/15 | 57792 | 48/GPU | 140 | 26.61/43.48 | 100 |
| 最终采集优化/30 | 103583 | 60/GPU | 145 | 33.61/63.30 | 100 |

最终30组与本轮优化前30组都约10.3万GMU、60页，但材质组成、活动格和运行时分布仍不同，不能认定为严格相同工作量。原始最后60帧：Capture平均17.49→13.77ms；Crown8.83→5.64ms；FluxSolver10.95→8.42ms；FireReaction5.10→1.96ms。最终FireReaction内CollectFire约0.290ms、ProcessContacts约1.663ms。Marker有父子嵌套，不能全部相加。固定输入表才用于声称明确的28.9%/64.3%局部改进。

本轮共保留34个有效窗口、9份完整60帧原始Profiler及1个无效暂停窗口。最终collector-35的TimeScale=0、MeasurementValid=false，不能用于流动FPS结论；collector-40是在下一次恢复后独立记录的窗口，不当作连续无暂停40组验收。无效数据单列在[invalid-performance-windows.json](invalid-performance-windows.json)，验证工具已增加暂停时立即结束的保护。旧199/205批次不是最后生产源码结果。

较早98页CPU窗口片段平均1.62ms、最坏3.85ms、显示延迟约1.36秒；采集修改前另一98页窗口片段平均1.357ms、最坏3.219ms，但显示延迟约3.51秒，说明分批构建完整结果的时间仍有明显边界。最终206项检查兼容输出与Stable裁剪交接，没有把旧窗口描述为采集修改后的大CPU重跑。最终30组整帧Main Thread GC metadata平均约704B、最大6341B；Renderer GC0B不代表整个游戏所有系统零分配。GPU共享分配仍为127579776B≈121.67MiB，输入256页、细页96、128MiB硬预算保持。超过容量仍完整CPU回退，不删液体、不降低Gameplay分辨率。

Native Graphics Jobs使原生GPU时间状态含`NotSupportedWithNativeGfxJobs`；不能用Dispatch的CPU时间或单个GPU数值宣称完整GPU帧时间通过。Standalone Player、长期大量活动格和所有近景美术细节未完成验收。大量天然湖优先保持Stable表示，只有接触/出水的局部活动。制作方法和新的现场检查见《湖泊与地形制作指南》第十节的第5、8小节。

15:32只读Audit=0失败，P7保持Edit Mode、Clean、TimeScale1、后台运行false；所有临时场景/Play已退出。Clean与本轮开始观察一致，本轮没有保存、丢弃或重启关卡。验证恢复原有Profiler设置。中间一次缓存生命周期测试把Raycast起点正好放在抬升后的Collider表面而失败，修正测试高度后通过；该失败[单独保存](seam-cache-test-ray-on-surface.json)，没有把测试设置错误作为生产修复证据。

15:49最终只读[Editor状态](final-scene-state.json)再次确认：P7 Edit Mode、Clean、TimeScale1、后台false，恰好一个统一Renderer；未继续运行临时验证。生产源码/Shader Hash仍与最终206项对应。修改范围源码的whitespace检查、Python工具语法、文档链接及9份原始TSV各60帧检查通过；未整理工作区既有Unity场景的空字段尾部空格。
