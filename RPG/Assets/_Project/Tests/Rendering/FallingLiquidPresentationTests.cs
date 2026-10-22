using System;
using System.Reflection;
using Game.ElementField;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Rendering.Tests
{
    public sealed class FallingLiquidPresentationTests
    {
        private sealed class World:IDisposable
        {
            private readonly GameObject _host=new GameObject("Small Falling Liquid Test");
            private readonly LiquidWorldProfile _profile=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<LiquidWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_P7_DemoRun_Grid.asset"));
            internal readonly LiquidWorldRuntime Liquid;
            internal readonly GridLiquidFieldFrame Frame;
            internal World(int capacity=64)
            {
                _host.SetActive(false);_host.hideFlags=HideFlags.DontSave;
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":16,\"_maximumActiveCells\":128,\"_enableAutoStableDetection\":false}",_profile);
                Liquid=_host.AddComponent<LiquidWorldRuntime>();
                typeof(LiquidWorldRuntime).GetField("_profile",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(Liquid,_profile);
                Assert.That(Liquid.TryInitialize(Vector3.one*100,.25f,8,_host.AddComponent<ElementFieldSolidBaker>()),Is.True);
                Frame=new GridLiquidFieldFrame(Liquid,32,128,.8f,1.8f);Frame.ConfigureFallingDrops(capacity);
            }
            public void Dispose(){UnityEngine.Object.DestroyImmediate(_host);UnityEngine.Object.DestroyImmediate(_profile);}
        }
        private static double Volume(GridLiquidSmallDrop drop)=>4*Math.PI/3*drop.Radius*drop.Radius*drop.Radius;
        [Test]
        public void TinyCondensateUsesOneMassScaledDropInsteadOfAThinProxySheet()
        {
            using var world=new World();var cell=new Vector3Int(0,3,0);
            world.Liquid.TryAddInitial(cell,MaterialId.Water,2,false);
            // 首次写入的 Previous=0 表示出生，不能把出生渐入误当作已有水量闪烁。
            // 不唤醒 Solver，下一 Tick 清除出生 History 后才是两帧相同的静止量。
            world.Liquid.Step(.1f);world.Frame.Capture();var drops=new GridLiquidSmallDrop[64];
            double expected=2d/world.Liquid.CellCapacityGmu*Math.Pow(world.Liquid.CellSize,3)*world.Frame.CartoonVolumeScale;
            for(int phase=0;phase<=4;phase++)
            {
                Assert.That(world.Frame.CopyFallingDrops(phase*.25f,drops),Is.EqualTo(1),"静止量的两个 Tick 不能重复叠画半量水滴。");
                Assert.That(Volume(drops[0]),Is.EqualTo(expected).Within(expected*.0001));
                Assert.That(drops[0].Radius,Is.LessThan(world.Liquid.CellSize*.2f),"几滴冷凝水不能成为宽大的空中液片。");
            }
            var sampler=new GridLiquidFieldSampler(world.Frame);
            Assert.That(sampler.Evaluate(drops[0].Position,1),Is.LessThan(0),"水滴实例和连续 Mesh 不能重复表现同一份量。");
            Assert.That(world.Liquid.TotalQuantity,Is.EqualTo(2));
        }
        [Test]
        public void SmallDropCapacityOverflowKeepsTheCompleteContinuousField()
        {
            using var world=new World(1);var drops=new GridLiquidSmallDrop[1];
            world.Liquid.TryAddInitial(new Vector3Int(0,3,0),MaterialId.Water,2,false);
            world.Liquid.TryAddInitial(new Vector3Int(4,3,0),MaterialId.Water,2,false);world.Frame.Capture();
            Assert.That(world.Frame.CopyFallingDrops(1,drops),Is.Zero);
            Assert.That(new GridLiquidFieldSampler(world.Frame).Evaluate(world.Liquid.Origin+new Vector3(.5f,3.5f,.5f)*world.Liquid.CellSize,1),Is.GreaterThan(0));
            Assert.That(world.Liquid.TotalQuantity,Is.EqualTo(4));
        }
        [Test]
        public void SmallDropInterpolationAndPackingAllocateNothingAfterWarmup()
        {
            using var world=new World();var drops=new GridLiquidSmallDrop[64];
            world.Liquid.TryAddInitial(new Vector3Int(0,3,0),MaterialId.Water,2,false);world.Liquid.Step(.1f);world.Frame.Capture();
            world.Frame.CopyFallingDrops(.5f,drops);long before=GC.GetAllocatedBytesForCurrentThread();int total=0;
            for(int i=0;i<300;i++)total+=world.Frame.CopyFallingDrops(i%5*.25f,drops);
            long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
            Assert.That(total,Is.EqualTo(300));Assert.That(allocated,Is.Zero);
        }
        [Test]
        public void FullDomainFallingSnapshotSurvivesGpuPageSelectionWithoutAVisibleBoundary()
        {
            using var world=new World();var motion=new GridLiquidFallingMotion[64];
            world.Liquid.TryAddInitial(new Vector3Int(0,3,0),MaterialId.Water,2,false);
            world.Liquid.TryAddInitial(new Vector3Int(64,3,0),MaterialId.Water,2,false);world.Liquid.Step(.1f);world.Frame.Capture();
            int count=world.Frame.CopyFallingMotion(motion);Assert.That(count,Is.EqualTo(2));
            double expected=4d/world.Liquid.CellCapacityGmu*Math.Pow(world.Liquid.CellSize,3)*world.Frame.CartoonVolumeScale;
            world.Frame.SelectGpuDomain(1,1);double volume=0;
            for(int i=0;i<count;i++)volume+=Volume(motion[i].Sample(.5f,world.Frame.Interval,world.Frame.CellSize,world.Frame.CartoonVolumeScale/world.Frame.Capacity));
            Assert.That(volume,Is.EqualTo(expected).Within(expected*.001),"CPU 远页的落水也要在完整域实例批次中保持同一外形和数量。");
        }
        [Test]
        public void NewlyBornDropFadesInWithItsQuantityWithoutAnOversizedMinimumRadius()
        {
            using var world=new World();var drops=new GridLiquidSmallDrop[64];
            world.Liquid.TryAddInitial(new Vector3Int(0,3,0),MaterialId.Water,2,false);world.Frame.Capture();
            double expected=2d/world.Liquid.CellCapacityGmu*Math.Pow(world.Liquid.CellSize,3)*world.Frame.CartoonVolumeScale;
            Assert.That(world.Frame.CopyFallingDrops(0,drops),Is.Zero);
            for(int phase=1;phase<=4;phase++)
            {
                Assert.That(world.Frame.CopyFallingDrops(phase*.25f,drops),Is.EqualTo(1));
                Assert.That(Volume(drops[0]),Is.EqualTo(expected*phase*.25).Within(expected*.001));
            }
            Assert.That(world.Liquid.TotalQuantity,Is.EqualTo(2),"渐入只影响显示，Gameplay 数量在出生时已存在。");
        }
        [Test]
        public void CopyToSmallerDropPoolFallsBackWithoutDroppingLiquidOrRescanningAir()
        {
            using var world=new World();
            world.Liquid.TryAddInitial(new Vector3Int(0,3,0),MaterialId.Water,2,false);
            world.Liquid.TryAddInitial(new Vector3Int(4,3,0),MaterialId.Water,2,false);world.Liquid.Step(.1f);world.Frame.Capture();
            var smaller=new GridLiquidFieldFrame(world.Liquid,32,128,.8f,1.8f,true);smaller.ConfigureFallingDrops(1);
            smaller.CopyFrom(world.Frame);
            Assert.That(smaller.CopyFallingDrops(1,new GridLiquidSmallDrop[1]),Is.Zero);
            Assert.That(new GridLiquidFieldSampler(smaller).Evaluate(world.Liquid.Origin+new Vector3(.5f,3.5f,.5f)*world.Liquid.CellSize,1),Is.GreaterThan(0));
            Assert.That(world.Frame.CopyFallingDrops(1,new GridLiquidSmallDrop[64]),Is.EqualTo(2),"目标快照兜底不能改写来源快照。");
            Assert.That(world.Liquid.TotalQuantity,Is.EqualTo(4));
        }
        [Test]
        public void TinyWaterOnActualGroundRemainsAContinuousPuddle()
        {
            using var world=new World();var floor=new GameObject("Small Condensate Floor");
            floor.transform.position=new Vector3(100,99.75f,100);floor.AddComponent<BoxCollider>().size=new Vector3(16,.5f,16);
            try
            {
                Physics.SyncTransforms();world.Liquid.TryAddInitial(Vector3Int.zero,MaterialId.Water,2,false);world.Frame.Capture();
                Assert.That(world.Frame.CopyFallingDrops(1,new GridLiquidSmallDrop[64]),Is.Zero,"接地少量水继续并入水滩，不能保留一颗悬浮圆珠。");
                Assert.That(world.Liquid.TotalQuantity,Is.EqualTo(2));
            }
            finally{UnityEngine.Object.DestroyImmediate(floor);}
        }
        [Test]
        public void FallingQuantityStaysConstantAcrossTickAndSubframeTransitions()
        {
            using var world=new World();var drops=new GridLiquidSmallDrop[64];
            world.Liquid.TryAddInitial(new Vector3Int(0,6,0),MaterialId.Water,2,false);
            double expected=2d/world.Liquid.CellCapacityGmu*Math.Pow(world.Liquid.CellSize,3)*world.Frame.CartoonVolumeScale;
            for(int tick=0;tick<20;tick++)
            {
                world.Liquid.Step(.1f);world.Frame.Capture();
                for(int phase=0;phase<=4;phase++)
                {
                    int count=world.Frame.CopyFallingDrops(phase*.25f,drops);double volume=0;
                    for(int i=0;i<count;i++)volume+=Volume(drops[i]);
                    Assert.That(volume,Is.EqualTo(expected).Within(expected*.001),"数量/半径在格转移时不能突然膨胀或缩水。");
                }
                Assert.That(world.Liquid.TotalQuantity,Is.EqualTo(2));
            }
        }
    }
}
