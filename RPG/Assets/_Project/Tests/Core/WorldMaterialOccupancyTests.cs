using System;
using System.Reflection;
using NUnit.Framework;
using Unity.Profiling;
using UnityEngine;

namespace Game.Core.Tests
{
    /// <summary>直接验证生产规范化器；缺少类型先形成RED，反射分配只属于测试。</summary>
    public sealed class WorldMaterialOccupancyTests
    {
        private object _snapshot;
        private Type _type;
        [SetUp] public void SetUp()
        {
            _type = typeof(GameLog).Assembly.GetType("Game.Core.WorldMaterialOccupancySnapshot");
            Assert.That(_type, Is.Not.Null, "缺少材料逻辑占据规范化器");
            _snapshot = Activator.CreateInstance(_type, 4, 128);
        }
        private bool Capture(params WorldMaterialCellSample[] cells)
            => (bool)_type.GetMethod("TryCapture").Invoke(_snapshot, new object[] { cells, cells.Length });
        private T Read<T>(string name) => (T)_type.GetProperty(name).GetValue(_snapshot);
        private WorldMaterialCellSample Cell(int index)
            => (WorldMaterialCellSample)_type.GetMethod("GetCell").Invoke(_snapshot, new object[] { index });
        private static WorldMaterialCellSample Sample(uint quantity, WorldMaterialStorage storage = WorldMaterialStorage.Dynamic,
            int x = -1, byte material = 1, WorldMaterialDomain domain = WorldMaterialDomain.Liquid)
            => new WorldMaterialCellSample(domain, storage, new Vector3Int(x, 0, 0), material, quantity);

