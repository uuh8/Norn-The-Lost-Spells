using Game.Materials;
using Game.Combat;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// 跨 Chunk Solver 的最小边界案例。测试刻意使用 ChunkSize=2，让 Local 0/1 就代表边界，
    /// 从而把错误限定在坐标映射、邻居预创建、Snapshot/Delta 或 Activity 语义中。
    /// </summary>
    public sealed class ElementWorldSimulatorTests
    {
        [Test]
        public void WaterFlowsAcrossPositiveXChunkBoundaryAndConservesAmount()
        {
            ElementWorldStore store = Store();
            ElementWorldChunk source = ActiveChunk(store, new ElementChunkKey(0, 0, 0));
            source.SetCell(new Vector3Int(1, 1, 0), Water(64));
            source.SolidMask[ElementFieldCoordinates.ToIndex(
                new Vector3Int(1, 0, 0), source.Grid.Dimensions)] = true;

            ElementFieldSimulationStats stats = Simulate(
                store, new[] { source }, down: 64, lateral: 16);

            Assert.That(store.TryGetChunk(
                new ElementChunkKey(1, 0, 0), out ElementWorldChunk target), Is.True,
                "边界 Water 必须在枚举模拟前预创建可能接收流量的相邻 Chunk。");
            Assert.That(target.GetCell(new Vector3Int(0, 1, 0)).Amount, Is.EqualTo(8));
            Assert.That(Sum(store, MaterialId.Water), Is.EqualTo(64));
            Assert.That(stats.WaterTransfers, Is.GreaterThan(0));
        }

        [Test]
        public void WaterFallsAcrossNegativeYChunkBoundary()
        {
            ElementWorldStore store = Store();
            ElementWorldChunk source = ActiveChunk(store, new ElementChunkKey(0, 1, 0));
            source.SetCell(new Vector3Int(0, 0, 0), Water(100));

            Simulate(store, new[] { source }, down: 64);

            Assert.That(store.TryGetChunk(
                new ElementChunkKey(0, 0, 0), out ElementWorldChunk target), Is.True);
            Assert.That(source.GetCell(new Vector3Int(0, 0, 0)).Amount, Is.EqualTo(36));
            Assert.That(target.GetCell(new Vector3Int(0, 1, 0)).Amount, Is.EqualTo(64));
            Assert.That(Sum(store, MaterialId.Water), Is.EqualTo(100));
        }

        [Test]
        public void SolidInitializedInLowerChunk_BlocksCrossChunkWaterFall()
        {
            var lowerKey = new ElementChunkKey(0, 0, 0);
            var store = new ElementWorldStore(
                chunkSize: 2,
                maximumResidentChunks: 16,
                chunkInitializer: chunk =>
                {
                    if (chunk.Key == lowerKey)
                    {
                        int floorIndex = ElementFieldCoordinates.ToIndex(
                            new Vector3Int(0, 1, 0),
                            chunk.Grid.Dimensions);
                        chunk.SolidMask[floorIndex] = true;
                    }
                });
            ElementWorldChunk source = ActiveChunk(store, new ElementChunkKey(0, 1, 0));
            source.SetCell(new Vector3Int(0, 0, 0), Water(100));

            ElementFieldSimulationStats stats = Simulate(store, new[] { source }, down: 64);

            Assert.That(store.TryGetChunk(lowerKey, out ElementWorldChunk lower), Is.True);
            Assert.That(source.GetCell(new Vector3Int(0, 0, 0)).Amount, Is.EqualTo(100));
            Assert.That(lower.GetCell(new Vector3Int(0, 1, 0)).IsEmpty, Is.True);
            Assert.That(stats.WaterTransfers, Is.Zero,
                "Collider 已 Rasterize 为 Solid 后，水不能继续穿过地板并向负 Y 创建流水链。");
        }

        [Test]
        public void SubThresholdBoundaryWater_DoesNotCreateNeighborForZeroTransfer()
        {
            ElementWorldStore store = Store();
            ElementWorldChunk source = ActiveChunk(store, new ElementChunkKey(0, 0, 0));
            source.SetCell(new Vector3Int(1, 1, 0), Water(7));
            var planner = new ElementBoundaryNeighborPlanner(maximumResidentChunks: 16);
            var simulationChunks = new ElementWorldChunk[16];
            simulationChunks[0] = source;

            int simulationChunkCount = planner.PrepareNeighbors(
                store,
                simulationChunks,
                simulationChunkCount: 1,
                worldTick: 1,
                maxDownFlowPerTick: 64,
                maxLateralFlowPerTick: 16);

            Assert.That(store.TryGetChunk(new ElementChunkKey(1, 0, 0), out _), Is.False,
                "difference / 8 为 0 时，不应为不存在的水平接收端分配整个 Chunk。");
            Assert.That(store.TryGetChunk(new ElementChunkKey(0, 0, -1), out _), Is.False);
            Assert.That(simulationChunkCount, Is.EqualTo(1));
        }

        [Test]
        public void WaterAndFireAcrossChunkBoundaryConsumeEqualAmounts()
        {
            ElementWorldStore store = Store();
            ElementWorldChunk waterChunk = ActiveChunk(store, new ElementChunkKey(0, 0, 0));
            ElementWorldChunk fireChunk = Chunk(store, new ElementChunkKey(1, 0, 0));
            waterChunk.SetCell(new Vector3Int(1, 0, 0), Water(100));
            fireChunk.SetCell(new Vector3Int(0, 0, 0), Fire(100));

            ElementFieldSimulationStats stats = Simulate(
                store, new[] { waterChunk }, deltaTime: 1f);

            Assert.That(waterChunk.GetCell(new Vector3Int(1, 0, 0)).IsEmpty, Is.True);
            Assert.That(fireChunk.GetCell(new Vector3Int(0, 0, 0)).IsEmpty, Is.True);
            Assert.That(stats.ReactionPairs, Is.EqualTo(1));
        }

        [Test]
        public void SleepingChunkDoesNotFlowOrDecayWithoutAnActiveNeighbor()
        {
            ElementWorldStore store = Store();
            ElementWorldChunk sleeping = Chunk(store, new ElementChunkKey(5, 0, 0));
            sleeping.ActivityState = ElementChunkActivityState.Sleeping;
            sleeping.SetCell(new Vector3Int(0, 1, 0), Water(80));
            sleeping.SetCell(new Vector3Int(1, 1, 0), Fire(20));

            Simulate(
                store,
                new ElementWorldChunk[0],
                down: 64,
                lateral: 16,
                decay: 3);

            Assert.That(sleeping.GetCell(new Vector3Int(0, 1, 0)).Amount, Is.EqualTo(80));
            Assert.That(sleeping.GetCell(new Vector3Int(1, 1, 0)).Amount, Is.EqualTo(20));
            Assert.That(store.ResidentChunkCount, Is.EqualTo(1));
        }

        [Test]
        public void UnchangedWaterChunkSettlesAndLaterWriteWakesIt()
        {
            ElementWorldStore store = Store();
            ElementWorldChunk source = ActiveChunk(store, new ElementChunkKey(0, 0, 0));
            source.SetCell(new Vector3Int(1, 1, 0), Water(7));
            source.SolidMask[ElementFieldCoordinates.ToIndex(
                new Vector3Int(1, 0, 0), source.Grid.Dimensions)] = true;

            Simulate(store, new[] { source }, down: 64, lateral: 16);
            Assert.That(source.RequiresSimulation, Is.True,
                "仅一个无变化 Tick 不能立即休眠，避免离散取整造成过早冻结。");

            Simulate(store, new[] { source }, down: 64, lateral: 16);
            Assert.That(source.RequiresSimulation, Is.False,
                "连续两个 Tick 没有任何 Cell 改变后，应把稳定水从 Solver 工作集移除。");

            var write = new ElementWriteRequest(
                worldPosition: new Vector3(1.5f, 1.5f, 0.5f),
                materialKind: MaterialId.Water,
                totalAmount: 1,
                radius: 0f,
                useLinearFalloff: false);
            Assert.That(ElementWorldWriteProcessor.TryApply(
                store,
                in write,
                Vector3.zero,
                cellSize: 1f,
                worldTick: 2,
                out _), Is.True);
            Assert.That(source.RequiresSimulation, Is.True,
                "法术再次写入稳定 Chunk 时必须唤醒 Solver，不能只修改数据而保持休眠。");
        }

        [Test]
        public void SameWorldInputProducesSameChecksum()
        {
            Assert.That(RunDeterministicScenario(), Is.EqualTo(RunDeterministicScenario()));
        }

        [Test]
        public void GpuPbfModeSkipsResidualCellWaterButKeepsFireDecayAndCommit()
        {
            ElementWorldStore store = Store();
            ElementWorldChunk source = ActiveChunk(store, new ElementChunkKey(0, 0, 0));
            source.SetCell(new Vector3Int(1, 1, 0), Water(64));
            source.SetCell(new Vector3Int(0, 1, 0), Fire(20));
            var simulator = new ElementWorldSimulator(16, ReactionCatalog());
            ElementFieldSimulationSettings settings = Settings(
                down: 64,
                lateral: 16,
                decay: 3,
                simulateCellWater: false);

            ElementFieldSimulationStats stats = simulator.SimulateActiveChunks(
                store,
                new[] { source },
                activeChunkCount: 1,
                worldTick: 1,
                deltaTime: 1f,
                in settings);

            Assert.That(store.TryGetChunk(new ElementChunkKey(1, 0, 0), out _), Is.False,
                "PBF 模式不能因残留 Water 在 PrepareNeighbors 阶段创建 Cell Chunk。");
            Assert.That(source.GetCell(new Vector3Int(1, 1, 0)).Amount, Is.EqualTo(64));
            Assert.That(source.GetCell(new Vector3Int(0, 1, 0)).Amount, Is.EqualTo(17));
            Assert.That(stats.WaterTransfers, Is.Zero);
            Assert.That(stats.ReactionPairs, Is.Zero);
            Assert.That(stats.ChangedCells, Is.GreaterThan(0),
                "Fire Decay 的变化仍必须在 Commit/activity 路径发布。");
        }

        private static uint RunDeterministicScenario()
        {
            ElementWorldStore store = Store();
            ElementWorldChunk first = ActiveChunk(store, new ElementChunkKey(0, 0, 0));
            ElementWorldChunk second = ActiveChunk(store, new ElementChunkKey(1, 0, 0));
            first.SetCell(new Vector3Int(1, 1, 0), Water(120));
            first.SetCell(new Vector3Int(1, 1, 1), Water(80));
            second.SetCell(new Vector3Int(0, 1, 0), Fire(60));

            var simulator = new ElementWorldSimulator(16, ReactionCatalog());
            ElementWorldChunk[] active = { second, first };
            ElementFieldSimulationSettings settings = Settings(64, 16, 1);
            for (long tick = 1; tick <= 4; tick++)
            {
                simulator.SimulateActiveChunks(
                    store, active, active.Length, tick, 0.1f, in settings);
            }

            uint checksum = 2166136261u;
            for (int z = 0; z < 2; z++)
            for (int y = -2; y < 4; y++)
            for (int x = -2; x < 6; x++)
            {
                ElementCell cell = store.GetCellOrEmpty(new Vector3Int(x, y, z));
                checksum = (checksum ^ (byte)cell.MaterialKind) * 16777619u;
                checksum = (checksum ^ cell.Amount) * 16777619u;
            }

            return checksum;
        }

        private static ElementFieldSimulationStats Simulate(
            ElementWorldStore store,
            ElementWorldChunk[] activeChunks,
            byte down = 0,
            byte lateral = 0,
            byte decay = 0,
            float deltaTime = 0.1f)
        {
            var simulator = new ElementWorldSimulator(16, ReactionCatalog());
            ElementFieldSimulationSettings settings = Settings(down, lateral, decay);
            return simulator.SimulateActiveChunks(
                store,
                activeChunks,
                activeChunks.Length,
                worldTick: 1,
                deltaTime,
                in settings);
        }

        private static ElementWorldStore Store() =>
            new ElementWorldStore(chunkSize: 2, maximumResidentChunks: 16);

        private static MaterialReactionCatalogSnapshot ReactionCatalog()
        {
            MaterialCatalog materials = AssetDatabase.LoadAssetAtPath<MaterialCatalog>(
                "Assets/_Project/ScriptableObjects/Materials/MaterialCatalog_Default.asset");
            MaterialReactionBindingProfile bindings =
                AssetDatabase.LoadAssetAtPath<MaterialReactionBindingProfile>(
                    "Assets/_Project/ScriptableObjects/Combat/Reactions/MaterialReactionBindings_Default.asset");
            Assert.That(materials, Is.Not.Null);
            Assert.That(bindings, Is.Not.Null);
            return bindings.CreateSnapshot(materials.CreateSnapshot());
        }

        private static ElementWorldChunk ActiveChunk(
            ElementWorldStore store,
            ElementChunkKey key)
        {
            ElementWorldChunk chunk = Chunk(store, key);
            chunk.ActivityState = ElementChunkActivityState.Active;
            return chunk;
        }

        private static ElementWorldChunk Chunk(
            ElementWorldStore store,
            ElementChunkKey key)
        {
            Assert.That(store.TryGetOrCreateChunk(key, out ElementWorldChunk chunk), Is.True);
            return chunk;
        }

        private static ElementFieldSimulationSettings Settings(
            byte down,
            byte lateral,
            byte decay,
            bool simulateCellWater = true)
        {
            return new ElementFieldSimulationSettings(
                cellSize: 1f,
                maxDownFlowPerTick: down,
                maxLateralFlowPerTick: lateral,
                fireDecayPerTick: decay,
                chunkSize: 2,
                simulateCellWater,
                new ExtinguishTuning
                {
                    FormalThreshold = 10f,
                    LowRatePerSecond = 100f,
                    FormalRatePerSecond = 100f,
                });
        }

        private static ElementCell Water(byte amount) =>
            new ElementCell(MaterialId.Water, amount);

        private static ElementCell Fire(byte amount) =>
            new ElementCell(MaterialId.Fire, amount);

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
