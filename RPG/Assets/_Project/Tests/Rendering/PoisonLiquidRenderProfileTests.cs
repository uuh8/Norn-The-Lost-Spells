using NUnit.Framework;
using UnityEditor;

namespace Game.Rendering.Tests
{
    public sealed class PoisonLiquidRenderProfileTests
    {
        [Test]
        public void PoisonUsesStrongerStylizedCrownThanWater()
        {
            LiquidRenderSettings water = Load("LiquidRenderProfile_Water").CreateSettings();
            LiquidRenderSettings poison = Load("LiquidRenderProfile_Poison").CreateSettings();

            Assert.That(poison.UseStylizedCrown, Is.True);
            Assert.That(poison.CrownHeightRatio, Is.GreaterThan(water.CrownHeightRatio));
        }

        private static LiquidRenderProfile Load(string name)
        {
            string path = $"Assets/_Project/ScriptableObjects/ElementField/Fluid/{name}.asset";
            LiquidRenderProfile profile = AssetDatabase.LoadAssetAtPath<LiquidRenderProfile>(path);
            Assert.That(profile, Is.Not.Null, $"缺少液面重建 Profile：{path}");
            return profile;
        }
    }
}
