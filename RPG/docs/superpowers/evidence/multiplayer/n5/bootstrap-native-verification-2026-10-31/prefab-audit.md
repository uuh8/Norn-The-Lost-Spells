# N5 正式导出 Prefab 静态语义审计

HEAD: `0d87d0da931ec2b39f7cd569661e750bafa0df84`。未修改仓库，未请求Unity/测试/构建/协议生成/服务。

## 方法与规模
按GameObject层级路径（重名兄弟保留顺序）+组件类型/脚本GUID对齐对象；仅归一化本地fileID，忽略YAML文档块排序。所有字段、外部引用、m_Component和m_Children列表顺序均保留。合成数据4项自检通过。

|资源|文档 HEAD→当前|匹配对象改号|新增/移除|现有对象真实变化|
|---|---:|---:|---:|---:|
|P7World.prefab|1025→1045|581|20/0|10|
|P8World.prefab|73000→73021|71323|21/0|11|
|P8_05.prefab|563→563|6|0/0|0|

## P8_05 与顺序结论
P8_05 的563个序列化对象全语义相同：6个fileID改号，新增/移除/真实字段差异均为0，Transform、Collider、Renderer、NPC/战斗参数和引用无变化。
P7/P8无GameObject新增/删除，无Transform/子对象顺序变化；现有对象差异全部是m_Component插入新组件。全部21处组件列表的原有组件相对顺序保留，插入项在audit-final.json逐项列出。

## 新材料组件对原Scene
新增P7 20、P8 21个组件；其中2个为新OnlineMaterialWorldBinding，其余39个逐字段对照原Scene（含P8嵌套火焰Prefab+Scene override）。预期差异为Replica模式、GPU/玩家锚点引用清空、Exposure禁用、三相Profile换为各自在线副本。
源组件未解释差异：0；详见source-component-comparison.json。

## 10个CPU Profile逐一比较
所有原SO当前内容与HEAD一致。表中默认值补写对应当前类初始化值，属于原YAML未存储字段的显式序列化；不把该静态结论当作运行时验证。

