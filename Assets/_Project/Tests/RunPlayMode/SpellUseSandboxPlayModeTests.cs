#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using Game.Core;
using Game.Character;
using Game.Combat;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Game.Run.Tests
{
    /// <summary>直接进入次数测试 Scene，注入隔离存档目录；不通关 P7，不修改模板资产。</summary>
    public sealed class SpellUseSandboxPlayModeTests
    {
        private const string ScenePath = "Assets/_Project/Scenes/Tests/SpellUseSandbox.unity";
        private const string AssetsRoot = "Assets/_Project/Tests/Fixtures/Run/SinglePlayerSpells/";
        private Scene _scene;
        private SinglePlayerInventorySession _session;
        private OwnedWandCastAdapter _adapter;
        private SpellCaster _caster;
        private SpellUseHud _hud;
        private SinglePlayerInventoryController _ui;
        private string _directory;
        private float _time;
        private CursorLockMode _lock;
        private bool _cursor;
        private EditorWindow _previous;
        [UnitySetUp] public IEnumerator Setup()
        {
            _time = Time.timeScale; _lock = Cursor.lockState; _cursor = Cursor.visible; Time.timeScale = 1;
            _directory = Path.GetFullPath(Path.Combine("Temp/SinglePlayer/SpellTests", Guid.NewGuid().ToString("N")));
            SceneManager.sceneLoaded += Loaded;
            _scene = EditorSceneManager.LoadSceneInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            yield return null; yield return null; yield return null; SceneManager.sceneLoaded -= Loaded;
            Assert.That(_session.SpellUsesReadOnly.RaidId, Is.Not.Null);
        }
        private void Loaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.path != ScenePath) return; _scene = scene; _session = Find<SinglePlayerInventorySession>();
            _adapter = Find<OwnedWandCastAdapter>(); _caster = Find<SpellCaster>(); _hud = Find<SpellUseHud>(); _ui = Find<SinglePlayerInventoryController>();
            Assert.That(_session.ConfigureAndInitialize(AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/_Project/Tests/Fixtures/Run/SinglePlayerSpells/Catalog.asset"),
                AssetDatabase.LoadAssetAtPath<SinglePlayerStartingInventory>("Assets/_Project/Tests/Fixtures/Run/SinglePlayerSpells/StartingInventory.asset"), _directory), Is.True);
        }
        private T Find<T>() where T : Component
        { foreach (var root in _scene.GetRootGameObjects()) { var value = root.GetComponentInChildren<T>(true); if (value != null) return value; } return null; }
        private InventoryItemSnapshot Item(string content, ItemLocationKind location)
        {
            var read = _session.ReadOnly; var items = new InventoryItemSnapshot[read.Count]; int count = read.CopyItemsTo(items);
            for (int i = 0; i < count; i++) if (items[i].ContentId == "spelltest." + content && items[i].Location.Kind == location) return items[i];
            Assert.Fail("未找到具体实例 " + content); return default;
        }
        private void Move(string id, InventoryDestination destination, string owner = null, int slot = 0)
        {
            var read = _session.ReadOnly; EventBus<InventoryCommandEvent>.Publish(new InventoryCommandEvent { InventoryId = read.InventoryId,
                Kind = InventoryCommandKind.Place, ItemId = id, Destination = destination, OwnerId = owner, SlotIndex = slot, ExpectedVersion = read.Version });
        }
        private int Cast(int slot)
        { _adapter.Select(slot); Assert.That(_adapter.TryCapture(out var program, out var context), Is.True);
            return _caster.CastOwnedProgram(program, context, new Vector3(0, 4, 0), new Vector3(0, 4, 30), 1, 888, Find<WizardController>().CharacterController); }
        [UnityTest] public IEnumerator LastQualificationAtZeroRemainsUsableAndHudExplainsIt()
        {
            var modifier = Item("modifier", ItemLocationKind.WandSlot);
            Assert.That(modifier.RemainingCharges, Is.Zero); _session.SpellUsesReadOnly.TryGetState(modifier.InstanceId, out var use);
            Assert.That(use.Eligible, Is.True); Assert.That(_hud.DisplayText, Does.Contain("未来入局 0 次 · 本局可用"));
            Assert.That(Cast(0), Is.EqualTo(1)); var fire = Item("fire", ItemLocationKind.WandSlot);
            Assert.That(fire.RemainingCharges, Is.EqualTo(2)); Assert.That(_session.Save().Success, Is.False); yield return null;
        }
        [UnityTest] public IEnumerator MulticastAcquiresOnceAcrossReinstallAndIndependentEmitsEachLoseOne()
        {
            var spare = Item("multicast", ItemLocationKind.Warehouse); _session.WandsReadOnly.TryGetSlot(0, out var wand);
            Move(spare.InstanceId, InventoryDestination.WandSlot, wand.InstanceId, 2);
            Assert.That(_session.ReadOnly.TryGetView(spare.InstanceId, out var installed), Is.True); Assert.That(installed.RemainingCharges, Is.EqualTo(1));
            Move(spare.InstanceId, InventoryDestination.Warehouse); Move(spare.InstanceId, InventoryDestination.WandSlot, wand.InstanceId, 2);
            Assert.That(_session.ReadOnly.TryGetView(spare.InstanceId, out installed), Is.True); Assert.That(installed.RemainingCharges, Is.EqualTo(1));
            var fire = Item("fire", ItemLocationKind.Warehouse); Move(fire.InstanceId, InventoryDestination.WandSlot, wand.InstanceId, 3);
            Assert.That(Cast(0), Is.EqualTo(2)); _session.ReadOnly.TryGetView(fire.InstanceId, out var used); Assert.That(used.RemainingCharges, Is.EqualTo(2));
            _session.ReadOnly.TryGetView(spare.InstanceId, out installed); Assert.That(installed.RemainingCharges, Is.EqualTo(1)); yield return null;
        }
        [UnityTest] public IEnumerator EndClearsQualificationAndNextRaidCannotRestoreExhaustedEntry()
        {
            _hud.End(); Assert.That(_session.SpellUsesReadOnly.RaidId, Is.Null);
            _hud.Begin(); Assert.That(_session.SpellUsesReadOnly.RaidId, Is.Null);
            Assert.That(Cast(0), Is.Zero); Assert.That(_adapter.LastFailure, Is.EqualTo(OwnedWandFailureReason.MissingEligibility));
            Assert.That(Cast(1), Is.EqualTo(1)); yield return null;
        }
        [UnityTest] public IEnumerator SceneShowsUsesAndQualificationInRealGameView()
        {
            Assert.That(_hud.DisplayText, Does.Contain("剩余 2 次")); Assert.That(_hud.DisplayText, Does.Contain("无限使用"));
            _previous = EditorWindow.focusedWindow; EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor")).Focus();
            yield return null; yield return new WaitForSecondsRealtime(.3f); ScreenCapture.CaptureScreenshot("Temp/SinglePlayer/spell-use-sandbox.png");
            yield return null; yield return new WaitForSecondsRealtime(.3f); Assert.That(File.Exists("Temp/SinglePlayer/spell-use-sandbox.png"), Is.True);
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            SceneManager.sceneLoaded -= Loaded; if (_ui != null) _ui.Close();
            if (_scene.IsValid() && _scene.isLoaded) yield return SceneManager.UnloadSceneAsync(_scene);
            foreach (var projectile in Object.FindObjectsByType<ProjectileBase>(FindObjectsSortMode.None)) Object.Destroy(projectile.gameObject);
            foreach (var audio in Object.FindObjectsByType<AudioSource>(FindObjectsSortMode.None)) if (audio.name == "One shot audio") Object.Destroy(audio.gameObject);
            yield return null; Time.timeScale = _time; Cursor.lockState = _lock; Cursor.visible = _cursor; if (_previous != null) _previous.Focus();
            string root = Path.GetFullPath("Temp/SinglePlayer/SpellTests") + Path.DirectorySeparatorChar;
            if (_directory.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
}
#endif
