using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Skills.Tests
{
    /// <summary>
    /// 锁定毒球作为正式开局法术所需的数据面，避免只有 Prefab 半成品却没有 UI/Library 接入。
    /// </summary>
    public sealed class PoisonSpellContentTests
    {
        private const string PoisonSpellPath =
            "Assets/_Project/ScriptableObjects/Spell/Definitions/Spell_Emit_PoisonProjectile.asset";
        private const string PoisonIconPath =
            "Assets/_Project/Art/UI/Skill_Icon/Spell_Emit_Poisonball.png";
        private const string FullLibraryPath =
            "Assets/_Project/ScriptableObjects/Spell/Libraries/SpellLibrary_Player.asset";
        private const string WaterProjectilePath =
            "Assets/_Project/Art/Elemental/Prefabs/PF_ElementWaterProjectile.prefab";
        private const string PoisonProjectileMaterialPath =
            "Assets/_Project/Art/Elemental/Materials/M_ElementPoison_Projectile.mat";
        private const string WaterPbfMaterialPath =
            "Assets/_Project/Art/Elemental/Materials/M_ElementWater_PBF.mat";
        private const string PoisonPbfMaterialPath =
            "Assets/_Project/Art/Elemental/Materials/M_ElementPoison_PBF.mat";

        [Test]
        public void SpellElementAppendsPoisonWithoutChangingPublishedValues()
        {
            Assert.That(Enum.GetName(typeof(SpellElement), 0), Is.EqualTo(nameof(SpellElement.None)));
            Assert.That(Enum.GetName(typeof(SpellElement), 1), Is.EqualTo(nameof(SpellElement.Fire)));
            Assert.That(Enum.GetName(typeof(SpellElement), 2), Is.EqualTo(nameof(SpellElement.Water)));
            Assert.That(Enum.GetName(typeof(SpellElement), 3), Is.EqualTo(nameof(SpellElement.Arcane)));
            Assert.That(Enum.GetName(typeof(SpellElement), 4), Is.EqualTo("Poison"));
        }

        [Test]
        public void PoisonSpellUsesPoisonElementPreparedIconAndProjectile()
        {
            SpellDefinition poison = AssetDatabase.LoadAssetAtPath<SpellDefinition>(PoisonSpellPath);
            Assert.That(poison, Is.Not.Null);

            var serialized = new SerializedObject(poison);
            Assert.That(serialized.FindProperty("Element").intValue, Is.EqualTo(4));
            Assert.That(AssetDatabase.GetAssetPath(poison.Icon), Is.EqualTo(PoisonIconPath));
            Assert.That(poison.ProjectilePrefab, Is.Not.Null);
            Assert.That(poison.BaseDamage, Is.Zero,
                "毒球第一版只负责沉积 Poison，不能同时偷偷走直接伤害语义。");
        }

        [Test]
        public void PoisonProjectileUsesVisibleMeshShaderAndIndependentGreenMaterial()
        {
            SpellDefinition poison = AssetDatabase.LoadAssetAtPath<SpellDefinition>(PoisonSpellPath);
            GameObject waterProjectile = AssetDatabase.LoadAssetAtPath<GameObject>(WaterProjectilePath);
            Material expectedPoisonMaterial =
                AssetDatabase.LoadAssetAtPath<Material>(PoisonProjectileMaterialPath);

            Assert.That(poison, Is.Not.Null);
            Assert.That(poison.ProjectilePrefab, Is.Not.Null);
            Assert.That(waterProjectile, Is.Not.Null);
            Assert.That(expectedPoisonMaterial, Is.Not.Null,
                "毒球必须使用独立的普通 Mesh Material，不能复用 PBF Procedural Material。");

            MeshFilter poisonFilter = poison.ProjectilePrefab.GetComponent<MeshFilter>();
            MeshRenderer poisonRenderer = poison.ProjectilePrefab.GetComponent<MeshRenderer>();
            MeshRenderer waterRenderer = waterProjectile.GetComponent<MeshRenderer>();

            Assert.That(poisonFilter, Is.Not.Null);
            Assert.That(poisonFilter.sharedMesh, Is.Not.Null);
            Assert.That(poisonRenderer, Is.Not.Null);
            Assert.That(poisonRenderer.enabled, Is.True);
            Assert.That(waterRenderer, Is.Not.Null);
            Assert.That(poisonRenderer.sharedMaterial, Is.SameAs(expectedPoisonMaterial));
            Assert.That(poisonRenderer.sharedMaterial, Is.Not.SameAs(waterRenderer.sharedMaterial));
            Assert.That(poisonRenderer.sharedMaterial.shader.name,
                Is.EqualTo(waterRenderer.sharedMaterial.shader.name));
            Assert.That(poisonRenderer.sharedMaterial.shader.name,
                Is.Not.EqualTo("Game/Elemental/Liquid Procedural"));

            AssertGreenPalette(poisonRenderer.sharedMaterial);
        }

        [Test]
        public void PoisonPbfPaletteStaysGreenAcrossDepthFresnelAndFoam()
        {
            Material water = AssetDatabase.LoadAssetAtPath<Material>(WaterPbfMaterialPath);
            Material poison = AssetDatabase.LoadAssetAtPath<Material>(PoisonPbfMaterialPath);

            Assert.That(water, Is.Not.Null);
            Assert.That(poison, Is.Not.Null);
            AssertGreenPalette(poison);

            string[] colorProperties =
            {
                "_ShallowColor", "_DeepColor", "_FresnelColor", "_FoamColor"
            };
            for (int index = 0; index < colorProperties.Length; index++)
            {
                string property = colorProperties[index];
                Assert.That(ColorDistance(poison.GetColor(property), water.GetColor(property)),
                    Is.GreaterThan(0.2f),
                    $"{property} 与 Water 太接近，透明混合后无法稳定区分 Poison。");
            }
        }

        [TestCase(FullLibraryPath)]
        public void PoisonSpellIsAvailableFromPlayerLibrary(string libraryPath)
        {
            SpellDefinition poison = AssetDatabase.LoadAssetAtPath<SpellDefinition>(PoisonSpellPath);
            SpellLibrary library = AssetDatabase.LoadAssetAtPath<SpellLibrary>(libraryPath);

            Assert.That(poison, Is.Not.Null);
            Assert.That(library, Is.Not.Null);
            Assert.That(library.Available, Does.Contain(poison));
        }

        private static void AssertGreenPalette(Material material)
        {
            string[] colorProperties =
            {
                "_ShallowColor", "_DeepColor", "_FresnelColor", "_FoamColor"
            };
            for (int index = 0; index < colorProperties.Length; index++)
            {
                string property = colorProperties[index];
                Assert.That(material.HasProperty(property), Is.True);
                Color color = material.GetColor(property);
                Assert.That(color.g, Is.GreaterThan(color.b),
                    $"{property} 必须由绿色通道主导，不能继续沿用水的青蓝边缘色。\n实际值：{color}");
            }
        }

        private static float ColorDistance(Color left, Color right)
        {
            float red = left.r - right.r;
            float green = left.g - right.g;
            float blue = left.b - right.b;
            return Mathf.Sqrt(red * red + green * green + blue * blue);
        }
    }
}