|新资产|原资产|逐字段差异（含m_Name）|
|---|---|---|
|P7_OnlineCPU_ElementWorldProfile.asset|RPG/Assets/_Project/ScriptableObjects/ElementField/Liquids/ElementWorldProfile_P7_DemoRun_Grid.asset|/_materialSimulationRouting/guid: b512c17b51831ea42b3620a8e074a702 → 5b2d7209d97a8ca4ab141c192e0dce3e；/m_Name: ElementWorldProfile_P7_DemoRun_Grid → P7_OnlineCPU_ElementWorldProfile|
|P7_OnlineCPU_FixedSolidWorldProfile.asset|RPG/Assets/_Project/ScriptableObjects/ElementField/Phases/FixedSolidWorld_Default.asset|/_enableGravity: <missing> → 1；/_gravityAcceleration: <missing> → 9.81；/_maximumFallCellsPerTick: <missing> → 4；/_supportRecheckSeconds: <missing> → 0.25；/m_Name: FixedSolidWorld_Default → P7_OnlineCPU_FixedSolidWorldProfile|
|P7_OnlineCPU_GasWorldProfile.asset|RPG/Assets/_Project/ScriptableObjects/ElementField/Phases/GasWorld_Default.asset|/m_Name: GasWorld_Default → P7_OnlineCPU_GasWorldProfile|
|P7_OnlineCPU_LiquidWorldProfile.asset|RPG/Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_P7_DemoRun_Grid.asset|/_fallGravity: <missing> → 9.81；/_fallTerminalVelocity: <missing> → 12；/m_Name: LiquidWorldProfile_P7_DemoRun_Grid → P7_OnlineCPU_LiquidWorldProfile|
|P7_OnlineCPU_MaterialSimulationRoutingProfile.asset|RPG/Assets/_Project/ScriptableObjects/ElementField/Liquids/MaterialSimulationRouting_P7_DemoRun_Grid.asset|/m_Name: MaterialSimulationRouting_P7_DemoRun_Grid → P7_OnlineCPU_MaterialSimulationRoutingProfile|
|P8_OnlineCPU_ElementWorldProfile.asset|RPG/Assets/_Project/ScriptableObjects/World/Boss/ElementField/ElementWorldProfile_P8BossField.asset|/_materialSimulationRouting/guid: b512c17b51831ea42b3620a8e074a702 → 9a182499ea49b0948b83b11eab1218fd；/m_Name: ElementWorldProfile_P8BossField → P8_OnlineCPU_ElementWorldProfile|
|P8_OnlineCPU_FixedSolidWorldProfile.asset|RPG/Assets/_Project/ScriptableObjects/ElementField/Phases/FixedSolidWorld_Default.asset|/_enableGravity: <missing> → 1；/_gravityAcceleration: <missing> → 9.81；/_maximumFallCellsPerTick: <missing> → 4；/_supportRecheckSeconds: <missing> → 0.25；/m_Name: FixedSolidWorld_Default → P8_OnlineCPU_FixedSolidWorldProfile|
|P8_OnlineCPU_GasWorldProfile.asset|RPG/Assets/_Project/ScriptableObjects/ElementField/Phases/GasWorld_Default.asset|/m_Name: GasWorld_Default → P8_OnlineCPU_GasWorldProfile|
|P8_OnlineCPU_LiquidWorldProfile.asset|RPG/Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_P7_DemoRun_Grid.asset|/_fallGravity: <missing> → 9.81；/_fallTerminalVelocity: <missing> → 12；/m_Name: LiquidWorldProfile_P7_DemoRun_Grid → P8_OnlineCPU_LiquidWorldProfile|
|P8_OnlineCPU_MaterialSimulationRoutingProfile.asset|RPG/Assets/_Project/ScriptableObjects/ElementField/Liquids/MaterialSimulationRouting_P7_DemoRun_Grid.asset|/m_Name: MaterialSimulationRouting_P7_DemoRun_Grid → P8_OnlineCPU_MaterialSimulationRoutingProfile|

两份ElementWorldProfile的_waterSimulationMode原本为GridLiquid(2)、_enableFluidChunkStreaming原本为0；Routing原配置也已全部CPU，因此导出没有再改backend。Catalog、状态投影、Reaction与容量/分辨率等其他已有配置保持原值。

## 引用与保护资源
内部非零引用共167330处，悬空引用0、父子/组件归属错误0、未映射对象0。外部引用29491处、231个唯一GUID：106个文本资源已核对local fileID，33个仓库脚本+5个PackageCache脚本已核对11500000，3个Unity内建，84个导入资产只确认GUID存在。外部已检错误0。
P7_DemoRun、P8_BossField及EditorBuildSettings/DynamicsManager/GraphicsSettings/QualitySettings/ProjectSettings共7个保护文件与HEAD逐字节一致；3个Prefab .meta内容/GUID保留；13个审计目标最终Hash均稳定。Materials目录在HEAD为空，10个资产均为新增。

## 未能静态验证的外部引用
以下84项的导入subasset fileID尚需Unity importer确认；GUID/实体文件存在已核对。逐项GUID、fileID和使用资产见audit-final.json的unverified_imported_references。

- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Rocks/SM_RockGroup_01.fbx；fileID=-8566025353879195019,-4273630521863772410,-1436258356516446278,4691817191719787528
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Props/SM_Fence_05.fbx；fileID=-8373227931482181294,821657142167922456,5708683901925433009
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Background/SM_Hill_01.fbx；fileID=-1905350735161694973,-783370469568508727,1492018710721586176
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/VFX/SM_VFX_WindMesh_03.fbx；fileID=-3084037855026495202
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Trees/SM_BirchBush_01.fbx；fileID=-4880006951846881249,-852849523569709139,3314200096565205972,6299561673089898594
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Rocks/SM_Rock_03.fbx；fileID=-2020268936034852607,446744456061386703,4399440645063463177
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Rocks/SM_Rock_07.fbx；fileID=-2682343268371506395,2751742982262498901,8390146080096562572
- RPG/Assets/_Project/Art/UI/HP_frame_bg.png；fileID=21300000
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Props/SM_Fence_06.fbx；fileID=-3136052824168240091,144651467901432986,5060595463365047973,5914734793070169923
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Props/SM_Fence_08.fbx；fileID=-8655112918852519461,-5877596161611231036,-3604226478964730579,3560314383364187757
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/VFX/SM_VFX_LeafPlane_01.fbx；fileID=309165825404809760
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Trees/SM_BirchBush_03.fbx；fileID=-9040063574330265831,334242372474985179,1991057977876226676,6347534152448504638
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Props/SM_Plank_Group_12.fbx；fileID=-5835146265766323085,-70817749861365088,4202265126929183751
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/CoverPlants/SM_CoverPlant_04.fbx；fileID=-3782278425459668961,5486587167190617230,7021779772233446295
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Props/SM_Plank_Group_06.fbx；fileID=-9038354156681671234,-1708888806686680540,9100055349856297866
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Trees/SM_BirchTree_01.fbx；fileID=-9000268928253461095,1565902799760850486,3431283556898081888,6741034057627580905
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Terrain Data/Terrain_MeadowEnvironment_01_Summer.asset；fileID=15600000
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Props/SM_Plank_03.fbx；fileID=-1505548642550369844,2115090035571386009,7204701830011395533
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Trees/SM_BirchBush_04.fbx；fileID=-8348047806127656373,-6438501980174880455,-5775544033616320673,352209087568464492
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Rocks/SM_Rock_05.fbx；fileID=-728302883812902487,646779659259744747,2533524557938017155
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Props/SM_Plank_Group_10.fbx；fileID=-5234066893968031941,-4724443088070487573,3164166595699523999
- RPG/Assets/_Project/Art/Elemental/Compute/FireParcelMarchingCubes.compute；fileID=7200000
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Props/SM_Plank_Group_04.fbx；fileID=-3894407107890888299,1967927767866269184,3162114290395338737
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Rocks/SM_Rock_06.fbx；fileID=-7981526462810293553,-2479694328028819116,9041912641572531062
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Mushrooms/SM_Mushroom_11.fbx；fileID=-1695531311246070620,1279554921617574284,3558415856887222798
- RPG/Assets/ThirdParty/ModularRPGHeroesPBR/Mesh/DefaultCharacter.fbx；fileID=4300000,4300002,4300004,4300006,4300008,4300010,4300012,4300014,4300016,4300018,4300020,4300022,4300024,4300026,4300028,4300030,4300032,4300034,4300036,4300038,4300040,4300042,4300044,4300046,4300048,4300050,4300052,4300054,4300056,4300058,4300060,4300062,4300064,4300066,4300068,4300070,4300072,4300074,4300076,4300078,4300080,4300082,4300084,4300086,4300088,4300090,4300092,4300094,4300096,4300098,4300100,4300102,4300104,4300106,4300108,4300110,4300112,4300114,4300116,4300118,4300120,4300122,4300124,4300126,4300128,4300130,4300132,4300134,4300136,4300138,4300140,4300142,4300144,4300146,4300148,4300150,4300152,4300154,4300156,4300158,4300160,4300162,4300164,4300166,4300168,4300170,4300172,4300174,4300176,4300178,4300180,4300182,4300184,4300186,4300188,4300190,4300192,4300194,4300196,4300198,4300200,4300202,4300204,4300206,4300208,4300210,4300212,9000000
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Rocks/SM_Rock_04.fbx；fileID=-6271149120155926148,1821904615745829303,5831026964744784724
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Rocks/SM_Rock_02.fbx；fileID=-7943447469958543471,-6259601500081401492,-3521149365504116726
- RPG/Assets/_Project/Scenes/P8_BossField/NavMesh-P8_NavMeshRoot.asset；fileID=23800000
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Rocks/SM_RockGroup_06.fbx；fileID=-2351900458421690544,1702181793386226556,3839575376320600555,6134393353549999998
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Mushrooms/SM_Mushroom_07.fbx；fileID=-8861348586163464626,-2506708801230103560,-550371860073980759
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Rocks/SM_RockGroup_04.fbx；fileID=-8494614380381300099,3227268244774254826,6505049490371438368,8907470344197390897
- RPG/Assets/_Project/Art/Elemental/Compute/FireParcelLifecycle.compute；fileID=7200000
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Props/SM_Log_01.fbx；fileID=-7885907922036666354,-2925886339879300612,159976832595536814,7814037856630466259
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Props/SM_Fence_02.fbx；fileID=-6302187771144838457,-1831610377016228081,1266061834045244977
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Props/SM_Fence_09.fbx；fileID=-7636004599202230013,769967936168057320,1263988511647537631,1268301935344382021
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Trees/SM_BirchBush_02.fbx；fileID=-4886985128715696817,-364817493597837392,7564979950552755316,8872682329987539168
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Rocks/SM_Rock_09.fbx；fileID=-5853808261535672804,-879591160801825439,3540712354049533716
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Mushrooms/SM_Mushroom_04.fbx；fileID=-8412033912336376254,417856931634408329,680057146816683964
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Rocks/SM_Rock_10.fbx；fileID=-334577541434832680,-74939344268458015,9141849700280529087
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Trees/SM_OakTree_02_Ivy.fbx；fileID=-4891811038423600983,-1416834695813595218,259400956242690799,6717954842010124997
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/VFX/SM_VFX_Bird_01.fbx；fileID=-8126102905394083926
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/CoverPlants/SM_CoverPlant_05.fbx；fileID=-5476216852083838462,-4783914379553874480,-458416286052866800
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/VFX/SM_VFX_Butterfly_01.fbx；fileID=-1353479057589933757
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Props/SM_Fence_04.fbx；fileID=-1264678243556338709,1274877549493130117,7813752439292613026
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Props/SM_AMStatue_ME_01.fbx；fileID=-8933478319446070250,-6304169888296566092,4396326986134031592
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Props/SM_Fence_07.fbx；fileID=-8010893350626401815,1473531537948088174,1646994955648193467,2639129116561284620
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Mushrooms/SM_Mushroom_10.fbx；fileID=-8176141667511089414,-2660483701758439420,2813788352999366296
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Props/SM_Stump_02.fbx；fileID=-5149913255272755229,-4343311428220254113,-2758455933399871075
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/CoverPlants/SM_CoverPlant_06.fbx；fileID=-3959666737495977361,-2582026382179846878,4035802738288547096
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Rocks/SM_RockGroup_02.fbx；fileID=-7323159665901230401,-2036418183207548036,-836114331825652382,3065337650778038361
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Mushrooms/SM_Mushroom_12.fbx；fileID=-6499206086043262925,-1385133327899845291,6273078629986059551
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Props/SM_Plank_Group_08.fbx；fileID=-6849571648574817793,19063520644447936,8691099113834272390
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/VFX/SM_VFX_WindMesh_01.fbx；fileID=4681112316660308814
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Mushrooms/SM_Mushroom_09.fbx；fileID=-1341995265358542410,-843719430925514894,1586279437692856002
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Trees/SM_BirchTree_02.fbx；fileID=-4764799718061730111,-2687916772419505267,2197730317656677895,2208546261545943198
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Mushrooms/SM_Mushroom_05.fbx；fileID=-7013335363340388681,1702386174288698969,5155071481386305521
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Rocks/SM_RockGroup_03.fbx；fileID=575434224907619515,690552686120504738,8441442745327187060,9173658783693135957
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Props/SM_Plank_02.fbx；fileID=-9044480710533595370,-4821128771118431873,6061313539938918490
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Props/SM_Log_02.fbx；fileID=-7048363881561050037,-3977000000367150370,-1423350172458192631,684115497744087646
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Props/SM_Plank_Group_05.fbx；fileID=-1401016752767518828,45928370327297273,8752842631476356346
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Props/SM_Fence_03.fbx；fileID=-8734065360879455566,-4257378451468963157,550790536503835480
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Props/SM_Plank_04.fbx；fileID=3140253059220862095,5907665863150406972,8824262705241815019
- RPG/Assets/_Project/Art/UI/HP_line.png；fileID=21300000
- RPG/Assets/_Project/Art/Elemental/Compute/FireParcelDensity.compute；fileID=7200000
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Rocks/SM_Rock_01.fbx；fileID=-7636771928295434382,-205345688152156884,8312006113569988040
- RPG/Assets/ANGRY MESH/Stylized Pack - Common/Sources/Meshes/SM_ASP_Global_Settings.fbx；fileID=-1479152323990373841
- RPG/Assets/ThirdParty/Epic Toon FX/Models/aura_straight.FBX；fileID=4300000
- RPG/Assets/_Project/ScriptableObjects/World/Dungeon/DungeonNavigation.asset；fileID=23800000
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Rocks/SM_RockGroup_07.fbx；fileID=506782869092447959,508321905820331908,2474052769029669588,7393989672430138188
- RPG/Assets/_Project/Art/UI/HP_enemy_frame.png；fileID=21300000
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Mushrooms/SM_Mushroom_06.fbx；fileID=-3962970084183907384,580719942540291858,3666274088518319661
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Props/SM_Stump_01.fbx；fileID=-7244100825077664652,-3319003389604981185,-2710391578091335799
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Rocks/SM_RockGroup_05.fbx；fileID=-4507636112016877748,5228595213763363693,7286084786505910465,8766641271071422962
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Trees/SM_BirchBush_05.fbx；fileID=-7997555246982086171,-1837189332308963120,5517668203333742185,5665914078186165954
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Props/SM_Fence_01.fbx；fileID=-4653315249026913454,1039475108079289476,2787895856629357880
- RPG/Assets/_Project/Art/Elemental/Shaders/ElementLiquidDormant.shader；fileID=4800000
- RPG/Assets/ThirdParty/ARPG Effects/Models/RPGFXVerticalGlow.FBX；fileID=4300000
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Props/SM_Plank_01.fbx；fileID=1985081857958377502,6595464586711487790,9077122895210716077
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Rocks/SM_Rock_08.fbx；fileID=-8892257251356425955,-3783461666211393458,7505615255751699881
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/VFX/SM_VFX_WindMesh_02.fbx；fileID=-4671360143401521789
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/Props/SM_Plank_Group_07.fbx；fileID=-3031441599297435471,4029669132364914512,6541584652337617519
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/CoverPlants/SM_CoverPlant_03.fbx；fileID=-7904512609856510962,-6262607908951180862,-3834426505620607206
- RPG/Assets/ANGRY MESH/Stylized Pack - Meadow Environment/Sources/Meshes/CoverPlants/SM_CoverPlant_01.fbx；fileID=-7683921515602148812,-6025934926930829255,-1947690561736470691

## 限制
- 本审计没有Unity导入/实例化/渲染/Physics/Native双端或KCP体验证据，不能替代这些Gate。
- 仅验证审计目标直接序列化引用，不递归证明全部外部资源依赖闭包。
- Git索引发现两组docs历史快照.meta重复GUID，均含不在Unity导入树的归档副本；生产导入范围重复GUID为0。
- 无权限或执行阻塞。审计首次沙箱启动受Windows split writable roots限制，窄范围只读提升后成功；未修改仓库。
