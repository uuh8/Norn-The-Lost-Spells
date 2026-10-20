using System;
using System.IO;
using Game.Core;
using Game.Run;
using Game.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object=UnityEngine.Object;

namespace Game.EditorTools
{
    /// <summary>五项统一Review；正式据点只通过Undo改父区域/删除旧右区，从不代保存。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerInventoryRefinementSetup
    {
        private const string Request="Temp/SinglePlayer/inventory-refinement.request";
        private const string Hub="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity";
        private const string Review="Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_UnifiedItemsReview.unity";
        private const string Evidence="TestResults/SinglePlayer/M6-T21-ItemsAndPopup";
        static SinglePlayerInventoryRefinementSetup(){EditorApplication.update+=Poll;}
        private static void Poll()
        {
            if(!File.Exists(Request)||EditorApplication.isCompiling||EditorApplication.isUpdating||EditorApplication.isPlayingOrWillChangePlaymode)return;
            string command=File.ReadAllText(Request).Trim();File.Delete(Request);
            try
            {
                if(EditorUtility.scriptCompilationFailed)throw new InvalidOperationException("编译失败，禁止旧DLL接线。");
                for(int i=0;i<SceneManager.sceneCount;i++)if(SceneManager.GetSceneAt(i).isDirty)throw new InvalidOperationException("有未保存正式Scene，不代保存/丢弃。");
                if(command=="prepare")Prepare();
                else if(command=="apply-current")
                {
                    var scene=SceneManager.GetActiveScene();if(scene.path!=Hub || SceneManager.sceneCount!=1)throw new InvalidOperationException("只在单一干净据点应用。");
                    Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("背包右键详情与左区扩容");
                    Apply(scene,true);Undo.FlushUndoRecordObjects();Undo.CollapseUndoOperations(group);EditorSceneManager.MarkSceneDirty(scene);
                    Write("formal-applied.txt","FormalSceneSaved=false\nUndo=true\nDirty="+scene.isDirty);
                }
                else throw new InvalidOperationException("未知命令 "+command);
            }
            catch(Exception e){Write("setup-error.txt",e.ToString());GameLog.Error(e.ToString(),"InventoryRefinement");}
        }
        private static void Write(string name,string content)
        {Directory.CreateDirectory(Evidence);File.WriteAllText(Evidence+"/"+name,DateTime.UtcNow.ToString("O")+"\n"+content);}
        private static void Set(Object target,string field,Object value)
        {var data=new SerializedObject(target);data.FindProperty(field).objectReferenceValue=value;data.ApplyModifiedPropertiesWithoutUndo();}
        private static GameObject Node(string name,Transform parent,Scene scene,params Type[] components)
        {var go=new GameObject(name,components);SceneManager.MoveGameObjectToScene(go,scene);if(parent!=null)go.transform.SetParent(parent,false);go.layer=5;return go;}
        private static void Stretch(RectTransform rect)
        {rect.anchorMin=Vector2.zero;rect.anchorMax=Vector2.one;rect.offsetMin=rect.offsetMax=Vector2.zero;}
        private static Text Label(string name,Transform parent,Scene scene,string text,Vector2 min,Vector2 max,int size)
        {
            var go=Node(name,parent,scene,typeof(RectTransform),typeof(Text));var label=go.GetComponent<Text>();
            label.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");label.text=text;label.fontSize=size;label.color=Color.white;
            label.raycastTarget=false;var rect=label.rectTransform;rect.anchorMin=min;rect.anchorMax=max;rect.offsetMin=rect.offsetMax=Vector2.zero;
            label.alignment=TextAnchor.MiddleLeft;return label;
        }
        private static void Assets()
        {
            foreach(string guid in AssetDatabase.FindAssets("t:ItemDefinition",new[]{"Assets/_Project/ScriptableObjects/"}))
            {
                var item=AssetDatabase.LoadAssetAtPath<ItemDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if(item==null || item.Kind!=ItemKind.Consumable && item.Kind!=ItemKind.Alchemy && item.Kind!=ItemKind.Material || item.WorldModel==null)continue;
                var data=new SerializedObject(item);if(data.FindProperty("_useKind").intValue==2)continue;
                data.FindProperty("_useKind").intValue=2;data.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(item);
            }
            const string path="Assets/_Project/Prefabs/UI/InventorySlots/ItemInfoPopup.prefab";
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if(prefab==null)
            {
                var preview=EditorSceneManager.NewPreviewScene();GameObject root=null;
                try
                {
                    root=Node("ItemInfoPopup",null,preview,typeof(RectTransform),typeof(Image),typeof(InventoryInfoPopup));Stretch((RectTransform)root.transform);
                    root.GetComponent<Image>().color=new Color(0,0,0,.18f);
                    var frame=Node("Frame",root.transform,preview,typeof(RectTransform),typeof(Image));var rect=(RectTransform)frame.transform;
                    rect.anchorMin=rect.anchorMax=new Vector2(.5f,.5f);rect.pivot=new Vector2(0,1);rect.sizeDelta=new Vector2(430,260);
                    frame.GetComponent<Image>().color=new Color(.06f,.09f,.13f,.98f);
                    var border=Node("Border",frame.transform,preview,typeof(RectTransform),typeof(Image));Stretch((RectTransform)border.transform);
                    var image=border.GetComponent<Image>();image.sprite=AssetDatabase.LoadAssetAtPath<Sprite>("Assets/_Project/Art/UI/Function_UI/icon_frame_square_elite.png");
                    image.type=Image.Type.Sliced;image.raycastTarget=false;
                    var title=Label("Title",frame.transform,preview,"物品信息",new Vector2(.08f,.75f),new Vector2(.82f,.94f),26);
                    var body=Label("Body",frame.transform,preview,"详细信息待开发",new Vector2(.08f,.16f),new Vector2(.92f,.70f),22);
                    var close=Node("Close",frame.transform,preview,typeof(RectTransform),typeof(Image),typeof(Button));var cr=(RectTransform)close.transform;
                    cr.anchorMin=new Vector2(.86f,.78f);cr.anchorMax=new Vector2(.96f,.94f);cr.offsetMin=cr.offsetMax=Vector2.zero;
                    close.GetComponent<Image>().color=new Color(.25f,.18f,.12f,1);var cross=Label("Cross",close.transform,preview,"×",Vector2.zero,Vector2.one,26);cross.alignment=TextAnchor.MiddleCenter;
                    var popup=root.GetComponent<InventoryInfoPopup>();Set(popup,"_frame",rect);Set(popup,"_title",title);Set(popup,"_body",body);Set(popup,"_close",close.GetComponent<Button>());
                    root.SetActive(false);prefab=PrefabUtility.SaveAsPrefabAsset(root,path);
                }
                finally{if(root!=null)Object.DestroyImmediate(root);EditorSceneManager.ClosePreviewScene(preview);}
            }
            var styles=AssetDatabase.LoadAssetAtPath<InventorySlotPrefabs>("Assets/_Project/Resources/InventorySlotPrefabs.asset");
            styles.InfoPopup=prefab.GetComponent<InventoryInfoPopup>();EditorUtility.SetDirty(styles);AssetDatabase.SaveAssets();
        }
        private static void Apply(Scene scene,bool undo)
        {
            foreach(var root in scene.GetRootGameObjects())foreach(var ui in root.GetComponentsInChildren<SinglePlayerInventoryController>(true))
            {
                var data=new SerializedObject(ui);if(data.FindProperty("_backpackWorkspace").objectReferenceValue==null)continue;
                var panel=(GameObject)data.FindProperty("_panelRoot").objectReferenceValue;
                var left=panel.transform.Find("LeftPart") as RectTransform;var middle=panel.transform.Find("MiddlePart") as RectTransform;var right=panel.transform.Find("RightPart");
                if(left==null || middle==null)throw new InvalidOperationException("缺少作者LeftPart/MiddlePart，不猜测布局。");
                if(undo){Undo.RecordObject(ui,"清除旧详情引用");Undo.RecordObject(left,"背包左区扩容");Undo.RecordObject(middle,"法杖区域列位置");}
                // 旧详情控制器挂在Canvas，删RightPart不会自动删它；必须同时移除，避免生命周期订阅失效按钮。
                foreach(var ownerRoot in scene.GetRootGameObjects())foreach(var attachment in ownerRoot.GetComponentsInChildren<SpellAttachmentView>(true))
                {
                    if(right==null || attachment.transform.IsChildOf(right))continue;
                    var binding=new SerializedObject(attachment);var owned=binding.FindProperty("_inventory");var controls=binding.FindProperty("_controls").objectReferenceValue as GameObject;
                    if(owned.objectReferenceValue!=ui && (controls==null || !controls.transform.IsChildOf(right)))continue;
                    if(undo)Undo.DestroyObjectImmediate(attachment);else Object.DestroyImmediate(attachment);
                }
                foreach(string field in new[]{"_details","_status","_groundTarget"})
                {var property=data.FindProperty(field);if(right!=null && property.objectReferenceValue is Component node && node.transform.IsChildOf(right))property.objectReferenceValue=null;}
                data.ApplyModifiedPropertiesWithoutUndo();
                Columns(left,.005f,.44f);Columns(middle,.445f,.995f);
                var grid=(InventoryGridView)data.FindProperty("_gridView").objectReferenceValue;
                var nearby=ui.GetComponentInChildren<NearbyItemGridView>(true);
                if(undo){Undo.RecordObject(grid,"背包八列适配");if(nearby!=null)Undo.RecordObject(nearby,"附近八列适配");}
                grid.ConfigureViewportWidthFit(true);nearby?.ConfigureViewportWidthFit(true);
                EnlargeScroll(grid.GetComponentInParent<ScrollRect>(true),left,undo);
                if(nearby!=null)EnlargeScroll(nearby.GetComponentInParent<ScrollRect>(true),left,undo);
                if(right!=null){if(undo)Undo.DestroyObjectImmediate(right.gameObject);else Object.DestroyImmediate(right.gameObject);}
            }
        }
        private static void Columns(RectTransform rect,float min,float max)
        {
            // 只改用户要求的父列X范围，保留所有子Wand/Frame/Icon的局部布局和父列Y范围。
            var a=rect.anchorMin;var b=rect.anchorMax;a.x=min;b.x=max;rect.anchorMin=a;rect.anchorMax=b;
            var lo=rect.offsetMin;var hi=rect.offsetMax;lo.x=hi.x=0;rect.offsetMin=lo;rect.offsetMax=hi;
        }
        private static void EnlargeScroll(ScrollRect scroll,RectTransform left,bool undo)
        {
            if(scroll==null || !scroll.transform.IsChildOf(left))return;
            // 旧作者Scroll/Region有固定宽度；只拉伸这些操作区的X，不重排Wand子节点或改Y布局。
            for(var node=scroll.transform;node!=null && node!=left;node=node.parent)
            {var rect=(RectTransform)node;if(undo)Undo.RecordObject(rect,"操作区横向扩容");Columns(rect,.02f,.98f);}
            if(scroll.viewport!=null){if(undo)Undo.RecordObject(scroll.viewport,"视口横向扩容");Columns(scroll.viewport,0,1);var hi=scroll.viewport.offsetMax;hi.x=-12;scroll.viewport.offsetMax=hi;}
            if(scroll.horizontalScrollbar!=null){var rect=(RectTransform)scroll.horizontalScrollbar.transform;if(undo)Undo.RecordObject(rect,"手动滚动条宽度");Columns(rect,0,.98f);}
        }
        private static void Prepare()
        {
            Assets();var original=SceneManager.GetActiveScene();File.Copy(Hub,Review,true);AssetDatabase.ImportAsset(Review,ImportAssetOptions.ForceUpdate);
            var scene=EditorSceneManager.OpenScene(Review,OpenSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);Apply(scene,false);EditorSceneManager.SaveScene(scene,Review);
                SinglePlayerItemCarrySetup.Generate(scene);Write("prepared.txt","Review="+"Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_UnifiedItemsReview.unity\\nPracticePrefabUpdated=true\\nFormalSceneSaved=false");
            }
            finally{EditorSceneManager.CloseScene(scene,true);SceneManager.SetActiveScene(original);}
        }
    }
}
