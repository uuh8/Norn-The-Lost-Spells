using Game.Materials;
using System.Collections.Generic;
using Game.ElementField;
using NUnit.Framework;
using UnityEngine;

namespace Game.Rendering.Tests
{
    /// <summary>
    /// 验证 Water Cell 到连续隐式体积的局部语义。
    /// 测试只依赖 IElementWorldReadOnly，不创建 Mesh、GameObject 或 Material，
    /// 便于把“水体内部在哪里”与后续“怎样提取 Triangle”分开定位。
    /// </summary>
    public sealed class WorldWaterImplicitFieldTests
    {
        [Test]
        public void SupportedPartialWaterIsInsideAboveFloorAndOutsideAboveItsSurface()
        {
            var world = new FakeWorld();
            world.SetSolid(new Vector3Int(0, -1, 0));
            world.SetWater(Vector3Int.zero, amount: 64);
            WaterVolumeMeshingSettings settings = CreateDefaultSettings();

            float justAboveFloor = WorldWaterImplicitField.Sample(
                world,
                new Vector3(0.5f, 0.03f, 0.5f),
                in settings);
            float aboveSurface = WorldWaterImplicitField.Sample(
                world,
                new Vector3(0.5f, 0.60f, 0.5f),
                in settings);

            Assert.That(justAboveFloor, Is.LessThan(0f),
                "Supported Water 必须从 Cell Bottom 向上填充，支撑面上方应属于水体内部。");
            Assert.That(aboveSurface, Is.GreaterThan(0f),
                "Amount=64 只形成部分高度，明显高于自由表面的位置必须在水体外部。");
        }

        [Test]
        public void UnsupportedPartialWaterHasVolumeAboveAndBelowCellCenter()
        {
            var world = new FakeWorld();
            world.SetWater(Vector3Int.zero, amount: 64);
            WaterVolumeMeshingSettings settings = CreateDefaultSettings();

            float belowCenter = WorldWaterImplicitField.Sample(
                world,
                new Vector3(0.5f, 0.35f, 0.5f),
                in settings);
            float aboveCenter = WorldWaterImplicitField.Sample(
                world,
                new Vector3(0.5f, 0.65f, 0.5f),
                in settings);

            Assert.That(belowCenter, Is.LessThan(0f),
                "空中水必须围绕 Cell Center 形成体积，不能只贴在 Cell Bottom。");
            Assert.That(aboveCenter, Is.LessThan(0f),
                "空中水在中心上方也应有体积，避免再次退化成水平薄片。");
        }

        [Test]
        public void AdjacentAirborneWaterIsConnectedAtSharedBoundary()
        {
            var world = new FakeWorld();
            world.SetWater(new Vector3Int(0, 0, 0), amount: 64);
            world.SetWater(new Vector3Int(1, 0, 0), amount: 64);
            WaterVolumeMeshingSettings settings = CreateDefaultSettings();

            float sharedBoundary = WorldWaterImplicitField.Sample(
                world,
                new Vector3(1f, 0.5f, 0.5f),
                in settings);

            Assert.That(sharedBoundary, Is.LessThan(0f),
                "相邻空中 Water Cell 必须在公共边界连成同一水团，不能成为两颗分离珠子。");
        }

        [Test]
        public void OpenAirDirectionExpandsWithAmount()
        {
            var lowAmountWorld = new FakeWorld();
            lowAmountWorld.SetWater(Vector3Int.zero, amount: 16);
            var highAmountWorld = new FakeWorld();
            highAmountWorld.SetWater(Vector3Int.zero, amount: byte.MaxValue);
            WaterVolumeMeshingSettings settings = CreateDefaultSettings();
            var openDirectionSample = new Vector3(0.94f, 0.5f, 0.5f);

            float lowAmountDistance = WorldWaterImplicitField.Sample(
                lowAmountWorld,
                openDirectionSample,
                in settings);
            float highAmountDistance = WorldWaterImplicitField.Sample(
                highAmountWorld,
                openDirectionSample,
                in settings);

            Assert.That(lowAmountDistance, Is.GreaterThan(0f),
                "低 Amount 水滴的开放方向不应膨胀到接近整个 Cell。");
            Assert.That(highAmountDistance, Is.LessThan(0f),
                "高 Amount 水滴应比低 Amount 更大，给玩家直观的水量反馈。");
        }

