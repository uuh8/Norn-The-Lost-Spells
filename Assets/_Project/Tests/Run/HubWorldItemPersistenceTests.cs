using System;
using Game.Core;
using NUnit.Framework;
using UnityEngine;

namespace Game.Run.Tests
{
    public sealed class HubWorldItemPersistenceTests:InventoryTestFixture
    {
        private SinglePlayerProfile Profile()
        {var store=Store(Define("food",ItemKind.Consumable));Add(store,"a","food");return new SinglePlayerProfile("player",store);}
        [Test] public void SpawnLedgerAndPoseRoundTripAndSnapshotDoNotShareWritableRecords()
        {
            var p=Profile();p.HubWorldItems.Add(new HubWorldItemRecord{SceneKey="hub.map1",WorldSceneKey="hub.map1",SpawnId="apple.1",InstanceId="a",Position=new Vector3(3,2,1),Rotation=Quaternion.Euler(0,45,0)});
            var dto=ProfileSaveMapper.ToDto(p,0);Assert.That(ProfileSaveMapper.TryRestore(dto,p.Items.Catalog,out var restored).Success,Is.True);
            Assert.That(restored.HubWorldItems.Count,Is.EqualTo(1));Assert.That(restored.HubWorldItems[0].Position,Is.EqualTo(new Vector3(3,2,1)));
            var snapshot=ProfileAssetSnapshot.From(dto);dto.HubWorldItems[0].Position=Vector3.zero;
            Assert.That(snapshot.HubWorldItems[0].Position,Is.EqualTo(new Vector3(3,2,1)));
            var cloned=RaidProfileOps.Clone(restored);cloned.HubWorldItems[0].Position=Vector3.one;
            Assert.That(restored.HubWorldItems[0].Position,Is.EqualTo(new Vector3(3,2,1)));
        }
        [Test] public void SchemaSevenMigratesWithoutInventingSceneAssets()
        {var p=Profile();var dto=ProfileSaveMapper.ToDto(p,0);dto.SchemaVersion=7;dto.HubWorldItems=null;Assert.That(ProfileSaveMapper.TryRestore(dto,p.Items.Catalog,out var next).Success,Is.True);Assert.That(next.HubWorldItems.Count,Is.Zero);}
        [Test] public void RemovedItemKeepsItsSpawnTombstone()
        {var p=Profile();p.HubWorldItems.Add(new HubWorldItemRecord{SceneKey="hub",SpawnId="once",InstanceId="sold-item"});var dto=ProfileSaveMapper.ToDto(p,0);Assert.That(ProfileSaveMapper.TryRestore(dto,p.Items.Catalog,out var next).Success,Is.True);Assert.That(next.HubWorldItems[0].InstanceId,Is.EqualTo("sold-item"));}
        [Test] public void DuplicateSpawnAndNonFinitePoseAreRejected()
        {
            var p=Profile();var dto=ProfileSaveMapper.ToDto(p,0);
            dto.HubWorldItems=new[]{new HubWorldItemRecord{SceneKey="hub",SpawnId="same",InstanceId="a"},new HubWorldItemRecord{SceneKey="hub",SpawnId="same",InstanceId="b"}};
            Assert.That(ProfileSaveMapper.TryRestore(dto,p.Items.Catalog,out _).Success,Is.False);
            dto.HubWorldItems=new[]{new HubWorldItemRecord{SceneKey="hub",InstanceId="a",Position=new Vector3(float.NaN,0,0)}};
            Assert.That(ProfileSaveMapper.TryRestore(dto,p.Items.Catalog,out _).Success,Is.False);
        }
    }
}
