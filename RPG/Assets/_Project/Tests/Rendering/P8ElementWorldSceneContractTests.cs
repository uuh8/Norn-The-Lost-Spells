using System;
using System.Collections.Generic;
using Game.Core;
using Game.ElementField;
using Game.Materials;
using Game.Rendering;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.Rendering.Tests
{
    /// <summary>核对正式地图选择的材料世界、在线导出和原表现资产；运行时验证使用独立 Scene 副本。</summary>
    public sealed class P8ElementWorldSceneContractTests
    {
        private const string P7 = "Assets/_Project/Scenes/P7_DemoRun.unity";
        private const string P8 = "Assets/_Project/Scenes/P8_BossField.unity";
        private const string Canonical = "Assets/_Project/ScriptableObjects/ElementField/Liquids/ElementWorldProfile_P7_DemoRun_Grid.asset";
        private const string P8Profile = "Assets/_Project/ScriptableObjects/World/Boss/ElementField/ElementWorldProfile_P8BossField.asset";

        [TestCase(P7, Canonical)]
        [TestCase(P8, P8Profile)]
        public void ProductionSceneRetainsGridFallbackConfiguration(string path, string profilePath)
        {
            WithScene(path, scene =>
            {
                var world = Single<ElementWorldRuntime>(scene);
                var profile = AssetDatabase.LoadAssetAtPath<ElementWorldProfile>(profilePath);
                var data = new SerializedObject(world);
                Assert.That(data.FindProperty("_profile").objectReferenceValue, Is.SameAs(profile));
                Assert.That(profile.WaterSimulationMode, Is.EqualTo(WaterSimulationMode.GridLiquid), "旧作者资料回退时应使用该地图原有的 GridLiquid Profile。");
                Assert.That(profile.EnableFluidChunkStreaming, Is.False);
                var liquid = Single<LiquidWorldRuntime>(scene);
                Assert.That(data.FindProperty("_gridLiquidRuntime").objectReferenceValue, Is.SameAs(liquid));
                Assert.That(liquid.gameObject, Is.SameAs(world.gameObject));
                Assert.That(liquid.enabled, Is.True);
                var liquidProfile = Reference<LiquidWorldProfile>(liquid, "_profile");
                Assert.That(liquidProfile, Is.Not.Null); liquidProfile.Validate();
                Assert.That(liquidProfile.MaterialCount, Is.EqualTo(3));
                var field = Single<GridLiquidFieldSurfaceRenderer>(scene);
                Assert.That(field.enabled, Is.True);
                Assert.That(Reference<ElementWorldRuntime>(field, "_world"), Is.SameAs(world));
                var renderProfile = Reference<GridLiquidFieldRenderProfile>(field, "_profile");
                Assert.That(renderProfile.Compute, Is.Not.Null);
                Assert.That(renderProfile.Shader.name, Is.EqualTo("Game/Elemental/Liquid Continuous Field"));
                for (int i=0;i<3;i++) Assert.That(renderProfile.GetMaterial(i), Is.Not.Null);
                Assert.That(Single<ElementWorldExposureSystem>(scene).enabled, Is.True);
                Assert.That(Single<ElementFieldSolidBaker>(scene).enabled, Is.True);
                Disabled<GpuPbfFluidRuntime>(scene); Disabled<FluidChunkStreamingRuntime>(scene);
                Disabled<FluidGameplayOccupancyBridge>(scene); Disabled<GpuLiquidSurfaceRenderer>(scene);
                Disabled<DormantLiquidCellRenderer>(scene); Disabled<ElementWorldWaterRenderer>(scene);
                foreach (var material in new[] {MaterialId.Water,MaterialId.Poison,MaterialId.Sticky})
                { Surface<LiquidSurfaceRenderer>(scene,world,material); Surface<StableLiquidSurfaceRenderer>(scene,world,material); }
                LocalReferences(world.transform,scene);
            });
        }
        [Test]
        public void P8SharesCanonicalReactionRulesAndKeepsItsLocalBindings()
        {
            WithScene(P8,scene=>
            {
                var world=Single<ElementWorldRuntime>(scene);
                var canonical=new SerializedObject(AssetDatabase.LoadAssetAtPath<ElementWorldProfile>(Canonical));
                var profile=new SerializedObject(Reference<ElementWorldProfile>(world,"_profile"));
                Assert.That(profile.FindProperty("_waterSimulationMode").intValue,Is.EqualTo((int)WaterSimulationMode.GridLiquid));
                foreach(var field in new[]{"_materialCatalog","_materialSimulationRouting","_materialStatusProjection","_reactionProfile","_materialReactionBindings"})
                    Assert.That(profile.FindProperty(field).objectReferenceValue,Is.SameAs(canonical.FindProperty(field).objectReferenceValue),field);
                // 共享的是规则资产；数量、History、Mesh 和 Buffer 仍由 P8 的独立 Runtime 持有。
                Assert.That(Single<LiquidWorldRuntime>(scene).gameObject.scene,Is.EqualTo(scene));
                Assert.That(Single<GridLiquidFieldSurfaceRenderer>(scene).gameObject.scene,Is.EqualTo(scene));
                Assert.That(Reference<Transform>(world,"_interestPoint").gameObject.scene,Is.EqualTo(scene));
                Assert.That(Single<ReactionBurstVisualController>(scene).enabled,Is.True);
                Assert.That(Components<TerrainCollider>(scene).Length,Is.GreaterThan(0));
                foreach(var child in world.GetComponentsInChildren<Transform>(true))
                    if(child.name=="FluidTerrainCollision")Assert.That(child.gameObject.activeSelf,Is.False,"Grid 使用真实 Terrain Collider；旧 PBF 代理应停用。");
            });
        }
        [TestCase(P7)]
        [TestCase(P8)]
        public void ProductionFireUsesUnifiedStyleAndItsOwnWorld(string path)
        {
            WithScene(path,scene=>
            {
                var world=Single<ElementWorldRuntime>(scene);var fire=Single<GpuFireParcelRenderer>(scene);
                Assert.That(fire.enabled,Is.True);
                Assert.That(Reference<ElementWorldRuntime>(fire,"_worldRuntime"),Is.SameAs(world));
                Assert.That(Reference<Transform>(fire,"_interestCenter").gameObject.scene,Is.EqualTo(scene));
                Assert.That(Reference<GpuFireParcelRenderProfile>(fire,"_profile").UnifiedSoftFlames,Is.True);
                Assert.That(Reference<ComputeShader>(fire,"_lifecycleCompute"),Is.Not.Null);
                Assert.That(Reference<Material>(fire,"_gasMaterial"),Is.Not.Null);
            });
        }
        [TestCase("home", "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Home.unity", "HOME")]
        [TestCase("p7", P7, "P7")]
        [TestCase("p8", P8, "P8")]
        public void ProductionSceneAndItsOnlineExportSelectTheSameCellularBase(string mapId, string path, string assetName)
        {
            WithScene(path, scene =>
            {
                var world = Single<ElementWorldRuntime>(scene);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/_Project/Prefabs/World/Online/{assetName}World.prefab");
                Assert.That(prefab, Is.Not.Null);
                Assert.That(prefab.activeSelf, Is.False, "在线地图先装配 Authority/Replica 所有权，再激活。");
                var exports = prefab.GetComponentsInChildren<ElementWorldRuntime>(true);
                Assert.That(exports.Length, Is.EqualTo(1));
                var exported = exports[0];
                foreach (var owner in new[] { world, exported })
                {
                    Assert.That(owner.CellularMapContentId, Is.EqualTo(mapId));
                    Assert.That(owner.UsesCellularWorld, Is.True, "作者几何不能只保存了配置却在初始化选择中回退。");
                }
                Assert.That(world.CellularMapBaseFingerprint, Is.Not.Zero);
                Assert.That(exported.CellularMapBaseFingerprint, Is.EqualTo(world.CellularMapBaseFingerprint));
            });
        }

        [Test]
        public void ReplicaUsesTheReboundActorCameraWhileTheOldCameraIsStillActive()
        {
            var owner = new GameObject("Inactive replica");
            owner.SetActive(false);
            var world = owner.AddComponent<ElementWorldRuntime>();
            world.ConfigureExecutionMode(WorldExecutionMode.Replica);
            var actor = new GameObject("Local actor");
            var oldCamera = new GameObject("Transition camera").AddComponent<Camera>();
            var camera = new GameObject("Bound game camera").AddComponent<Camera>();
            int ownerId = owner.GetInstanceID();
            try
            {
                LocalActorPresentation.Bind(ownerId, actor, true);
                LocalActorCameraPresentation.Bind(ownerId, actor, oldCamera);
                Assert.That(CellularMaterialRenderer.ResolveCamera(world, null), Is.SameAs(oldCamera));
                LocalActorCameraPresentation.Bind(ownerId, actor, camera);
                Assert.That(oldCamera.isActiveAndEnabled, Is.True);
                Assert.That(CellularMaterialRenderer.ResolveCamera(world, oldCamera), Is.SameAs(camera));
            }
            finally
            {
                LocalActorCameraPresentation.Clear(ownerId);
                LocalActorPresentation.Clear(ownerId);
                Object.DestroyImmediate(camera.gameObject); Object.DestroyImmediate(oldCamera.gameObject);
                Object.DestroyImmediate(actor); Object.DestroyImmediate(owner);
            }
        }

        [TestCase(WorldExecutionMode.Authority)]
        [TestCase(WorldExecutionMode.Replica)]
        public void NestedInactiveP7RoomFindsItsOwnRocksBeforeConfiguringMaterialOwnership(WorldExecutionMode mode)
        {
            var room = EditorSceneManager.NewPreviewScene();
            try
            {
                var bootstrap = new GameObject("OnlineRoom bootstrap");
                SceneManager.MoveGameObjectToScene(bootstrap, room);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/World/Online/P7World.prefab");
                var content = Object.Instantiate(prefab, bootstrap.transform, false);
                Assert.That(content.activeSelf, Is.False);
                var binding = content.GetComponent<OnlineMaterialWorldBinding>();
                Assert.That(binding, Is.Not.Null);
                // 房间仍未激活，__Level 在导出地图根下，Scene 的直接根只有 bootstrap。此时两端都要
                // 确认四个 MeshCollider 属于自己的地图，不能等 Awake 才修复已缓存的后端选择。
                Assert.That(binding.TrySetCellularDirectoryLimit(8), Is.True);
                Assert.That(binding.TryConfigure(mode, 1), Is.True);
                Assert.That(binding.UsesCellularReplication, Is.True);
                Assert.That(content.GetComponentInChildren<ElementWorldRuntime>(true).ExecutionMode, Is.EqualTo(mode));
            }
            finally { EditorSceneManager.ClosePreviewScene(room); }
        }

        [Test]
        public void P8ActualRuntimePublishesLiquidSandAndGasToThePresentationReadModel()
        {
            var scene = EditorSceneManager.OpenPreviewScene(P8);
            try
            {
                var world=Single<ElementWorldRuntime>(scene);
                // 真实 P8 副本走与施法一致的队列，四种液体、沙和蒸汽在同一批 Tick 中推进。
                var initialize=typeof(ElementWorldRuntime).GetMethod("TryInitialize",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                Assert.That((bool)initialize.Invoke(world,null),Is.True);
                Assert.That(world.EffectiveWaterSimulationMode,Is.EqualTo(WaterSimulationMode.Cellular));
                var player=Reference<Transform>(world,"_interestPoint");var terrain=Components<Terrain>(scene)[0];
                var materials=new[]{MaterialId.Water,MaterialId.Poison,MaterialId.Sticky,MaterialId.Oil,MaterialId.Sand,MaterialId.Steam};
                var changed = new HashSet<Vector3Int>();
                void Observe(Vector3Int cell) => changed.Add(cell);
                world.CellularContentsChanged += Observe;
                Physics.SyncTransforms();
                for(int i=0;i<materials.Length;i++)
                {
                    var position=player.position+Vector3.forward*(i*3f+2f);
                    position.y=terrain.SampleHeight(position)+terrain.transform.position.y+3f;
                    var request=new ElementWriteRequest(position,materials[i],255,0f,false);
                    Assert.That(world.TryEnqueueWrite(in request),Is.True,materials[i].ToString());
                }
                for(int i=0;i<3;i++)Assert.That(world.TickForTests(world.CellularTickDuration),Is.EqualTo(1));
                world.CellularContentsChanged -= Observe;
                var seen = new HashSet<MaterialId>();
                foreach (var cell in changed)
                {
                    if (!world.TryGetCellularContents(cell, out var primary, out uint quantity, out uint steam, out _)) continue;
                    if (quantity > 0 && world.MaterialAmounts.TryGetAmount(cell, primary, out byte amount) && amount > 0)
                    {
                        seen.Add(primary);
                        var fireCell = cell + Vector3Int.up;
                        var center = world.Origin + ((Vector3)fireCell + Vector3.one * .5f) * world.CellSize;
                        Assert.That(GpuFireParcelRenderer.HasGroundSupport(world, fireCell, center, world.CellSize),
                            Is.EqualTo(ElementCellMaterialRules.OccupiesWholeCell(primary)), primary.ToString());
                    }
                    if (steam > 0 && world.MaterialAmounts.TryGetAmount(cell, MaterialId.Steam, out byte gas) && gas > 0) seen.Add(MaterialId.Steam);
                }
                CollectionAssert.IsSubsetOf(materials, seen, "表现收到变更后读取的材料与 Gameplay 采样必须相符。");
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }
        private static void WithScene(string path,Action<Scene> check)
        {
            Scene active=SceneManager.GetActiveScene(),scene=SceneManager.GetSceneByPath(path);
            bool opened=!scene.IsValid()||!scene.isLoaded;
            if(opened)scene=EditorSceneManager.OpenScene(path,OpenSceneMode.Additive);
            try{check(scene);}
            finally{if(active.IsValid()&&active.isLoaded)SceneManager.SetActiveScene(active);if(opened&&scene.IsValid()&&scene.isLoaded)EditorSceneManager.CloseScene(scene,true);}
        }
        private static T[] Components<T>(Scene scene)where T:Component
        {var list=new System.Collections.Generic.List<T>();foreach(var root in scene.GetRootGameObjects())list.AddRange(root.GetComponentsInChildren<T>(true));return list.ToArray();}
        private static T Single<T>(Scene scene)where T:Component
        {var found=Components<T>(scene);Assert.That(found.Length,Is.EqualTo(1),typeof(T).Name);return found[0];}
        private static T Reference<T>(Object owner,string field)where T:Object=>new SerializedObject(owner).FindProperty(field).objectReferenceValue as T;
        private static void Disabled<T>(Scene scene)where T:Behaviour
        {foreach(var component in Components<T>(scene))Assert.That(component.enabled,Is.False,typeof(T).Name);}
        private static void Surface<T>(Scene scene,ElementWorldRuntime world,MaterialId material)where T:Behaviour
        {
            int count=0;foreach(var surface in Components<T>(scene))
            {var data=new SerializedObject(surface);if(data.FindProperty("_material").intValue!=(int)material)continue;count++;
                Assert.That(surface.enabled,Is.True);Assert.That(data.FindProperty("_world").objectReferenceValue,Is.SameAs(world));
                Assert.That(Reference<Material>(surface,"_sourceMaterial"),Is.Not.Null);
                Assert.That(Reference<Shader>(surface,"_liquidShader").name,Is.EqualTo("Game/Elemental/Liquid Dormant Cell"));}
            Assert.That(count,Is.EqualTo(1),material+" "+typeof(T).Name);
        }
        private static void LocalReferences(Transform root,Scene scene)
        {
            foreach(var component in root.GetComponentsInChildren<Component>(true))
            {if(component==null)continue;var property=new SerializedObject(component).GetIterator();
                while(property.NextVisible(true)){if(property.propertyType!=SerializedPropertyType.ObjectReference)continue;
                    Object value=property.objectReferenceValue;var target=value is Component c?c.gameObject:value as GameObject;
                    if(target!=null&&target.scene.IsValid())Assert.That(target.scene,Is.EqualTo(scene),component.name+"."+property.propertyPath);}}
        }
    }
}
