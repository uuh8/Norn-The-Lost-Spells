using System.Reflection;
using Game.Core;
using Game.Combat;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.ElementField.Tests
{
    /// <summary>活动域测试只初始化空间Store；真实Solver/数量守恒由既有真实P7初始化Fixture覆盖。</summary>
    public sealed class WorldAuthorityActivityTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject _host;
        private ElementWorldRuntime _world;
        private ElementWorldProfile _profile;
        private ElementWorldStore _store;
        [SetUp] public void SetUp()
        {
            _host = new GameObject("Authority activity fixture"); _host.SetActive(false);
            _world = _host.AddComponent<ElementWorldRuntime>();
            _profile = ScriptableObject.CreateInstance<ElementWorldProfile>();
            JsonUtility.FromJsonOverwrite("{\"_chunkSize\":2,\"_maximumResidentChunks\":64,\"_enableWorldActivityExpansion\":false}", _profile);
            Set("_profile", _profile);
            // CellSize在已初始化Runtime中来自_settings，不直接读Profile；隔离Fixture也必须安装真实设置，
            // 否则零尺寸会把合法活动域全部拒绝，并让“容量失败”断言产生无效的假阳性。
            var reactions = AssetDatabase.LoadAssetAtPath<ElementReactionProfile>("Assets/_Project/ScriptableObjects/Combat/Status/ElementReactionProfile_Default.asset");
            Assert.That(reactions, Is.Not.Null);
            typeof(ElementWorldProfile).GetField("_reactionProfile", Private).SetValue(_profile, reactions);
            Set("_settings", _profile.CreateSimulationSettings());
            _store = new ElementWorldStore(2, 64); Set("_store", _store);
            _world.ConfigureExecutionMode(WorldExecutionMode.Authority);
            typeof(ElementWorldRuntime).GetProperty("IsInitialized").SetValue(_world, true);
        }
        [TearDown] public void TearDown() { Object.DestroyImmediate(_host); Object.DestroyImmediate(_profile); }
        private void Set(string name, object value) => typeof(ElementWorldRuntime).GetField(name, Private).SetValue(_world, value);
        private object Call(string name, params object[] args)
        {
            var method = typeof(ElementWorldRuntime).GetMethod(name);
            Assert.That(method, Is.Not.Null, "缺少认证Actor活动入口 " + name);
            return method.Invoke(_world, args);
        }
        private void Refresh() => typeof(ElementWorldRuntime).GetMethod("RefreshWorldActivity", Private).Invoke(_world, null);

        [Test] public void FourAuthenticatedActorsRemainActiveWithExpansionDisabledInSinglePlayerProfile()
        {
            for (int i = 0; i < 4; i++) Assert.That(Call("TrySetActor", i, (ulong)(i + 1), Vector3.right * (i * 100 - 200)), Is.True);
            Refresh();
            Assert.That(_world.ActivityExpansionEnabled, Is.True);
            for (int i = 0; i < 4; i++) Assert.That(_world.ActivityRegions.Contains(Vector3.right * (i * 100 - 200)), Is.True);
            Assert.That(_world.ActivityRegions.Contains(Vector3.left * 150), Is.False, "Actor并集不能被Envelope填成整条走廊。");
        }
        [Test] public void InvalidActorUpdatePreservesPreviouslyAuthenticatedPosition()
        {
            Assert.That(Call("TrySetActor", 0, 1UL, Vector3.left * 100), Is.True);
            Assert.That(Call("TrySetActor", 0, 1UL, new Vector3(float.NaN, 0, 0)), Is.False);
            Assert.That(Call("TrySetActor", 4, 2UL, Vector3.zero), Is.False);
            Refresh(); Assert.That(_world.ActivityRegions.Contains(Vector3.left * 100), Is.True);
        }
        [Test] public void ClearingDisconnectedActorRemovesOnlyItsRegion()
        {
            Call("TrySetActor", 0, 1UL, Vector3.left * 100); Call("TrySetActor", 1, 2UL, Vector3.right * 100);
            Call("ClearActor", 0); Refresh();
            Assert.That(_world.ActivityRegions.Contains(Vector3.left * 100), Is.False);
            Assert.That(_world.ActivityRegions.Contains(Vector3.right * 100), Is.True);
        }
        [Test] public void AuthorityCpuReactionDomainDoesNotDependOnClientInterest()
        {
            var domain = typeof(ElementWorldRuntime).GetMethod("LiquidActivityDomain", Private).Invoke(_world, null);
            Assert.That(domain, Is.Null, "Authority必须推进有界resident/pending，不能按发送Interest裁剪反应。");
        }
        [Test] public void ManyDistantFireChunksRemainScheduledAfterLastActorLeaves()
        {
            Call("TrySetActor", 0, 1UL, Vector3.zero); Call("ClearActor", 0);
            for (int i = 0; i < 40; i++)
            {
                Assert.That(_store.TryGetOrCreateChunk(new ElementChunkKey(i * 100 + 1000, 0, 0), out var chunk), Is.True);
                chunk.SetCell(Vector3Int.zero, new ElementCell(MaterialId.Fire, 255));
            }
            Set("_activeChunks", new ElementWorldChunk[64]); Set("_reclaimKeys", new ElementChunkKey[64]);
            Set("_worldTick", 100L);
            Refresh(); typeof(ElementWorldRuntime).GetMethod("RebuildActivitySets", Private).Invoke(_world, null);
            Assert.That(_world.ActiveChunkCount, Is.EqualTo(40));
            Assert.That(_world.IsSimulationFaulted, Is.False, "Fire规则推进不应消耗有限的Exposure区域名额。");
        }
        [Test] public void EveryProjectileBeyondOldSixSourceBudgetIsRetained()
        {
            var sources = (ElementWorldActivitySources)typeof(ElementWorldRuntime).GetField("_activitySources", Private).GetValue(_world);
            var projectiles = new GameObject[12];
            try
            {
                for (int i = 0; i < projectiles.Length; i++)
                {
                    projectiles[i] = new GameObject("Projectile source"); projectiles[i].transform.position = Vector3.right * (i * 100 + 1000);
                    sources.Apply(new WorldActivitySourceEvent { Source = projectiles[i].transform, Kind = WorldActivitySourceKind.Projectile, Enabled = true });
                }
                Refresh();
                for (int i = 0; i < projectiles.Length; i++) Assert.That(_world.ActivityRegions.Contains(projectiles[i].transform.position), Is.True);
                Assert.That(_world.IsSimulationFaulted, Is.False);
            }
            finally { foreach (var projectile in projectiles) if (projectile != null) Object.DestroyImmediate(projectile); }
        }
        [Test] public void RegionCapacityFailureIsExplicitInsteadOfSilentlyDroppingLastProjectile()
        {
            var sources = (ElementWorldActivitySources)typeof(ElementWorldRuntime).GetField("_activitySources", Private).GetValue(_world);
            var projectiles = new GameObject[33];
            try
            {
                for (int i = 0; i < projectiles.Length; i++)
                {
                    projectiles[i] = new GameObject("Over capacity source"); projectiles[i].transform.position = Vector3.right * (i * 100 + 1000);
                    sources.Apply(new WorldActivitySourceEvent { Source = projectiles[i].transform, Kind = WorldActivitySourceKind.Projectile, Enabled = true });
                }
                Refresh(); Assert.That(_world.IsSimulationFaulted, Is.True);
            }
            finally { foreach (var projectile in projectiles) if (projectile != null) Object.DestroyImmediate(projectile); }
        }
        [Test] public void ExistingSinglePlayerProfileKeepsItsOriginalActivitySwitchAndPrimaryDomain()
        {
            // 切换仅发生在未初始化的测试对象；正式运行期仍禁止变更所有权。
            typeof(ElementWorldRuntime).GetProperty("IsInitialized").SetValue(_world, false);
            Set("_initializationStarted", false); _world.ConfigureExecutionMode(WorldExecutionMode.SinglePlayer);
            Assert.That(_world.ActivityExpansionEnabled, Is.False);
            Assert.That(typeof(ElementWorldRuntime).GetMethod("LiquidActivityDomain", Private).Invoke(_world, null), Is.Not.Null);
        }
    }
}
