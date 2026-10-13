using System;
using System.IO;
using System.Collections.Generic;
using Game.Character;
using Game.Run;
using Game.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.EditorTools
{
    /// <summary>增量接线：只保存自己的Review/Sandbox/新资产，当前正式Scene只能Undo且不保存。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerItemPickupSetup
    {
        internal const string Review="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub_ItemReview.unity";
        internal const string Sandbox="Assets/_Project/Scenes/SinglePlayer/ItemPickupSandbox.unity";
        private const string Formal="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity";
        private const string Request="Temp/SinglePlayer/item-pickup-setup.request";
        private const string Evidence="TestResults/SinglePlayer/M6-T1-Items";
        private static bool _undo;
        static SinglePlayerItemPickupSetup()=>EditorApplication.update+=Poll;
        private static void Poll()
        {
            if(!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)return;
            string command=File.ReadAllText(Request).Trim();File.Delete(Request);
            try
            {
                if(EditorUtility.scriptCompilationFailed)throw new InvalidOperationException("编译失败，拒绝旧Assembly。");
                if(command=="prepare")Prepare();else if(command=="apply-current")ApplyCurrent();else if(command=="inspect")InspectModels();else if(command=="link-prefabs")LinkPickupPrefabs();else throw new ArgumentException(command);
            }
            catch(Exception e){Write("setup-error.txt",e.ToString());Game.Core.GameLog.Error(e.ToString(),"ItemsAuthoring");}
        }
        private static void Prepare()
        {
            for(int i=0;i<SceneManager.sceneCount;i++)if(SceneManager.GetSceneAt(i).isDirty)throw new InvalidOperationException("未保存Scene；不替用户保存或丢弃。");
            if(SceneManager.GetSceneByPath(Review).isLoaded || SceneManager.GetSceneByPath(Sandbox).isLoaded)throw new InvalidOperationException("测试Scene由用户打开，拒绝覆盖。");
            if(!File.Exists(Review) && !AssetDatabase.CopyAsset(Formal,Review))throw new IOException("复制Review失败。");
            var original=SceneManager.GetActiveScene();var scene=EditorSceneManager.OpenScene(Review,OpenSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);_undo=false;
                var definitions=CreateDefinitions();RegisterCatalog(Find<SinglePlayerInventorySession>(scene),definitions);
                Apply(scene);EditorSceneManager.SaveScene(scene,Review);
            }
            finally{EditorSceneManager.CloseScene(scene,true);SceneManager.SetActiveScene(original);}
            if(!File.Exists(Sandbox) && !AssetDatabase.CopyAsset(Review,Sandbox))throw new IOException("创建Sandbox失败。");
            scene=EditorSceneManager.OpenScene(Sandbox,OpenSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);Apply(scene);
                var session=Find<SinglePlayerInventorySession>(scene);Set(session,"_sandboxProfile",true);Set(session,"_sandboxName","ItemPickupSandbox");Set(session,"_worldSceneKey","items.sandbox");
                var actor=Find<WizardController>(scene).transform;
                var gameplay=Root(scene,"__Gameplay");
                var existing=gameplay.Find("WorldItems_可拾取物品");
                var world=existing!=null?existing.gameObject:New("WorldItems_可拾取物品",gameplay);
                var definitions=CreateDefinitions();
                for(int i=0;i<definitions.Count;i++)
                {
                    // 工具重复执行保留已有摆放和唯一SpawnId，不追加第二批同ID物品。
                    bool placed=false;foreach(var placedPoint in world.GetComponentsInChildren<WorldItemSpawnPoint>(true))if(placedPoint.SpawnId=="sample."+i)placed=true;
                    if(placed)continue;
                    string prefabPath="Assets/_Project/Prefabs/Items/"+definitions[i].name+".prefab";
                    var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                    var point=(GameObject)PrefabUtility.InstantiatePrefab(prefab,scene);point.transform.SetParent(world.transform,false);
                    point.transform.position=actor.position+new Vector3((i%6-2)*1.2f,.3f,2+i/6*1.2f);
                    var spawn=point.GetComponent<WorldItemSpawnPoint>();Set(spawn,"_spawnId","sample."+i);Set(spawn,"_sceneKey","items.sandbox");Set(spawn,"_session",session);
                }
                EditorSceneManager.SaveScene(scene,Sandbox);
            }
            finally{EditorSceneManager.CloseScene(scene,true);SceneManager.SetActiveScene(original);}
            SessionState.SetString("Norn.HubAlignment.ReviewPath",Review);Write("prepared.txt",DateTime.UtcNow.ToString("O")+"\n"+Review+"\n"+Sandbox);
        }
        [MenuItem("Tools/Norn/物品/应用自动滚动、三杖展开与F拾取（Undo，不保存）")]
        private static void ApplyCurrent()
        {
            var scene=SceneManager.GetActiveScene();if(scene.path!=Formal || EditorApplication.isPlaying)throw new InvalidOperationException("仅正式据点EditMode。");
            Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("据点物品拾取与背包体验");_undo=true;
            string before=PreservedUi(scene);
            try{Apply(scene);string after=PreservedUi(scene);if(before!=after)throw new InvalidOperationException("外围布局或右侧详情发生意外改动。");
                Undo.CollapseUndoOperations(group);EditorSceneManager.MarkSceneDirty(scene);SessionState.EraseString("Norn.HubAlignment.ReviewPath");
                Write("current-applied.txt",DateTime.UtcNow.ToString("O")+"\n单Undo应用；未保存当前Scene。\n外围RectTransform与RightPart全部组件序列化数据前后一致。");}
            catch{Undo.RevertAllDownToGroup(group);throw;}finally{_undo=false;}
        }
        private static string PreservedUi(Scene scene)
        {
            var result=new System.Text.StringBuilder();
            foreach(var root in scene.GetRootGameObjects())foreach(var ui in root.GetComponentsInChildren<SinglePlayerInventoryController>(true))
            {
                var panel=Get<GameObject>(ui,"_panelRoot");if(panel==null)continue;
                result.Append(EditorJsonUtility.ToJson(panel.transform));
                foreach(Transform child in panel.transform)result.Append(EditorJsonUtility.ToJson(child));
                var right=panel.transform.Find("RightPart");if(right==null)continue;
                foreach(var component in right.GetComponentsInChildren<Component>(true))if(component!=null)result.Append(EditorJsonUtility.ToJson(component));
            }
            return result.ToString();
        }
        private static void InspectModels()
        {
            var report=new System.Text.StringBuilder(DateTime.UtcNow.ToString("O")+"\n");
            foreach(string guid in AssetDatabase.FindAssets("t:ItemDefinition",new[]{"Assets/_Project/ScriptableObjects/SinglePlayer/Items"}))
            {
                var definition=AssetDatabase.LoadAssetAtPath<ItemDefinition>(AssetDatabase.GUIDToAssetPath(guid));report.AppendLine(definition.name);
                foreach(var renderer in definition.WorldModel.GetComponentsInChildren<Renderer>(true))foreach(var material in renderer.sharedMaterials)
                    report.AppendLine(material==null?"  MISSING material":"  "+material.name+" / "+material.shader.name+" / supported="+material.shader.isSupported);
            }
            Write("model-materials.txt",report.ToString());
        }
        private static void Apply(Scene scene)
        {
            var session=Find<SinglePlayerInventorySession>(scene);Set(session,"_worldSceneKey","hub.map1");
            var nearby=Find<NearbyPickupController>(scene);var player=Find<WizardController>(scene);
            Set(Add<NearbyPickupInput>(player.gameObject),"_pickups",nearby);
            foreach(var root in scene.GetRootGameObjects())foreach(var panel in root.GetComponentsInChildren<WandProgramPanelView>(true))
            {
                var fold=panel.transform.Find("Fold");if(fold!=null){if(_undo)Undo.DestroyObjectImmediate(fold.gameObject);else UnityEngine.Object.DestroyImmediate(fold.gameObject);}
                var expanded=panel.transform.Find("Expanded");if(expanded!=null){Record(expanded.gameObject);expanded.gameObject.SetActive(true);}
            }
            SinglePlayerInventoryController backpack=null;
            foreach(var root in scene.GetRootGameObjects())foreach(var ui in root.GetComponentsInChildren<SinglePlayerInventoryController>(true))
                if(Get<BackpackWorkspaceView>(ui,"_backpackWorkspace")!=null)backpack=ui;
            if(backpack==null)throw new InvalidOperationException("找不到独立背包。");
            var grid=Get<InventoryGridView>(backpack,"_gridView");var scroll=grid.GetComponentInParent<ScrollRect>();
            var auto=Add<InventoryDragAutoScroll>(scroll.gameObject);Set(auto,"_scroll",scroll);
            var promptRoot=Root(scene,"__UI").Find("WorldPickupPrompt_附近拾取");
            if(promptRoot==null)promptRoot=New("WorldPickupPrompt_附近拾取",Root(scene,"__UI")).transform;
            var prompt=Add<WorldPickupPromptView>(promptRoot.gameObject);Set(prompt,"_session",session);Set(prompt,"_inventory",backpack);
            var canvasTransform=promptRoot.Find("Entry");
            if(canvasTransform==null)
            {
                var go=New("Entry",promptRoot,true);var rect=(RectTransform)go.transform;rect.sizeDelta=new Vector2(500,166);rect.localScale=Vector3.one*.0035f;
                var canvas=Add<Canvas>(go);canvas.renderMode=RenderMode.WorldSpace;canvas.sortingOrder=50;
                var bg=Add<Image>(go);bg.sprite=AssetDatabase.LoadAssetAtPath<Sprite>("Assets/_Project/Art/Prefabs/Item/PickupItemEntry.png");bg.preserveAspect=true;bg.raycastTarget=false;
                var label=New("Label_F拾取",rect,true).AddComponent<Text>();label.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");label.fontSize=36;label.alignment=TextAnchor.MiddleLeft;label.color=Color.white;label.raycastTarget=false;
                var lr=label.rectTransform;lr.anchorMin=Vector2.zero;lr.anchorMax=Vector2.one;lr.offsetMin=new Vector2(120,30);lr.offsetMax=new Vector2(-25,-30);
                var icon=New("ItemIcon",rect,true).AddComponent<Image>();icon.raycastTarget=false;icon.preserveAspect=true;
                icon.rectTransform.anchorMin=icon.rectTransform.anchorMax=new Vector2(0,.5f);icon.rectTransform.anchoredPosition=new Vector2(62,0);icon.rectTransform.sizeDelta=Vector2.one*80;
                canvasTransform=rect;Set(prompt,"_label",label);Set(prompt,"_icon",icon);
            }
            Set(prompt,"_visual",canvasTransform.gameObject);Record(canvasTransform.gameObject);canvasTransform.gameObject.SetActive(false);
        }
        private static List<ItemDefinition> CreateDefinitions()
        {
            string source="Assets/_Project/Art/Prefabs/Item";var definitions=new List<ItemDefinition>();
            string[] files=Directory.GetFiles(source,"*.prefab",SearchOption.AllDirectories);Array.Sort(files,StringComparer.Ordinal);
            foreach(string file in files)
            {
                string path=file.Replace('\\','/'),folder=Path.GetFileName(Path.GetDirectoryName(file)),name=Path.GetFileNameWithoutExtension(file);
                if(folder!="Herb" && folder!="Container" && folder!="Food")continue;
                string destination="Assets/_Project/ScriptableObjects/SinglePlayer/Items/"+folder+"/"+name+".asset";EnsureFolder(Path.GetDirectoryName(destination).Replace('\\','/'));
                var definition=AssetDatabase.LoadAssetAtPath<ItemDefinition>(destination);
                if(definition==null)
                {
                    definition=ScriptableObject.CreateInstance<ItemDefinition>();var data=new SerializedObject(definition);
                    data.FindProperty("_contentId").stringValue="item."+folder.ToLowerInvariant()+"."+name.ToLowerInvariant();
                    data.FindProperty("_kind").intValue=(int)(folder=="Container"?ItemKind.Container:folder=="Herb"?ItemKind.Alchemy:ItemKind.Consumable);
                    data.FindProperty("_category").intValue=(int)(folder=="Container"?ItemCategory.Container:folder=="Herb"?ItemCategory.Herb:ItemCategory.Food);
                    data.FindProperty("_displayName").stringValue=Display(name);data.FindProperty("_worldModel").objectReferenceValue=AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    int width=1,height=1;
                    if(folder=="Herb"){height=name.EndsWith("02")?3:2;if(name.EndsWith("03") || name.EndsWith("04"))width=2;}
                    if(folder=="Container"){width=name.EndsWith("01")?1:2;height=name.EndsWith("03")?3:2;data.FindProperty("_containerCapacityGmu").intValue=name.EndsWith("01")?150:name.EndsWith("02")?300:600;}
                    if(name.StartsWith("Bread")){width=2;height=1;}if(name=="Carrot")height=2;
                    if(name=="Meat" || name=="CheeseBig")width=height=2;if(name=="Pumpkin")width=height=3;
                    data.FindProperty("_shapeWidth").intValue=width;data.FindProperty("_shapeHeight").intValue=height;
                    bool lShape=name=="Meat" || name=="Herb_Flower_03";var cells=data.FindProperty("_occupiedCells");cells.arraySize=width*height-(lShape?1:0);
                    for(int i=0;i<cells.arraySize;i++)cells.GetArrayElementAtIndex(i).vector2IntValue=new Vector2Int(i%width,i/width);
                    data.ApplyModifiedPropertiesWithoutUndo();AssetDatabase.CreateAsset(definition,destination);
                }
                definitions.Add(definition);string wrapper="Assets/_Project/Prefabs/Items/"+name+".prefab";EnsureFolder("Assets/_Project/Prefabs/Items");
                if(!File.Exists(wrapper))
                {
                    var root=new GameObject(name+"_可拾取");
                    try{var spawn=root.AddComponent<WorldItemSpawnPoint>();Set(spawn,"_definition",definition);
                        var visual=(GameObject)PrefabUtility.InstantiatePrefab(definition.WorldModel);visual.transform.SetParent(root.transform,false);
                        PrefabUtility.SaveAsPrefabAsset(root,wrapper);}
                    finally{UnityEngine.Object.DestroyImmediate(root);}
                }
                // 只补缺失引用。现有包装Prefab和Scene Override属于用户，不能为接线重建/归一化它们。
                if(definition.PickupPrefab==null)
                {
                    var data=new SerializedObject(definition);data.FindProperty("_pickupPrefab").objectReferenceValue=AssetDatabase.LoadAssetAtPath<GameObject>(wrapper);
                    data.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(definition);AssetDatabase.SaveAssetIfDirty(definition);
                }
            }
            return definitions;
        }
        [MenuItem("Tools/Norn/物品/补齐可拾取Prefab引用（保留现有Prefab和场景）")]
        private static void LinkPickupPrefabs()
        {var definitions=CreateDefinitions();Write("pickup-prefabs-linked.txt",DateTime.UtcNow.ToString("O")+"\nLinked definitions: "+definitions.Count+"\n未保存场景、未改Scale、未换SpawnId。");}
        private static string Display(string name)
        {
            switch(name){case "Apple":return "苹果";case "Beetroot":return "甜菜";case "Carrot":return "胡萝卜";case "Meat":return "肉块";case "Pumpkin":return "南瓜";case "CheeseBig":return "大块奶酪";case "CheeseSmall":return "小块奶酪";}
            if(name.StartsWith("Bread"))return "面包 "+name.Substring(name.Length-1);
            if(name.StartsWith("Herb"))return "草药花 "+name.Substring(name.Length-2);
            if(name.StartsWith("Container"))return "容器 "+name.Substring(name.Length-2);return name;
        }
        private static void RegisterCatalog(SinglePlayerInventorySession session,List<ItemDefinition> definitions)
        {
            var catalog=Get<ItemCatalog>(session,"_catalog");var data=new SerializedObject(catalog);var items=data.FindProperty("_definitions");
            foreach(var definition in definitions)
            {bool found=false;for(int i=0;i<items.arraySize;i++)if(items.GetArrayElementAtIndex(i).objectReferenceValue==definition)found=true;
                if(!found){int index=items.arraySize++;items.GetArrayElementAtIndex(index).objectReferenceValue=definition;}}
            data.ApplyModifiedPropertiesWithoutUndo();if(!catalog.TryCreateIndex(out _,out var error))throw new InvalidOperationException(error);AssetDatabase.SaveAssetIfDirty(catalog);
        }
        private static void EnsureFolder(string path){if(AssetDatabase.IsValidFolder(path))return;string parent=Path.GetDirectoryName(path).Replace('\\','/');EnsureFolder(parent);AssetDatabase.CreateFolder(parent,Path.GetFileName(path));}
        private static GameObject New(string name,Transform parent,bool rect=false)
        {var go=rect?new GameObject(name,typeof(RectTransform)):new GameObject(name);if(_undo)Undo.RegisterCreatedObjectUndo(go,"物品拾取接线");go.transform.SetParent(parent,false);return go;}
        private static Transform Root(Scene scene,string name){foreach(var root in scene.GetRootGameObjects())if(root.name==name)return root.transform;var go=new GameObject(name);if(_undo)Undo.RegisterCreatedObjectUndo(go,"物品分组");return go.transform;}
        private static T Find<T>(Scene scene)where T:Component{foreach(var root in scene.GetRootGameObjects()){var value=root.GetComponentInChildren<T>(true);if(value!=null)return value;}throw new InvalidOperationException("缺少"+typeof(T).Name);}
        private static T Add<T>(GameObject go)where T:Component{var existing=go.GetComponent<T>();return existing!=null?existing:_undo?Undo.AddComponent<T>(go):go.AddComponent<T>();}
        private static void Record(UnityEngine.Object value){if(_undo)Undo.RecordObject(value,"物品拾取接线");}
        private static T Get<T>(UnityEngine.Object target,string field)where T:UnityEngine.Object=>new SerializedObject(target).FindProperty(field)?.objectReferenceValue as T;
        private static void Set(UnityEngine.Object target,string field,object value)
        {Record(target);var data=new SerializedObject(target);var p=data.FindProperty(field);if(p==null)throw new InvalidOperationException(field);
            if(value is bool b)p.boolValue=b;else if(value is string s)p.stringValue=s;else p.objectReferenceValue=value as UnityEngine.Object;data.ApplyModifiedPropertiesWithoutUndo();}
        private static void Write(string name,string value){Directory.CreateDirectory(Evidence);File.WriteAllText(Evidence+"/"+name,value);}
    }
}
