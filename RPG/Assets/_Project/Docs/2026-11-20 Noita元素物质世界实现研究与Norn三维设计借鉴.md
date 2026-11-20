# Noita 元素物质世界的实现，以及 Norn 的三维设计取舍

研究日期 2026-11-20。样本为本机 Steam 版 Noita，Build `17130612`。下文的原版字段、数值和实验均对应这份样本。涉及早期引擎设计的部分注明了开发者与年份。

对 Norn，我建议把**山体、水池和空气都纳入可查询、可修改的材料坐标系，只让正在变化的局部区域承担逐格计算**。一座浮岛可以由百万格石头构成。玩家在下面挖通一格时，岛顶的石头仍保持原位。洞口旁的水、沙、毒气和人物碰撞才需要接到这次变化。这个设计既允许世界处处有材料，也解释得出每一次更新为什么发生。

## 一、Noita 怎样把石山、沙子和水放进同一个世界

我从原版 `data/data.wak` 读取了 `data/materials.xml`。它有 224 条 `CellData`、244 条 `CellDataChild`、325 条普通 `Reaction` 和 5 条 `ReqReaction`。468 条材料声明对应 466 个不同名称，原文件里有两个重名。244 条子材料中，165 条显式开启继承反应，23 条显式关闭，56 条省略该字段。这使作者可以单独决定一块变种岩石继承哪些基础行为，以及是否沿用母材的反应。这里的计数描述原始声明。运行时最终注册数量需要另外观察加载结果。[原版统计与材料样例](../../../docs/superpowers/evidence/noita-material-world-20261120/research-evidence.json)

| 原版材料 | 配置里实际写了什么 | 设计含义 |
|---|---|---|
| `rock_static` 天然岩石 | `cell_type=liquid`，`liquid_sand=1`，`liquid_static=1`，`density=10`，`durability=10`，带 `[corrodible]` | 一块岩石可固定在世界坐标上，同时接受腐蚀和破坏。主类别的命名容易误导，光看 `liquid` 会把山理解错。 |
| `sand`、`soil` 松沙与泥土 | 都是颗粒，`density=6`。泥土另有 `liquid_viscosity=75` 和 `liquid_sticks_to_ceiling=50`，两者的重力参数也不同 | 配置密度相同的颗粒可以有不同的落速、铺展和挂顶倾向。天然固定沙层另有 `sand_static`。 |
| `water`、`oil` 水与油 | 水密度 4，油密度 1。水声明冷却、加热产物。油另外声明可燃，初始 `on_fire=0` | 分层、相变、能否燃烧与是否正在燃烧，可以由不同规则决定。密度值是游戏内相对值。 |
| `steam`、`smoke` 蒸汽与烟 | 都是 `gas`、密度 1，声明的 `lifetime` 分别为 1000 和 350 | 相同运动类别可以有不同的持续时间。表中数值沿用配置单位。 |
| `wood`、`ice_glass` | 两者均为 `cell_type=solid`。冰另有 `solid_on_sleep_convert` 和 `solid_break_to_type` | 原版还容纳有形状、可以运动的固体。它和固定山体有各自的表示与转换条件。 |

**原生程序印证了这些分类确实被读取。** REA 定位到 `CellFactory_GetAllSands` 与 `CellFactory_GetAllLiquids` 的两个原生回调，两者进入同一个筛选函数 `0x78D8D0`，传入相同大类、不同颗粒条件。筛选器读取材料加载器写入的 `liquid_sand` 标记，也检查是否包含静态项和 `is_just_particle_fx`。随包的 Lua 签名给出的默认行为是包含静态项、排除纯表现材料，调用方可以改这两个筛选条件。RTTI 和虚函数表还可以区分 `CLiquidCell`、`CGasCell`、`CSolidCell`、`CFireCell`。对 Norn 最有价值的是这种组合方式。美术把地层标成「砂岩」「泥层」「松沙」时，可以独立配置自然沉降、可破坏性和破坏产物，关卡作者无须给每一种山另写物理逻辑。[原生证据中的 load_sections、class_sand、class_liquid 与 select_materials](../../../docs/superpowers/evidence/noita-material-world-20261120/research-evidence.json)

## 二、一粒沙为什么能沉入水，为什么有时要等

