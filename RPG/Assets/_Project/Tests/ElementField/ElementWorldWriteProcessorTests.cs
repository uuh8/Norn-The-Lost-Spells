using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// World Deposit 的测试重点不是某一个 Chunk 内部分到了多少，而是跨 Chunk 后仍遵守
    /// 同一条“命令总量守恒”规则，并且只为实际覆盖到的世界空间按需创建数据。
    /// </summary>
    public sealed class ElementWorldWriteProcessorTests
    {
        [Test]
        public void SphereDepositCrossingPositiveXBoundaryConservesTotalAmount()
        {
            var store = new ElementWorldStore(chunkSize: 2, maximumResidentChunks: 8);
            var request = new ElementWriteRequest(
                worldPosition: new Vector3(2f, 0.5f, 0.5f),
                materialKind: MaterialId.Water,
                totalAmount: 100,
                radius: 1.1f,
                useLinearFalloff: false);

            bool applied = ElementWorldWriteProcessor.TryApply(
                store,
                in request,
                worldOrigin: Vector3.zero,
                cellSize: 1f,
                worldTick: 7,
                out int changedCells);

            Assert.That(applied, Is.True);
            Assert.That(changedCells, Is.EqualTo(2));
            Assert.That(store.TryGetChunk(new ElementChunkKey(0, 0, 0), out _), Is.True);
            Assert.That(store.TryGetChunk(new ElementChunkKey(1, 0, 0), out _), Is.True);
            Assert.That(Sum(store, MaterialId.Water), Is.EqualTo(100),
                "球体跨越 Chunk 边界后，离散化只能重新分配 Amount，不能复制或吞掉写入总量。");
        }

        [Test]
        public void PointDepositAtNegativeWorldCoordinateUsesFloorBasedChunkIdentity()
        {
            var store = new ElementWorldStore(chunkSize: 2, maximumResidentChunks: 4);
            var request = new ElementWriteRequest(
                new Vector3(-0.1f, 0.1f, 0.1f),
                MaterialId.Fire,
                totalAmount: 40,
                radius: 0f,
                useLinearFalloff: false);

            Assert.That(ElementWorldWriteProcessor.TryApply(
                store, in request, Vector3.zero, 1f, worldTick: 3, out _), Is.True);

            ElementCell cell = store.GetCellOrEmpty(new Vector3Int(-1, 0, 0));
            Assert.That(cell.MaterialKind, Is.EqualTo(MaterialId.Fire));
            Assert.That(cell.Amount, Is.EqualTo(40));
            Assert.That(store.TryGetChunk(new ElementChunkKey(-1, 0, 0), out _), Is.True);
        }

        private static int Sum(ElementWorldStore store, MaterialId materialKind)
        {
            int total = 0;
            foreach (ElementWorldChunk chunk in store.Chunks.Values)
            {
                for (int index = 0; index < chunk.CellCount; index++)
                {
                    ElementCell cell = chunk.CurrentCells[index];
                    if (cell.MaterialKind == materialKind)
                        total += cell.Amount;
                }
            }

            return total;
        }
    }
}
