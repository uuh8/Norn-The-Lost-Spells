using Game.Materials;
using Game.Combat;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// Simulator 测试固定 Stage Pipeline 的可观察规则：反应、下落、横向均衡和衰减。
    /// 所有输入都是值数据，同一初始状态必须得到同一结果。
    /// </summary>
    public sealed class ElementFieldSimulatorTests
    {
        [Test]
        public void WaterFallsOneCellPerTick()
        {
            ElementGrid grid = Grid(new Vector3Int(1, 3, 1));
            grid.SetCell(new Vector3Int(0, 2, 0), Water(100));

            new ElementFieldSimulator().Tick(
                grid, new ElementWriteQueue(1), Vector3.zero, 0.1f, Settings(down: 64));

            Assert.That(grid.GetCell(0, 2, 0).Amount, Is.EqualTo(36));
            Assert.That(grid.GetCell(0, 1, 0).Amount, Is.EqualTo(64));
            Assert.That(grid.GetCell(0, 0, 0).IsEmpty, Is.True,
                "同一 Tick 新收到的 Water 不能再次成为 Source 向下穿透。 ");
        }

        [Test]
        public void WaterDoesNotEnterSolidCell()
        {
            ElementGrid grid = Grid(new Vector3Int(1, 2, 1));
            grid.SetCell(new Vector3Int(0, 1, 0), Water(100));
            grid.SetSolid(Vector3Int.zero, true);

            new ElementFieldSimulator().Tick(
                grid, new ElementWriteQueue(1), Vector3.zero, 0.1f, Settings(down: 64));

            Assert.That(grid.GetCell(0, 1, 0).Amount, Is.EqualTo(100));
            Assert.That(grid.GetCell(0, 0, 0).IsEmpty, Is.True);
        }

        [Test]
        public void HorizontalFlowConservesTotalAmount()
        {
            ElementGrid grid = Grid(new Vector3Int(3, 1, 1));
            grid.SetCell(new Vector3Int(1, 0, 0), Water(100));

            int before = Sum(grid, MaterialId.Water);
            ElementFieldSimulationStats stats = new ElementFieldSimulator().Tick(
                grid, new ElementWriteQueue(1), Vector3.zero, 0.1f, Settings(lateral: 16));

            Assert.That(Sum(grid, MaterialId.Water), Is.EqualTo(before));
            Assert.That(stats.WaterTransfers, Is.GreaterThan(0));
        }

        [Test]
        public void HorizontalFlowUsesStableFourNeighborDiffusionRate()
        {
            // 这是原始棋盘闪烁的最小复现：中心 64、四邻居 0、每方向上限 16。
            // 旧公式 difference/2 会向四边各送 16，使中心同一 Tick 从 64 变成 Empty；
            // 下一 Tick 周围又向中心回流，导致可见 Mesh 的 Cell 拓扑反复出现/消失。
            ElementGrid grid = Grid(new Vector3Int(3, 1, 3));
            grid.SetCell(new Vector3Int(1, 0, 1), Water(64));

            new ElementFieldSimulator().Tick(
                grid, new ElementWriteQueue(1), Vector3.zero, 0.1f, Settings(lateral: 16));

            // 四邻域显式扩散取 k=1/8：每条边传 64/8=8，总流出 32，中心仍保留 32。
            // k 小于二维四邻域的稳定上限 1/4，可衰减奇偶格高频振荡，而不是在临界点来回翻转。
            Assert.That(grid.GetCell(1, 0, 1).Amount, Is.EqualTo(32));
            Assert.That(grid.GetCell(0, 0, 1).Amount, Is.EqualTo(8));
            Assert.That(grid.GetCell(2, 0, 1).Amount, Is.EqualTo(8));
            Assert.That(grid.GetCell(1, 0, 0).Amount, Is.EqualTo(8));
            Assert.That(grid.GetCell(1, 0, 2).Amount, Is.EqualTo(8));
            Assert.That(Sum(grid, MaterialId.Water), Is.EqualTo(64),
                "稳定扩散只能重新分配 Amount，不能凭空增加或删除水量。");
        }

        [Test]
        public void StableLateralTransferDampsFourNeighborCheckerboardMode()
        {
            int transfer = ElementFieldSimulator.CalculateStableLateralTransfer(
                sourceAmount: 64,
                targetAmount: 0,
                maxTransfer: 16);

            Assert.That(transfer, Is.EqualTo(8),
                "四邻域扩散使用 k=1/8，使高频奇偶格误差随 Tick 衰减，而不是在 k=1/4 临界点振荡。");
        }

        [Test]
        public void WaterExtinguishesTwiceItsAmountOfFire()
        {
            ElementGrid grid = Grid(new Vector3Int(2, 1, 1));
            grid.SetCell(new Vector3Int(0, 0, 0), Water(100));
            grid.SetCell(new Vector3Int(1, 0, 0), Fire(100));

            ElementFieldSimulationStats stats = new ElementFieldSimulator().Tick(
                grid, new ElementWriteQueue(1), Vector3.zero, 1f, Settings());

            Assert.That(grid.GetCell(0, 0, 0).Amount, Is.EqualTo(50));
            Assert.That(grid.GetCell(1, 0, 0).IsEmpty, Is.True);
            Assert.That(stats.ReactionPairs, Is.EqualTo(1));
        }

        [Test]
        public void FireDecaysWithoutWater()
        {
            ElementGrid grid = Grid(Vector3Int.one);
            grid.SetCell(Vector3Int.zero, Fire(3));
            var simulator = new ElementFieldSimulator();
            var queue = new ElementWriteQueue(1);

            simulator.Tick(grid, queue, Vector3.zero, 0.1f, Settings(decay: 2));
            Assert.That(grid.GetCell(Vector3Int.zero).Amount, Is.EqualTo(1));

            simulator.Tick(grid, queue, Vector3.zero, 0.1f, Settings(decay: 2));
            Assert.That(grid.GetCell(Vector3Int.zero).IsEmpty, Is.True);
        }

        [Test]
        public void ChangedChunkVersionAdvancesOncePerTick()
        {
            ElementGrid grid = Grid(new Vector3Int(2, 1, 1), chunkSize: 1);
            var queue = new ElementWriteQueue(2);
            queue.TryEnqueue(new ElementWriteRequest(
                new Vector3(0.5f, 0.5f, 0.5f), MaterialId.Water,
                50, 0f, false));
            var simulator = new ElementFieldSimulator();

            simulator.Tick(grid, queue, Vector3.zero, 0.1f, Settings(chunkSize: 1));
            Assert.That(grid.GetChunkVersion(0, 0, 0), Is.EqualTo(1u));

            simulator.Tick(grid, queue, Vector3.zero, 0.1f, Settings(chunkSize: 1));
            Assert.That(grid.GetChunkVersion(0, 0, 0), Is.EqualTo(1u),
                "没有 Cell 变化的 Tick 不应制造假的 Dirty Version。 ");
        }

        [Test]
        public void SameGridAndWritesProduceSameChecksum()
        {
            uint first = SimulateDeterministicScenario();
            uint second = SimulateDeterministicScenario();
            Assert.That(second, Is.EqualTo(first));
        }

        [Test]
        public void InvalidTickDoesNotConsumePendingWrite()
        {
            ElementGrid grid = Grid(Vector3Int.one);
            var queue = new ElementWriteQueue(1);
            queue.TryEnqueue(new ElementWriteRequest(
                new Vector3(0.5f, 0.5f, 0.5f),
                MaterialId.Water,
                10,
                0f,
                false));

            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                new ElementFieldSimulator().Tick(
                    grid, queue, Vector3.zero, -0.1f, Settings()));

            Assert.That(queue.Count, Is.EqualTo(1),
                "参数错误必须在消费 Command Queue 前失败，修正参数后才能安全重试。 ");
        }

        private static uint SimulateDeterministicScenario()
        {
            ElementGrid grid = Grid(new Vector3Int(5, 3, 5));
            var queue = new ElementWriteQueue(4);
            queue.TryEnqueue(new ElementWriteRequest(
                new Vector3(2.5f, 2.5f, 2.5f),
                MaterialId.Water,
                300,
                1.5f,
                true));
            var simulator = new ElementFieldSimulator();
            ElementFieldSimulationSettings settings = Settings(down: 64, lateral: 16);

            for (int i = 0; i < 6; i++)
                simulator.Tick(grid, queue, Vector3.zero, 0.1f, in settings);

            uint checksum = 2166136261u;
            for (int i = 0; i < grid.CellCount; i++)
            {
                ElementCell cell = grid.GetCell(ElementFieldCoordinates.FromIndex(i, grid.Dimensions));
                checksum = (checksum ^ (byte)cell.MaterialKind) * 16777619u;
                checksum = (checksum ^ cell.Amount) * 16777619u;
            }

            return checksum;
        }

        private static ElementGrid Grid(Vector3Int dimensions, int chunkSize = 2)
        {
            return new ElementGrid(dimensions, maximumCellCount: 1024, chunkSize);
        }

        private static ElementFieldSimulationSettings Settings(
            byte down = 0,
            byte lateral = 0,
            byte decay = 0,
            int chunkSize = 2)
        {
            return new ElementFieldSimulationSettings(
                cellSize: 1f,
                maxDownFlowPerTick: down,
                maxLateralFlowPerTick: lateral,
                fireDecayPerTick: decay,
                chunkSize,
                new ExtinguishTuning
                {
                    FormalThreshold = 10f,
                    LowRatePerSecond = 100f,
                    FormalRatePerSecond = 100f,
                    FireRemovedPerWater = 2f,
                });
        }

        private static ElementCell Water(byte amount) =>
            new ElementCell(MaterialId.Water, amount);

        private static ElementCell Fire(byte amount) =>
            new ElementCell(MaterialId.Fire, amount);

        private static int Sum(ElementGrid grid, MaterialId kind)
        {
            int total = 0;
            for (int i = 0; i < grid.CellCount; i++)
            {
                ElementCell cell = grid.GetCell(ElementFieldCoordinates.FromIndex(i, grid.Dimensions));
                if (cell.MaterialKind == kind)
                    total += cell.Amount;
            }

            return total;
        }
    }
}
