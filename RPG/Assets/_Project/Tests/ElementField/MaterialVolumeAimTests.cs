using System;
using System.Reflection;
using Game.Core;
using Game.Materials;
using NUnit.Framework;
using UnityEngine;
namespace Game.ElementField.Tests
{
    public sealed class MaterialVolumeAimTests
    {
        [TestCase(MaterialId.Steam)][TestCase(MaterialId.ToxicVapor)][TestCase(MaterialId.Ice)][TestCase(MaterialId.DriedSlime)]
        public void CameraRayFindsActualMaterialWithoutColliderAndHonoursOcclusionAndReach(MaterialId material)
        {
            using var world=new MaterialInventoryTransferTests.World();
            MaterialInventoryTransferTests.World.Set(world.Runtime,"_settings",new ElementFieldSimulationSettings(1,0,0,0,2,false,default));
            if(material==MaterialId.Ice||material==MaterialId.DriedSlime){world.Solid.BeginConversionGeometry();Assert.That(world.Solid.StageConversionAdd(Vector3Int.zero,material,100),Is.True);}
            else Assert.That(world.Gas.StageConversionAdd(Vector3Int.zero,material,100),Is.True);
            var callback=(Action<MaterialCollectionAimRequestedEvent>)Delegate.CreateDelegate(typeof(Action<MaterialCollectionAimRequestedEvent>),world.Adapter,
                typeof(MaterialInventoryTransferAdapter).GetMethod("OnAim",BindingFlags.Instance|BindingFlags.NonPublic));
            var aim=new MaterialCollectionAimExchange{WorldId="test-world",ActorPosition=new Vector3(100.5f,100.5f,99),RayOrigin=new Vector3(100.5f,100.5f,95),RayDirection=Vector3.forward,Reach=3,MaximumRayDistance=10,AllowedMaterials=(ushort)(1<<(byte)material)};
            callback(new MaterialCollectionAimRequestedEvent{Exchange=aim});Assert.That(aim.Found,Is.True);Assert.That(aim.MaterialKey,Is.EqualTo((byte)material));
            aim.Handled=false;aim.MaximumRayDistance=4;callback(new MaterialCollectionAimRequestedEvent{Exchange=aim});Assert.That(aim.Found,Is.False,"不能隔着Physics遮挡采气");
            aim.Handled=false;aim.MaximumRayDistance=10;aim.ActorPosition=Vector3.one*90;callback(new MaterialCollectionAimRequestedEvent{Exchange=aim});Assert.That(aim.Found,Is.False);
        }
        [Test]public void FallingIslandDisplacesGasConservativelyAndPreservesBothSolidMaterials()
        {
            using var world=new MaterialInventoryTransferTests.World();
            world.Solid.BeginConversionGeometry();Assert.That(world.Solid.StageConversionAdd(new Vector3Int(0,2,0),MaterialId.Ice,1024),Is.True);
            Assert.That(world.Solid.StageConversionAdd(new Vector3Int(1,2,0),MaterialId.DriedSlime,1024),Is.True);
            Assert.That(world.Gas.StageConversionAdd(new Vector3Int(0,1,0),MaterialId.Steam,100),Is.True);
            Assert.That(world.Gas.StageConversionAdd(new Vector3Int(1,1,0),MaterialId.ToxicVapor,80),Is.True);
            world.Solid.StepGravity(.4f);
            Assert.That(world.Solid.TotalQuantity,Is.EqualTo(2048));Assert.That(world.Gas.TotalQuantity,Is.EqualTo(180));
            Assert.That(world.Solid.TryGetQuantity(new Vector3Int(0,1,0),MaterialId.Ice,out _),Is.True);
            Assert.That(world.Solid.TryGetQuantity(new Vector3Int(1,1,0),MaterialId.DriedSlime,out _),Is.True);
            Assert.That(world.Gas.TryGetQuantity(new Vector3Int(0,2,0),MaterialId.Steam,out var quantity)&&quantity==100,Is.True);
        }
    }
}
