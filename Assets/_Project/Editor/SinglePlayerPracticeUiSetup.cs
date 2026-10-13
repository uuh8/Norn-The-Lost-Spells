using System;
using System.Collections.Generic;
using System.IO;
using Game.Character;
using Game.Core;
using Game.ElementField;
using Game.Rendering;
using Game.Run;
using Game.Skills;
using Game.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

namespace Game.EditorTools
{
    /// <summary>复用作者据点UI，增量补Catalog并创建隔离地图Review。正式地图只提供Undo应用，不保存。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerPracticeUiSetup
    {
        private const string Hub="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity";
        private const string Prefab="Assets/_Project/Prefabs/SinglePlayer/PracticeInventoryWorkspace.prefab";
        private const string Data="Assets/_Project/ScriptableObjects/SinglePlayer/Hub/SinglePlayerRaids/";
        private const string Evidence="TestResults/SinglePlayer/M6-T5-PracticeModernUi";
        private const string Request="Temp/SinglePlayer/practice-modern-ui.request";
        private static readonly string[] Maps={"Assets/_Project/Scenes/P7_DemoRun.unity","Assets/_Project/Scenes/P8_BossField.unity"};
        static SinglePlayerPracticeUiSetup()
        {
            EditorApplication.update+=Poll;
            EditorApplication.playModeStateChanged+=OnPlayModeStateChanged;
        }
        private static void Poll()
        {
            if(!File.Exists(Request)||EditorApplication.isCompiling||EditorApplication.isUpdating||EditorApplication.isPlayingOrWillChangePlaymode)return;
            string command=File.ReadAllText(Request).Trim();File.Delete(Request);
            try
            {
                if(EditorUtility.scriptCompilationFailed)throw new InvalidOperationException("编译失败");
                if(command=="prepare")Prepare();else if(command=="apply-current")ApplyCurrent();
                else if(command=="apply-maps")throw new InvalidOperationException("已移除同时打开两张正式地图的交付入口。请逐张打开P7/P8，应用当前地图后自行保存；每次只留下一个游戏场景。");
                else if(command=="restore-single-hub")RestoreSingleHub();
                else if(command=="inspect")Write("inspection.txt",AuditLoadedScenes());
                else throw new ArgumentException(command);
            }
            catch(Exception error){Write("setup-error.txt",error.ToString());GameLog.Error(error.ToString(),"PracticeSetup");}
        }
        internal static string ReviewPath(string path)=>"Assets/_Project/Scenes/SinglePlayer/"+Path.GetFileNameWithoutExtension(path)+"_ModernUiReview.unity";
        private static T Find<T>(Scene scene)where T:Component
        {foreach(var root in scene.GetRootGameObjects()){var value=root.GetComponentInChildren<T>(true);if(value!=null)return value;}return null;}
        private static void Set(Object target,string field,Object value)
        {var data=new SerializedObject(target);data.FindProperty(field).objectReferenceValue=value;data.ApplyModifiedPropertiesWithoutUndo();}
        private static void SetString(Object target,string field,string value)
        {var data=new SerializedObject(target);data.FindProperty(field).stringValue=value;data.ApplyModifiedPropertiesWithoutUndo();}
        private static void Catalog()
        {
            var catalog=AssetDatabase.LoadAssetAtPath<ItemCatalog>(Data+"Catalog.asset");
            var library=AssetDatabase.LoadAssetAtPath<SpellLibrary>("Assets/_Project/ScriptableObjects/SpellLibrary.asset");
            var data=new SerializedObject(catalog);var entries=data.FindProperty("_definitions");
            var mapped=new HashSet<SpellDefinition>();
            for(int i=0;i<entries.arraySize;i++){var item=(ItemDefinition)entries.GetArrayElementAtIndex(i).objectReferenceValue;if(item.SpellDefinition!=null)mapped.Add(item.SpellDefinition);}
            const string folder="Assets/_Project/ScriptableObjects/SinglePlayer/Hub/PracticeSpellItems";Directory.CreateDirectory(folder);AssetDatabase.Refresh();
            foreach(var spell in library.Available)
            {
                if(spell==null||!mapped.Add(spell))continue;
                string guid=AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(spell));string path=folder+"/"+guid+".asset";
                var item=AssetDatabase.LoadAssetAtPath<ItemDefinition>(path);
                if(item==null)
                {
                    item=ScriptableObject.CreateInstance<ItemDefinition>();var config=new SerializedObject(item);
                    config.FindProperty("_contentId").stringValue="practice.spell."+guid;
                    config.FindProperty("_displayName").stringValue=spell.DisplayName;
                    config.FindProperty("_spellDefinition").objectReferenceValue=spell;
                    config.FindProperty("_icon").objectReferenceValue=spell.Icon;
                    config.ApplyModifiedPropertiesWithoutUndo();AssetDatabase.CreateAsset(item,path);
                }
                entries.InsertArrayElementAtIndex(entries.arraySize);entries.GetArrayElementAtIndex(entries.arraySize-1).objectReferenceValue=item;
            }
            data.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(catalog);AssetDatabase.SaveAssets();
            if(!catalog.TryCreateIndex(out _,out var error))throw new InvalidOperationException(error);
        }
        private static GameObject ReturnPrefab(Scene hub)
        {foreach(var root in hub.GetRootGameObjects())foreach(var portal in root.GetComponentsInChildren<HubPracticePortal>(true))
            {var result=(GameObject)new SerializedObject(portal).FindProperty("_returnPortalPrefab").objectReferenceValue;if(result!=null)return result;}
            throw new InvalidOperationException("缺少据点回程门Prefab");}
        private static void Prepare()
        {
            for(int i=0;i<SceneManager.sceneCount;i++)if(SceneManager.GetSceneAt(i).isDirty)throw new InvalidOperationException("有未保存Scene；不代保存或丢弃");
            var original=SceneManager.GetActiveScene();var hashes=new List<string>();foreach(var map in Maps)hashes.Add(Hash(map));string hubHash=Hash(Hub);
            var hub=SceneManager.GetSceneByPath(Hub);bool closeHub=!hub.isLoaded;
            if(closeHub)hub=EditorSceneManager.OpenScene(Hub,OpenSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(hub);Catalog();var prefab=SinglePlayerItemCarrySetup.Generate(hub);var portal=ReturnPrefab(hub);
                foreach(var map in Maps)
                {
                    string review=ReviewPath(map);
                    if(!File.Exists(review)){if(!AssetDatabase.CopyAsset(map,review))throw new IOException(review);}
                    else{File.Copy(map,review,true);AssetDatabase.ImportAsset(review);}
                    var scene=EditorSceneManager.OpenScene(review,OpenSceneMode.Additive);
                    try{SceneManager.SetActiveScene(scene);Configure(scene,prefab,portal,false,true);Write(Path.GetFileNameWithoutExtension(map)+"-review.txt",Audit(scene));EditorSceneManager.SaveScene(scene,review);}
                    finally{EditorSceneManager.CloseScene(scene,true);SceneManager.SetActiveScene(hub);}
                }
                Write("prepared.txt",DateTime.UtcNow.ToString("O")+"\n"+Prefab);
            }
            finally
            {
                if(closeHub)EditorSceneManager.CloseScene(hub,true);SceneManager.SetActiveScene(original);
                Write("review-isolation.txt","HubHashUnchanged="+(Hash(Hub)==hubHash)+"\nP7HashUnchanged="+(Hash(Maps[0])==hashes[0])+"\nP8HashUnchanged="+(Hash(Maps[1])==hashes[1])+"\nOriginalDirty="+original.isDirty);
            }
        }
        private static void Configure(Scene scene,GameObject prefab,GameObject portal,bool undo,bool review)
        {
            var root=new GameObject("PracticeModernInventoryBootstrap_新版库存入口");if(undo)Undo.RegisterCreatedObjectUndo(root,"接入新版练习UI");
            var bootstrap=root.AddComponent<PracticeInventorySceneBootstrap>();Set(bootstrap,"_workspacePrefab",prefab);Set(bootstrap,"_returnPortalPrefab",portal);
            Set(bootstrap,"_catalog",AssetDatabase.LoadAssetAtPath<ItemCatalog>(Data+"Catalog.asset"));
            Set(bootstrap,"_startingInventory",AssetDatabase.LoadAssetAtPath<SinglePlayerStartingInventory>(Data+"StartingInventory.asset"));
            // 老准心是纯UGUI节点，没有CrosshairUI消费者；按这两图已核对的节点身份移除，避免白点与红十字叠加。
            foreach(var sceneRoot in scene.GetRootGameObjects())foreach(var node in sceneRoot.GetComponentsInChildren<Transform>(true))
                if(node!=null&&node.name.Trim()=="AimPoint")Remove(node.gameObject,undo);
            // 只清理旧法杖编辑Canvas和旧Mana/准心，保留关卡、HP、Guide、Pause、Fade与音乐。
            foreach(var sceneRoot in scene.GetRootGameObjects())
            {
                foreach(var editor in sceneRoot.GetComponentsInChildren<WandEditorController>(true))Remove(editor.gameObject,undo);
                foreach(var mana in sceneRoot.GetComponentsInChildren<PlayerManaBar>(true))Remove(mana.gameObject,undo);
                foreach(var cross in sceneRoot.GetComponentsInChildren<CrosshairUI>(true))
                {var dot=(GameObject)new SerializedObject(cross).FindProperty("_crosshair").objectReferenceValue;if(dot!=null)Remove(dot,undo);Remove(cross,undo);}
                foreach(var text in sceneRoot.GetComponentsInChildren<UnityEngine.UI.Text>(true))
                    if(text.text!=null && (text.text.Contains("Wand Editor")||text.text.Contains("法杖编辑")))
                    {if(undo)Undo.RecordObject(text,"更新背包入口提示");text.text=text.text.Replace("Wand Editor","背包").Replace("法杖编辑","背包");}
            }
            var hud=Find<DemoRunHudController>(scene);
            if(hud!=null)
            {
                var fields=new SerializedObject(hud);
                foreach(var field in new[]{"_objectiveText","_encounterText","_remainingEnemyText"})
                {
                    var text=(UnityEngine.UI.Text)fields.FindProperty(field).objectReferenceValue;
                    if(text!=null){if(undo)Undo.RecordObject(text.rectTransform,"让出手持栏");text.rectTransform.anchoredPosition+=Vector2.down*112;}
                }
            }
            var guide=Find<GameplayGuideController>(scene);
            if(guide!=null)
            {
                const string guidePath="Assets/_Project/ScriptableObjects/SinglePlayer/Hub/PracticeGameplayGuide.asset";
                var asset=AssetDatabase.LoadAssetAtPath<GameplayGuideDefinition>(guidePath);
                if(asset==null)
                {
                    asset=ScriptableObject.CreateInstance<GameplayGuideDefinition>();asset.Pages=new[]{
                        new GameplayGuideDefinition.Page{Title="试炼与回程",Body="清理四个区域，拾取法术奖励，再通过出口返回据点P7入口外。Boss练习通过据点P8入口进入。两张地图的蓝色回程门都可带着背包物品返回据点。"},
                        new GameplayGuideDefinition.Page{Title="移动与施法",Body="WASD移动，鼠标控制镜头，Space跳跃。1、2、3切换法杖，鼠标左键施放当前法杖程序。屏幕中心小白点是瞄准位置。"},
                        new GameplayGuideDefinition.Page{Title="背包与材料",Body="Tab打开背包，拖动法术进入法杖程序。新法术优先进入法术专区，满后进入背包；奖励装不下时保留领取入口，先整理背包。4–0选择道具；容器左键撒出，F采集，G投掷。F拾取附近物品；列表出现时滚轮切换目标。"}};
                    AssetDatabase.CreateAsset(asset,guidePath);
                }
                if(undo)Undo.RecordObject(guide,"更新试玩操作说明");Set(guide,"_definition",asset);
            }
            var world=Find<ElementWorldRuntime>(scene);
            if(world!=null)
            {
                var collect=world.GetComponent<MaterialInventoryTransferAdapter>();if(collect==null)collect=undo?Undo.AddComponent<MaterialInventoryTransferAdapter>(world.gameObject):world.gameObject.AddComponent<MaterialInventoryTransferAdapter>();
                var deposit=world.GetComponent<ContainerDepositAdapter>();if(deposit==null)deposit=undo?Undo.AddComponent<ContainerDepositAdapter>(world.gameObject):world.gameObject.AddComponent<ContainerDepositAdapter>();
                Set(collect,"_world",world);Set(deposit,"_world",world);
            }
            var gate=Find<StageExitPortal>(scene);if(review && gate!=null)SetString(gate,"_targetSceneName",ReviewPath(Maps[1]));
        }
        [MenuItem("Tools/Norn/练习地图/应用当前P7或P8新版UI（Undo，不保存）")]
        private static void ApplyCurrent()
        {
            // Additive 是同时加载完整世界，不是复用 UI。修改之前拒绝多地图编辑状态，
            // 避免留下三套角色、Camera、EventSystem，再把收尾工作转嫁给试玩者。
            if(HasIndependentSceneConflict(LoadedScenePaths()))
                throw new InvalidOperationException("当前同时加载多个独立地图；请先保存要保留的修改，再单独打开一张P7/P8地图。没有应用、保存或关闭任何场景。");
            var scene=SceneManager.GetActiveScene();if(scene.path!=Maps[0]&&scene.path!=Maps[1])throw new InvalidOperationException("请先打开P7_DemoRun或P8_BossField");
            if(Find<PracticeInventorySceneBootstrap>(scene)!=null){Write("current-applied.txt","当前地图已经接入新版入口");return;}
            var original=scene;var hub=SceneManager.GetSceneByPath(Hub);bool close=!hub.isLoaded;
            if(close)hub=EditorSceneManager.OpenScene(Hub,OpenSceneMode.Additive);
            try
            {var portal=ReturnPrefab(hub);SceneManager.SetActiveScene(scene);string hash=Hash(scene.path);Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("统一练习UI");
                Configure(scene,AssetDatabase.LoadAssetAtPath<GameObject>(Prefab),portal,true,false);Undo.FlushUndoRecordObjects();Undo.CollapseUndoOperations(group);EditorSceneManager.MarkSceneDirty(scene);
                Write(Path.GetFileNameWithoutExtension(scene.path)+"-applied.txt","FormalHashUnchanged="+(Hash(scene.path)==hash)+"\n"+Audit(scene));}
            finally{if(close)EditorSceneManager.CloseScene(hub,true);SceneManager.SetActiveScene(original);}
        }
        private static void Remove(Object target,bool undo){if(undo)Undo.DestroyObjectImmediate(target);else Object.DestroyImmediate(target);}
        internal static bool HasIndependentSceneConflict(string[] paths)
        {
            int count=0;
            foreach(string path in paths)
            {
                if(path==Hub || path==Maps[0] || path==Maps[1] || path==ReviewPath(Maps[0]) || path==ReviewPath(Maps[1]))count++;
                if(count>1)return true;
            }
            return false;
        }
        private static string[] LoadedScenePaths()
        {
            var paths=new List<string>();
            for(int i=0;i<SceneManager.sceneCount;i++)
            {var scene=SceneManager.GetSceneAt(i);if(scene.isLoaded)paths.Add(scene.path);}
            return paths.ToArray();
        }
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if(state!=PlayModeStateChange.ExitingEditMode || !HasIndependentSceneConflict(LoadedScenePaths()))return;
            // 取消进入 Play 只阻止运行，不保存、不丢弃任何编辑状态。正常 Single 传送和单图测试不受影响。
            EditorApplication.isPlaying=false;
            Write("multimap-play-blocked.txt",AuditLoadedScenes());
            GameLog.Error("据点、P7、P8必须各自独立运行；当前同时加载了多个完整地图，已取消进入Play。请保存要保留的修改，再单独打开目标地图。","PracticeSetup");
        }
        [MenuItem("Tools/Norn/练习地图/恢复单独据点入口（仅已保存场景）")]
        private static void RestoreSingleHub()
        {
            for(int i=0;i<SceneManager.sceneCount;i++)
                if(SceneManager.GetSceneAt(i).isDirty)
                    throw new InvalidOperationException("仍有未保存场景；恢复单据点入口已暂停，没有保存或关闭任何场景。请自行保存要保留的修改。");
            EditorSceneManager.OpenScene(Hub,OpenSceneMode.Single);
            Write("single-hub-restored.txt",AuditLoadedScenes());
        }
        private static string AuditLoadedScenes()
        {
            var text=new System.Text.StringBuilder(DateTime.UtcNow.ToString("O")+"\nPlaying="+EditorApplication.isPlaying+"\nActive="+SceneManager.GetActiveScene().path+"\nSceneCount="+SceneManager.sceneCount+"\n");
            for(int i=0;i<SceneManager.sceneCount;i++)
            {var scene=SceneManager.GetSceneAt(i);text.AppendLine("Loaded="+scene.isLoaded+" "+Audit(scene));}
            return text.ToString();
        }
        private static string Audit(Scene scene)
        {
            var text=new System.Text.StringBuilder(scene.path+"\nDirty="+scene.isDirty+"\n");
            foreach(var root in scene.GetRootGameObjects())foreach(var c in root.GetComponentsInChildren<MonoBehaviour>(true))
                if(c!=null&&(c is PracticeInventorySceneBootstrap||c is WandEditorController||c is PlayerManaBar||c is CrosshairUI||c is DemoRunHudController||c is GameplayGuideController||c is RunPauseCoordinator))text.AppendLine(c.GetType().Name+" : "+c.name);
            return text.ToString();
        }
        private static string Hash(string path){using var sha=System.Security.Cryptography.SHA256.Create();return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path)));}
        private static void Write(string name,string value){Directory.CreateDirectory(Evidence);File.WriteAllText(Path.Combine(Evidence,name),value);}
    }
}
