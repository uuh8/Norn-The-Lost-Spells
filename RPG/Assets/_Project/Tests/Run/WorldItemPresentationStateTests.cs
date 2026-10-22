using Game.Core;
using NUnit.Framework;
using UnityEngine;

namespace Game.Run.Tests
{
    public sealed class WorldItemPresentationStateTests:InventoryTestFixture
    {
        private SinglePlayerProfile Profile()
        {
            var store=Store(Define("food",ItemKind.Consumable));Add(store,"one","food");
            Assert.That(store.TryMove("one",new ItemLocation(ItemLocationKind.WorldItem,"ground",0,0,0,-1)).Success,Is.True);
            var profile=new SinglePlayerProfile("player",store);
            profile.HubWorldItems.Add(new HubWorldItemRecord{SceneKey="hub.map1",WorldSceneKey="hub.map1",SpawnId="first",InstanceId="one",
                Position=new Vector3(2,.4f,5),Rotation=Quaternion.Euler(0,60,0),PoseKind=WorldItemPoseKind.Grounded,
                HasVisualScaleOverride=true,VisualScaleRatio=new Vector3(2,3,4)});return profile;
        }
        [Test] public void SchemaNineRoundTripAndTransactionSnapshotKeepVisualOverrideAndIdentity()
        {
            var original=Profile();var dto=ProfileSaveMapper.ToDto(original,3);dto.SchemaVersion=9;
            var json=JsonUtility.ToJson(dto);dto=JsonUtility.FromJson<SinglePlayerSaveDto>(json);
            var restored=ProfileSaveMapper.TryRestore(dto,original.Items.Catalog,out var profile);
            Assert.That(restored.Success,Is.True,restored.Message);
            Assert.That(profile.HubWorldItems[0].InstanceId,Is.EqualTo("one"));Assert.That(profile.HubWorldItems[0].SpawnId,Is.EqualTo("first"));
            Assert.That(profile.HubWorldItems[0].VisualScaleRatio,Is.EqualTo(new Vector3(2,3,4)));
            var clone=RaidProfileOps.Clone(profile);profile.HubWorldItems[0].VisualScaleRatio=Vector3.one;
            Assert.That(clone.HubWorldItems[0].VisualScaleRatio,Is.EqualTo(new Vector3(2,3,4)),"事务快照不能共享可变地面显示状态。");
        }
        [Test] public void SchemaEightWithoutVisualFieldsKeepsOriginAndUsesPrefabDefaults()
        {
            var original=Profile();var dto=ProfileSaveMapper.ToDto(original,3);dto.SchemaVersion=8;
            // 真正删除Schema8中不存在的JSON字段，验证Unity缺省反序列化，而不只手动填默认值。
            string json=JsonUtility.ToJson(dto);
            json=System.Text.RegularExpressions.Regex.Replace(json,@",""(PoseKind|HasVisualScaleOverride|VisualScaleRatio)"":(\{[^}]*\}|\d+|true|false)","");
            dto=JsonUtility.FromJson<SinglePlayerSaveDto>(json);
            Assert.That(ProfileSaveMapper.TryRestore(dto,original.Items.Catalog,out var restored).Success,Is.True);
            Assert.That(restored.HubWorldItems[0].HasVisualScaleOverride,Is.False);Assert.That(restored.HubWorldItems[0].SpawnId,Is.EqualTo("first"));
            Assert.That(restored.HubWorldItems[0].PoseKind,Is.EqualTo(WorldItemPoseKind.Legacy));Assert.That(restored.Items.Count,Is.EqualTo(1));
        }
        [TestCase(0f)] [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)]
        public void InvalidVisualScaleDoesNotPublishAProfile(float invalid)
        {
            var original=Profile();var dto=ProfileSaveMapper.ToDto(original,3);dto.HubWorldItems[0].VisualScaleRatio=new Vector3(invalid,1,1);
            Assert.That(ProfileSaveMapper.TryRestore(dto,original.Items.Catalog,out var restored).Status,Is.EqualTo(ProfilePersistenceStatus.InvalidData));
            Assert.That(restored,Is.Null);
        }
    }
}
