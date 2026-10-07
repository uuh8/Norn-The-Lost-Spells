using Game.ElementField;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Rendering.Tests
{
    public sealed class MaterialPhaseAssetTests
    {
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
