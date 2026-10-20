using System;
using System.IO;
using Game.Core;
using Game.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.EditorTools
{
    /// <summary>从已保存作者Scene提取样式，在独立Review生成资产；从不保存正式Scene，不覆盖已有精修Prefab。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerInventorySlotPrefabSetup
    {
        private const string Request="Temp/SinglePlayer/slot-prefabs.request";
        private const string Hub="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity";
        private const string Review="Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_UnifiedItemsReview.unity";
        private const string Folder="Assets/_Project/Prefabs/UI/InventorySlots";
        private const string SetPath="Assets/_Project/Resources/InventorySlotPrefabs.asset";
        private const string Evidence="TestResults/SinglePlayer/M6-T14-SlotAuthoring";
        static SinglePlayerInventorySlotPrefabSetup(){EditorApplication.update+=Poll;}
        [MenuItem("Tools/SinglePlayer/UI/Prepare Independent Slot Prefabs")]
        private static void Prepare()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode || EditorUtility.scriptCompilationFailed)throw new InvalidOperationException("不能在Play/编译失败时提取作者UI。");
            for(int i=0;i<SceneManager.sceneCount;i++)if(SceneManager.GetSceneAt(i).isDirty)throw new InvalidOperationException("作者Scene有未保存修改，不代保存/丢弃。");
            var original=SceneManager.GetActiveScene();
            if(SceneManager.GetSceneByPath(Review).isLoaded)throw new InvalidOperationException("Review已打开，请先关闭已保存Review。");
            Directory.CreateDirectory(Folder);Directory.CreateDirectory("Assets/_Project/Resources");
            File.Copy(Hub,Review,true);AssetDatabase.ImportAsset(Review,ImportAssetOptions.ForceUpdate);
            var review=EditorSceneManager.OpenScene(Review,OpenSceneMode.Additive);SceneManager.SetActiveScene(review);
            try
            {
                var set=AssetDatabase.LoadAssetAtPath<InventorySlotPrefabs>(SetPath);
                if(set==null){set=ScriptableObject.CreateInstance<InventorySlotPrefabs>();AssetDatabase.CreateAsset(set,SetPath);}
                set.BackpackCell=Cell("BackpackCell",new Color(.13f,.17f,.21f,1));
                set.NearbyCell=Cell("NearbyCell",new Color(.09f,.14f,.14f,1));
                set.WarehouseCell=Cell("WarehouseCell",new Color(.09f,.16f,.22f,1));
                set.BackpackItem=Save("BackpackItem",()=>Item("BackpackItem",true,false));
                set.NearbyItem=Save("NearbyItem",()=>Item("NearbyItem",true,false));
                set.WarehouseItem=Save("WarehouseItem",()=>Item("WarehouseItem",false,true));
                set.WandProgramSlot=Save("WandProgramSlot",()=>Slot("WandProgramSlot",false,false));
                set.SpellStorageSlot=Save("SpellStorageSlot",()=>Slot("SpellStorageSlot",true,false));
                set.WandSelectorSlot=Save("WandSelectorSlot",()=>Slot("WandSelectorSlot",false,true));
                ToolbeltSlotView full=null,compact=null;
                foreach(var root in review.GetRootGameObjects())foreach(var tool in root.GetComponentsInChildren<ToolbeltSlotView>(true))
                {if(tool.Compact){if(compact==null)compact=tool;}else if(full==null)full=tool;}
                if(full==null)throw new InvalidOperationException("未找到作者背包道具槽，停止而不猜测替换。");
                set.BackpackToolSlot=Save("BackpackToolSlot",()=>CloneTool(full,false));
                set.WorkspaceToolSlot=Save("WorkspaceToolSlot",()=>CloneTool(compact!=null?compact:full,true));
                EditorUtility.SetDirty(set);AssetDatabase.SaveAssetIfDirty(set);
                foreach(var root in review.GetRootGameObjects())foreach(var ui in root.GetComponentsInChildren<SinglePlayerInventoryController>(true))Set(ui,"_slotPrefabs",set);
                EditorSceneManager.SaveScene(review);
                // 练习图从同一作者背包生成共享Prefab；只保存Prefab资产，不加载或保存P7/P8正式Scene。
                SinglePlayerIconFillSetup.ApplyPrompts(review,false);
                EditorSceneManager.SaveScene(review,Review);
                SinglePlayerItemCarrySetup.Generate(review);
                Directory.CreateDirectory(Evidence);File.WriteAllText(Evidence+"/prefab-generation.txt",DateTime.UtcNow.ToString("O")+"\nSlotSet="+"Assets/_Project/Resources/InventorySlotPrefabs.asset\\nPrefabs="+"Assets/_Project/Prefabs/UI/InventorySlots\\nFormalSaved=false\\nExistingPrefabsOverwritten=false\\nPracticeWorkspace=AuthorHubClone\\n");
            }
            finally
            {
                SceneManager.SetActiveScene(original);EditorSceneManager.CloseScene(review,true);
            }
        }
        private static void Poll()
        {
            if(!File.Exists(Request)||EditorApplication.isCompiling||EditorApplication.isUpdating||EditorApplication.isPlayingOrWillChangePlaymode)return;
            string command=File.ReadAllText(Request).Trim();File.Delete(Request);
            try
            {
                if(command=="repair-selected-frame")RepairSelectedFrame();
                else if(command=="bottom-spell-count"){ApplyBottomSpellCount();Prepare();}
                else if(command=="square-spell-style"){ApplySquareSpellStyle();Prepare();}
                else Prepare();
            }
            catch(Exception e){Directory.CreateDirectory(Evidence);File.WriteAllText(Evidence+"/setup-error.txt",e.ToString());GameLog.Error(e.ToString(),"SlotPrefabs");}
        }
        private static void ApplyBottomSpellCount()
        {
            // 定点迁移独立Prefab的次数节点；保留用户其他图像、父子布局和覆盖值。
            string[] names={"BackpackItem","NearbyItem","WarehouseItem","SpellStorageSlot","WandProgramSlot"};
            foreach(string name in names)
            {
                string path="Assets/_Project/Prefabs/UI/InventorySlots/"+name+".prefab";var root=PrefabUtility.LoadPrefabContents(path);
                try
                {
                    foreach(var view in root.GetComponentsInChildren<InventoryItemView>(true))
                    {
                        var count=Ref<Text>(view,"_spellCount");if(count==null)continue;
                        Anchors(count.rectTransform,new Vector2(.03f,.05f),new Vector2(.97f,.55f));
                        count.alignment=TextAnchor.LowerCenter;count.fontSize=28;count.resizeTextForBestFit=false;
                    }
                    PrefabUtility.SaveAsPrefabAsset(root,path);
                }
                finally{PrefabUtility.UnloadPrefabContents(root);}
            }
            const string evidence="TestResults/SinglePlayer/M6-T20-SpellUi";Directory.CreateDirectory(evidence);
            File.WriteAllText(evidence+"/prefab-migration.txt",DateTime.UtcNow.ToString("O")+"\nCount=BottomCenter\nFontSize=28\nFormalSceneSaved=false\n");
        }
        private static T Save<T>(string name,Func<T> build) where T:Component
        {
            string path="Assets/_Project/Prefabs/UI/InventorySlots/"+name+".prefab";var asset=AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if(asset!=null)return asset.GetComponent<T>();
            var view=build();view.name=name;
            try{return PrefabUtility.SaveAsPrefabAsset(view.gameObject,path).GetComponent<T>();}
            finally{UnityEngine.Object.DestroyImmediate(view.gameObject);}
        }
        private static Image Cell(string name,Color color)=>Save(name,()=>
        {var root=Rect(null,name);var image=root.gameObject.AddComponent<Image>();image.color=color;image.raycastTarget=false;return image;});
        private static InventoryItemView Item(string name,bool shape,bool catalog)
        {
            var root=Rect(null,name);var view=root.gameObject.AddComponent<InventoryItemView>();root.GetComponent<Image>().color=Color.clear;
            Set(view,"_authoredLayout",true);
            RectTransform visual=root,iconParent=root;
            if(shape)
            {
                visual=Rect(root,"ShapeVisual_RuntimeGeometry");Set(view,"_shapeVisualRoot",visual);
                var rotation=Rect(visual,"IconRotation_RuntimeGeometry");rotation.anchorMin=rotation.anchorMax=rotation.pivot=new Vector2(.5f,.5f);
                iconParent=rotation;Set(view,"_iconRotationRoot",rotation);
                var cell=Rect(root,"OccupiedCellTemplate");var fill=cell.gameObject.AddComponent<Image>();fill.color=new Color(.10f,.22f,.24f,.97f);fill.raycastTarget=false;
                Set(view,"_occupiedCellTemplate",fill);cell.gameObject.SetActive(false);
            }
            var icon=Rect(iconParent,"Icon").gameObject.AddComponent<Image>();Stretch(icon.rectTransform,new Vector2(4,4),new Vector2(-4,-4));icon.raycastTarget=false;icon.preserveAspect=true;
            var label=Label(visual,"Caption");Stretch(label.rectTransform,new Vector2(4,2),new Vector2(-4,-2));label.alignment=shape?TextAnchor.LowerRight:TextAnchor.MiddleCenter;
            label.fontSize=shape?18:14;label.resizeTextForBestFit=true;label.resizeTextMinSize=8;label.resizeTextMaxSize=shape?18:16;
            label.horizontalOverflow=HorizontalWrapMode.Overflow;
            if(catalog)
            {
                Anchors(icon.rectTransform,new Vector2(.08f,.44f),new Vector2(.92f,.97f));
                Anchors(label.rectTransform,new Vector2(.02f,.01f),new Vector2(.98f,.43f));
            }
            Set(view,"_icon",icon);Set(view,"_label",label);return view;
        }
        private static InventorySlotView Slot(string name,bool storage,bool selector)
        {
            var root=Rect(null,name);root.sizeDelta=Vector2.one*76;
            var bg=root.gameObject.AddComponent<Image>();bg.color=storage?new Color(.12f,.16f,.22f,.96f):new Color(.08f,.12f,.18f,.92f);
            var target=root.gameObject.AddComponent<InventoryDropTarget>();var view=root.gameObject.AddComponent<InventorySlotView>();Set(view,"_target",target);
            var card=Item("Item",false,false);card.transform.SetParent(root,false);Stretch(card.Rect,Vector2.zero,Vector2.zero);Set(view,"_card",card);
            if(selector)
            {
                root.sizeDelta=new Vector2(220,100);root.gameObject.AddComponent<Button>();
                var label=Ref<Text>(card,"_label");Anchors(label.rectTransform,new Vector2(.03f,.73f),new Vector2(.97f,.97f));label.alignment=TextAnchor.UpperLeft;
                var icon=Ref<Image>(card,"_icon");Anchors(icon.rectTransform,new Vector2(.05f,.26f),new Vector2(.95f,.73f));
                var resource=Label(card.transform,"WandResource");Anchors(resource.rectTransform,Vector2.zero,new Vector2(1,.26f));resource.color=new Color(.22f,.87f,.93f);Set(card,"_wandResource",resource);
                var empty=Label(root,"EmptyWand_空位提示");Stretch(empty.rectTransform,Vector2.zero,Vector2.zero);Set(view,"_empty",empty);
                var frame=Rect(root,"Frame").gameObject.AddComponent<Image>();Stretch(frame.rectTransform,Vector2.zero,Vector2.zero);frame.color=Color.white;frame.raycastTarget=false;frame.type=Image.Type.Sliced;
                Set(view,"_selection",frame);
                Set(view,"_selectedSprite",SelectedFrame());
            }
            else if(!storage)
            {
                var index=Label(root,"Index");Stretch(index.rectTransform,new Vector2(0,-16),new Vector2(0,-68));index.alignment=TextAnchor.LowerCenter;index.fontSize=12;Set(view,"_index",index);
            }
            card.gameObject.SetActive(false);return view;
        }
        private static ToolbeltSlotView CloneTool(ToolbeltSlotView source,bool compact)
        {
            var clone=UnityEngine.Object.Instantiate(source);clone.gameObject.SetActive(true);clone.SetSlotIndex(0);
            if(compact)clone.ConfigureCompact();
            // 内置Legacy字体没有汉字；留空由控制器一次性补当前中文Font，其余作者样式原样保存。
            foreach(var label in clone.GetComponentsInChildren<Text>(true))if(label.font==null || label.font.name=="LegacyRuntime")label.font=null;
            return clone;
        }
        private static Sprite SelectedFrame()
        {
            // Multiple模式的Sprite是TextureImporter子资产，按主资产类型搜索/LoadAssetAtPath不能保证取得。
            foreach(var asset in AssetDatabase.LoadAllAssetsAtPath("Assets/_Project/Art/UI/Funtion_UI2/Frame_Selected.png"))
                if(asset is Sprite sprite)return sprite;
            throw new InvalidOperationException("作者Frame_Selected未包含Sprite子资产。");
        }
        private static void RepairSelectedFrame()
        {
            if(EditorUtility.scriptCompilationFailed)throw new InvalidOperationException("编译失败。");
            var root=PrefabUtility.LoadPrefabContents("Assets/_Project/Prefabs/UI/InventorySlots/WandSelectorSlot.prefab");
            try
            {
                var view=root.GetComponent<InventorySlotView>();
                if(Ref<Sprite>(view,"_selectedSprite")==null)
                {Set(view,"_selectedSprite",SelectedFrame());PrefabUtility.SaveAsPrefabAsset(root,"Assets/_Project/Prefabs/UI/InventorySlots/WandSelectorSlot.prefab");}
                Directory.CreateDirectory(Evidence);File.WriteAllText(Evidence+"/selected-frame-repair.txt",DateTime.UtcNow.ToString("O")+"\nLoadedMultipleSpriteSubasset=true\nFormalSceneSaved=false\nOnlyMissingSelectedSpriteAssigned=true\n");
            }
            finally{PrefabUtility.UnloadPrefabContents(root);}
        }

        private static void ApplySquareSpellStyle()
        {
            // 明确的本轮样式迁移；只修改用户请求的角标/边框，不能重建作者已经精修的其他节点。
            var elite=AssetDatabase.LoadAssetAtPath<Sprite>("Assets/_Project/Art/UI/Function_UI/icon_frame_square_elite.png");
            if(elite==null)foreach(var asset in AssetDatabase.LoadAllAssetsAtPath("Assets/_Project/Art/UI/Function_UI/icon_frame_square_elite.png"))
                if(asset is Sprite sprite){elite=sprite;break;}
            if(elite==null)throw new InvalidOperationException("指定icon_frame_square_elite没有Sprite，停止迁移。");
            for(int i=0;i<SceneManager.sceneCount;i++)if(SceneManager.GetSceneAt(i).isDirty)
                throw new InvalidOperationException("正式Scene Dirty，不代保存/丢弃。");
            foreach(var name in new[]{"BackpackItem","NearbyItem","WarehouseItem","WandProgramSlot","SpellStorageSlot","WandSelectorSlot","WorkspaceToolSlot"})
            {
                string path="Assets/_Project/Prefabs/UI/InventorySlots/"+name+".prefab";var root=PrefabUtility.LoadPrefabContents(path);
                try
                {
                    foreach(var view in root.GetComponentsInChildren<InventoryItemView>(true))
                    {
                        if(name=="WandSelectorSlot")continue;
                        var count=Ref<Text>(view,"_spellCount");
                        if(count==null)
                        {
                            var go=new GameObject("SpellCount",typeof(RectTransform),typeof(Text));
                            // 创建后立即移入Prefab预览Scene，再建立父子关系；正式Active Scene不会获得额外持久对象。
                            SceneManager.MoveGameObjectToScene(go,root.scene);
                            var visual=Ref<RectTransform>(view,"_shapeVisualRoot");go.transform.SetParent(visual!=null?visual:view.transform,false);
                            go.layer=5;count=go.GetComponent<Text>();Set(view,"_spellCount",count);
                            Anchors(count.rectTransform,new Vector2(.03f,.05f),new Vector2(.97f,.55f));
                            count.alignment=TextAnchor.LowerCenter;count.font=null;count.fontSize=28;count.fontStyle=FontStyle.Bold;
                            count.resizeTextForBestFit=false;count.horizontalOverflow=HorizontalWrapMode.Overflow;count.verticalOverflow=VerticalWrapMode.Overflow;
                            count.color=Color.white;count.raycastTarget=false;count.text="";
                            var shadow=go.AddComponent<Shadow>();shadow.effectColor=new Color(0,0,0,.9f);shadow.effectDistance=new Vector2(1,-1);
                            go.SetActive(false);
                        }
                    }
                    if(name=="WandSelectorSlot")Set(root.GetComponent<InventorySlotView>(),"_selectedSprite",elite);
                    if(name=="WorkspaceToolSlot")
                    {
                        var background=root.GetComponent<Image>();background.sprite=elite;background.color=Color.white;background.type=Image.Type.Simple;
                        var surface=root.transform.Find("Surface_道具底色");
                        if(surface==null)
                        {
                            var go=new GameObject("Surface_道具底色",typeof(RectTransform),typeof(Image));SceneManager.MoveGameObjectToScene(go,root.scene);
                            go.transform.SetParent(root.transform,false);go.transform.SetAsFirstSibling();go.layer=5;
                            var fill=go.GetComponent<Image>();Stretch(fill.rectTransform,new Vector2(4,4),new Vector2(-4,-4));fill.color=new Color(.12f,.08f,.16f,.96f);fill.raycastTarget=false;
                        }
                        var tool=root.GetComponent<ToolbeltSlotView>();var icon=Ref<Image>(tool,"_icon");
                        Anchors(icon.rectTransform,new Vector2(.12f,.37f),new Vector2(.88f,.87f));
                        var caption=Ref<Text>(tool,"_label");Anchors(caption.rectTransform,new Vector2(.03f,.01f),new Vector2(.97f,.36f));
                        caption.fontSize=11;caption.alignment=TextAnchor.LowerCenter;caption.horizontalOverflow=HorizontalWrapMode.Wrap;
                        caption.resizeTextForBestFit=true;caption.resizeTextMinSize=8;caption.resizeTextMaxSize=11;
                        caption.verticalOverflow=VerticalWrapMode.Overflow;
                    }
                    PrefabUtility.SaveAsPrefabAsset(root,path);
                }
                finally{PrefabUtility.UnloadPrefabContents(root);}
            }
            const string evidence="TestResults/SinglePlayer/M6-T15-SquareStyle";Directory.CreateDirectory(evidence);
            File.WriteAllText(evidence+"/style-migration.txt",DateTime.UtcNow.ToString("O")+"\nIndependentSpellCount=true\nFrame=icon_frame_square_elite\nFormalSceneSaved=false\nOtherAuthorChildrenPreserved=true\n");
        }
        private static RectTransform Rect(Transform parent,string name)
        {
            var root=new GameObject(name,typeof(RectTransform));root.layer=5;if(parent!=null)root.transform.SetParent(parent,false);
            var rect=(RectTransform)root.transform;rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(0,1);rect.sizeDelta=Vector2.one*64;return rect;
        }
        private static Text Label(Transform parent,string name)
        {var rect=Rect(parent,name);var text=rect.gameObject.AddComponent<Text>();text.font=null;text.color=new Color(.88f,.91f,.94f,1);text.raycastTarget=false;text.alignment=TextAnchor.MiddleCenter;text.fontSize=14;return text;}
        private static void Stretch(RectTransform rect,Vector2 min,Vector2 max){rect.anchorMin=Vector2.zero;rect.anchorMax=Vector2.one;rect.offsetMin=min;rect.offsetMax=max;}
        private static void Anchors(RectTransform rect,Vector2 min,Vector2 max){Stretch(rect,Vector2.zero,Vector2.zero);rect.anchorMin=min;rect.anchorMax=max;}
        private static T Ref<T>(UnityEngine.Object target,string field)where T:UnityEngine.Object=>(T)new SerializedObject(target).FindProperty(field).objectReferenceValue;
        private static void Set(UnityEngine.Object target,string field,UnityEngine.Object value){var s=new SerializedObject(target);s.FindProperty(field).objectReferenceValue=value;s.ApplyModifiedPropertiesWithoutUndo();}
        private static void Set(UnityEngine.Object target,string field,bool value){var s=new SerializedObject(target);s.FindProperty(field).boolValue=value;s.ApplyModifiedPropertiesWithoutUndo();}
    }
}