        [Test] public void StableDynamicHandoff_DoesNotInventLogicalChange()
        {
            Assert.That(Capture(Sample(100, WorldMaterialStorage.Stable)), Is.True);
            Assert.That(Read<bool>("Changed"), Is.True);
            Assert.That(Capture(Sample(40, WorldMaterialStorage.Stable), Sample(60)), Is.True);
            Assert.That(Read<bool>("Changed"), Is.False);
            Assert.That(Read<int>("Count"), Is.EqualTo(1));
            Assert.That(Cell(0).Quantity, Is.EqualTo(100));
        }
        [Test] public void EnumerationOrder_DoesNotChangeSnapshot_AndInputIsNotBorrowed()
        {
            var input = new[] { Sample(20, x: 3), Sample(10, x: -2) };
            Assert.That(Capture(input), Is.True);
            input[0] = Sample(99);
            Assert.That(Cell(0).Cell.x, Is.EqualTo(-2));
            Assert.That(Capture(Sample(10, x: -2), Sample(20, x: 3)), Is.True);
            Assert.That(Read<bool>("Changed"), Is.False);
        }
        [Test] public void QuantityMaterialAndDomain_StayDistinct()
        {
            Assert.That(Capture(Sample(10), Sample(20, material: 3), Sample(30, WorldMaterialStorage.Resident, domain: WorldMaterialDomain.Gas)), Is.True);
            Assert.That(Read<int>("Count"), Is.EqualTo(3));
            Assert.That(Capture(Sample(11), Sample(20, material: 3), Sample(30, WorldMaterialStorage.Resident, domain: WorldMaterialDomain.Gas)), Is.True);
            Assert.That(Read<bool>("Changed"), Is.True);
        }
        [Test] public void Overflow_RejectsWithoutReplacingPublishedData()
        {
            Capture(Sample(7));
            Assert.That(Capture(Sample(uint.MaxValue), Sample(1, WorldMaterialStorage.Stable)), Is.False);
            Assert.That(Cell(0).Quantity, Is.EqualTo(7));
        }
        [Test] public void CapacityFailure_RejectsWholeCandidate()
        {
            Capture(Sample(7));
            Assert.That(Capture(Sample(1, x: 1), Sample(1, x: 2), Sample(1, x: 3), Sample(1, x: 4), Sample(1, x: 5)), Is.False);
            Assert.That(Read<int>("Count"), Is.EqualTo(1));
            Assert.That(Cell(0).Quantity, Is.EqualTo(7));
        }
        [Test] public void InspectionBudget_CountsInputEvenWhenAllQuantitiesAreZero()
        {
            _snapshot = Activator.CreateInstance(_type, 4, 2);
            Assert.That(Capture(Sample(0), Sample(0), Sample(0)), Is.False);
            Assert.That(Read<int>("Count"), Is.EqualTo(0));
        }
        [Test] public void InvalidIdentity_RejectsWithoutReplacingPublishedData()
        {
            Capture(Sample(7));
            Assert.That(Capture(Sample(1, material: 0)), Is.False);
            Assert.That(Cell(0).Quantity, Is.EqualTo(7));
            Assert.That(Capture(Sample(1, domain: (WorldMaterialDomain)255)), Is.False);
        }
        [Test] public void EmptyCandidate_RemovesOccupancy_ThenIsStable()
        {
            Capture(Sample(7));
            Assert.That(Capture(), Is.True);
            Assert.That(Read<bool>("Changed"), Is.True);
            Assert.That(Read<int>("Count"), Is.EqualTo(0));
            Assert.That(Capture(Sample(0)), Is.True);
            Assert.That(Read<bool>("Changed"), Is.False);
        }
        [Test] public void DuplicateOwner_IsRejected_AndNextCandidateIsClean()
        {
            Capture(Sample(7));
            Assert.That(Capture(Sample(3), Sample(4)), Is.False);
            Assert.That(Cell(0).Quantity, Is.EqualTo(7));
            Assert.That(Capture(Sample(9, WorldMaterialStorage.Stable)), Is.True);
            Assert.That(Cell(0).Quantity, Is.EqualTo(9));
        }
        [Test] public void FireStrength_IsNotSummedOrAcceptedAboveByteRange()
        {
            Assert.That(Capture(Sample(100, WorldMaterialStorage.Resident, material: 2, domain: WorldMaterialDomain.LegacyCell)), Is.True);
            Assert.That(Capture(Sample(256, WorldMaterialStorage.Resident, material: 2, domain: WorldMaterialDomain.LegacyCell)), Is.False);
            Assert.That(Cell(0).Quantity, Is.EqualTo(100));
        }
        [Test] public void SortingBudgetFailure_PreservesPublishedSnapshot()
        {
            _snapshot = Activator.CreateInstance(_type, 4, 3);
            Assert.That(Capture(Sample(7)), Is.True);
            Assert.That(Capture(Sample(2, x: 2), Sample(1, x: 1)), Is.False);
            Assert.That(Read<int>("Count"), Is.EqualTo(1));
            Assert.That(Cell(0).Quantity, Is.EqualTo(7));
        }
        [Test] public void ExtremeCoordinates_AreComparedWithoutSubtractionOverflow()
        {
            Assert.That(Capture(Sample(1, x: int.MaxValue), Sample(2, x: int.MinValue)), Is.True);
            Assert.That(Cell(0).Cell.x, Is.EqualTo(int.MinValue));
            Assert.That(Cell(1).Cell.x, Is.EqualTo(int.MaxValue));
        }
        [Test] public void StableDuplicatesAndRepeatedHandoffOwner_AreRejected()
        {
            Capture(Sample(7));
            Assert.That(Capture(Sample(3, WorldMaterialStorage.Stable), Sample(4, WorldMaterialStorage.Stable)), Is.False);
            Assert.That(Capture(Sample(3, WorldMaterialStorage.Stable), Sample(4), Sample(1)), Is.False);
            Assert.That(Capture(Sample(3, WorldMaterialStorage.Stable), Sample(4), Sample(1, WorldMaterialStorage.Stable)), Is.False);
            Assert.That(Cell(0).Quantity, Is.EqualTo(7));
        }
        [TestCase(WorldMaterialDomain.Liquid, WorldMaterialStorage.Resident)]
        [TestCase(WorldMaterialDomain.Gas, WorldMaterialStorage.Dynamic)]
        [TestCase(WorldMaterialDomain.Solid, WorldMaterialStorage.Dynamic)]
        public void InvalidStorage_DoesNotPublish(WorldMaterialDomain domain, WorldMaterialStorage storage)
        {
            Capture(Sample(7));
            Assert.That(Capture(Sample(1, storage, domain: domain)), Is.False);
            Assert.That(Cell(0).Quantity, Is.EqualTo(7));
        }
        [Test] public void FinalComparisonBudgetFailure_PreservesChangedAndCanRecover()
        {
            // 单元素扫描+归并占2个工作单位，最终与旧发布值比较需要第3个。
            _snapshot = Activator.CreateInstance(_type, 4, 2);
            Assert.That(Capture(Sample(7)), Is.True);
            Assert.That(Capture(Sample(7)), Is.False);
            Assert.That(Read<bool>("Changed"), Is.True);
            Assert.That(Cell(0).Quantity, Is.EqualTo(7));
            Assert.That(Capture(), Is.True);
            Assert.That(Capture(Sample(8)), Is.True);
            Assert.That(Cell(0).Quantity, Is.EqualTo(8));
        }
        [Test] public void WarmNormalizeAndCompare_HaveZeroPositivelyCalibratedAllocationEvents()
        {
            // 直接调用生产API；反射/NUnit断言/构造均放在计量窗口外。
            var snapshot = new WorldMaterialOccupancySnapshot(4, 128);
            var split = new[] { Sample(40, WorldMaterialStorage.Stable), Sample(60),
                Sample(10, WorldMaterialStorage.Resident, domain: WorldMaterialDomain.Gas) };
            var merged = new[] { split[2], Sample(100) };
            for (int i = 0; i < 16; i++) { Assert.True(snapshot.TryCapture(split, 3)); Assert.True(snapshot.TryCapture(merged, 2)); }
            using var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 128,
                ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
            Assert.True(recorder.Valid);
            recorder.Stop(); recorder.Reset(); recorder.Start();
            var positive = new byte[1024]; GC.KeepAlive(positive);
            recorder.Stop(); int calibration = recorder.Count;
            Assert.That(calibration, Is.GreaterThan(0)); Assert.That(calibration, Is.LessThan(recorder.Capacity));
            bool succeeded = true;
            recorder.Reset(); recorder.Start();
            for (int i = 0; i < 256; i++)
            {
                succeeded &= snapshot.TryCapture(split, 3) && snapshot.GetCell(0).Quantity == 100;
                succeeded &= snapshot.TryCapture(merged, 2) && !snapshot.Changed;
            }
            recorder.Stop(); int events = recorder.Count;
            Assert.True(succeeded); Assert.That(events, Is.Zero, "仅规范化方法范围，不能代替Native全帧验收。");
            TestContext.WriteLine("GC.Alloc calibration=" + calibration + ", normalize=" + events);
        }
        [Test] public void ReverseOrderedChunk_FitsBoundedSortingWorkWithoutQuadraticCost()
        {
            // 真实冻结块会包含上千个native Cell。反序输入使插入排序达到平方最坏情况，
            // 50000检查预算应足够O(n log n)实现，不能为通过测试无限放宽帧工作量。
            var snapshot=new WorldMaterialOccupancySnapshot(1024,50000);
            var values=new WorldMaterialCellSample[1000];
            for(int i=0;i<values.Length;i++)values[i]=Sample(1,x:999-i);
            Assert.True(snapshot.TryCapture(values,values.Length));
            Assert.AreEqual(1000,snapshot.Count);Assert.AreEqual(0,snapshot.GetCell(0).Cell.x);
            Assert.AreEqual(999,snapshot.GetCell(999).Cell.x);
        }
    }
}
