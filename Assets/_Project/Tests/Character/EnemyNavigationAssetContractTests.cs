using Game.Combat;
using NUnit.Framework;
using UnityEditor;

namespace Game.Character.Tests
{
    /// <summary>
    /// 旧 ScriptableObject 不会因为 C# 新增字段就自动把推荐值写回 YAML。
    /// 这里锁定所有当前生产 EnemyDefinition 的导航参数，避免缺失字段在 Runtime 退化为 0。
    /// </summary>
    public sealed class EnemyNavigationAssetContractTests
    {
        [TestCase("Assets/_Project/ScriptableObjects/Enemy/Enemy_Sword_Definition.asset")]
        [TestCase("Assets/_Project/ScriptableObjects/Enemy/Enemy_Wizard_Definition.asset")]
        // 精英资源已归入 EliteEnemy；检查真实生产资产，避免旧路径导致覆盖静默丢失。
        [TestCase("Assets/_Project/ScriptableObjects/P7/EliteEnemy/P7_EliteMagicEnemyDefinition.asset")]
        public void ProductionEnemyDefinition_HasUsableSerializedNavigationParameters(string assetPath)
        {
            EnemyDefinition definition = AssetDatabase.LoadAssetAtPath<EnemyDefinition>(assetPath);

            Assert.That(definition, Is.Not.Null, $"EnemyDefinition 资产不存在：{assetPath}");
            Assert.That(definition.PathRefreshInterval, Is.GreaterThan(0f), "PathRefreshInterval 必须大于 0");
            Assert.That(definition.DestinationMoveThreshold, Is.GreaterThan(0f), "DestinationMoveThreshold 必须大于 0");
            Assert.That(definition.DestinationSampleRadius, Is.GreaterThan(0f), "DestinationSampleRadius 必须大于 0");
            Assert.That(definition.RetreatStepDistance, Is.GreaterThan(0f), "RetreatStepDistance 必须大于 0");
            Assert.That(definition.RetreatSampleRadius, Is.GreaterThan(0f), "RetreatSampleRadius 必须大于 0");
            Assert.That(definition.StuckCheckInterval, Is.GreaterThan(0f), "StuckCheckInterval 必须大于 0");
            Assert.That(definition.StuckProgressDistance, Is.GreaterThan(0f), "StuckProgressDistance 必须大于 0");
        }
    }
}
