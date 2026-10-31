#if UNITY_EDITOR
using System.Collections;
using System.Reflection;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Game.Run.Tests
{
    /// <summary>
    /// 验证实际导出的原Tab在PlayMode中的输入/Scene寿命。使用内存Authority导出当前值，
    /// 不读取单机存档，不建立账号或假装网络验收；真实DS到Client的Wire由独立Native验收覆盖。
    /// </summary>
    public sealed class OnlineSharedInventoryPlayModeTests
    {
        private Scene _scene;
        private CombatPresentationScope _scope;
        private CombatPresentationHost _host;
        private OnlineOwnedWorkspace _authority;
        private OnlineInventoryPresentationSource _source;
        private InputTestFixture _input;
        private Keyboard _keyboard;
        private bool _background;
        private float _scale;

        [UnityTest] public IEnumerator OriginalTabUsesOnlineSourceWithoutPausingAndIsReleasedWithItsScene()
        {
            _background = Application.runInBackground; Application.runInBackground = true; _scale = Time.timeScale;
            _input = new InputTestFixture(); _input.Setup(); _keyboard = InputSystem.AddDevice<Keyboard>();
            var definition = Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition");
            Assert.That(definition.Catalog.TryCreateIndex(out var catalog, out var error), Is.True, error);
            const string wand = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", card = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            var assets = new OnlineOwnedAssetSnapshot { SchemaVersion = 1, ActorId = 1, RunId = "tab-run", PlayerId = "tab-player", InventoryId = "tab-inventory",
                InventoryVersion = 1, GameplayConfigHash = definition.GameplayConfigHash, SelectedWandInstanceId = wand,
                Items = new[] {
                    new OnlineOwnedItemSnapshot { InstanceId = wand, ContentId = "wandtest.wand.a", Revision = 1, LocationKind = 3, UseTemplateWandStats = true },
                    new OnlineOwnedItemSnapshot { InstanceId = card, ContentId = "wandtest.fire", Revision = 1, LocationKind = 2, ParentInstanceId = wand, RemainingCharges = 30 } } };
            Assert.That(OnlineOwnedWorkspaceFactory.TryCreate(catalog, assets, definition.GameplayConfigHash, out _authority, out error), Is.True, error);
            Assert.That(_authority.TryCapturePresentation(7, 1, 10, out var snapshot), Is.True);
            _scene = SceneManager.CreateScene("OnlineOriginalTabRuntime");
            var expected = new OnlineInventoryPresentationIdentity(assets.RunId, assets.PlayerId, 1, assets.InventoryId, definition.GameplayConfigHash, 7, _scene.name);
            Assert.That(OnlineInventoryPresentationSource.TryCreate(catalog, expected, snapshot, out _source, out error), Is.True, error);
            var actor = new GameObject("LocalActor"); actor.SetActive(false); SceneManager.MoveGameObjectToScene(actor, _scene); actor.AddComponent<HealthComponent>();
            var root = new GameObject("CombatScope"); root.SetActive(false); SceneManager.MoveGameObjectToScene(root, _scene);
            _scope = root.AddComponent<CombatPresentationScope>();
            typeof(CombatPresentationScope).GetField("_online", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_scope, true);
            root.SetActive(true); LocalActorPresentation.Bind(70177, actor, true); OnlineInventoryPresentation.Bind(70177, actor, _source);
            yield return null; yield return null; yield return null;
            var inventory = root.GetComponentInChildren<SinglePlayerInventoryController>(true);
            Assert.That(inventory, Is.Not.Null); Assert.That(inventory.Session, Is.Null); Assert.That(inventory.PresentationSource, Is.SameAs(_source));
            _host = CombatPresentationHost.Active; Assert.That(_host.InventorySource, Is.SameAs(_source));
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.Tab)); InputSystem.Update(); yield return null;
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState()); InputSystem.Update(); yield return null;
            Assert.That(inventory.IsOpen, Is.True); Assert.That(Time.timeScale, Is.EqualTo(_scale));
            Assert.That(_source.ReadOnly.Count, Is.EqualTo(2));
            // 晚到的相同Source重放不应关闭当前Tab，Source内部刷新也不会创建第二个Controller。
            OnlineInventoryPresentation.Bind(70177, actor, _source); yield return null;
            Assert.That(inventory.IsOpen, Is.True); Assert.That(root.GetComponentsInChildren<SinglePlayerInventoryController>(true).Length, Is.EqualTo(1));
            _scope.Clear(); yield return null;
            Assert.That(inventory == null, Is.True); Assert.That(_host.InventorySource, Is.Null);
            Assert.That(_host.gameObject.scene.name, Is.EqualTo("DontDestroyOnLoad")); Assert.That(Time.timeScale, Is.EqualTo(_scale));
        }

        [UnityTearDown] public IEnumerator ReleaseOnlyThisTestWorld()
        {
            if (_scope != null) _scope.Clear();
            LocalActorPresentation.Clear(70177); OnlineInventoryPresentation.Clear(70177);
            if (_scene.IsValid() && _scene.isLoaded) yield return SceneManager.UnloadSceneAsync(_scene);
            if (_host != null) Object.Destroy(_host.gameObject);
            _source?.Dispose(); _authority?.Dispose(); yield return null;
            _input?.TearDown();
            Application.runInBackground = _background;
            Assert.That(Time.timeScale, Is.EqualTo(_scale));
        }
    }
}
#endif