        [Test]
        public void SolidSampleNeverBecomesWaterInterior()
        {
            var world = new FakeWorld();
            world.SetWater(Vector3Int.zero, amount: byte.MaxValue);
            world.SetSolid(Vector3Int.zero);
            WaterVolumeMeshingSettings settings = CreateDefaultSettings();

            float solidCenter = WorldWaterImplicitField.Sample(
                world,
                new Vector3(0.5f, 0.5f, 0.5f),
                in settings);

            Assert.That(solidCenter, Is.GreaterThan(0f),
                "SolidMask 是几何边界；即使数据异常地同时残留 Water，Solid 内部也不能生成水面。");
        }

        [Test]
        public void WaterStackAboveSolidRemainsOneContinuousVolume()
        {
            var world = new FakeWorld();
            world.SetSolid(new Vector3Int(0, -1, 0));
            world.SetWater(new Vector3Int(0, 0, 0), amount: byte.MaxValue);
            world.SetWater(new Vector3Int(0, 1, 0), amount: 128);
            WaterVolumeMeshingSettings settings = CreateDefaultSettings();

            float layerBoundary = WorldWaterImplicitField.Sample(
                world,
                new Vector3(0.5f, 1f, 0.5f),
                in settings);
            float upperLayerInterior = WorldWaterImplicitField.Sample(
                world,
                new Vector3(0.5f, 1.20f, 0.5f),
                in settings);

            Assert.That(layerBoundary, Is.LessThan(0f),
                "上下 Water Cell 的交界必须位于同一连续体积内部，不能出现水平裂缝。");
            Assert.That(upperLayerInterior, Is.LessThan(0f),
                "下方 Water 能把上层水支撑为从 Cell Bottom 向上填充的容器式体积。");
        }

        private static WaterVolumeMeshingSettings CreateDefaultSettings()
        {
            return new WaterVolumeMeshingSettings(
                samplesPerCell: 2,
                supportedCornerRadius: 0.16f,
                minimumSupportedHeight: 0.08f,
                minimumAirborneRadius: 0.30f,
                maximumAirborneRadius: 0.48f,
                smoothUnionRadius: 0.16f);
        }

        private sealed class FakeWorld : IElementWorldReadOnly
        {
            private readonly Dictionary<Vector3Int, ElementCell> _cells =
                new Dictionary<Vector3Int, ElementCell>();
            private readonly HashSet<Vector3Int> _solid =
                new HashSet<Vector3Int>();

            public bool IsInitialized => true;
            public Vector3 Origin => Vector3.zero;
            public float CellSize => 1f;
            public int ChunkSize => 8;
            public int MaximumResidentChunkCount => 16;

            public int CopyVisibleChunkKeys(ElementChunkKey[] destination)
            {
                return 0;
            }

            public bool TryGetCell(Vector3Int globalCell, out ElementCell cell)
            {
                return _cells.TryGetValue(globalCell, out cell);
            }

            public bool IsSolid(Vector3Int globalCell)
            {
                return _solid.Contains(globalCell);
            }

            public uint GetChunkVersion(ElementChunkKey key)
            {
                return 1u;
            }

            public void SetWater(Vector3Int globalCell, byte amount)
            {
                _cells[globalCell] = new ElementCell(MaterialId.Water, amount);
            }

            public void SetSolid(Vector3Int globalCell)
            {
                _solid.Add(globalCell);
            }
        }
    }
}
