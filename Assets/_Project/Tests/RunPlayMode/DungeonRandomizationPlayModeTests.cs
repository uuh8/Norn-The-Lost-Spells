#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Game.Character;
using Game.Core;
using Game.Skills;
using UnityEngine.SceneManagement;
using System;
using System.IO;
namespace Game.Run.Tests
{
    public sealed class DungeonRandomizationPlayModeTests:DungeonEconomyPlayModeTests
    {
        [UnityTest]public IEnumerator SmallRoomRewardHasTwentyToFortyFiniteCharges()
        {
            var encounter=_dungeon.GetEncounter(0);Assert.That(encounter.TryBeginEncounter(),Is.True);yield return null;
            for(int i=0;i<encounter.EnemyCount;i++)Kill(encounter.GetEnemy(i));yield return null;
            var reward=encounter.GetComponentInChildren<SpellRewardPickup>(true);Assert.That(reward,Is.Not.Null);Assert.That(reward.IsAvailable,Is.True);
            int before=CountStorage();Warp(reward.transform.position);yield return new WaitForFixedUpdate();yield return new WaitForFixedUpdate();yield return null;
            Assert.That(reward.IsConsumed,Is.True);Assert.That(CountStorage(),Is.EqualTo(before+1));
            foreach(var item in _session.Profile.Items)if(item.Location.Kind==ItemLocationKind.SpellStorage)
            {Assert.That(item.Item.HasUnlimitedCharges,Is.False);Assert.That(item.Item.RemainingCharges,Is.InRange(20,40));}
        }
        [UnityTest]public IEnumerator FinalRoomGrantsThreeKindsRatherThanWholeLibrary()
        {
            for(int stage=0;stage<4;stage++)
            {var encounter=_dungeon.GetEncounter(stage);Assert.That(encounter.TryBeginEncounter(),Is.True);yield return null;
                for(int i=0;i<encounter.EnemyCount;i++)Kill(encounter.GetEnemy(i));yield return null;}
            var reward=_dungeon.GetEncounter(3).GetComponentInChildren<DungeonFinalRewardPickup>(true);Assert.That(reward,Is.Not.Null);
            int before=CountStorage();Assert.That(reward.TryCollect(_player.CharacterController),Is.True);
            Assert.That(CountStorage(),Is.EqualTo(before+3),"最后清场不能解锁整套法术。");
            Assert.That(reward.TryCollect(_player.CharacterController),Is.False);
        }
        [UnityTest]public IEnumerator ContactCoinsCreditOnceThenAnimateBeforeDestroy()
        {
            var encounter=_dungeon.GetEncounter(0);Assert.That(encounter.TryBeginEncounter(),Is.True);yield return null;Kill(encounter.GetEnemy(0));yield return null;
            var coin=FindCoin();Assert.That(coin,Is.Not.Null);long money=_session.Currency;
            Assert.That(coin.TryCollect(_player.transform),Is.True);Assert.That(_session.Currency,Is.EqualTo(money+17));
            Assert.That(coin.gameObject.activeSelf,Is.True,"入账后保持视觉用于弹跳，关闭的是触发器。");
            Assert.That(coin.TryCollect(_player.transform),Is.False);yield return new WaitForSeconds(.6f);Assert.That(coin==null,Is.True);
        }
        [UnityTest]public IEnumerator RolledWandEquipsCastsAndReturnsWithSameStats()
        {
            var encounter=_dungeon.GetEncounter(0);Assert.That(encounter.TryBeginEncounter(),Is.True);yield return null;Kill(encounter.GetEnemy(0));yield return null;
            RuntimeItemState wand=null;foreach(var entry in _session.Profile.Items)if(entry.Location.Kind==ItemLocationKind.WorldItem&&entry.Item.RolledWandStats.HasValue){wand=entry.Item;break;}
            Assert.That(wand,Is.Not.Null);var stats=wand.RolledWandStats.Value;string id=wand.InstanceId;
            var pickup=_session.FindWorldProxy(id);Warp(pickup.transform.position);var bag=new GridPlacement(0,4,0).InBackpack(_session.EquippedBackpackId);
            Assert.That(pickup.TryCollect(bag,_session.ReadOnly.Version).Success,Is.True);
            _session.WandsReadOnly.TryGetSlot(0,out var old);_session.Profile.Items.TryGet(old.InstanceId,out var previous);
            Assert.That(_session.Profile.Items.TryApply(new[]{new InventoryEntry(previous,bag),new InventoryEntry(wand,new ItemLocation(ItemLocationKind.EquippedWand,_session.Profile.ActorId,0,0,0,0))},_session.ReadOnly.Version).Success,Is.True);
            ItemDefinition fire=null;foreach(var entry in _session.Profile.Items)
                if(_session.Profile.Items.Catalog.TryGet(entry.Item.ContentId,out var definition)&&definition.Kind==ItemKind.Spell
                    &&definition.SpellDefinition.Kind==SpellKind.Emit&&definition.SpellDefinition.ManaCost<=stats.MaxMana){fire=definition;break;}
            Assert.That(fire,Is.Not.Null);Assert.That(_session.Profile.Items.TryAdd(new RuntimeItemState("random.cast",fire.ContentId,25),new ItemLocation(ItemLocationKind.WandSlot,id,0,0,0,0)),Is.True);
            EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent{InventoryId=_session.ReadOnly.InventoryId,Version=_session.ReadOnly.Version});
            _session.ReadOnly.TryGetView(id,out var view);Assert.That(view.Rules.WandProgramCapacity,Is.EqualTo(stats.ProgramCapacity));
            var adapter=_player.GetComponent<OwnedWandCastAdapter>();adapter.Select(0);Assert.That(adapter.TryCapture(out var program,out var context),Is.True);
            Assert.That(program.BaseDraws,Is.EqualTo(stats.BaseDraws));_session.WandsReadOnly.TryGetState(id,out var before);
            var spawn=_player.transform.position+Vector3.up;int emitted=_player.SpellCaster.CastOwnedProgram(program,context,spawn,spawn+Vector3.forward*10,1,_player.gameObject.GetInstanceID(),_player.CharacterController);
            Assert.That(emitted,Is.GreaterThan(0));_session.WandsReadOnly.TryGetState(id,out var spent);Assert.That(spent.CurrentMana,Is.LessThan(before.CurrentMana));
            Assert.That(spent.CastCooldown,Is.EqualTo(stats.CastCooldown));Assert.That(spent.ManaRegenPerSecond,Is.EqualTo(stats.ManaRegenPerSecond));
            _session.Profile.Items.TryGet("random.cast",out var card);Assert.That(card.RemainingCharges,Is.EqualTo(24));
            Assert.That(HubPracticeSession.Current.ReturnToHub(),Is.True);float end=Time.realtimeSinceStartup+40;
            while(Time.realtimeSinceStartup<end){var hub=SceneManager.GetSceneByPath("Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity");if(hub.isLoaded&&HubPracticeSession.Current==null){_scene=hub;break;}yield return null;}
            Assert.That(_scene.path,Does.EndWith("SinglePlayer_Hub.unity"));_session=Find<SinglePlayerInventorySession>();
            Assert.That(_session.Profile.Items.TryGet(id,out var returned),Is.True);Assert.That(returned.RolledWandStats.Value.SameAs(stats),Is.True);
            _session.WandsReadOnly.TryGetState(id,out var state);Assert.That(state.MaxMana,Is.EqualTo(stats.MaxMana));yield return new WaitForSecondsRealtime(.8f);
        }
        [UnityTest]public IEnumerator FinalRewardBusyRetryRetainsDrawAndOnlyGrantsThree()
        {
            for(int stage=0;stage<4;stage++){var encounter=_dungeon.GetEncounter(stage);Assert.That(encounter.TryBeginEncounter(),Is.True);yield return null;
                for(int i=0;i<encounter.EnemyCount;i++)Kill(encounter.GetEnemy(i));yield return null;}
            var reward=_dungeon.GetEncounter(3).GetComponentInChildren<DungeonFinalRewardPickup>(true);
            var field=typeof(DungeonFinalRewardPickup).GetField("_rewards",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
            var original=(DungeonSpellReward[])field.GetValue(reward);_session.Profile.PendingMaterialOutputs=1;
            Assert.That(reward.TryCollect(_player.CharacterController),Is.False);Assert.That(field.GetValue(reward),Is.SameAs(original));
            _session.Profile.PendingMaterialOutputs=0;Assert.That(reward.TryCollect(_player.CharacterController),Is.True);
            Assert.That(CountStorage(),Is.EqualTo(3));foreach(var roll in original)
            {int count=0;foreach(var item in _session.Profile.Items)if(item.Location.Kind==ItemLocationKind.SpellStorage&&_session.Profile.Items.Catalog.TryGet(item.Item.ContentId,out var def)&&def.SpellDefinition==roll.Spell)
                {count++;Assert.That(item.Item.RemainingCharges,Is.EqualTo(roll.Charges));Assert.That(item.Item.HasUnlimitedCharges,Is.False);}Assert.That(count,Is.EqualTo(1));}
        }
        [UnityTest]public IEnumerator UprightCoinRotatesAndCoinVisualHasNoGcAlloc()
        {
            // 单独验证金币视觉的真实可发生掉落结果；避免强制四类必掉的测试把金币挡在大模型后面。
            foreach(var entry in _dungeon.GetTable(0).Items)entry.Chance=0;
            var encounter=_dungeon.GetEncounter(0);Assert.That(encounter.TryBeginEncounter(),Is.True);yield return null;Kill(encounter.GetEnemy(0));yield return null;
            var coin=FindCoin();var visual=coin.transform.Find("Gold coin");Assert.That(Mathf.Abs(Vector3.Dot(visual.up,Vector3.up)),Is.LessThan(.01f));
            Assert.That(visual.GetComponent<Renderer>().bounds.min.y,Is.GreaterThanOrEqualTo(coin.transform.position.y-.02f));
            var rotation=visual.rotation;yield return new WaitForSeconds(.15f);Assert.That(Quaternion.Angle(rotation,visual.rotation),Is.GreaterThan(1));
            bool recording=UnityEditorInternal.ProfilerDriver.enabled;bool cpu=UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            try
            {
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,true);UnityEditorInternal.ProfilerDriver.enabled=true;
                for(int i=0;i<10;i++)yield return null;int first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;
                for(int i=0;i<60;i++)yield return null;int last=UnityEditorInternal.ProfilerDriver.lastFrameIndex;
                WriteCoinProfile(first,last,"coin-profiler.txt");
            }
            finally{UnityEditorInternal.ProfilerDriver.enabled=recording;UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,cpu);}
            var camera=Find<Camera>();var brain=camera.GetComponent("CinemachineBrain")as Behaviour;if(brain!=null)brain.enabled=false;
            camera.transform.position=coin.transform.position+new Vector3(1,.8f,-1.4f);camera.transform.LookAt(coin.transform.position+Vector3.up*.2f);
            yield return new WaitForEndOfFrame();CaptureCoin("upright-coin.png");
            try
            {
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,true);UnityEditorInternal.ProfilerDriver.enabled=true;
                int first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;
                float height=visual.localPosition.y;Assert.That(coin.TryCollect(_player.transform),Is.True);yield return new WaitForSeconds(.08f);
                Assert.That(visual.localPosition.y,Is.GreaterThan(height));foreach(var collider in coin.GetComponents<Collider>())Assert.That(collider.enabled,Is.False);
                yield return new WaitForEndOfFrame();CaptureCoin("coin-jump.png");for(int i=0;i<40;i++)yield return null;Assert.That(coin==null,Is.True);
                WriteCoinProfile(first,UnityEditorInternal.ProfilerDriver.lastFrameIndex,"coin-animation-profiler.txt");
                UnityEditorInternal.ProfilerDriver.SaveProfile("TestResults/SinglePlayer/M6-T19-Randomization/coin-profiler.data");
            }
            finally{UnityEditorInternal.ProfilerDriver.enabled=recording;UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,cpu);}
        }
        private static void WriteCoinProfile(int first,int last,string name)
        {
            long bytes=0;int samples=0,frames=0;
            for(int frame=Math.Max(first,UnityEditorInternal.ProfilerDriver.firstFrameIndex);frame<=last;frame++)
            {using var data=UnityEditorInternal.ProfilerDriver.GetRawFrameDataView(frame,0);if(!data.valid)continue;frames++;
                for(int sample=0;sample<data.sampleCount;sample++)if(data.GetSampleName(sample)=="Norn.Dungeon.CoinVisual")
                {samples++;int stop=sample+data.GetSampleChildrenCountRecursive(sample);for(int child=sample+1;child<=stop;child++)if(data.GetSampleName(child)=="GC.Alloc"&&data.GetSampleMetadataCount(child)>0)bytes+=data.GetSampleMetadataAsLong(child,0);}}
            Directory.CreateDirectory("TestResults/SinglePlayer/M6-T19-Randomization");File.WriteAllText("TestResults/SinglePlayer/M6-T19-Randomization/"+name,DateTime.UtcNow.ToString("O")+"\nFrames="+frames+" Samples="+samples+" GC.Alloc="+bytes+" B");
            Assert.That(frames,Is.GreaterThanOrEqualTo(30));Assert.That(samples,Is.GreaterThan(0));Assert.That(bytes,Is.Zero);
        }
        private static void CaptureCoin(string name)
        {
            var pixels=new Texture2D(Screen.width,Screen.height,TextureFormat.RGB24,false);
            try{pixels.ReadPixels(new Rect(0,0,Screen.width,Screen.height),0,0);pixels.Apply();Directory.CreateDirectory("TestResults/SinglePlayer/M6-T19-Randomization");File.WriteAllBytes("TestResults/SinglePlayer/M6-T19-Randomization/"+name,pixels.EncodeToPNG());}
            finally{UnityEngine.Object.Destroy(pixels);}
        }
        private int CountStorage(){int count=0;foreach(var item in _session.Profile.Items)if(item.Location.Kind==ItemLocationKind.SpellStorage)count++;return count;}
    }
}
#endif
