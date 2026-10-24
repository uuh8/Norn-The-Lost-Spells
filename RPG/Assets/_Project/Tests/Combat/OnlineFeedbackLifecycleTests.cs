using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEngine;

namespace Game.Combat.Tests
{
    /// <summary>表现不能在 Dedicated Server 授予死亡销毁，也不能让 Replica 禁掉网络拥有的组件。</summary>
    public sealed class OnlineFeedbackLifecycleTests
    {
        [TestCase(CombatExecutionMode.Authority)]
        [TestCase(CombatExecutionMode.Replica)]
        public void OnlineDeathDoesNotDisableNetworkOwnedComponents(CombatExecutionMode mode)
        {
            var root = new GameObject("Online feedback lifecycle");
            try
            {
                var feedback = root.AddComponent<CharacterCombatFeedback>();
                var sentinel = root.AddComponent<AudioSource>();
                typeof(CharacterCombatFeedback).GetField("_disableOnDeath", BindingFlags.NonPublic | BindingFlags.Instance)
                    .SetValue(feedback, new Behaviour[] { sentinel });
                var method = typeof(CharacterCombatFeedback).GetMethod("ConfigureExecutionMode");
                Assert.That(method, Is.Not.Null, "表现组件缺少在线权限配置；单机延迟 Destroy 不能接管网络角色。");
                method.Invoke(feedback, new object[] { mode });
                typeof(CharacterCombatFeedback).GetField("_id", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(feedback, root.GetInstanceID());
                typeof(CharacterCombatFeedback).GetMethod("OnDeath", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(feedback, new object[] { new DeathEvent { TargetId = root.GetInstanceID() } });
                Assert.That(sentinel.enabled, Is.True);
            }
            finally { Object.DestroyImmediate(root); }
        }
    }
}
