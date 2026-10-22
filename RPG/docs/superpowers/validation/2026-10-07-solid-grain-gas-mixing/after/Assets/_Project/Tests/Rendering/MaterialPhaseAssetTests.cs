using Game.ElementField;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Rendering.Tests
{
    public sealed class MaterialPhaseAssetTests
    {
        [Test]
        public void ProductionSteamNeedsColdAirAndIceRemainsSolidAtAmbientTemperature()
        {
            var profile=AssetDatabase.LoadAssetAtPath<MaterialConversionProfile>("Assets/_Project/ScriptableObjects/ElementField/Phases/MaterialConversions_Default.asset");
            var ambient=new MaterialEnvironmentSample(profile.AmbientTemperature,profile.AmbientDryness);
            var cold=profile.GetRule(1); var melt=profile.GetRule(3);
            Assert.That(profile.IsValid,Is.True);
            Assert.That(MaterialConversionEvaluator.Matches(in cold,in ambient),Is.False,"常温不能把每团蒸汽当成持续滴水源。");
            Assert.That(MaterialConversionEvaluator.Matches(in melt,in ambient),Is.False,"没有外界干扰时固体应保留。");
            Assert.That(cold.FirstUnits/(float)cold.InputUnits,Is.LessThanOrEqualTo(.125f),"小气团只形成少量可见冷凝水。");
            Assert.That(cold.InputFractionPerSecond,Is.LessThanOrEqualTo(.05f));
        }
        [Test]
        public void ProductionSteamAppliesGentleWetAndGasDiffusionKeepsMovingSlowly()
        {
            var projection=new SerializedObject(AssetDatabase.LoadAssetAtPath<MaterialStatusProjectionProfile>("Assets/_Project/ScriptableObjects/Materials/MaterialStatusProjection_Default.asset"));
            var bindings=projection.FindProperty("_bindings"); bool steamWet=false;
            for(int i=0;i<bindings.arraySize;i++)
            {
                var binding=bindings.GetArrayElementAtIndex(i);
                if(binding.FindPropertyRelative("Material").intValue!=(int)MaterialId.Steam) continue;
                steamWet=binding.FindPropertyRelative("Status").intValue==(int)Game.Combat.StatusKind.Wet
                    && binding.FindPropertyRelative("MaximumApplyPerExposureTick").floatValue>0;
            }
            Assert.That(steamWet,Is.True,"Steam 必须通过真实 Exposure 查询施加 Wet。");
            var gas=AssetDatabase.LoadAssetAtPath<GasWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Phases/GasWorld_Default.asset");
            Assert.That(gas.WindVelocity,Is.EqualTo(Vector3.zero),"尚无环境风规则，生产云团不得持续朝默认方向偏移。");
            for(int i=0;i<gas.MaterialCount;i++)
            {
                var behavior=gas.GetMaterial(i);
                Assert.That(behavior.DiffusionPerSecond,Is.InRange(.1f,1f));
                Assert.That(behavior.BuoyancyPerSecond,Is.Zero);
            }
        }
        [TestCase("Assets/_Project/ScriptableObjects/ElementField/Liquids/ElementWorldProfile_P7_DemoRun_Grid.asset")]
        [TestCase("Assets/_Project/ScriptableObjects/P8/ElementField/ElementWorldProfile_P8BossField.asset")]
        public void BothPlayableWorldsUseShorterResidualFire(string path)
        {
            var profile=new SerializedObject(AssetDatabase.LoadAssetAtPath<ElementWorldProfile>(path));
            Assert.That(profile.FindProperty("_fireDecayPerTick").intValue,Is.EqualTo(2));
            Assert.That(profile.FindProperty("_tickRate").floatValue,Is.EqualTo(10f));
        }
        [TestCase("Ice")]
        [TestCase("DriedSlime")]
        public void ActualSolidProjectileOnRaisedFloorPublishesTheWholeImpactInFirstTick(string name)
        {
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Elemental/Prefabs/PF_Element"+name+"Projectile.prefab");
            var profile=Object.Instantiate(AssetDatabase.LoadAssetAtPath<FixedSolidWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Phases/FixedSolidWorld_Default.asset"));
            var host=new GameObject("Actual Solid Impact Test"); host.SetActive(false);
            var floor=new GameObject("Raised Test Floor"); floor.transform.position=new Vector3(101,100.03f,101);
            floor.AddComponent<BoxCollider>().size=new Vector3(12,.1f,12);
            try
            {
                var baker=host.AddComponent<ElementFieldSolidBaker>();
                var runtime=host.AddComponent<FixedSolidWorldRuntime>();
                var settings=new SerializedObject(runtime); settings.FindProperty("_profile").objectReferenceValue=profile; settings.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(runtime.TryInitialize(null,baker,Vector3.one*100,.25f,8),Is.True);
                Physics.SyncTransforms();
                Assert.That(prefab.GetComponent<ElementDepositOnImpact>().TryBuildImpactRequest(new Vector3(100.875f,100.08f,100.875f),Vector3.down,Vector3.up,out var request),Is.True);
                runtime.TryEnqueueDeposit(in request); runtime.Step();
                Assert.That(runtime.TotalQuantity,Is.EqualTo((ulong)request.TotalAmount),"实际 Radius + 地板接触格不能把固体法术拆成散落小格。");
                Assert.That(runtime.PendingQuantity,Is.Zero);
                Assert.That(runtime.SurfaceRebuildCount,Is.LessThanOrEqualTo(8u));
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(floor); Object.DestroyImmediate(profile); }
        }
        [Test]
        public void ActualToxicProjectileShowsAtLeastHalfItsMassOnFirstColdTerrainTick()
        {
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Elemental/Prefabs/PF_ElementToxicVaporProjectile.prefab");
            var profile=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GasWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Phases/GasWorld_Default.asset"));
            var host=new GameObject("Actual Toxic Impact Test"); host.SetActive(false);
            var floor=new GameObject("Toxic Test Floor"); floor.transform.position=new Vector3(101,100.03f,101);
            floor.AddComponent<BoxCollider>().size=new Vector3(12,.1f,12);
            try
            {
                var runtime=host.AddComponent<GasWorldRuntime>(); var baker=host.AddComponent<ElementFieldSolidBaker>();
                var settings=new SerializedObject(runtime); settings.FindProperty("_profile").objectReferenceValue=profile; settings.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(runtime.TryInitialize(null,baker,Vector3.one*100,.25f,8),Is.True); Physics.SyncTransforms();
                prefab.GetComponent<ElementDepositOnImpact>().TryBuildImpactRequest(new Vector3(100.875f,100.08f,100.875f),Vector3.down,Vector3.up,out var request);
                runtime.TryEnqueueDeposit(in request); runtime.Step(.1f);
                Assert.That(runtime.TotalQuantity,Is.GreaterThanOrEqualTo((ulong)request.TotalAmount/2),"大毒雾必须在首 Tick 形成明显云团，不能等待数秒才显露。");
                Assert.That(runtime.TotalQuantity+runtime.PendingQuantity,Is.EqualTo((ulong)request.TotalAmount));
                Assert.That(runtime.TerrainSamplesLastTick,Is.LessThanOrEqualTo(128),"出生加速仍共享原 Physics 查询上限。");
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(floor); Object.DestroyImmediate(profile); }
        }
        [Test]
        public void ActualSteamGasDiffusesWithoutAmbientWindInsteadOfLeavingExposureAtBirth()
        {
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Elemental/Prefabs/PF_ElementSteamProjectile.prefab");
            var profile=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GasWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Phases/GasWorld_Default.asset"));
            var host=new GameObject("Actual Steam Motion Test"); host.SetActive(false);
            try
            {
                var gas=host.AddComponent<GasWorldRuntime>(); var settings=new SerializedObject(gas);
                settings.FindProperty("_profile").objectReferenceValue=profile; settings.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(gas.TryInitialize(null,null,Vector3.one*100,.25f,8),Is.True);
                prefab.GetComponent<ElementDepositOnImpact>().TryBuildImpactRequest(Vector3.one*100.875f,Vector3.down,Vector3.up,out var request);
                gas.TryEnqueueDeposit(in request); gas.Step(.1f);
                float first=GasSpread(gas);
                for(int tick=0;tick<20;tick++) gas.Step(.1f);
                float last=GasSpread(gas);
                Assert.That(profile.WindVelocity,Is.EqualTo(Vector3.zero));
                Assert.That(last-first,Is.GreaterThan(.01f),"气体真实数量场仍须扩散，不能仅让 Shader 图案动。");
                Assert.That(gas.TotalQuantity+gas.PendingQuantity,Is.EqualTo((ulong)request.TotalAmount));
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(profile); }
        }
        private static Vector3 GasCenter(GasWorldRuntime gas)
        {
            Vector3 weighted=Vector3.zero; ulong total=0;
            for(int i=0;i<gas.ResidentCellCount;i++) if(gas.TryGetCell(i,out var cell))
            { weighted+=(Vector3)cell.Cell*cell.Quantity; total+=cell.Quantity; }
            return total==0?Vector3.zero:weighted/(float)total*.25f;
        }
        private static float GasSpread(GasWorldRuntime gas)
        {
            Vector3 center=GasCenter(gas)/.25f; double spread=0; ulong total=0;
            for(int i=0;i<gas.ResidentCellCount;i++) if(gas.TryGetCell(i,out var cell))
            { spread+=((Vector3)cell.Cell-center).sqrMagnitude*cell.Quantity; total+=cell.Quantity; }
            return total==0?0:(float)(spread/total)*.25f*.25f;
        }
        [Test]
        public void SteamProjectileCarriesTheSameFiniteWaterMassAsWaterProjectile()
        {
            var water = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Elemental/Prefabs/PF_ElementWaterProjectile.prefab");
            var steam = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Elemental/Prefabs/PF_ElementSteamProjectile.prefab");
            Assert.That(water.GetComponent<ElementDepositOnImpact>().TryBuildImpactRequest(Vector3.zero,Vector3.down,Vector3.up,out var wet),Is.True);
            Assert.That(steam.GetComponent<ElementDepositOnImpact>().TryBuildImpactRequest(Vector3.zero,Vector3.down,Vector3.up,out var gas),Is.True);
            Assert.That(gas.TotalAmount,Is.EqualTo(wet.TotalAmount),"形态变化不能让同级蒸汽球冷凝出十倍于水球的水。");
        }
        [TestCase("Ice", MaterialId.Ice, MaterialBehaviorKind.Solid)]
        [TestCase("DriedSlime", MaterialId.DriedSlime, MaterialBehaviorKind.Solid)]
        [TestCase("Steam", MaterialId.Steam, MaterialBehaviorKind.Gas)]
        [TestCase("ToxicVapor", MaterialId.ToxicVapor, MaterialBehaviorKind.Gas)]
        public void ProductionAssetsContainRealMaterialAndMatchingProjectile(string name, MaterialId id, MaterialBehaviorKind behavior)
        {
            var definition = AssetDatabase.LoadAssetAtPath<MaterialDefinition>("Assets/_Project/ScriptableObjects/Materials/Material_" + name + ".asset");
            Assert.That(definition, Is.Not.Null); Assert.That(definition.Id, Is.EqualTo(id)); Assert.That(definition.Behavior, Is.EqualTo(behavior));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Elemental/Prefabs/PF_Element" + name + "Projectile.prefab");
            Assert.That(prefab, Is.Not.Null);
            var deposit = prefab.GetComponent<ElementDepositOnImpact>(); Assert.That(deposit, Is.Not.Null);
            Assert.That(deposit.TryBuildImpactRequest(Vector3.zero, Vector3.down, Vector3.up, out var request), Is.True);
            Assert.That(request.MaterialKind, Is.EqualTo(id));
            // 新形态不得继承水球的 Wet 命中状态。
            var status = prefab.GetComponent<Game.Combat.ProjectileStatusOnImpact>();
            if (id != MaterialId.ToxicVapor) Assert.That(status, Is.Null);
        }
        [TestCase("Game/Materials/FixedSolid")]
        [TestCase("Game/Materials/RealGasVolume")]
        [TestCase("Game/Elemental/Liquid Continuous Field")]
        public void NewShadersCompileWithoutErrors(string shaderName)
        {
            var shader = Shader.Find(shaderName); Assert.That(shader, Is.Not.Null);
            var messages = ShaderUtil.GetShaderMessages(shader);
            foreach (var message in messages)
                Assert.That(message.severity, Is.Not.EqualTo(UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error), message.message);
        }
    }
}
