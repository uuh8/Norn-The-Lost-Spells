using System;
using System.Reflection;
using Game.Core;
using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    /// <summary>真实Store/Solver行为回归：在途液体仍是原有GMU，碰撞只截断速度而不删除资源。</summary>
    public sealed class LiquidPourFlightTests
    {
        private static bool Launch(LiquidFluxSolver solver, Vector3Int cell, Vector3 velocity)
        {
            var method=typeof(LiquidFluxSolver).GetMethod("TrySetInitialVelocity",BindingFlags.Instance|BindingFlags.Public);
            Assert.That(method,Is.Not.Null,"洒出必须进入原Solver，不能只把材料瞬移到预览落点。");
            return (bool)method.Invoke(solver,new object[]{cell,MaterialId.Water,velocity,0u});
        }
        private static LiquidFluxSolver Solver(LiquidVolumeStore store,out LiquidActivityScheduler activity,int max=64)
        {
            activity=new LiquidActivityScheduler(128,store);
            return new LiquidFluxSolver(store,activity,null,new[]{MaterialId.Water},new[]{255},new[]{0},new[]{8},.25f,9.81f,12,max);
        }
        [Test] public void NozzleLiquidMovesSeveralHorizontalCellsWithConservedQuantity()
        {
            var store=new LiquidVolumeStore(8,16,255,new[]{MaterialId.Water},cell=>false);
            var solver=Solver(store,out _);var cell=new Vector3Int(0,12,0);
            Assert.That(store.TryAdd(cell,MaterialId.Water,40),Is.True);
            Assert.That(Launch(solver,cell,new Vector3(8,2,0)),Is.True);
            solver.Step(128,.1f,.1f);
            Assert.That(store.TryGetQuantity(new Vector3Int(3,12,0),MaterialId.Water,out var quantity),Is.True);
            Assert.That(quantity,Is.EqualTo(40));Assert.That(store.TotalQuantity,Is.EqualTo(40));
        }
        [Test] public void UpwardLaunchRisesThenFallsInsteadOfClampingItsLiftAway()
        {
            var store=new LiquidVolumeStore(8,16,255,new[]{MaterialId.Water},cell=>false);
            var solver=Solver(store,out _);var cell=new Vector3Int(0,12,0);
            store.TryAdd(cell,MaterialId.Water,40);Assert.That(Launch(solver,cell,new Vector3(0,4,0)),Is.True);
            solver.Step(128,.1f,.1f);
            Assert.That(store.TryGetQuantity(cell+Vector3Int.up,MaterialId.Water,out var q),Is.True);Assert.That(q,Is.EqualTo(40));
            for(int i=2;i<=16;i++)solver.Step(128,.1f,i*.1f);
            Assert.That(store.TryGetQuantity(cell,MaterialId.Water,out _),Is.False,"超过峰值后必须受原重力下落。");
            Assert.That(store.TotalQuantity,Is.EqualTo(40));
        }
        [Test] public void FastHorizontalFlightCannotTunnelThroughOneCellWall()
        {
            var store=new LiquidVolumeStore(8,16,255,new[]{MaterialId.Water},cell=>cell.x==2);
            var solver=Solver(store,out _);var cell=new Vector3Int(0,12,0);
            store.TryAdd(cell,MaterialId.Water,40);Assert.That(Launch(solver,cell,new Vector3(8,0,0)),Is.True);
            solver.Step(128,.1f,.1f);
            Assert.That(store.TryGetQuantity(new Vector3Int(1,12,0),MaterialId.Water,out var q),Is.True);Assert.That(q,Is.EqualTo(40));
            Assert.That(store.TryGetQuantity(new Vector3Int(3,12,0),MaterialId.Water,out _),Is.False);
            Assert.That(store.TotalQuantity,Is.EqualTo(40));
        }
        [Test] public void CeilingStopsRiseWithoutDroppingAnyLiquid()
        {
            var store=new LiquidVolumeStore(8,16,255,new[]{MaterialId.Water},cell=>cell.y>=14);
            var solver=Solver(store,out _);var cell=new Vector3Int(0,12,0);
            store.TryAdd(cell,MaterialId.Water,40);Assert.That(Launch(solver,cell,new Vector3(0,10,0)),Is.True);
            solver.Step(128,.1f,.1f);
            Assert.That(store.TryGetQuantity(new Vector3Int(0,13,0),MaterialId.Water,out var q),Is.True);Assert.That(q,Is.EqualTo(40));
            Assert.That(store.TryGetQuantity(new Vector3Int(0,14,0),MaterialId.Water,out _),Is.False);Assert.That(store.TotalQuantity,Is.EqualTo(40));
        }
        [Test] public void InvalidVelocityAndExhaustedMotionSlotsKeepStoreUnchanged()
        {
            var store=new LiquidVolumeStore(8,16,255,new[]{MaterialId.Water},cell=>false);
            var solver=Solver(store,out _,1);var cell=new Vector3Int(0,12,0);
            store.TryAdd(cell,MaterialId.Water,40);store.TryAdd(cell+Vector3Int.right,MaterialId.Water,40);
            Assert.That(Launch(solver,cell,new Vector3(float.NaN,0,0)),Is.False);
            Assert.That(Launch(solver,cell,new Vector3(8,2,0)),Is.True);
            Assert.That(Launch(solver,cell+Vector3Int.right,new Vector3(8,2,0)),Is.False);
            Assert.That(store.TotalQuantity,Is.EqualTo(80));
        }
        private sealed class Source:IMaterialTransferSource
        {
            public bool Committed,Reserved;
            public bool TryReserve(MaterialAmount[] amounts,int count,out MaterialTransferFailure failure){Reserved=true;failure=MaterialTransferFailure.None;return true;}
            public void Commit(){Committed=true;}public void Cancel(){Reserved=false;}
        }
        private static void SetMotion(MaterialDepositExchange exchange,Vector3 velocity)
        {
            var flag=typeof(MaterialDepositExchange).GetField("HasInitialMotion");
            Assert.That(flag,Is.Not.Null,"运动必须经原库存事务传递。");
            flag.SetValue(exchange,true);typeof(MaterialDepositExchange).GetField("InitialVelocity").SetValue(exchange,velocity);
        }
        [Test] public void MotionDepositSetsVelocityOnlyAfterAtomicQuantityCommit()
        {
            using var world=new MaterialInventoryTransferTests.World();var source=new Source();
            var exchange=new MaterialDepositExchange{Position=Vector3.one*100.5f,Radius=.2f,Source=source,Count=1,Amounts=new[]{new MaterialAmount(1,40)}};
            SetMotion(exchange,new Vector3(3,2,0));
            Assert.That(new MaterialInventoryDepositPort(world.Runtime).Deposit(exchange,out var q),Is.EqualTo(MaterialTransferFailure.None));
            Assert.That(q,Is.EqualTo(40));Assert.That(source.Committed,Is.True);Assert.That(world.Liquid.TotalQuantity,Is.EqualTo(40));
            Assert.That(world.Liquid.TryGetFallingMotion(Vector3Int.zero,MaterialId.Water,out var motion),Is.True);
            Assert.That(motion.HorizontalVelocityWorld.x,Is.EqualTo(3));Assert.That(motion.Velocity,Is.EqualTo(-2));
        }
        [Test] public void RejectedMixedDepositKeepsQuantityAndDoesNotLeaveMotion()
        {
            using var world=new MaterialInventoryTransferTests.World();var source=new Source();
            var exchange=new MaterialDepositExchange{Position=Vector3.one*100.5f,Radius=.2f,Source=source,Count=2,Amounts=new[]{new MaterialAmount(1,40),new MaterialAmount(7,10000)}};
            SetMotion(exchange,new Vector3(3,2,0));
            Assert.That(new MaterialInventoryDepositPort(world.Runtime).Deposit(exchange,out _),Is.EqualTo(MaterialTransferFailure.Capacity));
            Assert.That(source.Committed,Is.False);Assert.That(source.Reserved,Is.False);Assert.That(world.Liquid.TotalQuantity,Is.Zero);
            Assert.That(world.Liquid.TryGetFallingMotion(Vector3Int.zero,MaterialId.Water,out _),Is.False);
        }
        [Test] public void BlockedNozzleCannotSearchAcrossTheWallOrConsumeTheSource()
        {
            using var world=new MaterialInventoryTransferTests.World();var source=new Source();
            // 先读空位，再放固体，确保测试能覆盖旧空格缓存而不仅是首次采样。
            Assert.That(world.Liquid.ConversionFreeCapacity(Vector3Int.zero),Is.GreaterThan(0));
            world.Solid.BeginConversionGeometry();Assert.That(world.Solid.StageConversionAdd(Vector3Int.zero,MaterialId.Ice,255),Is.True);
            world.Solid.PublishConversionGeometry();
            var exchange=new MaterialDepositExchange{Position=Vector3.one*100.5f,Radius=.8f,Source=source,Count=1,Amounts=new[]{new MaterialAmount(1,40)}};
            SetMotion(exchange,new Vector3(3,2,0));
            Assert.That(new MaterialInventoryDepositPort(world.Runtime).Deposit(exchange,out _),Is.EqualTo(MaterialTransferFailure.Capacity));
            Assert.That(source.Committed,Is.False);Assert.That(world.Liquid.TotalQuantity,Is.Zero);
        }
        [Test] public void PendingVelocityWithNoMotionCapacityRetainsQuantityForLaterRetry()
        {
            using var world=new MaterialInventoryTransferTests.World();
            var field=typeof(LiquidWorldRuntime).GetField("_solver",BindingFlags.Instance|BindingFlags.NonPublic);
            var store=(LiquidVolumeStore)typeof(LiquidWorldRuntime).GetField("_store",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(world.Liquid);
            var activity=(LiquidActivityScheduler)typeof(LiquidWorldRuntime).GetField("_activity",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(world.Liquid);
            var solver=new LiquidFluxSolver(store,activity,null,new[]{MaterialId.Water},new[]{255},new[]{0},new[]{8},1,9.81f,12,1);
            field.SetValue(world.Liquid,solver);
            var active=new Vector3Int(0,2,0);Assert.That(world.Liquid.TryAddInitial(active,MaterialId.Water,40),Is.True);
            Assert.That(Launch(solver,active,new Vector3(1,0,0)),Is.True);
            Assert.That(world.Liquid.TryEnqueueDeposit(new ElementWriteRequest(new Vector3(102.5f,102.5f,100.5f),MaterialId.Water,40,0,false,new Vector3(3,2,0))),Is.True);
            world.Liquid.Step(.1f,.1f);
            Assert.That(world.Liquid.PendingQuantity,Is.EqualTo(40),"运动槽不足必须保留原Pending请求，不能把带速度的水偷偷静置。");
            Assert.That(world.Liquid.TotalQuantity+world.Liquid.PendingQuantity,Is.EqualTo(80));
        }
        [Test] public void TwoSameTickDepositsIntoAWetCellConserveWeightedMomentum()
        {
            var store=new LiquidVolumeStore(8,16,255,new[]{MaterialId.Water},cell=>false);
            var solver=Solver(store,out _);var cell=new Vector3Int(0,12,0);
            store.TryAdd(cell,MaterialId.Water,40);Assert.That(Launch(solver,cell,new Vector3(8,2,0)),Is.True);
            store.TryAdd(cell,MaterialId.Water,40);
            Assert.That(solver.TrySetInitialVelocity(cell,MaterialId.Water,new Vector3(0,4,0),40),Is.True);
            Assert.That(solver.TryGetFallingMotion(cell,MaterialId.Water,out var motion),Is.True);
            Assert.That(motion.HorizontalVelocityWorld.x,Is.EqualTo(4).Within(.0001f));Assert.That(motion.Velocity,Is.EqualTo(-3).Within(.0001f));
            Assert.That(store.TotalQuantity,Is.EqualTo(80));
        }
        [Test] public void FastDiagonalFlightChecksTheWallCornerAcrossBothAxes()
        {
            var store=new LiquidVolumeStore(8,16,255,new[]{MaterialId.Water},cell=>cell.x==2 && cell.z==2);
            var solver=Solver(store,out _);var cell=new Vector3Int(0,12,0);
            store.TryAdd(cell,MaterialId.Water,40);Assert.That(Launch(solver,cell,new Vector3(8,0,8)),Is.True);
            solver.Step(128,.1f,.1f);
            Assert.That(store.TryGetQuantity(new Vector3Int(1,12,1),MaterialId.Water,out var quantity),Is.True,
                "不能按先X后Z的L路径绕过实际对角射线上的单格墙。");
            Assert.That(quantity,Is.EqualTo(40));
            Assert.That(store.TotalQuantity,Is.EqualTo(40));
        }
        [Test] public void PendingWorldWriteUsesTheSameInitialVelocityInsteadOfDiscardingIt()
        {
            using var world=new MaterialInventoryTransferTests.World();
            Assert.That(world.Liquid.TryEnqueueDeposit(new ElementWriteRequest(new Vector3(100.5f,102.5f,100.5f),MaterialId.Water,40,.1f,false,new Vector3(3,2,0))),Is.True);
            world.Liquid.Step(.1f,.1f);
            Assert.That(world.Liquid.TryGetFallingMotion(new Vector3Int(0,2,0),MaterialId.Water,out var motion),Is.True);
            Assert.That(motion.HorizontalVelocityWorld.x,Is.EqualTo(3).Within(.0001f));
            Assert.That(motion.Velocity,Is.EqualTo(-2+.981f).Within(.0001f));
            Assert.That(world.Liquid.TotalQuantity+world.Liquid.PendingQuantity,Is.EqualTo(40));
        }
    }
}
