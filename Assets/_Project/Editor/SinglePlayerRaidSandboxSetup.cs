using System;
using System.IO;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.ElementField;
using Game.Run;
using Game.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
namespace Game.EditorTools
{
    [InitializeOnLoad]
    internal static class SinglePlayerRaidSandboxSetup
    {
        private const string Root = "Assets/_Project/ScriptableObjects/Tests/SinglePlayerRaids/";
        private const string HubScene = "Assets/_Project/Scenes/Tests/DepartureSandbox.unity";
        private const string WorldScene = "Assets/_Project/Scenes/Tests/RaidWorldSandbox.unity";
        private const string Request = "Temp/SinglePlayer/create-raid-sandbox.request";
        static SinglePlayerRaidSandboxSetup() { EditorApplication.update += Poll; }
        private static void Poll()
        {
            const string repair="Temp/SinglePlayer/repair-raid-sandbox.request";
            if(File.Exists(repair) && !EditorApplication.isCompiling && !EditorApplication.isUpdating && !EditorApplication.isPlayingOrWillChangePlaymode && !SessionState.GetBool("Norn.SinglePlayer.Tests.Running",false))
            {File.Delete(repair);Repair();return;}
            if (!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating || SessionState.GetBool("Norn.SinglePlayer.Tests.Running",false)) return;
            File.Delete(Request); try { Create(); } catch (Exception e) { Report("Error",e.ToString()); GameLog.Error(e.ToString(),"RaidSandbox"); }
        }
        [MenuItem("Tools/Norn/Single Player/Create Raid Sandbox")]
        private static void Create()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorUtility.scriptCompilationFailed) { Report("Blocked","Play/编译失败"); return; }
            for(int i=0;i<SceneManager.sceneCount;i++)if(SceneManager.GetSceneAt(i).isDirty){Report("Blocked","dirty Scene，未保存/丢弃");return;}
            if(File.Exists(HubScene)||File.Exists(WorldScene)||AssetDatabase.IsValidFolder(Root.TrimEnd('/'))){Report("Existing","已有或部分资产，拒绝覆盖");return;}
            AssetDatabase.CreateFolder("Assets/_Project/ScriptableObjects/Tests","SinglePlayerRaids");
            var seed=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<SinglePlayerStartingInventory>("Assets/_Project/ScriptableObjects/Tests/SinglePlayerAttachments/StartingInventory.asset"));AssetDatabase.CreateAsset(seed,Root+"StartingInventory.asset");
            var catalog=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/_Project/ScriptableObjects/Tests/SinglePlayerContainers/Catalog.asset"));AssetDatabase.CreateAsset(catalog,Root+"Catalog.asset");
            var small=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Tests/SinglePlayerWands/bag.asset"));AssetDatabase.CreateAsset(small,Root+"StarterBag.asset");
            SetString(small,"_contentId","raidtest.starterbag");SetString(small,"_displayName","免费小背包");SetInt(small,"_backpackWidth",2);SetInt(small,"_backpackHeight",2);
            var catData=new SerializedObject(catalog);var defs=catData.FindProperty("_definitions");defs.arraySize++;defs.GetArrayElementAtIndex(defs.arraySize-1).objectReferenceValue=small;catData.ApplyModifiedPropertiesWithoutUndo();
            var condition=ScriptableObject.CreateInstance<ExtractionConditionDefinition>();condition.RequiredCollectedGmu=10;AssetDatabase.CreateAsset(condition,Root+"Condition.asset");
            var raid=ScriptableObject.CreateInstance<RaidDefinition>();raid.WorldScenePath=WorldScene;raid.Condition=condition;raid.SpawnPosition=new Vector3(0,1,-3);raid.ExitPosition=new Vector3(0,.5f,5);AssetDatabase.CreateAsset(raid,Root+"Raid.asset");
            var kit=ScriptableObject.CreateInstance<StarterKitDefinition>();kit.Entries=new[]{new StarterKitDefinition.Entry{ContentId="wandtest.wand.a"},new StarterKitDefinition.Entry{ContentId="wandtest.fire",Unlimited=true},new StarterKitDefinition.Entry{ContentId=small.ContentId},new StarterKitDefinition.Entry{ContentId="containertest.bottle"}};AssetDatabase.CreateAsset(kit,Root+"StarterKit.asset");
            var loot=ScriptableObject.CreateInstance<SpellLootTable>();loot.DropChance=.35f;loot.Entries=new[]{new SpellLootTable.Entry{ContentId="wandtest.fire",Weight=3,Charges=5},new SpellLootTable.Entry{ContentId="wandtest.ice",Weight=1,Charges=3}};AssetDatabase.CreateAsset(loot,Root+"Loot.asset");
            var original=SceneManager.GetActiveScene();
            Scene world=EditorSceneManager.OpenScene("Assets/_Project/Scenes/Tests/AttachmentSandbox.unity",OpenSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(world);
                foreach(var root in world.GetRootGameObjects())
                {
                    bool keep=root.GetComponentInChildren<ElementWorldRuntime>(true)!=null || root.GetComponent<Light>()!=null
                        || root.GetComponent<Collider>()!=null && root.GetComponentInChildren<WizardController>(true)==null;
                    foreach(var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))if(behaviour!=null)
                    { string name=behaviour.GetType().Name;if(name.Contains("LiquidSurfaceRenderer")||name=="GasFieldVolumeRenderer"||name=="FixedSolidWorldRenderer")keep=true; }
                    if(!keep)UnityEngine.Object.DestroyImmediate(root);
                }
                for(int i=0;i<2;i++)
                {
                    var enemy=GameObject.CreatePrimitive(PrimitiveType.Cube);enemy.name="Raid Graybox Enemy "+i;enemy.transform.position=new Vector3(-2+i*4,1,2);enemy.transform.localScale=new Vector3(.8f,2,.8f);
                    var hp=enemy.AddComponent<HealthComponent>();SetInt(hp,"_teamId",2);SetFloat(hp,"_maxHp",30);
                    var adapter=enemy.AddComponent<SpellLootDropAdapter>();adapter.Table=loot;
                    Tint(enemy,new Color(.8f,.15f,.12f));
                }
                AddAnchor(world,raid.SpawnPosition);
                var exit=GameObject.CreatePrimitive(PrimitiveType.Cylinder);exit.name="Basic Extraction Exit";exit.transform.position=raid.ExitPosition;exit.transform.localScale=new Vector3(2.5f,.05f,2.5f);
                UnityEngine.Object.DestroyImmediate(exit.GetComponent<Collider>());exit.AddComponent<ExtractionZone>();Tint(exit,Color.green);
                if(!EditorSceneManager.SaveScene(world,WorldScene))throw new IOException("World保存失败");
            }
            finally{EditorSceneManager.CloseScene(world,true);if(original.IsValid()&&original.isLoaded)SceneManager.SetActiveScene(original);}
            Scene hub=EditorSceneManager.OpenScene("Assets/_Project/Scenes/Tests/AttachmentSandbox.unity",OpenSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(hub);var session=Find<SinglePlayerInventorySession>(hub);var ui=Find<SinglePlayerInventoryController>(hub);var wizard=Find<WizardController>(hub);
                Set(session,"_catalog",catalog);Set(session,"_startingInventory",seed);SetString(session,"_sandboxName","DepartureSandbox");
                var run=session.gameObject.AddComponent<SinglePlayerRunCoordinator>();Set(run,"_session",session);Set(run,"_definition",raid);Set(run,"_starter",kit);Set(run,"_player",wizard.GetComponent<HealthComponent>());Set(run,"_pause",Find<RunPauseCoordinator>(hub));
                Set(run,"_hubWorld",Find<ElementWorldRuntime>(hub).gameObject);
                SetEnvironment(run,hub);
                var controls=Rect(ui.transform,"RaidControls",new Vector2(118,-562),new Vector2(360,180));controls.gameObject.AddComponent<Image>().color=new Color(.04f,.06f,.1f,.95f);
                var selection=Label(controls,"Protection",new Vector2(8,-4),new Vector2(345,28),"保护0/2 · 先点击具体法术卡");
                var message=Label(controls,"Message",new Vector2(8,-32),new Vector2(345,56),"I整备 → 保护具体卡 → 出发；失败可补缺");
                var protect=Button(controls,"Protect","保护所选",new Vector2(8,-92));var unprotect=Button(controls,"Unprotect","取消保护",new Vector2(126,-92));var depart=Button(controls,"Depart","出发",new Vector2(244,-92));
                var starter=Button(controls,"Starter","基础补缺",new Vector2(8,-130));var abandon=Button(controls,"Abandon","放弃本局",new Vector2(126,-130));var retry=Button(controls,"Retry","重试保存",new Vector2(244,-130));
                var preparation=ui.gameObject.AddComponent<RaidPreparationView>();Set(preparation,"_session",session);Set(preparation,"_inventory",ui);Set(preparation,"_controls",controls.gameObject);
                AddCancel(preparation,controls);
                Set(preparation,"_protect",protect);Set(preparation,"_unprotect",unprotect);Set(preparation,"_depart",depart);Set(preparation,"_starter",starter);Set(preparation,"_abandon",abandon);Set(preparation,"_retry",retry);Set(preparation,"_selection",selection);Set(preparation,"_message",message);
                var conditionText=Label(ui.transform,"ExtractionCondition",new Vector2(460,-780),new Vector2(450,24),"据点 · I整备 / 出发 / 基础补给");var timer=Label(ui.transform,"ExtractionTimer",new Vector2(460,-806),new Vector2(550,26),"轻量出口：本局采集10GMU，进绿色圆盘等3秒");
                var hud=ui.gameObject.AddComponent<ExtractionHud>();Set(hud,"_session",session);Set(hud,"_inventory",ui);Set(hud,"_condition",conditionText);Set(hud,"_timer",timer);
                var resultText=Label(ui.transform,"RaidResult",new Vector2(460,-838),new Vector2(820,30),"");var result=ui.gameObject.AddComponent<RaidResultView>();Set(result,"_session",session);Set(result,"_inventory",ui);Set(result,"_result",resultText);
                controls.gameObject.SetActive(false);
                if(!EditorSceneManager.SaveScene(hub,HubScene))throw new IOException("Hub保存失败");
            }
            finally{EditorSceneManager.CloseScene(hub,true);if(original.IsValid()&&original.isLoaded)SceneManager.SetActiveScene(original);}
            foreach(string guid in AssetDatabase.FindAssets("",new[]{Root.TrimEnd('/')}))AssetDatabase.SaveAssetIfDirty(AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(guid)));
            Report("Created",null);
        }
        private static void Tint(GameObject go,Color color)
        {
            // MaterialPropertyBlock 属于运行时状态，不会写进 Scene；测试标识色需要可序列化的 Material 资产。
            string path=Root+(color==Color.green ? "Exit.mat" : "Enemy.mat");
            var material=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(material==null){material=new Material(Shader.Find("Universal Render Pipeline/Lit"));material.SetColor("_BaseColor",color);AssetDatabase.CreateAsset(material,path);}
            go.GetComponent<Renderer>().sharedMaterial=material;
        }
        private static void AddAnchor(Scene scene,Vector3 position)
        {
            var anchor=new GameObject("Raid Interest Anchor");SceneManager.MoveGameObjectToScene(anchor,scene);anchor.transform.position=position;anchor.AddComponent<RaidInterestAnchor>();
            Set(Find<ElementWorldRuntime>(scene),"_interestPoint",anchor.transform);
        }
        private static void Repair()
        {
            for(int i=0;i<SceneManager.sceneCount;i++)if(SceneManager.GetSceneAt(i).isDirty){Report("Blocked","dirty Scene，未修改");return;}
            var original=SceneManager.GetActiveScene();var world=EditorSceneManager.OpenScene(WorldScene,OpenSceneMode.Additive);
            try{SceneManager.SetActiveScene(world);if(FindOrNull<RaidInterestAnchor>(world)==null)AddAnchor(world,AssetDatabase.LoadAssetAtPath<RaidDefinition>(Root+"Raid.asset").SpawnPosition);EditorSceneManager.SaveScene(world);}
            finally{EditorSceneManager.CloseScene(world,true);if(original.IsValid()&&original.isLoaded)SceneManager.SetActiveScene(original);}
            var hub=EditorSceneManager.OpenScene(HubScene,OpenSceneMode.Additive);
            try{SceneManager.SetActiveScene(hub);SetEnvironment(Find<SinglePlayerRunCoordinator>(hub),hub);var view=Find<RaidPreparationView>(hub);var ui=Find<SinglePlayerInventoryController>(hub);var controls=ui.transform.Find("RaidControls").GetComponent<RectTransform>();if(controls.Find("CancelExtraction")==null)AddCancel(view,controls);if(controls.Find("StartExtraction")==null)Set(view,"_startExtraction",Button(controls,"StartExtraction","开始撤离",new Vector2(94,-168)));ArrangeControls(controls);EditorSceneManager.SaveScene(hub);Report("Repaired",null);}
            finally{EditorSceneManager.CloseScene(hub,true);if(original.IsValid()&&original.isLoaded)SceneManager.SetActiveScene(original);}
        }
        private static void AddCancel(RaidPreparationView view,RectTransform controls)
        {Set(view,"_cancelExtraction",Button(controls,"CancelExtraction","取消撤离",new Vector2(8,-168)));Set(view,"_startExtraction",Button(controls,"StartExtraction","开始撤离",new Vector2(94,-168)));ArrangeControls(controls);}
        private static void ArrangeControls(RectTransform controls)
        {
            // 保持在仓库栏范围，避免遮住可拖拽的程序槽；修复只保存本次生成的独立Scene。
            controls.sizeDelta=new Vector2(270,214);int buttonIndex=0;
            for(int i=0;i<controls.childCount;i++)
            {
                var child=controls.GetChild(i).GetComponent<RectTransform>();
                if(child.GetComponent<Button>()!=null){child.anchoredPosition=new Vector2(8+86*(buttonIndex%3),-92-38*(buttonIndex/3));child.sizeDelta=new Vector2(80,30);buttonIndex++;}
                else child.sizeDelta=new Vector2(254,child.sizeDelta.y);
            }
        }
        private static void SetEnvironment(SinglePlayerRunCoordinator run,Scene scene)
        {
            var roots=new System.Collections.Generic.List<GameObject>();
            foreach(var root in scene.GetRootGameObjects())
            {
                bool keep=root.GetComponentInChildren<ElementWorldRuntime>(true)!=null||root.GetComponent<Light>()!=null
                    ||root.GetComponent<Collider>()!=null&&root.GetComponentInChildren<WizardController>(true)==null;
                foreach(var b in root.GetComponentsInChildren<MonoBehaviour>(true))if(b!=null){string n=b.GetType().Name;if(n.Contains("LiquidSurfaceRenderer")||n=="GasFieldVolumeRenderer"||n=="FixedSolidWorldRenderer")keep=true;}
                if(keep)roots.Add(root);
            }
            var data=new SerializedObject(run);var array=data.FindProperty("_hubEnvironment");array.arraySize=roots.Count;
            for(int i=0;i<roots.Count;i++)array.GetArrayElementAtIndex(i).objectReferenceValue=roots[i];data.ApplyModifiedPropertiesWithoutUndo();
        }
        private static T FindOrNull<T>(Scene scene)where T:Component{foreach(var root in scene.GetRootGameObjects()){var c=root.GetComponentInChildren<T>(true);if(c!=null)return c;}return null;}
        private static T Find<T>(Scene scene)where T:Component{foreach(var root in scene.GetRootGameObjects()){var c=root.GetComponentInChildren<T>(true);if(c!=null)return c;}throw new InvalidOperationException(typeof(T).Name+"缺失");}
        private static RectTransform Rect(Transform parent,string name,Vector2 position,Vector2 size)
        {var go=new GameObject(name,typeof(RectTransform));var r=go.GetComponent<RectTransform>();r.SetParent(parent,false);r.anchorMin=r.anchorMax=r.pivot=new Vector2(0,1);r.anchoredPosition=position;r.sizeDelta=size;return r;}
        private static Text Label(Transform parent,string name,Vector2 pos,Vector2 size,string value){var t=Rect(parent,name,pos,size).gameObject.AddComponent<Text>();t.text=value;t.fontSize=16;t.color=Color.white;t.raycastTarget=false;return t;}
        private static Button Button(Transform parent,string name,string label,Vector2 pos)
        {var go=DefaultControls.CreateButton(new DefaultControls.Resources());go.name=name;var r=go.GetComponent<RectTransform>();r.SetParent(parent,false);r.anchorMin=r.anchorMax=r.pivot=new Vector2(0,1);r.anchoredPosition=pos;r.sizeDelta=new Vector2(110,30);go.GetComponentInChildren<Text>().text=label;return go.GetComponent<Button>();}
        private static void Set(UnityEngine.Object target,string field,UnityEngine.Object value){var s=new SerializedObject(target);s.FindProperty(field).objectReferenceValue=value;s.ApplyModifiedPropertiesWithoutUndo();}
        private static void SetString(UnityEngine.Object target,string field,string value){var s=new SerializedObject(target);s.FindProperty(field).stringValue=value;s.ApplyModifiedPropertiesWithoutUndo();}
        private static void SetInt(UnityEngine.Object target,string field,int value){var s=new SerializedObject(target);s.FindProperty(field).intValue=value;s.ApplyModifiedPropertiesWithoutUndo();}
        private static void SetFloat(UnityEngine.Object target,string field,float value){var s=new SerializedObject(target);s.FindProperty(field).floatValue=value;s.ApplyModifiedPropertiesWithoutUndo();}
        private static void Report(string state,string error){Directory.CreateDirectory("Temp/SinglePlayer");File.WriteAllText("Temp/SinglePlayer/raid-sandbox-report.json",JsonUtility.ToJson(new Result{State=state,Error=error},true));}
        [Serializable]private sealed class Result{public string State,Error;}
    }
}
