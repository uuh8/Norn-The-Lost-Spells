using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Multiplayer.Tests
{
    /// <summary>
    /// 正式地图必须来自原Scene而非另造灰盒。本组只创建Editor Preview并比对源字节，不加载玩家档、
    /// 不进入Play、不保存正式Scene；网络玩法与可见客户端验收由主控另行执行。
    /// </summary>
    public sealed class OnlineDungeonExportTests
    {
        private readonly List<GameObject> _previews = new List<GameObject>();
        private Type RequireExporter()
        {
            Type found = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            { found = assembly.GetType("Game.Project.Editor.OnlineDungeonTools"); if (found != null) break; }
            Assert.That(found, Is.Not.Null, "缺少从正式P7/P8离线导出受控地图的入口，不能继续把所有门送进灰盒。");
            return found;
        }
        private GameObject Preview(string id)
        {
            var root = RequireExporter().GetMethod("BuildPreview").Invoke(null, new object[] { id }) as GameObject;
            Assert.That(root, Is.Not.Null); _previews.Add(root); return root;
        }
        [TearDown] public void ReleaseOnlyOwnPreviews()
        {
            if (_previews.Count == 0) return;
            var release = RequireExporter().GetMethod("ReleasePreview");
            foreach (var root in _previews) if (root != null) release.Invoke(null, new object[] { root });
            _previews.Clear();
        }

        [TestCase("p7", 12, 4, 7, true)][TestCase("p8", 1, 1, 0, false)]
        public void SourceAuditKeepsActualNpcEncounterAndGateCountsWithoutChangingScene(string id, int npcs, int encounters, int gates, bool showEncounterHud)
        {
            string path = id == "p7" ? "Assets/_Project/Scenes/P7_DemoRun.unity" : "Assets/_Project/Scenes/P8_BossField.unity";
            byte[] before = File.ReadAllBytes(path);
            var info = RequireExporter().GetMethod("InspectSource").Invoke(null, new object[] { id });
            Assert.That(Get<int>(info, "NpcCount"), Is.EqualTo(npcs));
            Assert.That(Get<int>(info, "EncounterCount"), Is.EqualTo(encounters));
            Assert.That(Get<int>(info, "GateCount"), Is.EqualTo(gates));
            Assert.That(Get<int>(info, "EntryVolumeCount"), Is.EqualTo(encounters));
            Assert.That(Get<bool>(info, "ShowEncounterHud"), Is.EqualTo(showEncounterHud),
                "共同UI必须保留实际源场景的遭遇HUD显隐，不能因重新启用Adapter令Boss栏与旧区域文本重叠。");
            Assert.That(Get<string>(info, "SourceHash").Length, Is.EqualTo(64));
            Assert.That(Get<Vector3>(info, "SourcePlayerPosition"), Is.Not.EqualTo(Vector3.zero), "四席出生应从原Player位置派生，不能写死原点。");
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
        }

        [Test]
        public void HomePreviewKeepsTheOriginalPeacefulWorldWithoutInventingEncountersOrWritingCombatExports()
        {
            var protectedFiles = new Dictionary<string, byte[]>();
            foreach (string path in new[] { "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Home.unity",
                "Assets/_Project/Prefabs/World/Online/P7World.prefab", "Assets/_Project/Prefabs/World/Online/P8World.prefab",
                "Assets/_Project/ScriptableObjects/World/Online/P7.asset", "Assets/_Project/ScriptableObjects/World/Online/P8.asset",
                "Assets/_Project/Resources/Online/DungeonCatalog.asset", "ProjectSettings/EditorBuildSettings.asset" })
                protectedFiles.Add(path, File.ReadAllBytes(path));
            var root = Preview("home");
            Assert.That(root.activeSelf, Is.False);
            var source = EditorSceneManager.OpenPreviewScene("Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Home.unity");
            try
            {
                // 和平实验室的原静态内容本来很少。按原场景逐项计数，比任意“至少六个”的战斗地图阈值更能证明保真。
                int renderers = CountHomeWorldComponents<Renderer>(source), colliders = CountHomeWorldComponents<Collider>(source);
                Assert.That(renderers, Is.GreaterThan(0)); Assert.That(colliders, Is.GreaterThan(0));
                Assert.That(root.GetComponentsInChildren<Renderer>(true).Length, Is.EqualTo(renderers));
                Assert.That(root.GetComponentsInChildren<Collider>(true).Length, Is.EqualTo(colliders));
            }
            finally { EditorSceneManager.ClosePreviewScene(source); }
            var info = RequireExporter().GetMethod("InspectSource").Invoke(null, new object[] { "home" });
            Assert.That(Get<int>(info, "NpcCount"), Is.Zero); Assert.That(Get<int>(info, "EncounterCount"), Is.Zero);
            Assert.That(Get<int>(info, "EntryVolumeCount"), Is.Zero); Assert.That(Get<int>(info, "GateCount"), Is.Zero);
            Assert.That(Get<int>(info, "LootTableCount"), Is.Zero); Assert.That(Get<int>(info, "FixedSolidCount"), Is.Zero);
            Assert.That(Get<bool>(info, "ShowEncounterHud"), Is.False); Assert.That(Get<string>(info, "SourceHash").Length, Is.EqualTo(64));
            Assert.That(Get<Vector3>(info, "SourcePlayerPosition"), Is.Not.EqualTo(Vector3.zero));
            foreach (var pair in protectedFiles) Assert.That(File.ReadAllBytes(pair.Key), Is.EqualTo(pair.Value), pair.Key);
        }

        [Test]
        public void HomePreviewRetainsTheExactAuthoredResearchAndReturnPortalGeometryWithoutOldOwners()
        {
            const string sourcePath = "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Home.unity";
            byte[] before = File.ReadAllBytes(sourcePath); var root = Preview("home");
            var bindings = FindNamed(root, "Game.Character.OnlineHomeWorldBindings");
            Assert.That(bindings, Is.Not.Null, "Home需要原区域/门几何的只读载体，不能重新用3m距离代替作者Box。");
            var source = EditorSceneManager.OpenPreviewScene(sourcePath);
            try
            {
                var hub = FindNamed(source, "Game.Run.HubInteractionController"); Assert.That(hub, Is.Not.Null);
                var volume = new SerializedObject(hub).FindProperty("_researchArea").objectReferenceValue as Component;
                Assert.That(volume, Is.Not.Null);
                var originalBox = new SerializedObject(volume).FindProperty("_volume").objectReferenceValue as BoxCollider;
                if (originalBox == null) originalBox = volume.GetComponent<BoxCollider>();
                var portal = FindNamed(source, "Game.Run.HomePortal"); Assert.That(portal, Is.Not.Null);
                Assert.That(new SerializedObject(portal).FindProperty("_returnToHub").boolValue, Is.True);
                var originalReturn = portal.GetComponent<BoxCollider>();
                var copiedBox = Property<BoxCollider>(bindings, "ResearchVolume");
                var copiedReturn = Property<Collider>(bindings, "ReturnPortalCollider") as BoxCollider;
                var copiedPortal = Property<Transform>(bindings, "ReturnPortalTransform");
                AssertSameBox(originalBox, copiedBox, root.transform); AssertSameBox(originalReturn, copiedReturn, root.transform);
                Assert.That(copiedPortal, Is.SameAs(copiedReturn.transform));
                var validate = bindings.GetType().GetMethod("TryValidate"); Assert.That(validate, Is.Not.Null);
                var args = new object[] { null }; Assert.That((bool)validate.Invoke(bindings, args), Is.True, args[0] as string);
                Assert.That(root.GetComponentsInChildren<Canvas>(true), Is.Empty);
                int surfaces = 0;
                foreach (var component in root.GetComponentsInChildren<Component>(true))
                {
                    if (component == null) Assert.Fail("Home导出不能有Missing Script。");
                    string name = component.GetType().FullName;
                    if (name == "Unity.AI.Navigation.NavMeshSurface") surfaces++;
                    if (!(component is MonoBehaviour) || !(component.GetType().Namespace ?? "").StartsWith("Game.", StringComparison.Ordinal)) continue;
                    Assert.That(name == "Game.Character.OnlineHomeWorldBindings" || name == "Game.Character.OnlineDungeonWorldBindings"
                        || name == "Game.Character.OnlineDungeonEnvironment" || AllowedInactiveMaterial(name), Is.True,
                        "Home静态世界不能保留旧Session/Actor/Canvas/Portal/Account Owner：" + name);
                }
                Assert.That(surfaces, Is.Zero, "原和平Home没有NavMesh，导出不能为满足战斗地图规则伪造一个。");
            }
            finally { EditorSceneManager.ClosePreviewScene(source); }
            Assert.That(File.ReadAllBytes(sourcePath), Is.EqualTo(before));
        }
        private static Component FindNamed(GameObject root, string name)
        {
            foreach (var value in root.GetComponentsInChildren<Component>(true)) if (value != null && value.GetType().FullName == name) return value;
            return null;
        }
        private static Component FindNamed(Scene scene, string name)
        { foreach (var root in scene.GetRootGameObjects()) { var found = FindNamed(root, name); if (found != null) return found; } return null; }
        private static int CountHomeWorldComponents<T>(Scene source) where T : Component
        {
            int count = 0;
            foreach (var root in source.GetRootGameObjects()) foreach (var value in root.GetComponentsInChildren<T>(true))
            {
                bool actorOrUi = false;
                for (var parent = value.transform; parent != null && !actorOrUi; parent = parent.parent)
                {
                    if (parent.GetComponent<Canvas>() != null) { actorOrUi = true; break; }
                    foreach (var component in parent.GetComponents<MonoBehaviour>())
                        if (component != null) for (var type = component.GetType(); type != null; type = type.BaseType)
                            if (type.FullName == "Game.Character.PlayerControllerBase") { actorOrUi = true; break; }
                }
                if (!actorOrUi) count++;
            }
            return count;
        }
        private static T Property<T>(Component value, string name) where T : UnityEngine.Object
        { var property = value.GetType().GetProperty(name); Assert.That(property, Is.Not.Null, name); return property.GetValue(value) as T; }
        private static void AssertSameBox(BoxCollider original, BoxCollider copied, Transform root)
        {
            Assert.That(original, Is.Not.Null); Assert.That(copied, Is.Not.Null);
            Assert.That(copied.transform.IsChildOf(root), Is.True);
            Assert.That(copied.center, Is.EqualTo(original.center)); Assert.That(copied.size, Is.EqualTo(original.size));
            Assert.That(copied.isTrigger, Is.EqualTo(original.isTrigger)); Assert.That(copied.enabled, Is.EqualTo(original.enabled));
            Assert.That(Vector3.Distance(copied.transform.position, original.transform.position), Is.LessThan(.0001f));
            Assert.That(Quaternion.Angle(copied.transform.rotation, original.transform.rotation), Is.LessThan(.01f));
            Assert.That(copied.transform.lossyScale, Is.EqualTo(original.transform.lossyScale));
        }

        [Test]
        public void P7PreviewKeepsTheExactSuccessfulReturnPortalWithoutItsOldStageOwner()
        {
            const string path="Assets/_Project/Scenes/P7_DemoRun.unity";
            byte[] before=File.ReadAllBytes(path); var root=Preview("p7");
            var binding=FindNamed(root,"Game.Character.OnlineDungeonReturnBindings");
            Assert.That(binding,Is.Not.Null,"静态成功出口的GameObject不能随旧StageExitPortal Owner一起被删掉。");
            var source=EditorSceneManager.OpenPreviewScene(path);
            try
            {
                var original=FindNamed(source,"Game.Run.StageExitPortal"); Assert.That(original,Is.Not.Null);
                var copied=Property<Collider>(binding,"PortalCollider") as BoxCollider;
                AssertSameBox(original.GetComponent<BoxCollider>(),copied,root.transform);
                Assert.That(Property<Transform>(binding,"PortalTransform"),Is.SameAs(copied.transform));
                var originalData=new SerializedObject(original);
                Assert.That(binding.GetType().GetProperty("EncounterIndex").GetValue(binding),Is.EqualTo(originalData.FindProperty("_encounterIndex").intValue));
                Assert.That(((LayerMask)binding.GetType().GetProperty("PlayerLayers").GetValue(binding)).value,
                    Is.EqualTo(originalData.FindProperty("_playerLayers").intValue));
                Assert.That(binding.GetComponentsInChildren<Renderer>(true).Length,Is.EqualTo(original.GetComponentsInChildren<Renderer>(true).Length));
                Assert.That(FindNamed(root,"Game.Run.StageExitPortal"),Is.Null);
                Assert.That(FindNamed(root,"Game.Run.HubPracticePortal"),Is.Null);
                Assert.That(FindNamed(root,"Game.Run.SinglePlayerInventorySession"),Is.Null);
            }
            finally { EditorSceneManager.ClosePreviewScene(source); }
            Assert.That(File.ReadAllBytes(path),Is.EqualTo(before));
        }

        [Test]
        public void P8PreviewKeepsTheOriginalInactiveDynamicReturnSphereTemplateWithoutInventingAFixedDoor()
        {
            const string path="Assets/_Project/Scenes/P8_BossField.unity";
            byte[] before=File.ReadAllBytes(path);var root=Preview("p8");
            var binding=FindNamed(root,"Game.Character.OnlineDungeonReturnBindings");
            Assert.That(binding,Is.Not.Null,"P8只导出原动态门模板，不能凭空填写一个不存在的StageExit场景位置。");
            var source=EditorSceneManager.OpenPreviewScene(path);
            try
            {
                Assert.That(FindNamed(source,"Game.Run.StageExitPortal"),Is.Null,"原P8的门来自Boss胜利后的放门算法。");
                var bootstrap=FindNamed(source,"Game.Run.PracticeInventorySceneBootstrap");Assert.That(bootstrap,Is.Not.Null);
                var prefab=new SerializedObject(bootstrap).FindProperty("_returnPortalPrefab").objectReferenceValue as GameObject;
                Assert.That(prefab,Is.Not.Null);byte[] prefabBefore=File.ReadAllBytes(AssetDatabase.GetAssetPath(prefab));
                var original=prefab.GetComponent<SphereCollider>();var copied=Property<Collider>(binding,"PortalCollider") as SphereCollider;
                Assert.That(original,Is.Not.Null);Assert.That(copied,Is.Not.Null);
                Assert.That(copied.center,Is.EqualTo(original.center));Assert.That(copied.radius,Is.EqualTo(original.radius));
                Assert.That(copied.isTrigger,Is.EqualTo(original.isTrigger));Assert.That(copied.enabled,Is.EqualTo(original.enabled));
                var template=Property<GameObject>(binding,"InactiveTemplateRoot");Assert.That(template,Is.SameAs(binding.gameObject));
                Assert.That(template.activeSelf,Is.False);Assert.That(template.transform.IsChildOf(root.transform),Is.True);
                Assert.That(binding.GetType().GetProperty("DynamicPlacement").GetValue(binding),Is.True);
                Assert.That(template.GetComponentsInChildren<Renderer>(true).Length,Is.EqualTo(prefab.GetComponentsInChildren<Renderer>(true).Length));
                Assert.That(FindNamed(template,"Game.Run.HubPracticePortal"),Is.Null);Assert.That(FindNamed(template,"Game.Run.StageExitPortal"),Is.Null);
                Assert.That(File.ReadAllBytes(AssetDatabase.GetAssetPath(prefab)),Is.EqualTo(prefabBefore));
            }
            finally { EditorSceneManager.ClosePreviewScene(source); }
            Assert.That(File.ReadAllBytes(path),Is.EqualTo(before));
        }

        [TestCase("p7")] [TestCase("p8")]
        public void NarrowReturnRefreshBuildsOnlyAMemoryCandidateAndPreservesExistingNavigationAndMaterialAssets(string id)
        {
            string worldPath="Assets/_Project/Prefabs/World/Online/"+id.ToUpperInvariant()+"World.prefab";
            string definitionPath="Assets/_Project/ScriptableObjects/World/Online/"+id.ToUpperInvariant()+".asset";
            var protectedFiles=new Dictionary<string,byte[]>();
            foreach(string path in new[]{worldPath,definitionPath,"Assets/_Project/Resources/Online/DungeonCatalog.asset",
                "Assets/_Project/Scenes/P7_DemoRun.unity","Assets/_Project/Scenes/P8_BossField.unity","ProjectSettings/EditorBuildSettings.asset"})
                protectedFiles.Add(path,File.ReadAllBytes(path));
            var original=AssetDatabase.LoadAssetAtPath<GameObject>(worldPath);Assert.That(original,Is.Not.Null);
            var method=RequireExporter().GetMethod("BuildReturnRefreshPreview");Assert.That(method,Is.Not.Null);
            var candidate=method.Invoke(null,new object[]{id}) as GameObject;Assert.That(candidate,Is.Not.Null);_previews.Add(candidate);
            Assert.That(candidate.activeSelf,Is.False);Assert.That(FindNamed(candidate,"Game.Character.OnlineDungeonReturnBindings"),Is.Not.Null);
            foreach(string componentType in new[]{"Unity.AI.Navigation.NavMeshSurface","Game.ElementField.OnlineMaterialWorldBinding"})
            {
                var before=FindNamed(original,componentType);var after=FindNamed(candidate,componentType);
                Assert.That(before,Is.Not.Null);Assert.That(after,Is.Not.Null);
                var data=new SerializedObject(before);var property=data.GetIterator();
                while(property.Next(true))
                    if(property.propertyType==SerializedPropertyType.ObjectReference && property.objectReferenceValue!=null
                        // Prefab contents中的Scene副本有自己的GameObject/Prefab装配身份；这些不是NavMesh或材料配置。
                        && property.propertyPath!="m_PrefabAsset" && property.propertyPath!="m_PrefabInstance"
                        && property.propertyPath!="m_CorrespondingSourceObject" && property.propertyPath!="m_GameObject"
                        && EditorUtility.IsPersistent(property.objectReferenceValue))
                    {
                        var expected=property.objectReferenceValue;
                        var actual=new SerializedObject(after).FindProperty(property.propertyPath).objectReferenceValue;
                        Transform originalNode=expected is Component oldComponent?oldComponent.transform:expected is GameObject oldObject?oldObject.transform:null;
                        if(originalNode!=null && originalNode.IsChildOf(original.transform))
                        {
                            // 原Prefab子组件也是Persistent，但内存候选必须指向自己的对应组件，不能串回原Prefab实例。
                            Assert.That(actual,Is.Not.Null,componentType+"."+property.propertyPath);
                            Assert.That(actual.GetType(),Is.EqualTo(expected.GetType()));
                            Transform clonedNode=actual is Component newComponent?newComponent.transform:actual is GameObject newObject?newObject.transform:null;
                            Assert.That(clonedNode,Is.Not.Null);Assert.That(clonedNode.IsChildOf(candidate.transform),Is.True);
                            Assert.That(AnimationUtility.CalculateTransformPath(clonedNode,candidate.transform),
                                Is.EqualTo(AnimationUtility.CalculateTransformPath(originalNode,original.transform)),componentType+"."+property.propertyPath);
                        }
                        else Assert.That(actual,Is.SameAs(expected),componentType+"."+property.propertyPath);
                    }
            }
            foreach(var pair in protectedFiles)Assert.That(File.ReadAllBytes(pair.Key),Is.EqualTo(pair.Value),pair.Key);
        }

        [TestCase("p7", 4, 7)][TestCase("p8", 1, 0)]
        public void InactiveWorldKeepsOriginalGeometryNavigationAndEntryBindingsButNoSinglePlayerAuthority(string id, int areas, int gates)
        {
            var root = Preview(id);
            Assert.That(root.activeSelf, Is.False, "剥离权威脚本前不能激活任何克隆子树。");
            Assert.That(root.GetComponentsInChildren<Renderer>(true).Length, Is.GreaterThan(5));
            Assert.That(root.GetComponentsInChildren<Collider>(true).Length, Is.GreaterThan(5));
            Component bindings = null; int actualGates = 0, surfaces = 0;
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                Assert.That(component, Is.Not.Null, "导出世界不能留下Missing Script。");
                string name = component.GetType().FullName;
                if (name == "Game.Character.OnlineDungeonWorldBindings") bindings = component;
                if (name == "Game.Run.ArenaGate") actualGates++;
                if (name == "Unity.AI.Navigation.NavMeshSurface") surfaces++;
                if (!(component is MonoBehaviour)) continue;
                string ns = component.GetType().Namespace ?? "";
                if (ns.StartsWith("Game.", StringComparison.Ordinal))
                    Assert.That(name == "Game.Character.OnlineDungeonWorldBindings" || name == "Game.Character.OnlineDungeonEnvironment"
                        || name == "Game.Run.ArenaGate" || AllowedInactiveMaterial(name) || AllowedOriginalReturnPresentation(component), Is.True,
                        "地图仅保留inactive材料装配，不能启动单机Session/Actor或GPU玩法Owner：" + name);
            }
            Assert.That(surfaces, Is.EqualTo(1)); Assert.That(actualGates, Is.EqualTo(gates)); Assert.That(bindings, Is.Not.Null);
            var values = (Array)bindings.GetType().GetField("_areas", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(bindings);
            Assert.That(values.Length, Is.EqualTo(areas));
            for (int i = 0; i < values.Length; i++)
            {
                var area = values.GetValue(i);
                Assert.That(Get<Array>(area, "EntryVolumes").Length, Is.EqualTo(1));
                Assert.That(Get<Array>(area, "Gates").Length, Is.EqualTo(id == "p8" ? 0 : i == 3 ? 1 : 2));
                if (id == "p8") Assert.That(Get<GameObject>(area, "SafeZoneVisualRoot"), Is.Not.Null, "P8需保留原安全圈可见粒子根。");
            }
        }

        [TestCase("p7", 12)][TestCase("p8", 1)]
        public void EveryOriginalNpcProgramMatchesExportAndRoundTripsItsActualOutputPrefabKey(string dungeon, int expectedNpcs)
        {
            var inspect = RequireExporter().GetMethod("InspectNpcProgramMappings", new[] { typeof(string) });
            Assert.That(inspect, Is.Not.Null, "需要原Scene→导出NPC→各program→受控PrefabKey的逐敌人只读观测，不能由截图猜测毒球被替换。");
            var reports = (Array)inspect.Invoke(null, new object[] { dungeon });
            Assert.That(reports.Length, Is.EqualTo(expectedNpcs));
            int bossPrograms = 0;
            for (int i = 0; i < reports.Length; i++)
            {
                var row = reports.GetValue(i); var programs = Get<string[]>(row, "ProgramPaths");
                Assert.That(Get<ulong>(row, "ActorId"), Is.EqualTo((ulong)i + 5));
                Assert.That(Get<string>(row, "SourcePrefabPath"), Is.Not.Empty);
                Assert.That(Get<bool>(row, "MatchesExportedPrograms"), Is.True, dungeon + "/" + (i + 5));
                var paths = Get<string[]>(row, "OutputPrefabPaths"); var keys = Get<uint[]>(row, "OutputKeys");
                Assert.That(keys.Length, Is.EqualTo(paths.Length));
                for (int j = 0; j < keys.Length; j++) Assert.That(keys[j], Is.GreaterThan(0), paths[j]);
                if (dungeon == "p7" && (i + 5 == 10 || i + 5 == 16))
                    Assert.That(programs, Is.EqualTo(new[] { "Assets/_Project/ScriptableObjects/Spell/Programs/Enemy/Wand_Enemy_Wizard_Poisonball.asset" }));
                if (dungeon == "p7" && i + 5 == 12)
                    Assert.That(programs, Is.EqualTo(new[] { "Assets/_Project/ScriptableObjects/Spell/Programs/Enemy/Wand_Enemy_Wizard_Stickyball.asset" }));
                if (dungeon == "p8") bossPrograms += programs.Length;
            }
            if (dungeon == "p8") Assert.That(bossPrograms, Is.EqualTo(9), "P8必须逐一检查原9套Boss程序，不能只检查第一阶段火球。");
        }

        // N5沿用原材料内容与Renderer，激活前再由Binding选择Authority/Replica；不扩大为整模块通配白名单。
        private static bool AllowedOriginalReturnPresentation(Component value)
        {
            string name=value.GetType().FullName;
            if(name=="Game.Character.OnlineDungeonReturnBindings")return true;
            if(name!="Game.Run.PortalVisualPlacement" && name!="Game.UI.PortalVfxController")return false;
            for(var parent=value.transform;parent!=null;parent=parent.parent)
                foreach(var component in parent.GetComponents<Component>())
                    if(component!=null && component.GetType().FullName=="Game.Character.OnlineDungeonReturnBindings")return true;
            return false;
        }

        private static bool AllowedInactiveMaterial(string name) => name == "Game.ElementField.OnlineMaterialWorldBinding"
            || name == "Game.ElementField.ElementWorldRuntime" || name == "Game.ElementField.ElementFieldSolidBaker"
            || name == "Game.ElementField.ElementWorldExposureSystem" || name == "Game.ElementField.LiquidWorldRuntime"
            || name == "Game.ElementField.FixedSolidWorldRuntime" || name == "Game.ElementField.GasWorldRuntime"
            || name == "Game.ElementField.MaterialEnvironmentAuthoring" || name == "Game.ElementField.ContainerDepositAdapter"
            || name == "Game.ElementField.MaterialInventoryTransferAdapter" || name == "Game.Rendering.GridLiquidFieldSurfaceRenderer"
            || name == "Game.Rendering.LiquidSurfaceRenderer" || name == "Game.Rendering.StableLiquidSurfaceRenderer"
            || name == "Game.Rendering.FixedSolidSurfaceRenderer" || name == "Game.Rendering.GasFieldVolumeRenderer"
            || name == "Game.Rendering.ElementWorldWaterRenderer" || name == "Game.Rendering.ElementWorldFireRenderer"
            || name == "Game.Rendering.GpuFireParcelRenderer";

        [Test] public void UnknownDungeonNeverBuildsAFallbackGraybox()
        {
            var method = RequireExporter().GetMethod("BuildPreview");
            var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, new object[] { "untrusted/client/path" }));
            Assert.That(error.InnerException, Is.TypeOf<ArgumentException>());
        }

        [TestCase("p7")][TestCase("p8")]
        public void FourPlayerSpawnsUseActualFloorAndCapsuleWithBoundedSearchEvenWithoutNpcNavMesh(string id)
        {
            var root = Preview(id); var originalScene = root.scene;
            // 玩家由CharacterController驱动，不是NavMeshAgent。主动删掉克隆Surface，证明出生检查
            // 使用本图碰撞体，而不是碰巧采样到Editor里另一张已加载Scene的全局NavMesh。
            foreach (var component in root.GetComponentsInChildren<Component>(true))
                if (component != null && component.GetType().FullName == "Unity.AI.Navigation.NavMeshSurface")
                    UnityEngine.Object.DestroyImmediate(component);
            var method = RequireExporter().GetMethod("InspectPlayerSpawns");
            Assert.That(method, Is.Not.Null, "需要可独立验证出生空间的导出步骤，不能只在最后保存资产时才发现Spawn无效。");
            var report = method.Invoke(null, new object[] { root });
            var positions = Get<Vector3[]>(report, "Positions");
            var normals = Get<Vector3[]>(report, "GroundNormals");
            var source = Get<Vector3>(report, "SourcePosition");
            Assert.That(positions.Length, Is.EqualTo(4)); Assert.That(normals.Length, Is.EqualTo(4));
            Assert.That(Get<bool>(report, "UsedSourceCollidersOnly"), Is.True, "只能逐本图Collider查询，不能让作者当前打开的Hub/Review碰撞体影响出生结果。");
            Assert.That(Get<int>(report, "CandidateChecks"), Is.InRange(4, Get<int>(report, "MaxCandidateChecks")));
            Assert.That(Get<int>(report, "MaxCandidateChecks"), Is.EqualTo(33), "只搜索原点加4圈8方向，场地不足必须有界失败。");
            Assert.That(positions[0].x, Is.EqualTo(source.x).Within(.001f));
            Assert.That(positions[0].z, Is.EqualTo(source.z).Within(.001f));
            for (int i = 0; i < positions.Length; i++)
            {
                Assert.That(float.IsFinite(positions[i].x) && float.IsFinite(positions[i].y) && float.IsFinite(positions[i].z), Is.True);
                Assert.That(Vector3.Distance(positions[i], source), Is.LessThan(8f));
                Assert.That(normals[i].y, Is.GreaterThanOrEqualTo(.6f));
                for (int j = 0; j < i; j++)
                    Assert.That(Vector3.Distance(positions[i], positions[j]), Is.GreaterThanOrEqualTo(Get<float>(report, "MinimumSeparation") - .001f));
            }
            Assert.That(root.activeSelf, Is.False); Assert.That(root.scene, Is.EqualTo(originalScene), "验证完必须放回自己的Preview，不能遗留临时场景。");
        }

        [Test]
        public void MissingGroundRejectsSpawnWithoutOriginFallbackAndRestoresPreviewOwnership()
        {
            var root = Preview("p7"); var originalScene = root.scene;
            foreach (var collider in root.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            var method = RequireExporter().GetMethod("InspectPlayerSpawns"); Assert.That(method, Is.Not.Null);
            var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, new object[] { root }));
            Assert.That(error.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(error.InnerException.Message, Does.Contain("出生XZ缺少可站地面或胶囊净空"),
                "必须确实因本图地面缺失而拒绝，错误Scene API抛同类异常不能冒充通过。");
            Assert.That(root.activeSelf, Is.False); Assert.That(root.scene, Is.EqualTo(originalScene));
        }

        [Test]
        public void GroundFromAnotherLoadedSceneCannotMakeInvalidSourceSpawnPass()
        {
            var root = Preview("p7");
            var info = RequireExporter().GetMethod("InspectSource").Invoke(null, new object[] { "p7" });
            var source = Get<Vector3>(info, "SourcePlayerPosition");
            foreach (var collider in root.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            // 测试器当前的普通Scene里放一个可真正Raycast的外部地板，证明当前打开世界能提供
            // 一个看似合法的支撑面；导出仍只能认可自己那张已禁用全部Collider的地图。
            var external = new GameObject("OtherWorldGroundMustNotAuthorizeSpawn");
            try
            {
                external.transform.position = source - Vector3.up * .4f;
                var floor = external.AddComponent<BoxCollider>(); floor.size = new Vector3(20, 1, 20);
                Physics.SyncTransforms();
                Assert.That(floor.Raycast(new Ray(source + Vector3.up, Vector3.down), out _, 3f), Is.True,
                    "对照地板必须实际可查询，不能用无效对照声称已经证明隔离。");
                var error = Assert.Throws<TargetInvocationException>(() => RequireExporter().GetMethod("InspectPlayerSpawns").Invoke(null, new object[] { root }));
                Assert.That(error.InnerException.Message, Does.Contain("出生XZ缺少可站地面或胶囊净空"));
                Assert.That(root.activeSelf, Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(external); }
        }

        private static T Get<T>(object value, string name)
        {
            Assert.That(value, Is.Not.Null);
            var field = value.GetType().GetField(name); Assert.That(field, Is.Not.Null, "缺少导出合同字段" + name);
            return (T)field.GetValue(value);
        }
    }
}
