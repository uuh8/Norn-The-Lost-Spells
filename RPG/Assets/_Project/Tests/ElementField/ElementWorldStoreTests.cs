using Game.Materials;
using System;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// 稀疏 Store 只为真正出现过元素的世界 Chunk 分配内存；本组测试锁定容量保护、
    /// 同 Key 身份、未知空间 Empty 语义和“非空休眠数据不能被回收”的底线。
    /// </summary>
    public sealed class ElementWorldStoreTests
    {
        [Test]
        public void TryGetOrCreateChunk_ReusesSameChunkForSameWorldKey()
        {
            var store = new ElementWorldStore(chunkSize: 8, maximumResidentChunks: 4);
            var key = new ElementChunkKey(3, -1, 2);

            Assert.That(store.TryGetOrCreateChunk(key, out ElementWorldChunk first), Is.True);
            Assert.That(store.TryGetOrCreateChunk(key, out ElementWorldChunk second), Is.True);

            Assert.That(second, Is.SameAs(first));
            Assert.That(store.ResidentChunkCount, Is.EqualTo(1));
            Assert.That(first.CellCount, Is.EqualTo(8 * 8 * 8));
            Assert.That(first.CurrentCells.Length, Is.EqualTo(first.CellCount));
            Assert.That(first.NextCells.Length, Is.EqualTo(first.CellCount));
            Assert.That(first.SolidMask.Length, Is.EqualTo(first.CellCount));
            Assert.That(first.AmountDelta.Length, Is.EqualTo(first.CellCount));
        }

        [Test]
        public void TryGetOrCreateChunk_InitializesNewChunkBeforeReturningIt()
        {
            int initializationCount = 0;
            var store = new ElementWorldStore(
                chunkSize: 2,
                maximumResidentChunks: 4,
                chunkInitializer: chunk =>
                {
                    initializationCount++;
                    chunk.SolidMask[0] = true;
                });
            var key = new ElementChunkKey(-1, 0, 2);

            Assert.That(store.TryGetOrCreateChunk(key, out ElementWorldChunk first), Is.True);
            Assert.That(first.SolidMask[0], Is.True,
                "Store 返回新 Chunk 前必须先完成 Solid Bake，否则同一 Tick 的 Deposit 会写进地面。");
            Assert.That(initializationCount, Is.EqualTo(1));

            Assert.That(store.TryGetOrCreateChunk(key, out ElementWorldChunk second), Is.True);
            Assert.That(second, Is.SameAs(first));
            Assert.That(initializationCount, Is.EqualTo(1),
                "读取已存在 Chunk 不应重复执行昂贵的 Physics Bake。");
        }

        [Test]
        public void TryGetOrCreateChunk_RejectsNewKeyAtResidentCapacity()
        {
            var store = new ElementWorldStore(chunkSize: 8, maximumResidentChunks: 1);

            Assert.That(
                store.TryGetOrCreateChunk(new ElementChunkKey(0, 0, 0), out _),
                Is.True);
            Assert.That(
                store.TryGetOrCreateChunk(new ElementChunkKey(1, 0, 0), out ElementWorldChunk rejected),
                Is.False);
            Assert.That(rejected, Is.Null);
            Assert.That(store.ResidentChunkCount, Is.EqualTo(1));
        }

        [Test]
        public void GetCellOrEmpty_UnknownChunkDoesNotAllocate()
        {
            var store = new ElementWorldStore(chunkSize: 8, maximumResidentChunks: 4);

            ElementCell cell = store.GetCellOrEmpty(new Vector3Int(100, 2, -100));

            Assert.That(cell.IsEmpty, Is.True);
            Assert.That(store.ResidentChunkCount, Is.Zero);
        }

        [Test]
        public void NonEmptyChunkCannotBeRemovedButEmptyChunkCan()
        {
            var store = new ElementWorldStore(chunkSize: 8, maximumResidentChunks: 4);
            var key = new ElementChunkKey(-1, 0, 0);
            Assert.That(store.TryGetOrCreateChunk(key, out ElementWorldChunk chunk), Is.True);
            chunk.SetCell(
                new Vector3Int(7, 0, 0),
                new ElementCell(MaterialId.Water, 120));

            Assert.That(chunk.HasAnyElement, Is.True);
            Assert.That(store.TryRemoveEmptyChunk(key), Is.False);
            Assert.That(store.TryGetChunk(key, out _), Is.True);

            chunk.SetCell(new Vector3Int(7, 0, 0), default);
            Assert.That(chunk.HasAnyElement, Is.False);
            Assert.That(store.TryRemoveEmptyChunk(key), Is.True);
            Assert.That(store.TryGetChunk(key, out _), Is.False);
        }

        [Test]
        public void GlobalCellRead_UsesChunkAndLocalCoordinates()
        {
            var store = new ElementWorldStore(chunkSize: 8, maximumResidentChunks: 4);
            var key = new ElementChunkKey(-1, 0, 0);
            Assert.That(store.TryGetOrCreateChunk(key, out ElementWorldChunk chunk), Is.True);
            chunk.SetCell(
                new Vector3Int(7, 0, 0),
                new ElementCell(MaterialId.Fire, 90));

            ElementCell cell = store.GetCellOrEmpty(new Vector3Int(-1, 0, 0));

            Assert.That(cell.MaterialKind, Is.EqualTo(MaterialId.Fire));
            Assert.That(cell.Amount, Is.EqualTo(90));
        }

        [TestCase(0, 1)]
        [TestCase(8, 0)]
        public void Constructor_InvalidBudgetThrows(int chunkSize, int maximumResidentChunks)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ElementWorldStore(chunkSize, maximumResidentChunks));
        }
    }
}
