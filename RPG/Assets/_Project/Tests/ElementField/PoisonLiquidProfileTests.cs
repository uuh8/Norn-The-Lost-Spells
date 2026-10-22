using NUnit.Framework;
using UnityEditor;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// “史莱姆感”不是单纯提高 RestDensity，而是锁定 Poison 相对 Water 的
    /// Viscosity、Cohesion 与 anti-clumping 压力关系。
    /// </summary>
    public sealed class PoisonLiquidProfileTests
    {
        [Test]
        public void PoisonPresetIsMoreViscousAndCohesiveThanWater()
        {
            LiquidMaterialSettings water = Load("LiquidMaterialProfile_Water").CreateSettings();
            LiquidMaterialSettings poison = Load("LiquidMaterialProfile_Poison").CreateSettings();

            Assert.That(poison.Viscosity, Is.GreaterThanOrEqualTo(water.Viscosity * 3f));
            Assert.That(poison.CohesionStrength, Is.GreaterThanOrEqualTo(water.CohesionStrength * 1.8f));
            Assert.That(poison.ArtificialPressure, Is.LessThanOrEqualTo(water.ArtificialPressure));
        }

        private static LiquidMaterialProfile Load(string name)
        {
            string path = $"Assets/_Project/ScriptableObjects/ElementField/Fluid/{name}.asset";
            LiquidMaterialProfile profile = AssetDatabase.LoadAssetAtPath<LiquidMaterialProfile>(path);
            Assert.That(profile, Is.Not.Null, $"缺少液体材质 Profile：{path}");
            return profile;
        }
    }
}
