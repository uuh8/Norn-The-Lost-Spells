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
    }
}
