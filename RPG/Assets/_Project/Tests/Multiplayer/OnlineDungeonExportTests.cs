using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

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
                        || name == "Game.Run.ArenaGate" || AllowedInactiveMaterial(name), Is.True,
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

        // N5沿用原材料内容与Renderer，激活前再由Binding选择Authority/Replica；不扩大为整模块通配白名单。
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
