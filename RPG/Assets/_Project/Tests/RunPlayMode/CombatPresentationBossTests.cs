using System.Reflection;
using System.Collections;
using Game.Combat;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Game.Run.Tests
{
    /// <summary>持久 HUD 切换只读来源时，不能让已初始化的 Boss 血条丢掉事件身份。</summary>
    public sealed class CombatPresentationBossTests
    {
        private Scene _scene;
        private CombatPresentationHost _host;
        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (_host != null) Object.Destroy(_host.gameObject);
            if (_scene.IsValid() && _scene.isLoaded) yield return SceneManager.UnloadSceneAsync(_scene);
            yield return null;
        }

        [UnityTest] public IEnumerator SameWorldInventoryReplacementKeepsLiveBossInitialization()
        {
            _scene = SceneManager.CreateScene("BossHudSourceReplacement");
            var actor = new GameObject("Actor"); actor.SetActive(false); SceneManager.MoveGameObjectToScene(actor, _scene);
            actor.AddComponent<HealthComponent>();
            var owner = new GameObject("Owner"); SceneManager.MoveGameObjectToScene(owner, _scene);
            var nextSource = new GameObject("ReadOnlySource"); nextSource.SetActive(false); SceneManager.MoveGameObjectToScene(nextSource, _scene);
            var source = nextSource.AddComponent<SinglePlayerInventorySession>();
            var context = new CombatPresentationContext { Actor = actor, WorldScene = _scene };
            Assert.That(CombatPresentationHost.TryAcquire(owner, context, out var lease), Is.True);
            _host = CombatPresentationHost.Active;
            yield return null;
            var boss = _host.HudRoot.GetComponentInChildren<BossHealthBarController>(true);
            // 必须在 PlayMode 经真实启停回调验证；EditMode SetActive 不会提供等价的 OnDisable 证据。
            EventBus<BossHudInitializedEvent>.Publish(new BossHudInitializedEvent
            { WorldSceneHandle = _scene.handle, BossId = 701, DisplayName = "Current Boss", CurrentHp = 800, MaxHp = 1000, CurrentPhase = 1 });
            context.InventorySource = source;
            Assert.That(CombatPresentationHost.TryUpdate(lease, context), Is.True);
            var id = typeof(BossHealthBarController).GetField("_bossId", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That((int)id.GetValue(boss), Is.EqualTo(701),
                "同一世界只换库存来源不应触发 Boss OnDisable；后续 State 只携带状态，无法重新初始化身份。");
            EventBus<BossHudStateChangedEvent>.Publish(new BossHudStateChangedEvent
            { BossId = 701, Active = true, CurrentHp = 700, CurrentPhase = 2 });
            var displayed = typeof(BossHealthBarController).GetField("_displayedPhase", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That((byte)displayed.GetValue(boss), Is.EqualTo(2));
            CombatPresentationHost.Release(lease);
        }
    }
}
