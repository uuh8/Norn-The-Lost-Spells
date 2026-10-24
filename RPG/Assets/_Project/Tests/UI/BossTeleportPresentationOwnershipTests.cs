using System.Reflection;
using Game.Combat;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.UI.Tests
{
    /// <summary>共享 Boss Prefab 增加 Presenter 后，原 P8 Scene 自己的 Presenter 仍优先，防止生成两份 Portal。</summary>
    public sealed class BossTeleportPresentationOwnershipTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject _root;

        [TearDown] public void Cleanup() { if (_root != null) Object.DestroyImmediate(_root); }

        [Test]
        public void PreferredPrefabPresenterYieldsToInactiveScenePresenterForTheSameHealth()
        {
            var own = MakePresenter(out var health);
            var scene = Child("OriginalScenePresenter").AddComponent<BossTeleportVfxController>();
            Set(scene, "_bossHealth", health);
            Set(own, "_preferExistingPresenter", true);
            Awake(own);
            Assert.That(own.enabled, Is.False, "即使原 Scene Presenter 尚未 Awake/处于 inactive，也必须保持原配置优先。");
        }

        [Test]
        public void UnrelatedBossPresenterDoesNotSuppressOnlineBossPresentation()
        {
            var own = MakePresenter(out _);
            var other = Child("OtherBoss").AddComponent<HealthComponent>();
            var scene = Child("OtherPresenter").AddComponent<BossTeleportVfxController>();
            Set(scene, "_bossHealth", other);
            Set(own, "_preferExistingPresenter", true);
            Awake(own);
            Assert.That(own.enabled, Is.True);
        }

        [Test]
        public void ExistingScenePresenterDoesNotYieldToPreferredPrefabPresenter()
        {
            var own = MakePresenter(out var health);
            var prefab = Child("PreferredPrefabPresenter").AddComponent<BossTeleportVfxController>();
            Set(prefab, "_bossHealth", health); Set(prefab, "_preferExistingPresenter", true);
            Awake(own);
            Assert.That(own.enabled, Is.True, "旧场景序列化没有该标记，必须保持原来的表现 Owner。");
        }

        private BossTeleportVfxController MakePresenter(out HealthComponent health)
        {
            _root = new GameObject("BossPresentationOwnership"); _root.SetActive(false);
            health = _root.AddComponent<HealthComponent>();
            var own = Child("Owner").AddComponent<BossTeleportVfxController>(); Set(own, "_bossHealth", health); return own;
        }
        private GameObject Child(string name)
        { var child = new GameObject(name); child.transform.SetParent(_root.transform, false); return child; }
        private static void Set(object owner, string field, object value)
        {
            var member = owner.GetType().GetField(field, Private);
            Assert.That(member, Is.Not.Null, "缺少新旧 Presenter 所有权门禁字段。"); member.SetValue(owner, value);
        }
        private static void Awake(BossTeleportVfxController presenter) => typeof(BossTeleportVfxController).GetMethod("Awake", Private).Invoke(presenter, null);
    }
}