开发者 Petri Purho 的演示用水说明 Falling Sand 的基本更新思路。水先尝试下方，受阻再向侧面找空间。他在访谈中用相反方向解释蒸汽，先找上方，受阻再向侧面扩散。为了检验这个解释放到两种已占据的材料上是否成立，我继续沿原生更新方法找到液体/颗粒和气体分别询问「这个候选格能进入吗」的两段代码。液体路径在 `0x70B46F` 调用前一段，气体路径在 `0x70A73C` 调用后一段。两者都读材料描述中的 `density`，比较方向相反。颗粒条件也参与液体路径的判断。[水的开发者演示 02:51](https://www.youtube.com/watch?v=prXuyMCgbTc&t=171s) · [Petri 解释蒸汽的访谈](https://80.lv/articles/noita-a-game-based-on-falling-sand-simulation) · [原生调用与实验输入](../../../docs/superpowers/evidence/noita-material-world-20261120/research-evidence.json)

我用 Unicorn 执行了这个版本的两段原始 x86 机器码，沿用游戏原有的虚函数表，单独构造源格、候选格、密度、颗粒标志和随机状态。先做 18 组定向用例，再对以下六种接触各选 101 个随机状态，使这个判断里的 101 个随机余数各出现一次。

| 源格 → 候选格 | 原生函数返回允许的次数 |
|---|---:|
| 沙 6 → 空格 | 101 / 101，这条路径连随机状态都不用推进 |
| 沙 6 → 水 4 | 26 / 101 |
| 水 4 → 沙 6 | 0 / 101 |
| 沙 6 → 较轻的测试颗粒 2 | 0 / 101 |
| 蒸汽 1 → 较重的测试气体 2 | 26 / 101 |
| 较重的测试气体 2 → 蒸汽 1 | 0 / 101 |

定向用例还显示，在同一有利随机状态下，水 4 进入油 1 获得许可，反过来被拒绝。跨类别时，水遇到蒸汽或火，这两组用例直接获得许可，随机状态也没有前进。蒸汽遇到水则被拒绝。**先由材料类别决定走哪条判断路径。需要比密度时，重液遇轻液、轻气遇重气才有机会获准。** 26 / 101 对应所测分支接受的余数 75–100。它不能换算成「一粒沙每 Tick 有约四分之一概率下沉」，因为外层还决定尝试哪个邻格、尝试几次、是否真正提交位移。实验也只把密度 2 的气体和颗粒当作对照输入。[18 个输入、6 组余数覆盖及结果](../../../docs/superpowers/evidence/noita-material-world-20261120/research-evidence.json)

Norn 的沙落水可以沿用这个拆法。先看正下方有没有空间，再看对方能否被挤开，再根据材料设置决定这一步发生得快还是慢。当沙进入已装满水的格子，成功的一步必须同时给被挤开的水找到去处。积水可流到原先的沙格或邻格，找不到空间就等待。这样既能看到沙沉底，也能在投沙后收回正确数量的水。两种颗粒能否互相穿行可单独定义，避免石砾只因更重就一路穿过压实沙堆。

## 三、接触反应如何产生复杂结果

Noita 把许多机制写在原版反应表中。原生加载器识别 `Reaction` 和 `ReqReaction`，共用字段读取路径，随后存入两个不同的集合。325 条普通反应中，32 条写有第三输入，15 条显式写出第三输出，11 条指定 `cosmetic_particle`，5 条指定生成 `entity`。这些能力按规则选用。[反应统计、实例行号与原生加载入口](../../../docs/superpowers/evidence/noita-material-world-20261120/research-evidence.json)

| 原版组合 | 结果与可借鉴之处 |
|---|---|
| `[lava] + water` 与 `[lava] + sima` | 前者产生静态岩石和蒸汽，后者产生静态岩石和火。**两条都另外播放蒸汽粒子。** 玩家可以看见相似的白雾，只有前者在这条规则中写出真实蒸汽。两条还分别设置作用半径，并限制区域替换所能改动的原材料。见 `materials.xml` 第 15306、15351 行。 |
| `steam + [static]` | 在声明的冷凝反应中，蒸汽变水，静态邻格保留。壁面通过接触参与反应，便能让洞顶出现水。见第 14913 行。 |
| `[fire] + water_salt + water_salt` | 三输入产生火、盐和蒸汽。另一条 `snow_sticky + water + air` 用空气作为第三输入，仅显式写出两个产物。可以让「是否有空位」成为反应条件。见第 15140、15026 行。 |
| `[requires_air]` 植物与 `ReqReaction` | 原文件注释说明缺少空气时会变为泥土。`ceiling_plant_material` 的条件还限定 `direction=bottom`。顶生植物和普通植物可以复用规则结构，分别检查需要的环境方向。见第 15791–15802 行。 |
| `shock_powder + air` | 两个输出位置均写空气，同时生成短寿命的电气 Entity。该实体单独声明放电间隔 4 帧，寿命 7 帧。一次反应可以启动一个短暂事件，随后由事件自己结束。见第 16839 行及 `data/entities/misc/shock_powder.xml`。 |

有两处值得注意。第一，水泥在输入一侧和另一侧分别有声明，其中一条写了 `req_lifetime=600`，简单排序再合并会丢失意义。第二，火接触雪有产水和产蒸汽两条规则，分别写 80 和 20。只凭这两个数推不出引擎检查顺序。对 Norn，建议以材料 ID 或 Tag 先筛出少量候选，再检查接触方向、第三格或历时。首次接触时登记界面，需要等待的反应安排后续检查。否则一块待冷凝的蒸汽如果第一次没触发就被休眠，或一条水路同时被两个区块吃掉，同一张反应表也会产生完全不同的玩法。[原版水泥、雪与三输入规则](../../../docs/superpowers/evidence/noita-material-world-20261120/research-evidence.json)

**温度也可以有多条入口。** 水定义冷却后变静态冰、加热后变蒸汽。原版火把带一个半径 7、每帧步数 1 的局部温度转换组件。另一份冻结组件则显式把岩浆映射到 `rock_static`，而岩浆自身的冷却目标是 `lavarock_static`。所以「环境冷却」和「法术强制转换」完全可以有不同结果。Petri 也提到 Noita 使用了简化的温度模拟，许多材料的燃烧需要氧气。现有证据可以解释上述接触与局部转换，无法据此重建一个覆盖全图的热传导算法。Norn 可先由火源、冰术和材料接触调度局部反应。[原版转换组件与材料定义](../../../docs/superpowers/evidence/noita-material-world-20261120/research-evidence.json) · [Petri 关于温度与氧气的回答](https://www.reddit.com/r/Games/comments/d7cqjz/comment/f0zg6uo/)

## 四、地形怎样成为材料，怎么被挖开

**生成时就把材料写进世界。** 煤矿配置引用 Wang Tile 模板，并在不同条目中指定煤、泥土、静态沙层与湿岩。更直观的是魔杖祭坛。原版脚本在一次 `LoadPixelScene` 调用中同时传入 `wand_altar.png` 与 `wand_altar_visual.png`。我解码了两张原图，尺寸都是 20 × 30。材料图有 4 种颜色，其中 231 个像素精确匹配 `templebrick_static` 的材料色，24 个匹配 `glowstone`。外观图有 8 种颜色。一位关卡作者可以直接画出哪里是砖、哪里是发光石，再独立绘制细节。对于 Norn，一块悬空山体也可以在地图生成或导入时一次写成固定岩层，挖掘以后仍能知道每个洞壁的材质。[原版祭坛脚本、两张图的统计与矿区配置](../../../docs/superpowers/evidence/noita-material-world-20261120/research-evidence.json)

**破坏能力按工具定义。** 原版炸弹配置爆炸半径 60、允许打洞、液体破坏开关为 0，破坏耐久上限为 11。天然岩石 `rock_static` 的 `durability` 是 10，静态钢墙 `steel_static` 是 12。仅就耐久上限而言，前者在范围内、后者超出。完整弹坑还受其他爆炸参数与材料处理路径影响。黑洞采用另一种 `CellEaterComponent`，半径 12，允许吞动态物理体，跳过 `[indestructible]` 标签。点水法术又采用两个区域材料转换器，各有不同半径与步数。对山体定义「可挖、可炸、可腐蚀、命中后产出什么」，比一个全局 `isDestructible` 更能支持法术差异。[原版炸弹、黑洞、点水法术与材料硬度](../../../docs/superpowers/evidence/noita-material-world-20261120/research-evidence.json)

**只有需要整体运动的东西才值得成为刚体。** Petri 演示了从二维材料轮廓经 Marching Squares、轮廓简化和三角化得到 Box2D 形状的过程。物体运动时，网格里的占据也跟着更新。附近静态地形按物体预计经过的区域准备碰撞几何。原版 `ice_static` 还指定了可转换的 `ice_glass` 刚体材料，后者声明了休眠转换和破碎材料。Norn 可让浮空岩层直接保持原位，法术切断的冰桥或明确可推的木箱再承担整体运动的成本。是否掉下去应由关卡材料和受击结果共同决定。[原作者的刚体构建与网格交接 05:50](https://www.youtube.com/watch?v=prXuyMCgbTc&t=350s) · [附近碰撞几何 09:01](https://www.youtube.com/watch?v=prXuyMCgbTc&t=541s) · [冰的转换定义](../../../docs/superpowers/evidence/noita-material-world-20261120/research-evidence.json)

空中飞溅还有一种有用的取舍。开发者先用早期作品《Bloody Zombies》的血液展示带速度的材料粒子怎样暂时离开网格、飞行、碰撞后写回，随后明确说 Noita 沿用了这个机制。与此同时，原版的 `GameCreateParticle` 接口明确允许 `just_visual`。Norn 可以让普通瀑布继续沿格下落，用插值画水束。喷射或抛物线对玩法有意义时，才让少数携带确定数量的材料包暂时离格。撞击后将剩余量交回网格。细小飞沫可以只画，表现粒子多少不影响一瓶水最后能装回多少。[空中粒子演示 04:34](https://www.youtube.com/watch?v=prXuyMCgbTc&t=274s) · [作者说明 Noita 沿用了该机制 05:15](https://www.youtube.com/watch?v=prXuyMCgbTc&t=315s) · [原版接口与实际调用位置](../../../docs/superpowers/evidence/noita-material-world-20261120/research-evidence.json)

## 五、为什么屏幕外也有世界，而 CPU 仍有机会跟上

开发者在 2019 年分别介绍了块内更新、块间并行和世界流送。这三个层面各自节省不同的成本。[Petri Purho 的 GDC 演讲](https://www.gdcvault.com/play/1025695/Exploring-the-Tech-and-Design/)

| 更新层面 | Noita 公开过的做法 | 省下的工作 |
|---|---|---|
| 一个更新区块内部 | 演讲中的模拟块为 64 × 64 像素，块内用 `Dirty Rect` 包住需要更新的格，CPU 在同一网格上原地更新。自下而上的扫描允许竖排材料接连填入腾出的空间 | 静态山体和已经平静的区域不会因为存在就需要整块反复更新。扫描顺序也会直接影响流水外观。[活动范围 10:22](https://www.youtube.com/watch?v=prXuyMCgbTc&t=622s) · [共用缓冲 11:13](https://www.youtube.com/watch?v=prXuyMCgbTc&t=673s) · [Petri 解释扫描顺序](https://80.lv/articles/noita-a-game-based-on-falling-sand-simulation) |
| 多个模拟块之间 | 演讲将可同时处理的块排成分开的组，分四个阶段交给线程池，结合约束过的跨块访问距离 | 两个工作线程可以在各自允许的写入范围内更新。移到 3D 要按真实写入半径和并发冲突重新分组。[演示 11:37](https://www.youtube.com/watch?v=prXuyMCgbTc&t=697s) |
| 世界 Streaming | 开发者所述的流送块是 512 × 512 像素，目标保留约 12 块。远处的旧块可以保存并卸载。当前样本的 `magic_numbers.xml` 里仍有 `STREAMING_CHUNK_TARGET=12` | 屏外仍可更新被列入调度的区域。Petri 也提到有时玩家靠近后才看到悬停的液体开始下落。模拟块、屏幕可见范围和存档单位各有用途。[演示 13:01](https://www.youtube.com/watch?v=prXuyMCgbTc&t=781s) · [Petri 对保存与恢复的说明](https://www.reddit.com/r/gamedev/comments/d93op6/comment/f1g045b/) |

本次 REA 还在当前可执行文件的 `0x728A90` 附近找到添加更新范围的路径，它将范围映射到块并处理交集。这能与「变化区域驱动更新」交叉印证。演讲中的块尺寸、四阶段排程和单缓冲细节属于开发者当年公开的设计。本次没有从当前 Build 的整帧执行轨迹独立还原这三项。[当前二进制的 dirty_area 证据](../../../docs/superpowers/evidence/noita-material-world-20261120/research-evidence.json)

移到 3D 后，平面分辨率加倍会让同面积细格数变成 4 倍，三维边长分辨率加倍则让同体积细格数变成 8 倍。一块 64³ 已有 262,144 格。在区块对角各打一个洞，即使只有两小片材料在动，包住它们的单个三维包围盒也可能覆盖几乎整块。Norn 可以用小块活动位图或若干局部范围保存这两处变化。衡量它有没有用，直接计数每 Tick 实际访问了多少格，以及这些访问有多少只是在扫两洞之间的完整岩石。

## 六、原版用什么把材料变成玩法

- **角色暴露。** 原版玩家配置对酸、岩浆、放射性气体等分别给出伤害，另有肺内空气和缺氧设置。随包组件文档给出 `material_damage_min_cell_count` 默认值 4，允许角色过滤极少量接触。于是「脚尖蹭到一滴」和「站进一池」可以区别处理。Norn 可以在脚边采样衣物沾湿、在头部采样吸入毒气、按接触量累计剂量。摄像机能看见多少烟不会改变伤害。[玩家配置与组件定义](../../../docs/superpowers/evidence/noita-material-world-20261120/research-evidence.json)
- **容器。** 原版药瓶有吸入容量 1000、每帧最多吸 5 格、破碎时洒出，并以材料 ID 记录数量。粉袋选择另一种吸入类型，容量 1500。药瓶还允许用选定频率运行瓶内反应，分别决定是否产生爆炸或实体。Norn 现阶段最值得拿的是「灌入、倒出、打碎共用同一份数量」。瓶内化学可以作为独立玩法讨论。[原版药瓶、粉袋与 MaterialInventoryComponent](../../../docs/superpowers/evidence/noita-material-world-20261120/research-evidence.json)
- **同一世界可以有多种查询。** 原版 Lua API 分别提供查固体表面的 `RaytraceSurfaces`、也会命中液态材料的 `RaytraceSurfacesAndLiquiform`，以及查可站立表面的 `RaytracePlatforms`。Norn 的脚底、取水口和挖掘法术也可以在同一份材料坐标上使用不同过滤条件。这样浮空石台能站人、水面能取水、烟雾能伤人，每条规则都能独立解释。[打包 Lua 文档第 163–165 行与原生注册](../../../docs/superpowers/evidence/noita-material-world-20261120/research-evidence.json)
- **表现可以夸张，危险要能识别。** 原版 `fire` 与 `spark` 同有 `[fire]`、`[hot]` 标签，但后者定义 `on_fire=0`、火温为 0。岩浆自身 `on_fire=0`，同时显式要求点燃接触的 DamageModel。能否点火、是否正在燃烧、是否呈现亮色，作者可以分别控制。开发者在演讲中也展示了用角色身上的沾染提示帮助玩家理解材料后果。Norn 的毒雾可以保持现有效果，真正的危险仍由可采样的浓度和剂量给出。[火、火星与岩浆的原版定义](../../../docs/superpowers/evidence/noita-material-world-20261120/research-evidence.json) · [沾染与玩家反馈 26:05](https://www.youtube.com/watch?v=prXuyMCgbTc&t=1565s)

## 七、落到 Norn，底层具体应该承担什么

**1. 用材料格表达山体，从已有地图生成初始材料。** Norn 现在有从静态场景 Collider 生成 SolidMask 的路径，也有对已驻留固定固体检查支撑和连通区域的路径。后者有休眠条件，执行重力检查时仍会枚举驻留格。若把整座山直接灌入现有支撑扫描，完整山体也会进入计算范围。若只改材料显示、原始 Collider 继续挡人，玩家会看见洞却走不进去。建议以 Chunk 保存初始岩层加局部编辑，纯岩块能用一个材料编号表示，连续同材质区可按段存。挖穿时记录空气覆盖，持久化时也要保存这个空洞。只有标记为可坍塌的部分进入支撑检查。角色碰撞、法术命中和水路都读取同一次地形编辑的结果，开洞边界同时唤醒邻居。[现有环境阻挡来源](../Scripts/ElementField/Runtime/ElementFieldSolidBaker.cs#L105) · [现有固体支撑扫描](../Scripts/ElementField/Solids/FixedSolidWorldRuntime.Gravity.cs#L37) · [地形变化与液体的接入点](../Scripts/ElementField/Runtime/ElementWorldRuntime.MaterialPhases.cs#L65)

**2. 让每种材料声明有限的行为，把参数存在材料定义中。** 实例格只保存材料 ID、该表示所需的数量，以及确实发生的局部状态。火焰、寿命倒计时、受损进度等可在相应材料或活动区按需保存。下面是建议给策划和关卡作者的首批属性。

| 属性 | 实际决定的问题 | Norn 例子 |
|---|---|---|
| 运动方式与支撑策略 | 固定、松散颗粒、流动、漂浮或整块运动，失去支撑时是否唤醒 | 天然岩层悬空保持。松沙往下落。冰桥可选择断裂 |
| 占据、相对密度与置换许可 | 多少空间可用，谁能挤开谁，被挤走的量去哪里 | 沙能沉入水，水能挤开油，压实岩层会阻挡两者 |
| 输运参数 | 每步可移动的量、向侧面扩散的倾向、颗粒可接受的斜落，以及气体浮升或扩散 | 沙堆保留斜面，水铺平，黏液铺得慢，毒气可设置滞留 |
| 破坏抗性与产物 | 哪些工具能改变材料，一次破坏消耗多少，产生空气、颗粒、碎块还是掉落物 | 同一块岩石被挖空、被腐蚀和被碎石术击中，可选择不同结果 |
| 反应标签、触发与产物 | 接触、加热、受击、缺少邻接条件时，分别消耗什么、产生什么 | 岩浆遇水产岩壳与蒸汽。冰术冻结局部水。表面白雾可另外播放 |
| 暴露、碰撞、采集与外观 | 谁能站立、呼吸、沾染或采集。用什么表面或体积效果表达 | 石桥可站，水可装瓶，毒雾可采样，光照和颜色可独立调整 |

**3. 水和颗粒先实现局部规则，静水按需展开。** P7 在线 CPU 配置已经有 0.25 m 基础格、10 Hz 材料 Tick，液体格边长为其两倍，即 0.5 m，动态格每 Tick 的配置预算为 1024。现有 `StableLiquidRegion` 能用水量与湖盆容量曲线表达稳定水体。建议沿用已有的真实数量与休眠经验。对于山洞、上下叠两层水、同一柱里夹石梁的地方，只存一个水面高度会丢失通路。这些位置应按高度分段或局部拆回体素，打通后仅在变动区求流动。若粗水格覆盖一个狭窄孔，两侧是否连通还得看细地形实际开放的面。先明确「多宽的洞应该能流水」，再定水的分辨率。基本局部规则足以形成瀑布和沙堆。需要封闭管道压力或连通器快速等高时，再单独决定是否为这种玩法付费。[P7 World Prefab 的挂接](../Prefabs/World/Online/P7World.prefab#L3054) · [P7 世界配置](../ScriptableObjects/World/Online/Materials/P7_OnlineCPU_ElementWorldProfile.asset#L15) · [液体预算与尺度](../ScriptableObjects/World/Online/Materials/P7_OnlineCPU_LiquidWorldProfile.asset#L18) · [稳定水体的数量所有者](../Scripts/ElementField/Liquids/Stable/StableLiquidRegion.cs#L38)

**4. 气体先保留玩家喜欢的观感，分别测模拟、上传和绘制。** 现有 `GasFluxSolver` 已按活动格传输 GMU，变动后唤醒邻居。P7 的 `Steam` 与 `ToxicVapor` 都设扩散为 1、浮升为 0，风速也为 0。所以在 Norn 加入「蒸汽往上走」是新设计选择。渲染器另行上传密度，光线每穿过一个绘制的气体 Chunk，体积 Shader 最多走 16 步，每步采一次 3D 密度纹理，遇遮挡或累计不透明度足够时提前结束。多个 Chunk 覆盖同一个屏幕像素时，绘制成本可能叠加。已有验证把 96 处气体库存放在空活动域中，暖机后的 300 个 Tick 没有 Solver 格处理。该实验测的是冻结库存，GPU 帧耗时仍需在大雾镜头下测量。先观察密闭洞室、开门扩散、镜头贴近大雾三种情况。CPU 贵就查活动格与唤醒，上传贵就查改动块和字节数，填充贵就调渲染分辨率、步数或空区跳过。确有成本时可以只改贵的环节，保留已经满意的画面。[气体求解](../Scripts/ElementField/Gases/GasFluxSolver.cs#L76) · [P7 气体参数](../ScriptableObjects/World/Online/Materials/P7_OnlineCPU_GasWorldProfile.asset#L15) · [渲染上传](../Scripts/Rendering/ElementField/GasFieldVolumeRenderer.cs#L94) · [体积 Shader](../Art/Shaders/RealGasVolume.shader#L61) · [已有验证记录](../../../docs/superpowers/validation/2026-10-06-material-world-phase-e/README.md#L29)

**5. 每次跨格、跨区块的改变只提交一次。** 在 3D 里，几个方向的沙和水可能同时争同一空位。可先以六个面确定可达性，颗粒斜落要检查中间通道，防止从两块贴紧的石头之间穿角。相同优先级可按坐标和 Tick 轮换，避免水总往世界的同一个方向偏。一次移动、置换、反应或从稳定湖中取出一部分，都要同时提交相应的扣减与增加。空间或接收预算不足可以延后。这对联机尤其重要，Dedicated Server 应按所有玩家和必要的活动事件调度，客户端只消费已提交的材料变化。渲染区块重建、材料占据变化与网络 Revision 各自有自己的含义。现有 `Fire/Legacy` 强度仍应和液、固、气的原生格数量区分。角色伤害依据权威采样，远处的烟雾和离屏状态不会改变归属。[现有按后端查询数量的接口](../Scripts/ElementField/Runtime/ElementWorldRuntime.MaterialPhases.cs#L28) · [Authority 时钟](../Scripts/ElementField/Runtime/ElementWorldRuntime.Authority.cs#L53) · [Replica 材料视图](../Scripts/ElementField/Runtime/ElementWorldRuntime.Replica.cs#L83)

## 八、用一个场景判断设计是否成立

我建议用**浮空岩层里的跨 Chunk 水库**来决定重构方向。库底开两个宽度不同的孔，下面是有沙、有油的洞室，顶端留一袋毒气，另一名玩家能从下游观察。砂、油和可编辑岩层是为验证重构建议引入的材料与地形能力。这个场景能把「世界大」和「同时活跃的物质多」拆成可以分别测量的事情。[当前材料枚举](../Scripts/Materials/MaterialId.cs#L7)

| 玩家操作 | 应当可见、可核对的结果 |
|---|---|
| 水库静置，把周围完整岩层和静水体积扩大多倍 | 水量和岩层可被查询。记录驻留字节、每 Tick 实际处理格数与 CPU 时间，观察静态体积增加是否牵动模拟开销。 |
| 从下方挖空一段岩面，再分别打通 0.25 m、0.5 m 开口 | 天然岩层保持悬空。角色、法术、沙、水和气体都能感知同一次开洞。按预先选定的最小通水孔径检查粗细格边界。记录局部网格与 Collider 重建耗时。 |
| 在水上倒沙和油，从下游装回水，再封住出水口 | 沙沉底、油在上，流出口关闭后趋于平静。核对源头、下游、容器以及暂时离格的全部水量。带生成或消耗的反应单列账目。 |
| 打开毒气洞，先后站在雾内、雾外和下游，再贴近雾面观察 | 权威剂量与真实气体范围对应。分别记录扩散格数、密度上传字节、GPU 时间和视觉变化。 |
| 两至四名玩家分开活动，然后重连或离开再返回 | 各客户端重新进入同一 Interest 区域并收到对应版本后，对照洞、液位和材料后果是否一致。比较活跃区相邻与分散两种负载的 Tick 耗时、P95 帧时间和每客户端传输量。 |

两张地图可以保留完全相同的洞口，只改变远处静态材料的规模。比较两次开洞实际访问的格数与耗时，便能判断扩大世界会给一次局部操作增加多少成本。

### 样本与复核边界

使用 REA 6.5.0、Rizin 0.9.1 检查本机 `noita.exe`，用原始 PE 字节复核证据索引中的 22 个反汇编区间。使用原版 WAK、随包 Modding 文档、PNG 逐色计数，以及 Unicorn 2.1.4 的隔离实验进行交叉检查。两个实验入口分别是 `0x70B200`（液体／颗粒候选）和 `0x70A5A0`（气体候选）。随附索引保存输入 SHA-256、资产行号、原生区间、REA evidence ID、实验输入与观察值。原生实验覆盖两段候选判断。本文没有跑 Noita 或 Norn 的整场模拟，也没有取得 Norn 的新增 Profiler 数据。第七、八节是结合 Norn 当前源码提出的设计与验证场景，性能收益仍待实测。[本次研究证据索引](../../../docs/superpowers/evidence/noita-material-world-20261120/research-evidence.json)
