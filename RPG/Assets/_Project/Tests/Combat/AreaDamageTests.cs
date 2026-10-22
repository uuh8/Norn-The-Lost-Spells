using NUnit.Framework;
using UnityEngine;

namespace Game.Combat.Tests
{
    /// <summary>
    /// AreaDamage 的 Authoring 默认值可以来自 Prefab，但陨石等复合法术必须能在
    /// Instantiate 后、Start 首跳前注入本次施法快照；这里验证这条 Runtime 配置契约。
    /// </summary>
    public sealed class AreaDamageTests
    {
        [Test]
        public void RuntimeConfiguration_ClampsAndStoresDamageAndTickInterval()
        {
            GameObject root = new GameObject("AreaDamageTest");
            try
            {
                AreaDamage area = root.AddComponent<AreaDamage>();

                area.ConfigureDamage(2f, DamageType.Magical);
                area.ConfigureTickInterval(0.5f);
                area.ConfigureDuration(5f);

                Assert.AreEqual(2f, area.DamagePerHit, 1e-4f);
                Assert.AreEqual(0.5f, area.TickInterval, 1e-4f);
                Assert.AreEqual(10, area.TickLimit);

                area.ConfigureDamage(-10f, DamageType.True);
                area.ConfigureTickInterval(-1f);
                area.ConfigureDuration(-1f);

                Assert.AreEqual(0f, area.DamagePerHit, 1e-4f);
                Assert.AreEqual(0f, area.TickInterval, 1e-4f);
                Assert.AreEqual(-1, area.TickLimit);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
