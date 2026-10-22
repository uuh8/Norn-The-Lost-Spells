# beffio据点地图URP兼容处理计划

> 在原工作区执行。用户已授权处理导入包，不使用SubAgent/worktree/Git提交，不保存或覆盖用户当前MainScene。按排错、可回退副本、图形验证、据点候选分阶段交付。

**Goal：** 修复可见紫色地面/水面，核对整包总览与单张地图，提供URP环境副本和据点候选；本轮不代替M5试玩Gate，也不接入未经验证的正式探索地图。

**Architecture：** 仅Editor装配与Rendering Shader/材质，不改Run/Combat/ElementWorld权威规则。ThirdParty模型/贴图/原场景保留，新Shader/材质在`Assets/_Project/Art/Environment/BeffioURP`，新场景在`Assets/_Project/Scenes/SinglePlayer`。Editor审计/转换工具读取原引用，克隆材质并在新Scene中替换；Unity生成meta。

**Tech Stack：** Unity6000.3.16f1、URP17.3.0、HLSL、UGUI之外的静态环境、AssetDatabase/EditorSceneManager、离屏URP RenderRequest与实际像素计数。

## 1. 证据、范围与素材

当前MainScene clean/非Play，项目已Linear。只读审计发现139种材质、30193个启用Renderer；Map_1—Map_9及外围全启用。可见旧Shader为GroundBlending（外围16处）及Water（两处）；其他旧自定义Shader引用均在activeInHierarchy=false的旧对象中，不是已经确认的活动低LOD故障。76处Missing Script、15个空材质槽要逐一归类，不能把历史Console红数当本轮编译失败。

原Ground Shader为Built-in Surface Shader，地面依赖顶点RGBA、四组颜色/Normal/Height、湿润与光滑控制；不能换一张URP/Lit白材质假装完成。原Water用GrabPass/Surface Shader和旧Reflection_pass；URP兼容必须换实现，不安装缺失Amplify Inspector来修运行时紫色。

所有素材已由用户导入：复用四层Diffuse RGBA（RGB颜色、A光滑）、Normal贴图（Normal Map Import）、Height单通道/线性（混合边界）、Water Normal/颜色/速度；保留原路径/大小/import。颜色按sRGB，非颜色数值应线性，已有导入状态先审计不批量覆写。无新增付费包/噪声/模型/音频/字体依赖；缺失引用明确报告，不能无声换成空贴图。替代方案直接Lit会损失原混合，直接旧LWRP unitypackage会覆盖/引入过时Shader；本轮优先项目拥有的兼容实现与原包已转换材质复用。

## 2. 文件与技术

- Create Editor/BeffioHubCompatibility.cs：读取当前Scene的根对象/真实材质/碰撞/旧脚本；创建明确新路径副本，dirty/Play/编译失败时拒绝写Scene；输出TestResults/SinglePlayer/BeffioHub。
- Create Art/Environment/BeffioURP/BeffioGroundURP.shader：保留原Properties命名和四层顶点色/Height混合；Object→World→Clip空间与Tangent→World Normal明确注释，URP PBR/阴影/深度/雾/Lightmap路径。
- Create必要Water兼容Shader：保留可见水面、Normal动态/颜色/光滑与透明；旧GrabPass/平面反射不可当成完整移植，报告取舍和未实现项。
- Create对应克隆材质：地面保留原纹理/float/color；水面保留本轮支持的Normal/颜色/速度参数，不支持的旧折射/波形参数明确记录；不猜旧Mesh的UV或替换任意atlas。
- Modify Editor/Game.PbfTask13.Editor.asmdef：仅Editor工具新增URP/Core RP引用，以官方SingleCameraRequest渲染审计。Runtime Assembly与依赖不变。
- Create Scenes/SinglePlayer/HubEnvironment_Beffio.unity：完整总览兼容副本，不覆盖原MainScene。另建一张紧凑地图候选作为据点环境，保留其他Map于总览；不在总览随意删城镇降低负担。
- 候选碰撞修复：只为实际缺失Collider的`Map_1/Ground_Map_1`增加静态非Convex MeshCollider，sharedMesh引用原地面，保持视觉与高度；不批量给草木/LOD加Collider，不改供应方模型Import。修复前后的169点碰撞探针保存证据。
- Update总案外部资源/当前任务、统一学习/验收：本轮属于M6前置兼容工作，据点资产不能算箱庭探索地图已提供；真实仓库/交易/实验接线留阶段验证后。

LOD核对结果：工具检查了LODGroup的材质兼容性，实际LodRepairs=0，没有改变任何LOD层级或Cull距离。旧Shader引用全部来自关闭的旧对象，没有证据要求抬高活动远景的细节。本轮不移植整个包的所有Shader；以后启用这些旧对象时需重新兼容检查。模型UV/atlas不能由名称猜成相同。

## 3. 验证与回退

- [x] 原场景/项目色彩空间/Shader来源只读审计，定位可见Ground/Water；最终活动LOD没有旧Shader，实际0处LOD改动。
- [x] 保存before/after离屏渲染证据（同Camera/尺寸/光照），真正统计magenta像素。原图189832→最终0，候选0且非黑；Shader消息无新错误。
- [x] 新副本装配、旧组件缺失/材质槽归类、Collider与地图规模检查；据点候选直接打开，不通过P7/Boss通关。空槽都是关闭旧粒子效果；地面参数逐项对比0差异。
- [x] Unity Editor编译/实际Shader绘制、效果截图与静态碰撞检查；169点探针42→169命中。没有运行的PlayMode/Standalone/Profiler不写通过，不用历史480/114替代本轮证据。
- [x] 更新学习/Debug/进度和下一阶段入口，最终原MainScene dirty=false/非Play/TimeScale1。
- [ ] 用户预览后确定据点子地图；实际CharacterController通行、Hub功能装配、灯光重新烘焙与完整性能另行验证。当前不宣称整包一切无问题。

只修改新副本/新资产即可回退；原材质/Prefab/Scene和GUID保留。临时渲染Camera放PreviewScene，不能在用户Scene创建/删除对象导致dirty。材质操作为离散Editor行为，无新增Runtime Update，不宣称全地图零GC或性能合格。Frame成本必须后续Profiler定位，不删除材料或可见美术来掩盖热点。
