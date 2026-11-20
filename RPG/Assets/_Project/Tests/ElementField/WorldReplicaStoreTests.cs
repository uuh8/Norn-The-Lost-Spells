using Game.Core;
using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    /// <summary>粗液体格与细固体格共用数量事实；期望值直接按8细格和GMU体积手算。</summary>
    public sealed class WorldReplicaLiquidSolidProjectionTests
    {
        private GameObject _root;
        private LiquidWorldProfile _liquidProfile;
        private LiquidBehaviorProfile _water;
        private FixedSolidWorldProfile _solidProfile;
        private WorldReplicaStore _store;
        private WorldReplicaSolidReadModel _solids;
        private WorldReplicaLiquidReadModel _liquids;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("Replica coarse liquid projection");
            _liquidProfile = ScriptableObject.CreateInstance<LiquidWorldProfile>();
            _water = ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
            JsonUtility.FromJsonOverwrite("{\"_resolutionScale\":2,\"_cellCapacityGmu\":64}", _liquidProfile);
            typeof(LiquidWorldProfile).GetField("_materials", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(_liquidProfile, new[] { _water });
            _solidProfile = ScriptableObject.CreateInstance<FixedSolidWorldProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_cellCapacityGmu\":64,\"_maximumNavigationBoxesPerChunk\":4}", _solidProfile);
            _store = new WorldReplicaStore(7, 16, 64, 0, 256);
            var solidGeometry = new WorldGridGeometry(WorldMaterialDomain.Solid, Vector3.zero, 1, 2);
            var liquidGeometry = new WorldGridGeometry(WorldMaterialDomain.Liquid, Vector3.zero, 2, 2);
            Assert.That(_store.ConfigureGeometry(solidGeometry), Is.True);
            Assert.That(_store.ConfigureGeometry(liquidGeometry), Is.True);
            Assert.That(_store.ConfigureMaterials(WorldMaterialDomain.Solid, 64, new[] { (byte)MaterialId.Ice, (byte)MaterialId.DriedSlime }, true, 8), Is.True);
            _solids = new WorldReplicaSolidReadModel(_store, solidGeometry, _solidProfile, _root.transform);
            _liquids = new WorldReplicaLiquidReadModel(_store, liquidGeometry, _liquidProfile, 1, null, _solids);
        }

        [TearDown]
        public void TearDown()
        {
            _solids?.Dispose(); Object.DestroyImmediate(_root);
            Object.DestroyImmediate(_liquidProfile); Object.DestroyImmediate(_water); Object.DestroyImmediate(_solidProfile);
        }

        [TestCase(0, 0, 0)] [TestCase(0, 0, 1)] [TestCase(0, 1, 0)] [TestCase(0, 1, 1)]
        [TestCase(1, 0, 0)] [TestCase(1, 0, 1)] [TestCase(1, 1, 0)] [TestCase(1, 1, 1)]
        public void IceInAnyFineCellBlocksTheCoveringCoarseLiquidCell(int x, int y, int z)
        {
            Install(new Vector3Int(x, y, z), MaterialId.Ice, 1);
            Assert.That(_liquids.IsSolidForSurface(Vector3Int.zero), Is.True,
                "液体整格墙依据覆盖的全部细格，不能仅采粗格中心；少量Ice也沿Authority整格规则阻挡。");
        }

        [Test]
        public void NegativeCoarseCoordinatesDoNotAliasAnotherSolidCell()
        {
            Install(new Vector3Int(-2, -2, -2), MaterialId.Ice, 64);
            Assert.That(_liquids.IsSolidForSurface(new Vector3Int(-1, -1, -1)), Is.True);
            Assert.That(_liquids.IsSolidForSurface(Vector3Int.zero), Is.False);
        }

        [Test]
        public void PartialResidueDoesNotBecomeAWholeLiquidCellWall()
        {
            Install(Vector3Int.one, MaterialId.DriedSlime, 32);
            Assert.That(_liquids.IsSolidForSurface(Vector3Int.zero), Is.False,
                "DriedSlime的UsesPartialVolume占容量并抬升液面，不能走Ice整格墙语义。");
        }

        [Test]
        public void ResidueRaisesLiquidBaseAndRefreshesAfterReplacementAndInterestExit()
        {
            Install(Vector3Int.zero, MaterialId.DriedSlime, 32);
            Assert.That(_liquids.GetPresentationBaseHeight(Vector3Int.zero), Is.EqualTo(.125f).Within(.00001f),
                "32 GMU / 512 GMU粗格容量 × 2m格高 = 0.125m。");
            Assert.That(_liquids.GetGameplayBaseHeight(Vector3Int.zero), Is.EqualTo(.125f).Within(.00001f));
            Install(Vector3Int.zero, MaterialId.DriedSlime, 64, 2);
            Assert.That(_liquids.GetPresentationBaseHeight(Vector3Int.zero), Is.EqualTo(.25f).Within(.00001f));
            Assert.That(_store.TotalQuantity(WorldMaterialDomain.Solid), Is.EqualTo(64UL), "只读高度查询不能消费或复制材料。");
            Assert.That(_store.TryEvictInterest(new WorldChunkKey(WorldMaterialDomain.Solid, 0, 0, 0)), Is.True);
            Assert.That(_liquids.GetPresentationBaseHeight(Vector3Int.zero), Is.Zero);
        }

        [Test]
        public void GeneralPointQueryRetainsActualHeightInsteadOfWholeCellBlocking()
        {
            Install(Vector3Int.zero, MaterialId.Ice, 32);
            Assert.That(_solids.HasBlockingVolume(new Vector3(.5f, .25f, .5f)), Is.True);
            Assert.That(_solids.HasBlockingVolume(new Vector3(.5f, .75f, .5f)), Is.False,
                "修液体粗格规则不能把通用点查询改成整格，角色/占据仍读实际体积高度。");
        }

        [Test]
        public void SolidOnlyChangesInvalidateLiquidSurfaceChunkVersion()
        {
            var key = new ElementChunkKey(0, 0, 0);
            uint initial = _liquids.GetSurfaceChunkVersion(key, MaterialId.Water);
            Install(Vector3Int.zero, MaterialId.DriedSlime, 16);
            uint deposited = _liquids.GetSurfaceChunkVersion(key, MaterialId.Water);
            Assert.That(deposited, Is.Not.EqualTo(initial),
                "CPU Renderer按chunk version决定重建；仅改变全局SurfaceVersion不能刷新原Mesh的残留底高。");
            Install(Vector3Int.zero, MaterialId.DriedSlime, 32, 2);
            uint replaced = _liquids.GetSurfaceChunkVersion(key, MaterialId.Water);
            Assert.That(replaced, Is.Not.EqualTo(deposited));
            Assert.That(_store.TryEvictInterest(new WorldChunkKey(WorldMaterialDomain.Solid, 0, 0, 0)), Is.True);
            Assert.That(_liquids.GetSurfaceChunkVersion(key, MaterialId.Water), Is.Not.EqualTo(replaced));
        }

        [Test]
        public void ResidueColliderDoesNotDoubleCountSupportHeightAboveRealFloor()
        {
            var baker = _root.AddComponent<ElementFieldSolidBaker>();
            var ground = new GameObject("Projection support floor"); ground.transform.SetParent(_root.transform);
            ground.transform.position = new Vector3(1, -.1f, 1);
            ground.AddComponent<BoxCollider>().size = new Vector3(4, .2f, 4);
            var residue = new GameObject("Generated partial residue support"); residue.transform.SetParent(_root.transform);
            residue.transform.position = new Vector3(1, .125f, 1);
            residue.AddComponent<GeneratedMaterialSolidCollider>();
            residue.AddComponent<BoxCollider>().size = new Vector3(.8f, .25f, .8f);
            Install(new Vector3Int(1, 0, 1), MaterialId.DriedSlime, 16);
            Physics.SyncTransforms();
            // 先证明真实Raycast能命中残留顶面，避免没有碰撞命中而让过滤测试空通过。
            Assert.That(baker.TrySampleLiquidSupport(new Vector3(1, .5f, 1), 8, out float rawHeight, out _), Is.True);
            Assert.That(rawHeight, Is.EqualTo(.25f).Within(.0001f));
            _liquids = new WorldReplicaLiquidReadModel(_store,
                new WorldGridGeometry(WorldMaterialDomain.Liquid, Vector3.zero, 2, 2), _liquidProfile, 1, baker, _solids);
            Assert.That(_liquids.TryGetTerrainSupportForPresentation(Vector3Int.zero, out var support), Is.True);
            Assert.That(support.Height, Is.EqualTo(0f).Within(.0001f), "残留只贡献GMU底高，terrain support仍取真实地板。");
            Assert.That(_liquids.GetPresentationBaseHeight(Vector3Int.zero), Is.EqualTo(.0625f).Within(.0001f),
                "16/512×2m=0.0625m；不能再叠加Collider的0.25m顶面。");
            Assert.That(_store.TotalQuantity(WorldMaterialDomain.Solid), Is.EqualTo(16UL));
        }

        [TestCase(448u, 1.8f)]
        [TestCase(447u, 2f)]
        public void LiquidColumnInheritsTerrainOnlyWhenLiquidAndResidueExactlyFillLowerCell(uint lowerWater, float expectedBase)
        {
            var baker = _root.AddComponent<ElementFieldSolidBaker>();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(ElementFieldSolidBaker).GetField("_solidLayerMask", flags).SetValue(baker, (LayerMask)(1 << 29));
            typeof(ElementFieldSolidBaker).GetField("_includeDefaultEnvironment", flags).SetValue(baker, false);
            var ground = new GameObject("Offset terrain plane for full liquid column"); ground.transform.SetParent(_root.transform);
            ground.layer = 29; ground.transform.position = new Vector3(1, -.3f, 1);
            ground.AddComponent<BoxCollider>().size = new Vector3(4, .2f, 4);
            Install(Vector3Int.zero, MaterialId.DriedSlime, 64);
            InstallLiquidColumn(lowerWater, 64);
            Physics.SyncTransforms();
            _liquids = new WorldReplicaLiquidReadModel(_store,
                new WorldGridGeometry(WorldMaterialDomain.Liquid, Vector3.zero, 2, 2), _liquidProfile, 1, baker, _solids);
            Assert.That(_liquids.TryGetTerrainSupportForPresentation(Vector3Int.zero, out var support), Is.True);
            Assert.That(support.Height, Is.EqualTo(-.2f).Within(.0001f), "必须命中偏离Grid底的真实地板，避免水平默认底掩盖错误。");
            // 448 Water + 64 residue恰好占满512 GMU，故上格继承-0.2+2=1.8；少1 GMU时不得跨空隙继承。
            Assert.That(_liquids.GetPresentationBaseHeight(Vector3Int.up), Is.EqualTo(expectedBase).Within(.0001f));
            Assert.That(_liquids.GetGameplayBaseHeight(Vector3Int.up), Is.EqualTo(expectedBase).Within(.0001f));
            Assert.That(_store.TotalQuantity(WorldMaterialDomain.Liquid), Is.EqualTo((ulong)lowerWater + 64));
            Assert.That(_store.TotalQuantity(WorldMaterialDomain.Solid), Is.EqualTo(64UL));
        }

        private void InstallLiquidColumn(uint lowerWater, uint upperWater)
        {
            var words = new uint[25]; words[0] = WorldChunkPayloadCodec.Magic; words[1] = 2; words[3] = 1;
            WorldChunkPayloadCodec.WriteGeometry(words, 4, new WorldGridGeometry(WorldMaterialDomain.Liquid, Vector3.zero, 2, 2));
            WorldChunkPayloadCodec.WriteCell(words, 11, new WorldMaterialCellSample(WorldMaterialDomain.Liquid,
                WorldMaterialStorage.Resident, Vector3Int.zero, (byte)MaterialId.Water, lowerWater));
            WorldChunkPayloadCodec.WriteCell(words, 18, new WorldMaterialCellSample(WorldMaterialDomain.Liquid,
                WorldMaterialStorage.Resident, Vector3Int.up, (byte)MaterialId.Water, upperWater));
            var header = new WorldChunkPayloadHeader(7, 20, new WorldChunkKey(WorldMaterialDomain.Liquid, 0, 0, 0), 1, 0, 1, 1,
                words.Length, WorldChunkPayloadCodec.ComputeChecksum(words, words.Length), true);
            Assert.That(_store.TryInstall(header, words, words.Length), Is.True);
        }

        private void Install(Vector3Int cell, MaterialId material, uint quantity, ulong revision = 1)
        {
            var words = new uint[18]; words[0] = WorldChunkPayloadCodec.Magic; words[1] = 1; words[3] = 1;
            WorldChunkPayloadCodec.WriteGeometry(words, 4, new WorldGridGeometry(WorldMaterialDomain.Solid, Vector3.zero, 1, 2));
            WorldChunkPayloadCodec.WriteCell(words, 11, new WorldMaterialCellSample(WorldMaterialDomain.Solid,
                WorldMaterialStorage.Resident, cell, (byte)material, quantity));
            var key = WorldChunkPayloadCodec.CellKey(WorldMaterialDomain.Solid, cell, 2);
            var header = new WorldChunkPayloadHeader(7, revision, key, 1, 0, revision, 1,
                words.Length, WorldChunkPayloadCodec.ComputeChecksum(words, words.Length), true);
            Assert.That(_store.TryInstall(header, words, words.Length), Is.True);
        }
    }

    public sealed class WorldReplicaStoreTests
    {
        private static uint[] Payload(WorldMaterialDomain domain, uint quantity, Vector3Int cell = default)
        {
            var words = new uint[18];
            words[0] = WorldChunkPayloadCodec.Magic; words[1] = 1; words[3] = 1;
            WorldChunkPayloadCodec.WriteGeometry(words, 4, new WorldGridGeometry(domain, Vector3.zero, 1, 4));
            WorldChunkPayloadCodec.WriteCell(words, 11, new WorldMaterialCellSample(domain,
                WorldMaterialStorage.Resident, cell, domain == WorldMaterialDomain.LegacyCell ? (byte)MaterialId.Fire : (byte)MaterialId.Water, quantity));
            return words;
        }
        private static WorldChunkPayloadHeader Header(uint[] words, WorldMaterialDomain domain,
            ulong revision = 1, ulong basis = 0, ulong generation = 1) => new WorldChunkPayloadHeader(
                7, revision, new WorldChunkKey(domain, 0, 0, 0), generation, basis, revision, 12,
                words.Length, WorldChunkPayloadCodec.ComputeChecksum(words, words.Length), true);

        // RED阶段通过反射定位新增只读接口，缺实现时给出行为缺口，而不让整个Unity工程因类型缺失编译失败。
        private static bool ReadInstalledHeader(WorldReplicaStore store, WorldChunkKey key,
            out WorldChunkPayloadHeader header, out uint presentationVersion)
        {
            var method = typeof(WorldReplicaStore).GetMethod("TryGetInstalledChunkHeader");
            Assert.That(method, Is.Not.Null, "需要读取真实已发布Header，不能把PresentationVersion当网络版本");
            object[] args = { key, null, 0u };
            bool result = (bool)method.Invoke(store, args);
            header = (WorldChunkPayloadHeader)args[1]; presentationVersion = (uint)args[2];
            return result;
        }

        [Test]
        public void TimingHeaderReadsPublishedVersionAndRejectsUnpublishedCandidate()
        {
            var store = new WorldReplicaStore(7, 8, 32, 8, 256);
            var words = Payload(WorldMaterialDomain.LegacyCell, 20);
            var first = Header(words, WorldMaterialDomain.LegacyCell, 3, 0, 2);
            Assert.That(store.TryInstall(first, words, words.Length), Is.True);
            Assert.That(ReadInstalledHeader(store, first.Key, out var actual, out uint version), Is.True);
            Assert.That(actual.WorldEpoch, Is.EqualTo(7)); Assert.That(actual.Generation, Is.EqualTo(2));
            Assert.That(actual.Revision, Is.EqualTo(3)); Assert.That(actual.SnapshotId, Is.EqualTo(3));
            Assert.That(actual.ServerTick, Is.EqualTo(12)); Assert.That(actual.Checksum, Is.EqualTo(first.Checksum));
            Assert.That(version, Is.GreaterThan(0));
            // 更高候选CRC错误必须保留旧Header，不能给Renderer或计时器泄露未发布版本。
            var invalid = Header(words, WorldMaterialDomain.LegacyCell, 99, 3, 2);
            words[17] = 21;
            Assert.That(store.TryInstall(invalid, words, words.Length), Is.False);
            Assert.That(ReadInstalledHeader(store, first.Key, out actual, out uint preserved), Is.True);
            Assert.That(actual.Revision, Is.EqualTo(3)); Assert.That(actual.Checksum, Is.EqualTo(first.Checksum));
            Assert.That(preserved, Is.EqualTo(version));
        }

        [Test]
        public void TimingHeaderDoesNotExposeEvictedInterestOrUnknownChunk()
        {
            var store = new WorldReplicaStore(7, 8, 32, 8, 256);
            var words = Payload(WorldMaterialDomain.LegacyCell, 20);
            var first = Header(words, WorldMaterialDomain.LegacyCell);
            Assert.That(store.TryInstall(first, words, words.Length), Is.True);
            Assert.That(store.TryEvictInterest(first.Key), Is.True);
            Assert.That(ReadInstalledHeader(store, first.Key, out var hidden, out uint hiddenVersion), Is.False);
            Assert.That(hidden.WorldEpoch, Is.Zero); Assert.That(hiddenVersion, Is.Zero);
            Assert.That(ReadInstalledHeader(store, new WorldChunkKey(WorldMaterialDomain.LegacyCell, -1, 0, 0),
                out hidden, out hiddenVersion), Is.False);
            Assert.That(hidden.WorldEpoch, Is.Zero); Assert.That(hiddenVersion, Is.Zero);
        }

        [Test]
        public void TimingInstalledEventOccursAfterSuccessfulPublishOnly()
        {
            var assembly = typeof(WorldChunkPayloadHeader).Assembly;
            var type = assembly.GetType("Game.Core.WorldReplicationTimingTrace");
            Assert.That(type, Is.Not.Null, "默认关闭计时器尚未实现");
            var key = new WorldChunkKey(WorldMaterialDomain.LegacyCell, 0, 0, 0);
            object trace = System.Activator.CreateInstance(type, new object[] { 8, key, 7UL });
            Assert.That((bool)type.GetMethod("TryAttach").Invoke(null, new[] { trace }), Is.True);
            try
            {
                var store = new WorldReplicaStore(7, 8, 32, 8, 256);
                var words = Payload(WorldMaterialDomain.LegacyCell, 20);
                var first = Header(words, WorldMaterialDomain.LegacyCell);
                Assert.That(store.TryInstall(first, words, words.Length), Is.True);
                Assert.That(type.GetProperty("Count").GetValue(trace), Is.EqualTo(1), "真实Current交换后须记录一次安装");
                object[] read = { 0, null }; Assert.That((bool)type.GetMethod("TryRead").Invoke(trace, read), Is.True);
                var eventType = read[1].GetType();
                Assert.That(eventType.GetField("Stage").GetValue(read[1]).ToString(), Is.EqualTo("Installed"));
                var observed = (WorldChunkPayloadHeader)eventType.GetField("Header").GetValue(read[1]);
                Assert.That(observed.Revision, Is.EqualTo(first.Revision)); Assert.That(observed.Checksum, Is.EqualTo(first.Checksum));
                // 幂等重送和CRC失败都没有再次发布，不应伪造“新版本安装时间”。
                Assert.That(store.TryInstall(first, words, words.Length), Is.True);
                var invalid = Header(words, WorldMaterialDomain.LegacyCell, 9, 1); words[17] = 21;
                Assert.That(store.TryInstall(invalid, words, words.Length), Is.False);
                Assert.That(type.GetProperty("Count").GetValue(trace), Is.EqualTo(1));
            }
            finally { type.GetMethod("Detach").Invoke(null, new[] { trace }); }
        }

        [TestCase(WorldMaterialDomain.LegacyCell, 200u)]
        [TestCase(WorldMaterialDomain.Liquid, 70000u)]
        [TestCase(WorldMaterialDomain.Solid, 90000u)]
        [TestCase(WorldMaterialDomain.Gas, 80000u)]
        public void InstallsNativeDomainWithoutTruncatingQuantity(WorldMaterialDomain domain, uint quantity)
        {
            var store = new WorldReplicaStore(7, 8, 32, 8, 256);
            var words = Payload(domain, quantity);
            Assert.That(store.TryInstall(Header(words, domain), words, words.Length), Is.True);
            Assert.That(store.TryGetQuantity(domain, Vector3Int.zero, (byte)(domain == WorldMaterialDomain.LegacyCell ? MaterialId.Fire : MaterialId.Water), out uint actual), Is.True);
            Assert.That(actual, Is.EqualTo(quantity));
        }

        [Test]
        public void CorruptHighRevisionPreservesOldDataAndLaterLegalDelta()
        {
            var store = new WorldReplicaStore(7, 8, 32, 8, 256);
            var words = Payload(WorldMaterialDomain.Liquid, 20);
            Assert.That(store.TryInstall(Header(words, WorldMaterialDomain.Liquid), words, 18), Is.True);
            var corrupt = Header(words, WorldMaterialDomain.Liquid, 100, 1);
            words[17] = 21;
            Assert.That(store.TryInstall(corrupt, words, 18), Is.False);
            Assert.That(store.TryInstall(Header(words, WorldMaterialDomain.Liquid, 2, 1), words, 18), Is.True);
            store.TryGetQuantity(WorldMaterialDomain.Liquid, Vector3Int.zero, (byte)MaterialId.Water, out uint quantity);
            Assert.That(quantity, Is.EqualTo(21));
        }

        [Test]
        public void ReplacementDoesNotAccumulateAndTombstoneRejectsOldGeneration()
        {
            var store = new WorldReplicaStore(7, 8, 32, 8, 256);
            var words = Payload(WorldMaterialDomain.Gas, 20);
            Assert.That(store.TryInstall(Header(words, WorldMaterialDomain.Gas), words, 18), Is.True);
            words[17] = 4;
            Assert.That(store.TryInstall(Header(words, WorldMaterialDomain.Gas, 2, 1), words, 18), Is.True);
            Assert.That(store.TotalQuantity(WorldMaterialDomain.Gas), Is.EqualTo(4));
            var removal = new WorldChunkPayloadHeader(7, 3, new WorldChunkKey(WorldMaterialDomain.Gas, 0, 0, 0), 1, 2, 3, 13, 0, 2166136261u, false);
            Assert.That(store.TryInstall(removal, null, 0), Is.True);
            Assert.That(store.TotalQuantity(WorldMaterialDomain.Gas), Is.Zero);
            Assert.That(store.TryInstall(Header(words, WorldMaterialDomain.Gas, 2), words, 18), Is.False);
        }

        [Test]
        public void CapacityFailureDoesNotAdvanceVersion()
        {
            var store = new WorldReplicaStore(7, 8, 1, 8, 256);
            var liquid = Payload(WorldMaterialDomain.Liquid, 10);
            var gas = Payload(WorldMaterialDomain.Gas, 20);
            Assert.That(store.TryInstall(Header(liquid, WorldMaterialDomain.Liquid), liquid, 18), Is.True);
            Assert.That(store.TryInstall(Header(gas, WorldMaterialDomain.Gas), gas, 18), Is.False);
            Assert.That(store.TotalQuantity(WorldMaterialDomain.Liquid), Is.EqualTo(10));
        }

        [Test]
        public void ProfileCapacityRejectsHighRevisionBeforeViewUpload()
        {
            var store = new WorldReplicaStore(7, 8, 32, 8, 256);
            Assert.That(store.ConfigureMaterials(WorldMaterialDomain.Liquid, 255, new[] { (byte)MaterialId.Water }), Is.True);
            var words = Payload(WorldMaterialDomain.Liquid, 20);
            Assert.That(store.TryInstall(Header(words, WorldMaterialDomain.Liquid), words, 18), Is.True);
            words[17] = 70000;
            Assert.That(store.TryInstall(Header(words, WorldMaterialDomain.Liquid, 100, 1), words, 18), Is.False);
            words[17] = 21;
            Assert.That(store.TryInstall(Header(words, WorldMaterialDomain.Liquid, 2, 1), words, 18), Is.True);
        }

        [Test]
        public void TombstoneRequiresNewGenerationEvenForLargerBaseline()
        {
            var store = new WorldReplicaStore(7, 8, 32, 8, 256);
            var words = Payload(WorldMaterialDomain.Gas, 20);
            Assert.That(store.TryInstall(Header(words, WorldMaterialDomain.Gas), words, 18), Is.True);
            var removal = new WorldChunkPayloadHeader(7, 3, new WorldChunkKey(WorldMaterialDomain.Gas, 0, 0, 0), 1, 1, 2, 13, 0, 2166136261u, false);
            Assert.That(store.TryInstall(removal, null, 0), Is.True);
            Assert.That(store.TryInstall(Header(words, WorldMaterialDomain.Gas, 99), words, 18), Is.False);
            Assert.That(store.TryInstall(Header(words, WorldMaterialDomain.Gas, 1, 0, 2), words, 18), Is.True);
        }

        [Test]
        public void SameRevisionCorruptionDoesNotBecomeIdempotentSuccess()
        {
            var store = new WorldReplicaStore(7, 8, 32, 8, 256);
            var words = Payload(WorldMaterialDomain.Liquid, 20); var header = Header(words, WorldMaterialDomain.Liquid);
            Assert.That(store.TryInstall(header, words, 18), Is.True);
            words[17] = 21;
            Assert.That(store.TryInstall(header, words, 18), Is.False);
        }

        [Test]
        public void NativeNegativeCellAndGasVolumeCopyKeepIdentityAndQuantity()
        {
            var store = new WorldReplicaStore(7, 8, 32, 8, 256);
            var words = Payload(WorldMaterialDomain.Gas, 15, new Vector3Int(-1, 0, 0));
            var key = new WorldChunkKey(WorldMaterialDomain.Gas, -1, 0, 0);
            var header = new WorldChunkPayloadHeader(7, 1, key, 1, 0, 1, 10, 18, WorldChunkPayloadCodec.ComputeChecksum(words, 18), true);
            Assert.That(store.TryInstall(header, words, 18), Is.True);
            var source = new WorldReplicaVolumeReadModel(store, new WorldGridGeometry(WorldMaterialDomain.Gas, Vector3.zero, 1, 4), 32, new[] { MaterialId.Water });
            var density = new uint[1];
            source.CopyMaterialBox(new ElementChunkKey(-1, 0, 0), new Vector3Int(3, 0, 0), Vector3Int.one, MaterialId.Water, density, 0, 1, 1);
            Assert.That(density[0], Is.EqualTo(15));
            Assert.That(source.TotalQuantity, Is.EqualTo(15));
        }

        [Test]
        public void LiquidReadModelUsesOriginalPresentationContractWithoutRuntimeOwner()
        {
            var profile = ScriptableObject.CreateInstance<LiquidWorldProfile>();
            var behavior = ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
            try
            {
                typeof(LiquidWorldProfile).GetField("_materials", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .SetValue(profile, new[] { behavior });
                var store = new WorldReplicaStore(7, 8, 32, 8, 256);
                var geometry = new WorldGridGeometry(WorldMaterialDomain.Liquid, Vector3.zero, 1, 4);
                var words = Payload(WorldMaterialDomain.Liquid, 128);
                Assert.That(store.TryInstall(Header(words, WorldMaterialDomain.Liquid), words, 18), Is.True);
                ILiquidPresentationReadOnly source = new WorldReplicaLiquidReadModel(store, geometry, profile, 1, null);
                var quantities = new ushort[64]; source.CopyPresentationChunkQuantities(new ElementChunkKey(0, 0, 0), quantities);
                Assert.That(quantities[0], Is.EqualTo(128));
                Assert.That(source.TryGetPresentationSample(Vector3Int.zero, MaterialId.Water, false, out var sample), Is.True);
                Assert.That(sample.VelocityWorld, Is.EqualTo(Vector3.zero));
                Assert.That(source.TryGetLayer(Vector3Int.zero, 0, out var layer), Is.True);
                Assert.That(layer.ThicknessFill, Is.EqualTo(128f / 255).Within(.0001));
                Assert.That(source.TotalQuantity, Is.EqualTo(128));
            }
            finally { Object.DestroyImmediate(profile); Object.DestroyImmediate(behavior); }
        }

        [Test]
        public void InterestEvictionRequiresBaselineAndReinstallsEqualRevision()
        {
            var store = new WorldReplicaStore(7, 8, 32, 8, 256);
            var words = Payload(WorldMaterialDomain.Liquid, 20);
            var header = Header(words, WorldMaterialDomain.Liquid);
            Assert.That(store.TryInstall(header, words, 18), Is.True);
            Assert.That(store.TryEvictInterest(header.Key), Is.True);
            Assert.That(store.TotalQuantity(WorldMaterialDomain.Liquid), Is.Zero);
            Assert.That(store.TryInstall(Header(words, WorldMaterialDomain.Liquid, 2, 1), words, 18), Is.False);
            Assert.That(store.TryInstall(header, words, 18), Is.True);
            Assert.That(store.TotalQuantity(WorldMaterialDomain.Liquid), Is.EqualTo(20));
        }

        // 以下只验证既有“释放占据、保留版本、重入Baseline”合同；不选择正式Interest半径或Wire生命周期。
        // 将Evict误改成删除版本记录，会让旧Generation/Revision重新填回数量，四个原生域都必须拒绝。
        [TestCase(WorldMaterialDomain.LegacyCell)]
        [TestCase(WorldMaterialDomain.Liquid)]
        [TestCase(WorldMaterialDomain.Solid)]
        [TestCase(WorldMaterialDomain.Gas)]
        public void InterestEvictionPreservesGenerationAndRevisionFence(WorldMaterialDomain domain)
        {
            var store = new WorldReplicaStore(7, 8, 32, 8, 256);
            var words = Payload(domain, 20);
            var current = Header(words, domain, 3, 0, 2);
            Assert.That(store.TryInstall(current, words, 18), Is.True);
            Assert.That(store.TryEvictInterest(current.Key), Is.True);
            Assert.That(store.TryInstall(Header(words, domain, 99, 0, 1), words, 18), Is.False);
            Assert.That(store.TryInstall(Header(words, domain, 2, 0, 2), words, 18), Is.False);
            Assert.That(store.TotalQuantity(domain), Is.Zero);
            Assert.That(store.TryInstall(current, words, 18), Is.True);
            Assert.That(store.TotalQuantity(domain), Is.EqualTo(20));
        }

        // 相同版本的合法重装只能恢复原内容。仅验证新payload自身CRC仍不足以证明它属于原版本。
        [TestCase(WorldMaterialDomain.LegacyCell)]
        [TestCase(WorldMaterialDomain.Liquid)]
        [TestCase(WorldMaterialDomain.Solid)]
        [TestCase(WorldMaterialDomain.Gas)]
        public void InterestReentryRejectsConflictingContentAtEqualVersion(WorldMaterialDomain domain)
        {
            var store = new WorldReplicaStore(7, 8, 32, 8, 256);
            var original = Payload(domain, 20);
            var current = Header(original, domain, 3, 0, 2);
            Assert.That(store.TryInstall(current, original, 18), Is.True);
            Assert.That(store.TryEvictInterest(current.Key), Is.True);
            uint version = store.PresentationVersion;
            var conflicting = Payload(domain, 21);
            Assert.That(store.TryInstall(Header(conflicting, domain, 3, 0, 2), conflicting, 18), Is.False);
            Assert.That(store.PresentationVersion, Is.EqualTo(version));
            Assert.That(store.TotalQuantity(domain), Is.Zero);
            Assert.That(store.TryInstall(current, original, 18), Is.True);
            Assert.That(store.TotalQuantity(domain), Is.EqualTo(20));
        }

        [TestCase(WorldMaterialDomain.LegacyCell)]
        [TestCase(WorldMaterialDomain.Liquid)]
        [TestCase(WorldMaterialDomain.Solid)]
        [TestCase(WorldMaterialDomain.Gas)]
        public void InterestEvictionHidesReadModelAndRepeatedEvictionIsIdempotent(WorldMaterialDomain domain)
        {
            var store = new WorldReplicaStore(7, 8, 32, 8, 256);
            var words = Payload(domain, 20);
            var current = Header(words, domain);
            Assert.That(store.TryInstall(current, words, 18), Is.True);
            Assert.That(store.TryEvictInterest(current.Key), Is.True);
            uint version = store.PresentationVersion;
            Assert.That(store.CellCount, Is.Zero);
            Assert.That(store.ResidentCellCount(domain), Is.Zero);
            Assert.That(store.TryGetFirstCell(domain, Vector3Int.zero, out _), Is.False);
            Assert.That(store.CopyChunkKeys(domain, new ElementChunkKey[8]), Is.Zero);
            Assert.That(store.GetChunkVersion(domain, new ElementChunkKey(0, 0, 0)), Is.Zero);
            Assert.That(store.TryEvictInterest(current.Key), Is.True);
            Assert.That(store.PresentationVersion, Is.EqualTo(version));
        }

        [Test]
        public void InterestEvictionDoesNotTurnAuthorityTombstoneIntoResident()
        {
            var store = new WorldReplicaStore(7, 8, 32, 8, 256);
            var words = Payload(WorldMaterialDomain.Liquid, 20);
            var current = Header(words, WorldMaterialDomain.Liquid, 2);
            Assert.That(store.TryInstall(current, words, 18), Is.True);
            var removed = new WorldChunkPayloadHeader(7, 3, current.Key, 1, 2, 3, 13, 0, 2166136261u, false);
            Assert.That(store.TryInstall(removed, null, 0), Is.True);
            Assert.That(store.TryEvictInterest(current.Key), Is.True);
            Assert.That(store.TryInstall(Header(words, WorldMaterialDomain.Liquid, 99), words, 18), Is.False);
            Assert.That(store.TotalQuantity(WorldMaterialDomain.Liquid), Is.Zero);
            Assert.That(store.TryInstall(Header(words, WorldMaterialDomain.Liquid, 1, 0, 2), words, 18), Is.True);
            Assert.That(store.TotalQuantity(WorldMaterialDomain.Liquid), Is.EqualTo(20));
        }

        private static bool RetireInterest(WorldReplicaStore store, WorldChunkKey key)
        {
            var contract = typeof(IWorldChunkPayloadInterestSink).Assembly.GetType("Game.Core.IWorldChunkPayloadRetirementSink");
            Assert.That(contract, Is.Not.Null, "退休属于可信生命周期的可选扩展，不能改变原InterestSink合同。");
            Assert.That(contract.IsAssignableFrom(typeof(WorldReplicaStore)), Is.True);
            var method = typeof(WorldReplicaStore).GetMethod("TryRetireInterest");
            Assert.That(method, Is.Not.Null, "释放只读量后需要显式归还metadata槽。");
            return (bool)method.Invoke(store, new object[] { key });
        }

        private static WorldChunkPayloadHeader HeaderAtKey(uint[] words, WorldChunkKey key,
            ulong revision = 1, ulong generation = 1) => new WorldChunkPayloadHeader(
                7, revision, key, generation, 0, revision, 12, words.Length,
                WorldChunkPayloadCodec.ComputeChecksum(words, words.Length), true);

        [Test]
        public void RetirementRejectsLiveQuantityAndUnknownKeyWithoutMutatingPresentation()
        {
            var store = new WorldReplicaStore(7, 1, 4, 0, 64);
            var words = Payload(WorldMaterialDomain.Liquid, 20);
            var header = Header(words, WorldMaterialDomain.Liquid, 8, 0, 9);
            Assert.That(store.TryInstall(header, words, words.Length), Is.True);
            uint version = store.PresentationVersion;
            Assert.That(RetireInterest(store, header.Key), Is.False);
            Assert.That(RetireInterest(store, new WorldChunkKey(WorldMaterialDomain.Liquid, 1, 0, 0)), Is.False);
            Assert.That(store.TotalQuantity(WorldMaterialDomain.Liquid), Is.EqualTo(20));
            Assert.That(store.PresentationVersion, Is.EqualTo(version));
            Assert.That(ReadInstalledHeader(store, header.Key, out var preserved, out _), Is.True);
            Assert.That(preserved, Is.EqualTo(header));
        }

        [TestCase(WorldMaterialDomain.LegacyCell)]
        [TestCase(WorldMaterialDomain.Liquid)]
        [TestCase(WorldMaterialDomain.Solid)]
        [TestCase(WorldMaterialDomain.Gas)]
        public void OneMetadataSlotCanFollowMoreThan128DistinctReleasedKeys(WorldMaterialDomain domain)
        {
            var store = new WorldReplicaStore(7, 1, 4, 0, 64);
            for (int step = 0; step < 130; step++)
            {
                var cell = new Vector3Int(step * 4, 0, 0);
                var key = new WorldChunkKey(domain, step, 0, 0);
                var words = Payload(domain, 20, cell);
                // 第一块留下高版本，后续每块从G1/R1开始；只清Header、不清Version会在第二块真实失败。
                var header = HeaderAtKey(words, key, step == 0 ? 17UL : 1UL, step == 0 ? 9UL : 1UL);
                Assert.That(store.TryInstall(header, words, words.Length), Is.True, "step=" + step);
                Assert.That(store.TryGetQuantity(domain, cell, domain == WorldMaterialDomain.LegacyCell
                    ? (byte)MaterialId.Fire : (byte)MaterialId.Water, out uint quantity), Is.True);
                Assert.That(quantity, Is.EqualTo(20));
                Assert.That(store.TryEvictInterest(key), Is.True);
                uint releasedVersion = store.PresentationVersion;
                uint releasedDomainVersion = store.DomainVersion(domain);
                Assert.That(RetireInterest(store, key), Is.True, "step=" + step);
                Assert.That(store.PresentationVersion, Is.GreaterThan(releasedVersion));
                Assert.That(store.DomainVersion(domain), Is.GreaterThan(releasedDomainVersion));
                Assert.That(store.CellCount, Is.Zero); Assert.That(store.TotalQuantity(domain), Is.Zero);
                Assert.That(ReadInstalledHeader(store, key, out _, out _), Is.False);
                Assert.That(RetireInterest(store, key), Is.False, "已退休Key不再存在，重复请求不得再次推进版本。");
            }
        }

        [Test]
        public void RetirementCompactsMetadataWithoutChangingAnotherDomainChunk()
        {
            var store = new WorldReplicaStore(7, 3, 4, 0, 64);
            var a = new WorldChunkKey(WorldMaterialDomain.Liquid, 0, 0, 0);
            var b = new WorldChunkKey(WorldMaterialDomain.Solid, 1, 0, 0);
            var c = new WorldChunkKey(WorldMaterialDomain.Gas, 2, 0, 0);
            var wordsA = Payload(a.Domain, 20);
            var wordsB = Payload(b.Domain, 30, new Vector3Int(4, 0, 0));
            var wordsC = Payload(c.Domain, 40, new Vector3Int(8, 0, 0));
            var headerB = HeaderAtKey(wordsB, b, 7, 8);
            var headerC = HeaderAtKey(wordsC, c, 5, 6);
            Assert.That(store.TryInstall(HeaderAtKey(wordsA, a, 17, 9), wordsA, wordsA.Length), Is.True);
            Assert.That(store.TryInstall(headerB, wordsB, wordsB.Length), Is.True);
            Assert.That(store.TryInstall(headerC, wordsC, wordsC.Length), Is.True);
            Assert.That(ReadInstalledHeader(store, c, out _, out uint cVersion), Is.True);
            uint gasVersion = store.DomainVersion(c.Domain);
            Assert.That(store.TryEvictInterest(a), Is.True); Assert.That(RetireInterest(store, a), Is.True);
            Assert.That(ReadInstalledHeader(store, b, out var actualB, out _), Is.True);
            Assert.That(ReadInstalledHeader(store, c, out var actualC, out uint preservedCVersion), Is.True);
            Assert.That(actualB, Is.EqualTo(headerB)); Assert.That(actualC, Is.EqualTo(headerC));
            Assert.That(preservedCVersion, Is.EqualTo(cVersion)); Assert.That(store.DomainVersion(c.Domain), Is.EqualTo(gasVersion));
            Assert.That(store.TotalQuantity(b.Domain), Is.EqualTo(30)); Assert.That(store.TotalQuantity(c.Domain), Is.EqualTo(40));
            Assert.That(store.CellCount, Is.EqualTo(2));
            var d = new WorldChunkKey(WorldMaterialDomain.Liquid, 3, 0, 0);
            var wordsD = Payload(d.Domain, 50, new Vector3Int(12, 0, 0));
            Assert.That(store.TryInstall(HeaderAtKey(wordsD, d), wordsD, wordsD.Length), Is.True);
            Assert.That(store.TotalQuantity(d.Domain), Is.EqualTo(50)); Assert.That(store.CellCount, Is.EqualTo(3));
            Assert.That(ReadInstalledHeader(store, c, out actualC, out _), Is.True); Assert.That(actualC, Is.EqualTo(headerC));
        }

        [Test]
        public void TrustedRetirementAlsoReleasesATombstoneSlotForANewKey()
        {
            var store = new WorldReplicaStore(7, 1, 4, 0, 64);
            var key = new WorldChunkKey(WorldMaterialDomain.Liquid, 0, 0, 0);
            var removed = new WorldChunkPayloadHeader(7, 9, key, 9, 0, 17, 12, 0, 2166136261u, false);
            Assert.That(store.TryInstall(removed, null, 0), Is.True);
            Assert.That(RetireInterest(store, key), Is.True,
                "tombstone没有只读量，但仍占metadata；可信Leave栅栏建立后才可显式退休。");
            var nextKey = new WorldChunkKey(WorldMaterialDomain.Liquid, 1, 0, 0);
            var words = Payload(nextKey.Domain, 20, new Vector3Int(4, 0, 0));
            Assert.That(store.TryInstall(HeaderAtKey(words, nextKey), words, words.Length), Is.True);
            Assert.That(store.TotalQuantity(nextKey.Domain), Is.EqualTo(20));
        }
    }
}
