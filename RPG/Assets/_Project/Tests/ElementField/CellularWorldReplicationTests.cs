using System.Reflection;
using System.Collections.Generic;
using Game.Combat;
using Game.Core;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.ElementField.Tests
{
    /// <summary>从现有 CommitOwner 写出的正式 W2 数据经 Store 安装，再读取与角色/Renderer 相同的 Runtime。</summary>
    public sealed class CellularWorldReplicationTests
    {
        private const ulong Epoch = 7;
        private const uint Capacity = 64;
        private static readonly WorldChunkKey Key = new WorldChunkKey(WorldMaterialDomain.Cellular, 0, 0, 0);
        private static readonly Vector3Int Rock = Vector3Int.zero, Lake = Vector3Int.right, Cold = 2 * Vector3Int.right;
        private GameObject _host;
        private ElementWorldRuntime _world;
        private ElementWorldProfile _profile;
        private ElementCellWorldProfile _cells;
        private WorldReplicaStore _store;
        private WorldReplicationCommitOwner _commits;
        private readonly uint[] _words = new uint[512];

        [SetUp]
        public void SetUp()
        {
            _host = new GameObject("Cellular Replica");
            _host.SetActive(false);
            _world = _host.AddComponent<ElementWorldRuntime>();
            _profile = ScriptableObject.CreateInstance<ElementWorldProfile>();
            _cells = ScriptableObject.CreateInstance<ElementCellWorldProfile>();
            Set(_profile, "_cellularWorld", _cells);
            Set(_profile, "_cellSize", 1f); Set(_profile, "_chunkSize", 4);
            Set(_cells, "_capacityPerCell", (int)Capacity);
            Set(_cells, "_terrain", new[] { new ElementTerrainRegion { Cells = new BoundsInt(Rock, Vector3Int.one), Material = MaterialId.Rock } });
            Set(_cells, "_restingLiquids", new[] { new CellularRestingLiquidRegion { Cells = new BoundsInt(Lake, Vector3Int.one), Material = MaterialId.Water } });
            Set(_world, "_profile", _profile);
            _store = new WorldReplicaStore(Epoch, 8, 32, 4, _words.Length);
            _world.ConfigureReplica(_store);
            _commits = new WorldReplicationCommitOwner(Epoch, 8, 32, 4, 8, _words.Length, 100000);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_host); Object.DestroyImmediate(_profile); Object.DestroyImmediate(_cells);
        }

        private static void Set(object target, string field, object value) =>
            target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

        private WorldReplicaBuffer Candidate(int tick, WorldCellularSpan[] spans = null, WorldMaterialCellSample[] rows = null,
            ulong? baseVersion = null)
        {
            var buffer = new WorldReplicaBuffer(32, 4, 100000);
            buffer.Geometries[0] = new WorldGridGeometry(WorldMaterialDomain.Cellular, Vector3.zero, 1f, 4,
                baseVersion ?? _world.CellularBaseVersion, Capacity);
            if (spans != null) spans.CopyTo(buffer.Spans, 0);
            if (rows != null) rows.CopyTo(buffer.Cells, 0);
            buffer.Header = new WorldReplicaHeader(tick, rows?.Length ?? 0, 0, 1, spans?.Length ?? 0);
            return buffer;
        }

        private static WorldCellularSpan Span(WorldCellularSpanKind kind, Vector3Int cell, MaterialId material, uint before, uint after)
            => new WorldCellularSpan(kind, new BoundsInt(cell, Vector3Int.one), (byte)material, before, after);
        private static WorldMaterialCellSample Row(Vector3Int cell, MaterialId material, uint amount) =>
            new WorldMaterialCellSample(WorldMaterialDomain.Cellular, WorldMaterialStorage.Resident, cell, (byte)material, amount);

        private WorldChunkPayloadHeader Freeze(WorldReplicaBuffer buffer, ulong previous = 0)
        {
            Assert.That(_commits.TryCaptureCommit(buffer), Is.True, _commits.LastFailure.ToString());
            WorldChunkPayloadHeader header;
            bool copied = previous == 0 ? _commits.TryCopyChunk(Key, _words, out header)
                : _commits.TryCopyDelta(Key, 1, previous, _words, out header);
            Assert.That(copied, Is.True, _commits.LastFailure.ToString());
            return header;
        }

        private int ActiveSolidBoxes()
        {
            int boxes = 0;
            foreach (var page in _host.GetComponentsInChildren<GeneratedMaterialSolidCollider>(true))
                if (page.gameObject.activeSelf)
                    foreach (var box in page.GetComponents<BoxCollider>()) if (box.enabled) boxes++;
            return boxes;
        }

        [Test]
        public void EmptyAnchorAuthenticatesBaseBeforeExposingLakeAndRock()
        {
            Assert.That(_world.CellularRestingLiquids, Is.Null);
            Assert.That(_world.GetTerrainMaterial(Rock), Is.EqualTo(MaterialId.Empty));
            var header = Freeze(Candidate(1));
            Assert.That(_store.TryInstall(header, _words, header.WordCount), Is.True);
            Assert.That(_world.GetTerrainMaterial(Rock), Is.EqualTo(MaterialId.Rock));
            Assert.That(_world.TryGetQuantity(Lake, MaterialId.Water, out var quantity), Is.True);
            Assert.That(quantity, Is.EqualTo(Capacity));
        }

        [Test]
        public void WrongBaseFingerprintCannotPublishOrExposeAuthoredQuantities()
        {
            ulong other = _world.CellularBaseVersion == 1 ? 2UL : 1UL;
            var header = Freeze(Candidate(1, baseVersion: other));
            Assert.That(_store.TryInstall(header, _words, header.WordCount), Is.False);
            Assert.That(_store.TryGetInstalledChunkHeader(Key, out _, out _), Is.False);
            Assert.That(_world.TryGetQuantity(Lake, MaterialId.Water, out _), Is.False);
        }

        [Test]
        public void CompleteReplacementRestoresTerrainAndAllowsGasAbovePartialRestingQuantity()
        {
            var first = Freeze(Candidate(1, new[] {
                Span(WorldCellularSpanKind.Terrain, Rock, MaterialId.Empty, 0, 0),
                Span(WorldCellularSpanKind.AuthoredLiquid, Lake, MaterialId.Water, Capacity, 0),
                Span(WorldCellularSpanKind.ColdMaterial, Cold, MaterialId.Sand, 0, 32)
            }, new[] { Row(Lake, MaterialId.Oil, 12) }));
            Assert.That(_store.TryInstall(first, _words, first.WordCount), Is.True);
            Assert.That(_world.GetTerrainMaterial(Rock), Is.EqualTo(MaterialId.Empty));
            Assert.That(_world.IsMaterialCellBlocked(Cold), Is.True);
            Assert.That(ActiveSolidBoxes(), Is.EqualTo(1));
            Assert.That(_world.TryGetQuantity(Lake, MaterialId.Water, out _), Is.False);
            Assert.That(_world.TryGetQuantity(Lake, MaterialId.Oil, out var oil), Is.True);
            Assert.That(oil, Is.EqualTo(12u));

            var next = Freeze(Candidate(2, new[] { Span(WorldCellularSpanKind.AuthoredLiquid, Lake, MaterialId.Water, Capacity, 40) },
                new[] { Row(Lake, MaterialId.Steam, 12), Row(Lake, MaterialId.ToxicVapor, 8) }), first.Revision);
            Assert.That(_store.TryInstall(next, _words, next.WordCount), Is.True);
            Assert.That(_world.GetTerrainMaterial(Rock), Is.EqualTo(MaterialId.Rock));
            Assert.That(_world.IsMaterialCellBlocked(Cold), Is.False);
            Assert.That(ActiveSolidBoxes(), Is.Zero);
            Assert.That(_world.TryGetQuantity(Lake, MaterialId.Oil, out _), Is.False);
            Assert.That(_world.TryGetCellularContents(Lake, out var primary, out _, out var steam, out var toxic), Is.True);
            Assert.That(primary, Is.EqualTo(MaterialId.Empty)); Assert.That(steam, Is.EqualTo(12u)); Assert.That(toxic, Is.EqualTo(8u));
            Assert.That(_world.TryGetQuantity(Lake, MaterialId.Water, out var water), Is.True);
            Assert.That(water, Is.EqualTo(40u));
        }

        [Test]
        public void EvictionRestoresBaseAndSameRevisionReentryInstallsTheHolesAgain()
        {
            var header = Freeze(Candidate(1, new[] { Span(WorldCellularSpanKind.Terrain, Rock, MaterialId.Empty, 0, 0),
                Span(WorldCellularSpanKind.AuthoredLiquid, Lake, MaterialId.Water, Capacity, 0) }));
            Assert.That(_store.TryInstall(header, _words, header.WordCount), Is.True);
            Assert.That(_store.TryEvictInterest(Key), Is.True);
            Assert.That(_world.GetTerrainMaterial(Rock), Is.EqualTo(MaterialId.Rock));
            Assert.That(_world.TryGetQuantity(Lake, MaterialId.Water, out var water), Is.True);
            Assert.That(water, Is.EqualTo(Capacity));
            Assert.That(_store.TryInstall(header, _words, header.WordCount), Is.True);
            Assert.That(_world.GetTerrainMaterial(Rock), Is.EqualTo(MaterialId.Empty));
            Assert.That(_world.TryGetQuantity(Lake, MaterialId.Water, out _), Is.False);
        }

        [Test]
        public void ValidWireClaimAboutTheWrongAuthoredLakeLeavesOldChunkAndVersionUntouched()
        {
            var first = Freeze(Candidate(1, new[] { Span(WorldCellularSpanKind.Terrain, Rock, MaterialId.Empty, 0, 0) }));
            Assert.That(_store.TryInstall(first, _words, first.WordCount), Is.True);
            var forged = Freeze(Candidate(2, new[] { Span(WorldCellularSpanKind.AuthoredLiquid, Cold, MaterialId.Water, Capacity, 10) }), first.Revision);
            Assert.That(_store.TryInstall(forged, _words, forged.WordCount), Is.False);
            Assert.That(_world.GetTerrainMaterial(Rock), Is.EqualTo(MaterialId.Empty));
            Assert.That(_store.TryGetInstalledChunkHeader(Key, out var retained, out _), Is.True);
            Assert.That(retained.Revision, Is.EqualTo(first.Revision));
        }

        [Test]
        public void IncrementalCaptureNamesAnEmptiedNegativeChunkAndKeepsUnrelatedHistory()
        {
            // 跳过场景 Actor/AI，用真实元胞 Store 和提交发布边界隔离这项回归。
            var host = new GameObject("Cellular Authority");
            host.SetActive(false);
            try
            {
                var world = host.AddComponent<ElementWorldRuntime>();
                Set(world, "_profile", _profile); Set(world, "_cellDefinition", _cells);
                var reaction=AssetDatabase.LoadAssetAtPath<ElementReactionProfile>(
                    "Assets/_Project/ScriptableObjects/Combat/Status/ElementReactionProfile_Default.asset");
                Assert.That(reaction,Is.Not.Null);
                Set(_profile,"_reactionProfile",reaction);
                Set(world, "_settings", _profile.CreateSimulationSettings());
                Set(world, "_store", new ElementWorldStore(4, 8)); Set(world, "_worldTick", 1L);
                var queue = new ElementWriteQueue(8); Set(world, "_writeQueue", queue);
                var simulation = new ElementCellSimulation((int)Capacity, 64, c => false);
                Set(world, "<CellularSimulation>k__BackingField", simulation);
                Set(world, "<RestingLiquids>k__BackingField", new CellularRestingLiquidStore(_cells.RestingRegions, Capacity, 16));
                Set(world, "_cellularCollisions", new CellularSolidColliders(host.transform, 1, 4, 0));
                Set(world, "_cellularTerrain", world.CreateCellularTerrain(_cells));
                Set(world, "_cellPublications", new List<Vector3Int>());
                Set(world, "_publicationSet", new HashSet<Vector3Int>());
                Set(world, "<IsInitialized>k__BackingField", true);
                var left = new Vector3Int(-9, 0, 0); var far = new Vector3Int(12, 0, 0);
                Assert.That(simulation.TryDeposit(left, MaterialId.Water, 11), Is.True);
                Assert.That(simulation.TryDeposit(far, MaterialId.Water, 19), Is.True);
                world.PublishCellularChanges();
                var source = new WorldReplicationSource(world);
                var buffer = new WorldReplicaBuffer(32, 4, 100000);
                Assert.That(source.TryCapture(buffer, out _), Is.True);
                Assert.That(_commits.TryCaptureCommit(buffer), Is.True);
                var leftKey = WorldChunkPayloadCodec.CellKey(WorldMaterialDomain.Cellular, left, 4);
                var farKey = WorldChunkPayloadCodec.CellKey(WorldMaterialDomain.Cellular, far, 4);
                Assert.That(_commits.TryGetCurrentHeader(farKey, out var before), Is.True);
                Set(world, "_worldTick", 2L);
                Assert.That(source.TryAdvanceUnchangedCapture(buffer), Is.True);
                Assert.That(_commits.TryAdvanceUnchangedCapture(buffer.Header.WorldTick), Is.True);
                Assert.That(simulation.TryRemove(left, MaterialId.Water, 11), Is.True);
                world.PublishCellularChanges();
                Assert.That(source.TryAdvanceUnchangedCapture(buffer), Is.False);
                Assert.That(source.TryCaptureChangedChunks(buffer, out int changed, out _), Is.True);
                Assert.That(changed, Is.EqualTo(1)); Assert.That(buffer.ChangedChunks[0], Is.EqualTo(leftKey));
                Assert.That(buffer.Header.CellCount, Is.Zero);
                Assert.That(_commits.TryCaptureChangedChunks(buffer, changed), Is.True);
                Assert.That(_commits.TryGetCurrentHeader(leftKey, out var cleared), Is.True);
                Assert.That(cleared.IsResident, Is.False);
                Assert.That(_commits.TryGetCurrentHeader(farKey, out var after), Is.True);
                Assert.That(after.Revision, Is.EqualTo(before.Revision)); Assert.That(after.Checksum, Is.EqualTo(before.Checksum));

                var request = new ElementWriteRequest((Vector3)far + Vector3.one * .5f, MaterialId.Water, 5, 0f, false);
                Assert.That(queue.TryEnqueue(in request, 35L), Is.True);
                Assert.That(source.TryCaptureChangedChunks(buffer, out changed, out _), Is.True);
                Assert.That(buffer.Header.PendingCount, Is.EqualTo(1)); Assert.That(buffer.Pending[0].RetryTick, Is.EqualTo(35L));
                Assert.That(buffer.ChangedChunks[0], Is.EqualTo(farKey));
                Assert.That(queue.TryDequeue(out _), Is.True);
                Assert.That(source.TryCaptureChangedChunks(buffer, out changed, out _), Is.True);
                Assert.That(buffer.Header.PendingCount, Is.Zero);
                Assert.That(buffer.ChangedChunks[0], Is.EqualTo(farKey));
            }
            finally { Object.DestroyImmediate(host); }
        }
    }
}
