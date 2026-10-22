using Game.ElementField;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Rendering.Tests
{
    public sealed class MaterialPhaseAssetTests
    {
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
        public void ActualSteamGasMovesUpAndWithWindInsteadOfLeavingExposureAtBirth()
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
                Vector3 first=GasCenter(gas);
                for(int tick=0;tick<20;tick++) gas.Step(.1f);
                Vector3 last=GasCenter(gas);
                Assert.That(last.y-first.y,Is.GreaterThan(.1f),"蒸汽的真实浓度场需要向上漂移。");
                Assert.That(last.x-first.x,Is.GreaterThan(.05f),"真实风移动 Exposure，不能仅让 Shader 图案动。");
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
        public void NewShadersCompileWithoutErrors(string shaderName)
        {
            var shader = Shader.Find(shaderName); Assert.That(shader, Is.Not.Null);
            var messages = ShaderUtil.GetShaderMessages(shader);
            foreach (var message in messages)
                Assert.That(message.severity, Is.Not.EqualTo(UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error), message.message);
        }
    }
}
