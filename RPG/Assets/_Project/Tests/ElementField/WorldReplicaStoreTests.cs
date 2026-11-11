using Game.Core;
using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
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
    }
}
