using System;
using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Run.Tests
{
    /// <summary>首次账号Hub的站点只读身份和原几何门；不加载旧Profile、不创建假Run或真实HTTP。</summary>
    public sealed class AccountHubInteractionTests
    {
        private const int Owner = 63013;
        private const string Player = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private Scene _scene;
        private GameObject _sessionRoot, _hubRoot, _actorRoot, _areaRoot;
        private SinglePlayerInventorySession _session;
        private HubInteractionController _hub;
        private HubAreaVolume _area;
        private HubDefinition _definition;
        private HubInteractionResultEvent _result;
        private int _results;

        [SetUp] public void CreateOnlyRealAccountMetadataAndOriginalStationGeometry()
        {
            ResetScope(); _results = 0; _result = default;
            _scene = EditorSceneManager.NewPreviewScene(); _scene.name = "AccountHubInteraction_" + Guid.NewGuid().ToString("N");
            Assert.That(OnlineAccountInventoryHubScope.TryPublish(Owner, Profile(), out var error), Is.True, error);
            Assert.That(OnlineAccountInventoryHubScope.TryArmHubEntry(Owner, _scene.name, out error), Is.True, error);
            _sessionRoot = Root("Inactive account inventory"); _sessionRoot.SetActive(false);
            _session = _sessionRoot.AddComponent<SinglePlayerInventorySession>();
            _actorRoot = Root("Hub actor");
            _areaRoot = Root("Original warehouse volume");
            var box = _areaRoot.AddComponent<BoxCollider>(); box.isTrigger = true; box.size = new Vector3(4, 4, 4);
            _area = _areaRoot.AddComponent<HubAreaVolume>();
            _definition = ScriptableObject.CreateInstance<HubDefinition>();
            _definition.InteractionRadius = 10;
            _definition.Stations = new[]
            {
                new HubDefinition.Station { Kind = HubStationKind.Warehouse, Position = Vector3.zero, DisplayName = "仓库" },
                new HubDefinition.Station { Kind = HubStationKind.Shop, Position = Vector3.right * 20, DisplayName = "商店" },
                new HubDefinition.Station { Kind = HubStationKind.SpellResearch, Position = Vector3.right * 40, DisplayName = "研究" },
                new HubDefinition.Station { Kind = HubStationKind.Expedition, Position = Vector3.right * 60, DisplayName = "副本入口" }
            };
            _hubRoot = Root("Original Hub interaction");
            _hub = _hubRoot.AddComponent<HubInteractionController>();
            Invoke(_hub, "OnDisable");
            Set(_hub, "_session", _session); Set(_hub, "_definition", _definition);
            Set(_hub, "_player", _actorRoot.transform); Set(_hub, "_warehouseArea", _area);
            Invoke(_hub, "Awake"); Invoke(_hub, "OnEnable");
            EventBus<HubInteractionResultEvent>.Subscribe(Capture);
        }

        [TearDown] public void CloseOnlyFixtureSceneAndSubscriptions()
        {
            EventBus<HubInteractionResultEvent>.Unsubscribe(Capture);
            if (_hub != null) Invoke(_hub, "OnDisable");
            if (_scene.IsValid()) EditorSceneManager.ClosePreviewScene(_scene);
            if (_definition != null) UnityEngine.Object.DestroyImmediate(_definition);
            ResetScope();
        }

        [Test] public void OriginalWarehouseFUsesConfirmedAccountIdentityWithoutAnyLocalProfile()
        {
            Assert.That(_session.AccountInventoryReadOnly, Is.True);
            Assert.That(_session.ReadOnly, Is.Null); Assert.That(_session.ProfileStoragePath, Is.Null);
            Assert.That(_hub.TryGetStation(out var station), Is.True,
                "首次账号Hub没有旧_read也必须能查询原仓库站点，不能为F伪造本地Profile。");
            Assert.That(station.Kind, Is.EqualTo(HubStationKind.Warehouse));
            _hub.Request(HubInteractionKind.Warehouse);
            Assert.That(_results, Is.EqualTo(1)); Assert.That(_result.Success, Is.True);
            Assert.That(_result.InventoryId, Is.EqualTo("account." + Player));
            Assert.That(_session.CanAccessWarehouse, Is.False, "原本地仓库写门不得随账号交互解锁。");
            Assert.That(_session.Save().Status, Is.EqualTo(ProfilePersistenceStatus.WriteBlocked));
        }

        [Test] public void AccountWarehouseKeepsOriginalVolumeAndNeverFallsBackToTheLargerPointRadius()
        {
            Assert.That(CanAccessAccountWarehouse(), Is.True);
            _actorRoot.transform.position = Vector3.right * 3;
            Assert.That(CanAccessAccountWarehouse(), Is.False, "已配4m区域外不能退回10m点半径。");
            _actorRoot.transform.position = Vector3.zero; _area.enabled = false;
            Assert.That(CanAccessAccountWarehouse(), Is.False, "停用实际区域仍不可远程开仓库。");
            _area.enabled = true; _area.Volume.enabled = false;
            Assert.That(CanAccessAccountWarehouse(), Is.False);
            Assert.That(_session.ReadOnly, Is.Null); Assert.That(_session.CanAccessWarehouse, Is.False);
        }

        [Test] public void AccountLeaseActiveRunOrMissingIdentityCannotOpenTheOldStation()
        {
            Assert.That(_hub.TryGetStation(out _), Is.True);
            var profile = Profile(); profile.AssetsLocked = true;
            Assert.That(OnlineAccountInventoryHubScope.TryPublish(Owner, profile, out var error), Is.True, error);
            Assert.That(_hub.TryGetStation(out _), Is.False); Assert.That(CanAccessAccountWarehouse(), Is.False);
            profile.AssetsLocked = false; profile.ActiveRunId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            Assert.That(OnlineAccountInventoryHubScope.TryPublish(Owner, profile, out error), Is.True, error);
            Assert.That(_hub.TryGetStation(out _), Is.False); Assert.That(CanAccessAccountWarehouse(), Is.False);
            OnlineAccountInventoryHubScope.Clear(Owner);
            Assert.That(_hub.TryGetStation(out _), Is.False); Assert.That(CanAccessAccountWarehouse(), Is.False);
            Assert.That(_session.AccountInventoryReadOnly, Is.True);
            Assert.That(_session.ReadOnly, Is.Null); Assert.That(_session.Save().Status, Is.EqualTo(ProfilePersistenceStatus.WriteBlocked));
        }

        [Test] public void AccountHubExplicitlyRejectsUnwiredLocalShopAndResearchActions()
        {
            _actorRoot.transform.position = Vector3.right * 20;
            Assert.That(_hub.TryGetStation(out _), Is.False);
            _hub.Request(HubInteractionKind.Shop);
            Assert.That(_results, Is.EqualTo(1)); Assert.That(_result.Success, Is.False);
            _actorRoot.transform.position = Vector3.right * 40;
            Assert.That(_hub.CanResearch, Is.False); _hub.Request(HubInteractionKind.SpellResearch);
            Assert.That(_results, Is.EqualTo(2)); Assert.That(_result.Success, Is.False);
            Assert.That(_session.ReadOnly, Is.Null); Assert.That(_session.ProfileStoragePath, Is.Null);
        }

        [Test] public void KnownServerLeaseRevokesStationMetadataWithoutReplacingFactsOrProvidingAnUnlock()
        {
            Assert.That(_session.AccountInventoryReadOnly, Is.True);
            Assert.That(OnlineAccountInventoryHubScope.TryCopyForScene((int)_scene.handle, out var before), Is.True);
            const string run = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            Assert.That(TryMarkAssetsLocked(Owner + 1, Player, run), Is.False);
            Assert.That(TryMarkAssetsLocked(Owner, "cccccccccccccccccccccccccccccccc", run), Is.False);
            Assert.That(TryMarkAssetsLocked(Owner, Player, run), Is.True);
            Assert.That(OnlineAccountInventoryHubScope.TryCopyForScene((int)_scene.handle, out var locked), Is.True);
            Assert.That(locked.AssetsLocked, Is.True); Assert.That(locked.ActiveRunId, Is.EqualTo(run));
            Assert.That(locked.Gold, Is.EqualTo(before.Gold)); Assert.That(locked.Version, Is.EqualTo(before.Version));
            Assert.That(locked.Items, Has.Length.EqualTo(before.Items.Length));
            Assert.That(_hub.TryGetStation(out _), Is.False); Assert.That(CanAccessAccountWarehouse(), Is.False);
            Assert.That(TryMarkAssetsLocked(Owner, Player, ""), Is.True);
            Assert.That(OnlineAccountInventoryHubScope.TryCopyForScene((int)_scene.handle, out locked), Is.True);
            Assert.That(locked.AssetsLocked, Is.True, "metadata接口只撤销访问，空RunId不能成为客户端解锁。");
            Assert.That(OnlineAccountInventoryHubScope.TryPublish(Owner, Profile(), out var error), Is.True, error);
            Assert.That(_hub.TryGetStation(out _), Is.True, "完整重新确认的Profile才可恢复原站点资格。");
        }

        private static bool TryMarkAssetsLocked(int owner, string player, string run)
        {
            var method = typeof(OnlineAccountInventoryHubScope).GetMethod("TryMarkAssetsLocked", BindingFlags.Static | BindingFlags.Public);
            Assert.That(method, Is.Not.Null, "带锁Profile不覆盖旧Items时，仍必须立即撤销缓存的Hub访问资格。");
            return (bool)method.Invoke(null, new object[] { owner, player, run });
        }

        private bool CanAccessAccountWarehouse()
        {
            var property = typeof(SinglePlayerInventorySession).GetProperty("CanAccessAccountWarehouse");
            Assert.That(property, Is.Not.Null, "账号仓库需要独立保原空间规则的只读资格，不能放宽旧本地写门。");
            return (bool)property.GetValue(_session);
        }
        private GameObject Root(string name)
        { var root = new GameObject(name); SceneManager.MoveGameObjectToScene(root, _scene); return root; }
        private void Capture(HubInteractionResultEvent result) { _results++; _result = result; }
        private static void Set(object value, string name, object field)
            => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(value, field);
        private static void Invoke(object value, string name)
            => value.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(value, null);
        private static OnlineAccountInventorySnapshot Profile() => new OnlineAccountInventorySnapshot
        {
            SchemaVersion = OnlineAccountInventorySnapshot.CurrentSchemaVersion, PlayerId = Player,
            Version = 7, Gold = 10000, ContentManifestHash = new string('a', 64), Items = Array.Empty<OnlineOwnedItemSnapshot>()
        };
        private static void ResetScope() => typeof(OnlineAccountInventoryHubScope)
            .GetMethod("Reset", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
    }
}
