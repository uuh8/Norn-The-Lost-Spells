# Physics差异与dirty来源：只读结论（2026-11-01）

本报告不授予恢复许可。此前`DynamicsManager.asset`恢复被自动审批拒绝，未执行，未换路重试。

## DynamicsManager具体差异与证据

当前SHA256 `53cf96c365b85eefe78b3cda76af21f68246744277ae6ca21ffd6a67bdb72a1c`，1553 bytes；本轮03:54基线及HEAD为`b7c9a9dc08b98990109d4346f68745e15e238de400f96c583e53e1c83c743a78`，1254 bytes。当前mtime04:52:32，与本轮修正后PlayMode时间窗重合；没有捕获该次具体写调用，不能仅凭mtime证明写者。

实际diff：序列化版本13→23；`m_AutoSimulation:1`换为`m_SimulationMode:0`；旧`m_ContactsGeneration:1`及`m_EnableUnifiedHeightmaps:1`不再输出。新增默认字段为DefaultMaxDepenetrationVelocity=10、InvokeCollisionCallbacks=1、ImprovedPatchFriction=0、GenerateOnTriggerStayEvents=1、ScratchBufferChunkCount=4、CurrentBackendId=4072204805、FastMotionThreshold=3.4028235e+38、SceneBuffersReleaseInterval=0、ReleaseSceneBuffers=0、LogVerbosity=3、IncrementalStaticBroadphase=1。重力、材质、solver次数、碰撞矩阵、查询开关等既有值没有变化。

当前字节与`n5/native-delta-2026-10-31/physics-reserialization-observed.asset`逐字节一致；另有bootstrap-lifecycle、authenticated-material-channel、bootstrap-native-verification、interest-subscription、native-recovery及native-recovery-gates相同历史留档。`bootstrap-lifecycle-2026-10-31/physics-reserialization-restored.json`明确记录Physics.simulationMode API/SaveAssets引起重序列化、相同Before/Observed/Restored Hash。`native-player-reconnect-2026-10-31/resume-2026-11-01/settings-restoration.json`记录02:24已按精确Hash恢复同一变化。现有历史审计见task/resume-settings-audit.md；它是技术证据，不能代替此次被拒动作的新授权。

本机6000.3.16f1 PhysicsModule枚举元数据实际为FixedUpdate=0、Update=1、Script=2。[Unity 6000.3官方FixedUpdate说明](https://docs.unity.com/en-us/engine/6000.3/script-reference/unityengine/simulationmode/fixedupdate)确认0对应FixedUpdate。当前文件保持自动FixedUpdate语义，不是把DS改为自动物理；`AuthorityTickDriver.cs:35–36`在DS绑定时主动设Script并在77行恢复。其余新增字段是新格式输出，未逐个证明旧格式升级后的全部运行期默认值，因此不承诺整个文件在所有Unity版本语义绝对等价。

建议范围只有此单文件的本轮自动格式差异：若用户明确批准，执行时重新核对当前Hash仍为53cf…72a1c、保存观察字节，并验证候选1254bytes与03:54保护Hash和HEAD字节均一致后才恢复。任何附加变化立即保留停止。不得无条件git restore；不恢复Quality/Graphics/Player设置，不保存/清除Editor内存dirty。**当前保留1553bytes不影响复用已构建Client/DS测量，不应阻塞N5。**

## 67条dirty路径不等于67份用户未保存文件

05:47快照实际分组：Assets 28（均Shader/ShaderGraph来源）、Packages 32、Library 2、Resources内置1、ProjectSettings 4。28个Assets源均被Git跟踪，当前与HEAD无磁盘diff。Package/Library/内置项主要是导入/内部资源，不是67份普通用户编辑文件。IsDirty是对象自保存后变化标志，不记录作者或变化时间；[官方定义](https://docs.unity.com/en-us/engine/6000.3/script-reference/unityeditor/editorutility/isdirty)也不能据此推导用户修改了源文件。

四项为EditorBuildSettings、GraphicsSettings、ProjectSettings、QualitySettings，磁盘Hash均与03:54保护快照一致。现有ServerRenderingBuildScope会暂改管线/Player设置并在Dispose写回精确原字节，构建工具也临时处理场景列表；这支持dirty可能来自构建/导入，但没有前序IsDirty快照，不能把每个标志精确归因本轮。Quality仍保留先前来源未明的序列化状态，不恢复。Shader源磁盘干净也不等同于证明所有窗口内存绝无编辑，正常退出必须保留Unity自身提示。

05:58:09新快照已无四项ProjectSettings dirty，Scene仍干净，剩余63条全为上述导入/内置类别。四项文件mtime05:55:55但字节仍一致，未记录是谁触发保存，不能猜测。Agent没有调用SaveAssets/SaveAllAssets/清dirty。

05:59:54重新新鲜检查后，按用户05:41正常退出授权及05:56推进指令发CloseMainWindow；06:00:03确认Editor正常退出，没有选择丢弃或强杀。可用内存13026246656→20810674176 bytes。原67路径报告和初次谨慎阻塞保留；正常退出证据见`native-2/editor-normal-close.json`，新的63路径报告见`editor-safety-cleared-settings.json`。先前“需用户处理67项才可继续”的概括已被本次分类与实际退出结果修正。
