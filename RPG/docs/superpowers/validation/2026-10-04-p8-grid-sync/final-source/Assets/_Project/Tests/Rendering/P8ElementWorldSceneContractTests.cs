using System;
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
    /// <summary>正式地图必须消费当前 Grid 规则；Additive 审计保留当前 P7 实例和未保存修改。</summary>
    public sealed class P8ElementWorldSceneContractTests
    {
        private const string P7 = "Assets/_Project/Scenes/P7_DemoRun.unity";
        private const string P8 = "Assets/_Project/Scenes/P8_BossField.unity";
        private const string Canonical = "Assets/_Project/ScriptableObjects/ElementField/Liquids/ElementWorldProfile_P7_DemoRun_Grid.asset";
        private const string P8Profile = "Assets/_Project/ScriptableObjects/P8/ElementField/ElementWorldProfile_P8BossField.asset";

        [TestCase(P7, Canonical)]
        [TestCase(P8, P8Profile)]
        public void ProductionSceneOwnsGridRuntimeAndUsesCurrentContinuousSurface(string path, string profilePath)
        {
            WithScene(path, scene =>
            {
                var world = Single<ElementWorldRuntime>(scene);
                var profile = AssetDatabase.LoadAssetAtPath<ElementWorldProfile>(profilePath);
                var data = new SerializedObject(world);
                Assert.That(data.FindProperty("_profile").objectReferenceValue, Is.SameAs(profile));
                Assert.That(profile.WaterSimulationMode, Is.EqualTo(WaterSimulationMode.GridLiquid), "Scene 必须明确选择当前 GridLiquid。");
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
        public void P8UsesCanonicalMaterialRulesAndOwnsACompleteGpuRuntimeChain()
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
        [Test]
        public void P8ActualRuntimeRoutesThreeLiquidsAndCapturesTheirSurface()
        {
            WithScene(P8,scene=>
            {
                var world=Single<ElementWorldRuntime>(scene);var liquid=Single<LiquidWorldRuntime>(scene);
                // EditMode 显式驱动真实 Scene Runtime；不进入 Play，不执行 Boss AI，也不重载 P7。
                var initialize=typeof(ElementWorldRuntime).GetMethod("TryInitialize",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                Assert.That((bool)initialize.Invoke(world,null),Is.True);
                Assert.That(world.EffectiveWaterSimulationMode,Is.EqualTo(WaterSimulationMode.GridLiquid));
                var player=Reference<Transform>(world,"_interestPoint");var terrain=Components<Terrain>(scene)[0];
                var materials=new[]{MaterialId.Water,MaterialId.Poison,MaterialId.Sticky};
                var positions=new Vector3[3];ulong before=liquid.TotalQuantity;
                Physics.SyncTransforms();
                for(int i=0;i<materials.Length;i++)
                {
                    var position=player.position+Vector3.forward*(i*3f+2f);
                    position.y=terrain.SampleHeight(position)+terrain.transform.position.y+.75f;positions[i]=position;
                    var request=new ElementWriteRequest(position,materials[i],255,0f,false);
                    Assert.That(world.TryEnqueueWrite(in request),Is.True,materials[i].ToString());
                }
                for(int i=0;i<3;i++)Assert.That(world.TickForTests(.1f),Is.EqualTo(1));
                Assert.That(liquid.TotalQuantity,Is.EqualTo(before+765));
                Assert.That(liquid.PendingQuantity,Is.Zero);
                for(int i=0;i<materials.Length;i++)
                {
                    Vector3 p=(positions[i]-world.Origin)/world.CellSize;
                    var center=new Vector3Int(Mathf.FloorToInt(p.x),Mathf.FloorToInt(p.y),Mathf.FloorToInt(p.z));bool queried=false;
                    for(int y=-6;y<=3;y++)for(int z=-3;z<=3;z++)for(int x=-3;x<=3;x++)
                        if(world.MaterialAmounts.TryGetAmount(center+new Vector3Int(x,y,z),materials[i],out byte amount)&&amount>0)queried=true;
                    Assert.That(queried,Is.True,materials[i]+" 写入后必须能由 Gameplay 权威查询读取。");
                }
                var frame=new GridLiquidFieldFrame(liquid,64,256,.8f);frame.Capture();
                Assert.That(frame.Count,Is.GreaterThan(0));Assert.That(frame.OwnerCount,Is.GreaterThan(0));
            });
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
