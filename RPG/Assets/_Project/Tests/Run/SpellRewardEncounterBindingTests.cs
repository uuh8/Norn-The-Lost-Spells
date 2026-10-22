using System.Reflection;
using Game.Run;
using NUnit.Framework;
using UnityEngine;

namespace Game.Run.Tests
{
    public sealed class SpellRewardEncounterBindingTests
    {
        [Test]
        public void BindChildRewards_OverridesCopiedSerializedEncounterIndex_IncludingInactivePickup()
        {
            GameObject encounterObject = new GameObject("Encounter");
            GameObject rewardObject = new GameObject("CopiedReward");
            rewardObject.transform.SetParent(encounterObject.transform);

            try
            {
                DemoEncounterController encounter = encounterObject.AddComponent<DemoEncounterController>();
                // SpellRewardPickup 声明了 RequireComponent(Collider)。
                // 测试夹具必须先建立与真实 Reward Prefab 相同的组件前置条件，
                // 否则 Unity 会拒绝添加 Pickup，后续 Reflection 实际是在访问 null。
                BoxCollider rewardTrigger = rewardObject.AddComponent<BoxCollider>();
                rewardTrigger.isTrigger = true;
                SpellRewardPickup reward = rewardObject.AddComponent<SpellRewardPickup>();

                // 模拟常见的场景复制错误：新关卡的奖励仍保留 Prefab 中的旧编号 0。
                // 奖励在关卡完成前通常处于 inactive，父控制器仍必须能够找到并绑定它。
                SetPrivateField(encounter, "_encounterIndex", 2);
                SetPrivateField(reward, "_encounterIndex", 0);
                rewardObject.SetActive(false);

                MethodInfo bindMethod = typeof(DemoEncounterController).GetMethod(
                    "BindChildRewards",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                Assert.That(
                    bindMethod,
                    Is.Not.Null,
                    "关卡控制器必须负责绑定其子奖励，避免 Inspector 出现第二份关卡编号数据源。");

                bindMethod.Invoke(encounter, null);

                int boundIndex = GetPrivateField<int>(reward, "_encounterIndex");
                Assert.That(boundIndex, Is.EqualTo(2));
            }
            finally
            {
                Object.DestroyImmediate(encounterObject);
            }
        }

        [Test]
        public void BindChildRewards_BindsAllSpellsRewardAndStageExitPortal_IncludingInactiveObjects()
        {
            GameObject encounterObject = new GameObject("Encounter");
            GameObject rewardObject = new GameObject("AllSpellsReward");
            GameObject portalObject = new GameObject("StageExitPortal");
            rewardObject.transform.SetParent(encounterObject.transform);
            portalObject.transform.SetParent(encounterObject.transform);

            try
            {
                DemoEncounterController encounter = encounterObject.AddComponent<DemoEncounterController>();
                rewardObject.AddComponent<BoxCollider>().isTrigger = true;
                portalObject.AddComponent<BoxCollider>().isTrigger = true;
                DungeonFinalRewardPickup reward = rewardObject.AddComponent<DungeonFinalRewardPickup>();
                StageExitPortal portal = portalObject.AddComponent<StageExitPortal>();

                SetPrivateField(encounter, "_encounterIndex", 3);
                SetPrivateField(reward, "_encounterIndex", 0);
                SetPrivateField(portal, "_encounterIndex", 0);
                rewardObject.SetActive(false);
                portalObject.SetActive(false);

                MethodInfo bindMethod = typeof(DemoEncounterController).GetMethod(
                    "BindChildRewards",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(bindMethod, Is.Not.Null);
                bindMethod.Invoke(encounter, null);

                Assert.That(GetPrivateField<int>(reward, "_encounterIndex"), Is.EqualTo(3));
                Assert.That(GetPrivateField<int>(portal, "_encounterIndex"), Is.EqualTo(3));
            }
            finally
            {
                Object.DestroyImmediate(encounterObject);
            }
        }

        private static void SetPrivateField<T>(object target, string fieldName, T value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"缺少测试所需字段：{fieldName}");
            field.SetValue(target, value);
        }

        private static T GetPrivateField<T>(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"缺少测试所需字段：{fieldName}");
            return (T)field.GetValue(target);
        }
    }
}
