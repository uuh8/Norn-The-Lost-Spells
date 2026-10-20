#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Game.Run.Tests
{
    /// <summary>真正进入隔离四关地牢，测试实际Health死亡、物品代理、金币触碰与回程保存。</summary>
    public class DungeonEconomyPlayModeTests
    {
        private const string Map="Assets/_Project/Scenes/Tests/Hub/P7_DungeonReview.unity";
        private const string Evidence="TestResults/SinglePlayer/M6-T18-Dungeon";
        protected Scene _scene;
        protected DungeonRunContent _dungeon;
        protected WizardController _player;
        protected SinglePlayerInventorySession _session;
        private readonly List<UnityEngine.Object> _temporary=new List<UnityEngine.Object>();
        private bool _background;
        protected T Find<T>()where T:Component
        {foreach(var root in _scene.GetRootGameObjects()){var found=root.GetComponentInChildren<T>(true);if(found!=null)return found;}return null;}
        protected WorldCurrencyPickup FindCoin()
        {foreach(var root in _scene.GetRootGameObjects())foreach(var coin in root.GetComponentsInChildren<WorldCurrencyPickup>())if(!coin.Collected)return coin;return null;}
        private void Loaded(Scene scene,LoadSceneMode mode)
        {
            if(scene.path!=Map)return;_scene=scene;
            var dungeon=Find<DungeonRunContent>();if(dungeon==null)return;
            // 只克隆测试使用的SO，不能在Play中把作者的掉落概率改为必掉。
            var data=new SerializedObject(dungeon);var tables=data.FindProperty("_tables");
            for(int i=0;i<tables.arraySize;i++)
            {
                var table=UnityEngine.Object.Instantiate((DungeonLootTable)tables.GetArrayElementAtIndex(i).objectReferenceValue);_temporary.Add(table);
                table.CoinChance=1;table.MinCoins=table.MaxCoins=17;
                foreach(var entry in table.Items){entry.Chance=1;entry.MinCount=entry.MaxCount=1;}
                tables.GetArrayElementAtIndex(i).objectReferenceValue=table;
            }
            data.ApplyModifiedPropertiesWithoutUndo();
        }
        [UnitySetUp]public IEnumerator Setup()
        {
            _background=Application.runInBackground;Application.runInBackground=true;Time.timeScale=1;
            EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor")).Focus();
            SceneManager.sceneLoaded+=Loaded;
            _scene=EditorSceneManager.LoadSceneInPlayMode(Map,new LoadSceneParameters(LoadSceneMode.Additive));
            for(int i=0;i<6;i++)yield return null;
            SceneManager.sceneLoaded-=Loaded;Find<GameplayGuideController>()?.Close();Time.timeScale=1;
            _dungeon=Find<DungeonRunContent>();_player=Find<WizardController>();_session=Find<SinglePlayerInventorySession>();
            Assert.That(_dungeon,Is.Not.Null);Assert.That(_session?.ReadOnly,Is.Not.Null);
            _player.GetComponent<HealthComponent>().SetInvulnerable(true);_player.enabled=false;
        }
        protected void Warp(Vector3 position)
        {_player.CharacterController.enabled=false;_player.transform.position=position;_player.CharacterController.enabled=true;Physics.SyncTransforms();}
        protected static void Kill(HealthComponent health)
        {health.ReceiveHit(new DamageRequest(-123,1,100000,DamageType.True,health.transform.position,Vector3.forward,false));}
        [UnityTest]public IEnumerator FixedMazeAndMaterialPointsHaveConnectedNavigation()
        {
            Assert.That(_dungeon.StageCount,Is.EqualTo(4));Assert.That(_dungeon.RouteCheckCount,Is.EqualTo(8));
            Assert.That(_dungeon.GetComponentsInChildren<WorldItemSpawnPoint>(true).Length,Is.EqualTo(8));
            Assert.That(Find<DemoRunHudController>(),Is.Null,"上方教学HUD消费者必须移除，不能运行时再启用。");
            Assert.That(NavMesh.SamplePosition(_player.transform.position,out var start,8,NavMesh.AllAreas),Is.True);
            var path=new NavMeshPath();
            for(int i=0;i<_dungeon.RouteCheckCount;i++)
            {
                var target=_dungeon.GetRouteCheck(i).position;
                Assert.That(NavMesh.SamplePosition(target,out var end,2,NavMesh.AllAreas),Is.True,"探索点"+i);
                Assert.That(NavMesh.CalculatePath(start.position,end.position,NavMesh.AllAreas,path),Is.True);
                Assert.That(path.status,Is.EqualTo(NavMeshPathStatus.PathComplete),"探索点"+i+"必须可达，不能因加墙堵死。");
            }
            for(int stage=0;stage<4;stage++)for(int enemy=0;enemy<_dungeon.GetEncounter(stage).EnemyCount;enemy++)
            {
                // 高台远程敌人的最近Nav面可能孤立，验证实际掉落选择的表面能从主路到达。
                var actor=_dungeon.GetEncounter(stage).GetEnemy(enemy).transform;
                DungeonLootDropAdapter adapter=null;foreach(var candidate in _dungeon.GetComponentsInChildren<DungeonLootDropAdapter>())
                    if(candidate.EnemyId==actor.gameObject.GetInstanceID())adapter=candidate;
                Assert.That(adapter,Is.Not.Null);
                Assert.That(NavMesh.SamplePosition(adapter.ResolveDropGround(actor.position),out var end,1,NavMesh.AllAreas),Is.True);
                Assert.That(NavMesh.CalculatePath(start.position,end.position,NavMesh.AllAreas,path),Is.True);
                Assert.That(path.status,Is.EqualTo(NavMeshPathStatus.PathComplete),"stage="+stage+" enemy="+enemy);
            }
            int materialCount=0;foreach(var entry in _session.Profile.Items)
                if(entry.Location.Kind==ItemLocationKind.WorldItem)materialCount++;
            Assert.That(materialCount,Is.GreaterThanOrEqualTo(8));
            foreach(var point in _dungeon.GetComponentsInChildren<WorldItemSpawnPoint>(true))
                if(point.Definition.Kind==ItemKind.Material)
                {Assert.That(point.TryGetRuntimeLocation(out _),Is.True);Assert.That(_session.ReadOnly.TryGetView(point.RuntimeInstanceId,out var material),Is.True);
                    Assert.That(material.MaterialQuantity,Is.GreaterThan(0),"探索固体不能只发没有实际GMU的空材料卡。");}
            yield return new WaitForEndOfFrame();Capture("dungeon-entry.png");
            var camera=Find<Camera>();var brain=camera.GetComponent("CinemachineBrain")as Behaviour;if(brain!=null)brain.enabled=false;
            camera.transform.SetPositionAndRotation(new Vector3(9,80,35),Quaternion.Euler(90,0,0));camera.orthographic=true;camera.orthographicSize=30;
            yield return new WaitForEndOfFrame();Capture("fixed-maze-overview.png");
        }
        [UnityTest]public IEnumerator RealEnemyDeathDropsAllConfiguredKindsAndCoinsCollectOnceByContact()
        {
            var encounter=_dungeon.GetEncounter(0);Assert.That(encounter.TryBeginEncounter(),Is.True);yield return null;
            var enemy=encounter.GetEnemy(0);int before=_session.ReadOnly.Count;long money=_session.Currency;
            int enemyId=enemy.Id;Vector3 position=enemy.transform.position;Kill(enemy);yield return null;
            Assert.That(_session.ReadOnly.Count,Is.EqualTo(before+4));
            var coins=FindCoin();Assert.That(coins,Is.Not.Null);Assert.That(coins.Amount,Is.EqualTo(17));
            Assert.That(_session.Currency,Is.EqualTo(money),"击杀本身不直接加钱，必须接触金币。");
            EventBus<DeathEvent>.Publish(new DeathEvent{TargetId=enemyId,Position=position});yield return null;
            Assert.That(_session.ReadOnly.Count,Is.EqualTo(before+4),"重复死亡不能重复创建物品。");
            Warp(coins.transform.position);yield return new WaitForFixedUpdate();yield return new WaitForFixedUpdate();yield return null;
            Assert.That(_session.Currency,Is.EqualTo(money+17),"通过实际CharacterController与Trigger接触领取。");
            Assert.That(_session.HasUnsavedChanges,Is.True);Assert.That(_session.ReadOnly.Count,Is.EqualTo(before+4),"金币不占背包。");
            Assert.That(coins==null||coins.Collected,Is.True);
            yield return new WaitForEndOfFrame();Capture("enemy-loot.png");
        }
        [UnityTest]public IEnumerator FourStagesClearAndCollectedLootAndWalletSurviveReturnSnapshot()
        {
            long money=_session.Currency;
            long adventure=_session.Profile.AdventureProgress;
            for(int stage=0;stage<4;stage++)
            {
                var encounter=_dungeon.GetEncounter(stage);Assert.That(encounter.TryBeginEncounter(),Is.True,"stage"+stage);yield return null;
                for(int enemy=0;enemy<encounter.EnemyCount;enemy++)Kill(encounter.GetEnemy(enemy));
                yield return null;Assert.That(encounter.State,Is.EqualTo(EncounterState.Cleared));
            }
            Assert.That(Find<DemoRunController>().State,Is.EqualTo(RunState.StageCleared));
            Assert.That(_session.Profile.AdventureProgress,Is.EqualTo(adventure+1));
            EventBus<EncounterCompletedEvent>.Publish(new EncounterCompletedEvent{EncounterIndex=3,EncounterCount=4});
            Assert.That(_session.Profile.AdventureProgress,Is.EqualTo(adventure+1),"完整闯关只推动一次既有商店补货进度。");
            // 高台奖励可能回退到玩家脚边，接触触发器已经自动领取的金币也属于正常奖励。
            money=_session.Currency;
            WorldItemPickup picked=null;
            foreach(var entry in _session.Profile.Items)if(entry.Location.Kind==ItemLocationKind.WorldItem
                &&_session.Profile.Items.Catalog.TryGet(entry.Item.ContentId,out var definition)&&definition.Kind==ItemKind.Spell)
            {picked=_session.FindWorldProxy(entry.Item.InstanceId);break;}
            Assert.That(picked,Is.Not.Null);string pickedId=picked.ItemId;Warp(picked.transform.position);
            var result=picked.TryCollect(new ItemLocation(ItemLocationKind.SpellStorage,_session.ReadOnly.ActorId,0,0,0,8),_session.ReadOnly.Version);
            Assert.That(result.Success,Is.True,result.Reason.ToString());
            var coin=FindCoin();Assert.That(coin,Is.Not.Null);Warp(coin.transform.position);
            yield return new WaitForFixedUpdate();yield return null;Assert.That(_session.Currency,Is.EqualTo(money+17));
            Assert.That(Find<PracticeInventoryWorkspace>().SaveForLeaving().Success,Is.True);
            Assert.That(_session.ReadOnly.TryGetView(pickedId,out var item),Is.True);Assert.That(item.Location.Kind,Is.EqualTo(ItemLocationKind.SpellStorage));
            Assert.That(_session.Currency,Is.EqualTo(money+17));
            foreach(var entry in _session.Profile.Items)Assert.That(entry.Location.Kind,Is.Not.EqualTo(ItemLocationKind.WorldItem),"未拾取地牢物品不带回。");
            Assert.That(HubPracticeSession.Current.ReturnToHub(),Is.True);
            float deadline=Time.realtimeSinceStartup+40;
            while(Time.realtimeSinceStartup<deadline)
            {
                var hub=SceneManager.GetSceneByPath("Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity");
                if(hub.isLoaded&&HubPracticeSession.Current==null){_scene=hub;break;}
                yield return null;
            }
            Assert.That(_scene.path,Is.EqualTo("Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity"));
            _session=Find<SinglePlayerInventorySession>();Assert.That(_session.ReadOnly.TryGetView(pickedId,out var returned),Is.True);
            Assert.That(returned.Location.Kind,Is.EqualTo(ItemLocationKind.SpellStorage));Assert.That(_session.Currency,Is.EqualTo(money+17));
            yield return new WaitForSecondsRealtime(.8f);
        }
        [UnityTest]public IEnumerator CoinOverflowAndWrongActorKeepWorldReward()
        {
            var encounter=_dungeon.GetEncounter(0);Assert.That(encounter.TryBeginEncounter(),Is.True);yield return null;
            Kill(encounter.GetEnemy(0));yield return null;var coin=FindCoin();Assert.That(coin,Is.Not.Null);
            Assert.That(coin.TryCollect(encounter.GetEnemy(1).transform),Is.False,"敌人不能消费玩家金币。");
            _session.Profile.Currency=long.MaxValue-3;Warp(coin.transform.position);
            yield return new WaitForFixedUpdate();yield return null;
            Assert.That(_session.Currency,Is.EqualTo(long.MaxValue-3));Assert.That(coin.Collected,Is.False);Assert.That(coin.gameObject.activeSelf,Is.True);
        }
        [UnityTest]public IEnumerator BusyLootRetriesSameRollAndStableRetryHasNoGcAlloc()
        {
            var encounter=_dungeon.GetEncounter(0);Assert.That(encounter.TryBeginEncounter(),Is.True);yield return null;
            var enemy=encounter.GetEnemy(0);int before=_session.ReadOnly.Count;
            DungeonLootDropAdapter adapter=null;foreach(var candidate in _dungeon.GetComponentsInChildren<DungeonLootDropAdapter>())
                if(candidate.EnemyId==enemy.Id)adapter=candidate;
            Assert.That(adapter,Is.Not.Null);
            _session.Profile.PendingMaterialOutputs=1;Kill(enemy);yield return null;
            Assert.That(adapter.PendingCount,Is.EqualTo(4));Assert.That(_session.ReadOnly.Count,Is.EqualTo(before));
            Assert.That(Find<PracticeInventoryWorkspace>().SaveForLeaving().Success,Is.False,"尚未落地的奖励不能被回程快照丢弃。");
            bool recording=UnityEditorInternal.ProfilerDriver.enabled;
            bool cpu=UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            try
            {
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,true);UnityEditorInternal.ProfilerDriver.enabled=true;
                for(int i=0;i<10;i++)yield return null;int first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;
                for(int i=0;i<60;i++)yield return null;int last=UnityEditorInternal.ProfilerDriver.lastFrameIndex;
                long bytes=0;int samples=0,frames=0;
                for(int frame=Math.Max(first,UnityEditorInternal.ProfilerDriver.firstFrameIndex);frame<=last;frame++)
                {
                    using var data=UnityEditorInternal.ProfilerDriver.GetRawFrameDataView(frame,0);if(!data.valid)continue;frames++;
                    for(int sample=0;sample<data.sampleCount;sample++)if(data.GetSampleName(sample)=="Norn.Dungeon.LootRetry")
                    {
                        samples++;int end=sample+data.GetSampleChildrenCountRecursive(sample);
                        for(int child=sample+1;child<=end;child++)if(data.GetSampleName(child)=="GC.Alloc"&&data.GetSampleMetadataCount(child)>0)
                            bytes+=data.GetSampleMetadataAsLong(child,0);
                    }
                }
                Directory.CreateDirectory(Evidence);File.WriteAllText(Evidence+"/loot-retry-profiler.txt",DateTime.UtcNow.ToString("O")+"\nFrames="+frames+" Samples="+samples+" GC.Alloc="+bytes+" B\nScope=stable busy retry; death/spawn excluded");
                UnityEditorInternal.ProfilerDriver.SaveProfile(Evidence+"/loot-retry-profiler.data");
                Assert.That(frames,Is.GreaterThanOrEqualTo(30));Assert.That(samples,Is.GreaterThan(0));Assert.That(bytes,Is.Zero);
            }
            finally{UnityEditorInternal.ProfilerDriver.enabled=recording;UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,cpu);}
            _session.Profile.PendingMaterialOutputs=0;
            float deadline=Time.realtimeSinceStartup+4;while(adapter.PendingCount>0&&Time.realtimeSinceStartup<deadline)yield return null;
            Assert.That(adapter.PendingCount,Is.Zero);Assert.That(_session.ReadOnly.Count,Is.EqualTo(before+4));
            yield return new WaitForSeconds(.3f);Assert.That(_session.ReadOnly.Count,Is.EqualTo(before+4),"释放事务后仅完成原抽奖，不重复创建。");
        }
        private static void Capture(string name)
        {
            // 同步读回当前帧；异步CaptureScreenshot可能在Scene已卸载后才采图。
            var pixels=new Texture2D(Screen.width,Screen.height,TextureFormat.RGB24,false);
            try{Directory.CreateDirectory(Evidence);pixels.ReadPixels(new Rect(0,0,Screen.width,Screen.height),0,0);pixels.Apply();File.WriteAllBytes(Evidence+"/"+name,pixels.EncodeToPNG());}
            finally{UnityEngine.Object.Destroy(pixels);}
        }
        [UnityTearDown]public IEnumerator Cleanup()
        {
            SceneManager.sceneLoaded-=Loaded;Time.timeScale=1;
            Find<SinglePlayerInventoryController>()?.Close();
            if(HubPracticeSession.Current!=null)UnityEngine.Object.Destroy(HubPracticeSession.Current.gameObject);
            yield return null;
            var cleanup=SceneManager.GetSceneByName("Dungeon test cleanup");if(!cleanup.IsValid())cleanup=SceneManager.CreateScene("Dungeon test cleanup");SceneManager.SetActiveScene(cleanup);
            if(_scene.IsValid()&&_scene.isLoaded)yield return SceneManager.UnloadSceneAsync(_scene);
            foreach(var value in _temporary)if(value!=null)UnityEngine.Object.Destroy(value);_temporary.Clear();
            Application.runInBackground=_background;yield return Resources.UnloadUnusedAssets();GC.Collect();
        }
    }
}
#endif
