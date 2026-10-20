using System;
using System.Collections.Generic;
using System.IO;
using Game.Core;
using Game.Run;
using Game.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.EditorTools
{
    /// <summary>新商店灰盒在复制后的新Scene内搭建；不用保存用户场景，不改既有测试数据。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerTradeSandboxSetup
    {
        private const string Root="Assets/_Project/Tests/Fixtures/Run/SinglePlayerTrading";
        private const string ScenePath="Assets/_Project/Scenes/Tests/TradeSandbox.unity";
        private const string Request="Temp/SinglePlayer/create-trade-sandbox.request";
        static SinglePlayerTradeSandboxSetup(){EditorApplication.update+=Poll;}
        private static void Poll()
        {
            if(!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating || SessionState.GetBool("Norn.SinglePlayer.Tests.Running",false))return;
            File.Delete(Request);try{Create();}catch(Exception e){Report("Error",e.ToString());GameLog.Error(e.ToString(),"TradeSandbox");}
        }
        [MenuItem("Tools/Norn/Single Player/Create Trade Sandbox")]
        private static void Create()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode || EditorUtility.scriptCompilationFailed){Report("Blocked","Play/编译失败");return;}
            for(int i=0;i<SceneManager.sceneCount;i++)if(SceneManager.GetSceneAt(i).isDirty){Report("Blocked","当前Scene未保存；未保存或丢弃用户修改。");return;}
            if(File.Exists(ScenePath) || AssetDatabase.IsValidFolder(Root)){Report("Existing","已有或部分资产，拒绝覆盖。");return;}
            var catalog=AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/_Project/Tests/Fixtures/Run/SinglePlayerRaids/Catalog.asset");
            if(catalog==null || !catalog.TryCreateIndex(out var index,out _))throw new InvalidOperationException("M4物品目录缺失/非法。");
            AssetDatabase.CreateFolder("Assets/_Project/Tests/Fixtures/Run","SinglePlayerTrading");
            var definition=ScriptableObject.CreateInstance<ShopDefinition>();definition.ShopId="sandbox.shop";definition.PurchaseBudget=100;
            var data=new SerializedObject(catalog);var defs=data.FindProperty("_definitions");var offers=new List<ShopOffer>();
            for(int i=0;i<defs.arraySize;i++)
            {
                var item=(ItemDefinition)defs.GetArrayElementAtIndex(i).objectReferenceValue;
                if(item.Kind!=ItemKind.Spell && item.Kind!=ItemKind.Wand && item.Kind!=ItemKind.Container && item.Kind!=ItemKind.Material)continue;
                offers.Add(new ShopOffer{OfferId=item.ContentId,ContentId=item.ContentId,Stock=item.Kind==ItemKind.Wand?2UL:5UL,
                    BuyPrice=item.Kind==ItemKind.Wand?40:20,SellPrice=item.Kind==ItemKind.Wand?12:6,InitialCharges=12,InitialMaterialQuantity=1});
            }
            string[] names={"","水","火","毒液","黏液","冰","干黏液","蒸汽","毒气"};
            for(byte key=1;key<=8;key++)offers.Add(new ShopOffer{OfferId="material."+key,MaterialKey=key,DisplayName=names[key],Stock=100,BuyPrice=key==1?2:4,SellPrice=key==1?1:2});
            definition.Offers=offers.ToArray();if(!definition.TryFreeze(index,out _))throw new InvalidOperationException("商店建议配置未通过验证。");
            AssetDatabase.CreateAsset(definition,"Assets/_Project/Tests/Fixtures/Run/SinglePlayerTrading/Shop.asset");
            var actions=ScriptableObject.CreateInstance<InputActionAsset>();var map=actions.AddActionMap("Trading");
            var toggle=map.AddAction("ToggleShop",InputActionType.Button,"<Keyboard>/b");
            AssetDatabase.CreateAsset(actions,"Assets/_Project/Tests/Fixtures/Run/SinglePlayerTrading/TradingActions.asset");var actionRef=InputActionReference.Create(toggle);AssetDatabase.CreateAsset(actionRef,"Assets/_Project/Tests/Fixtures/Run/SinglePlayerTrading/ToggleShop.asset");
            // CopyAsset生成新的Scene/meta；引用保留，避免逐个复制root时跨root引用仍指向原Scene。
            if(!AssetDatabase.CopyAsset("Assets/_Project/Scenes/Tests/DepartureSandbox.unity",ScenePath))throw new IOException("复制独立Scene失败。");
            var original=SceneManager.GetActiveScene();var scene=EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);
                var session=Find<SinglePlayerInventorySession>(scene);var inventoryUi=Find<SinglePlayerInventoryController>(scene);var pause=Find<RunPauseCoordinator>(scene);
                SetString(session,"_sandboxName","TradeSandbox");SetBool(inventoryUi,"_openOnStart",false);
                var runtime=session.gameObject.AddComponent<SinglePlayerShopSession>();Set(runtime,"_inventory",session);Set(runtime,"_definition",definition);
                var canvasGo=new GameObject("ShopCanvas",typeof(RectTransform),typeof(Canvas),typeof(CanvasScaler),typeof(GraphicRaycaster));SceneManager.MoveGameObjectToScene(canvasGo,scene);
                var canvas=canvasGo.GetComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceOverlay;canvas.sortingOrder=110;
                var scaler=canvasGo.GetComponent<CanvasScaler>();scaler.uiScaleMode=CanvasScaler.ScaleMode.ScaleWithScreenSize;scaler.referenceResolution=new Vector2(1920,1080);scaler.matchWidthOrHeight=.5f;
                var controller=canvasGo.AddComponent<ShopController>();
                var open=Button(canvasGo.transform,"Open Shop","B 商店",new Vector2(1688,-16),new Vector2(204,48));
                var panel=Rect(canvasGo.transform,"ShopPanel",new Vector2(24,-76),new Vector2(1872,980));panel.gameObject.AddComponent<Image>().color=new Color(.025f,.04f,.065f,.98f);
                Label(panel,"Title",new Vector2(24,-14),new Vector2(1460,52),"商店 · 有限供货与实际物资交易",28);
                var close=Button(panel,"Close","关闭商店",new Vector2(1636,-14),new Vector2(210,48));
                var summary=Label(panel,"Summary",new Vector2(24,-76),new Vector2(1816,42),"钱包 / 收购剩余额度",22);
                Label(panel,"OffersTitle",new Vector2(24,-136),new Vector2(540,40),"有限供货 · 选择商品",24);
                Label(panel,"OwnedTitle",new Vector2(598,-136),new Vector2(560,40),"实际持有 · 选择具体物品",24);
                Label(panel,"DetailsTitle",new Vector2(1212,-136),new Vector2(620,40),"交易详情",24);
                var offersRoot=Scroll(panel,"Offers",new Vector2(24,-188),new Vector2(546,672));
                var ownedRoot=Scroll(panel,"Owned",new Vector2(598,-188),new Vector2(580,672));
                var details=Label(panel,"Details",new Vector2(1212,-188),new Vector2(628,346),"选择商品或资产",22);
                Label(panel,"QuantityLabel",new Vector2(1212,-542),new Vector2(240,32),"购买份数 / 材料 GMU",20);
                var inputGo=DefaultControls.CreateInputField(new DefaultControls.Resources());inputGo.name="Quantity";inputGo.transform.SetParent(panel,false);Position(inputGo.GetComponent<RectTransform>(),new Vector2(1500,-538),new Vector2(320,44));
                var quantity=inputGo.GetComponent<InputField>();quantity.contentType=InputField.ContentType.IntegerNumber;quantity.text="1";
                Label(panel,"ContainerLabel",new Vector2(1212,-598),new Vector2(600,30),"买元素：选择目标容器",20);
                var dropGo=DefaultControls.CreateDropdown(new DefaultControls.Resources());dropGo.name="TargetContainer";dropGo.transform.SetParent(panel,false);Position(dropGo.GetComponent<RectTransform>(),new Vector2(1212,-636),new Vector2(608,44));
                var dropdown=dropGo.GetComponent<Dropdown>();
                var toggleGo=DefaultControls.CreateToggle(new DefaultControls.Resources());toggleGo.name="ToBackpack";toggleGo.transform.SetParent(panel,false);Position(toggleGo.GetComponent<RectTransform>(),new Vector2(1212,-696),new Vector2(608,38));
                toggleGo.GetComponentInChildren<Text>().text="买实体：放入背包（不勾选则入仓库）";var backpack=toggleGo.GetComponent<Toggle>();backpack.isOn=false;
                var buy=Button(panel,"Buy","购买所选商品",new Vector2(1212,-754),new Vector2(608,48));
                var sellItem=Button(panel,"SellItem","出售这份物品 / 空瓶壳",new Vector2(1212,-812),new Vector2(608,48));
                var sellMaterial=Button(panel,"SellMaterial","只出售实际材料（保留瓶壳）",new Vector2(1212,-870),new Vector2(608,48));
                var status=Label(panel,"Status",new Vector2(24,-890),new Vector2(1154,72),"先出售普通物资取得货币；免费本体不可直接出售。",20);
                var template=Rect(canvasGo.transform,"EntryTemplate",Vector2.zero,new Vector2(540,72));template.gameObject.AddComponent<Image>().color=new Color(.09f,.13f,.19f);
                var button=template.gameObject.AddComponent<Button>();var element=template.gameObject.AddComponent<LayoutElement>();element.preferredHeight=72;element.minHeight=72;
                var text=Label(template,"Label",new Vector2(12,-6),new Vector2(516,60),"商品",20);var entry=template.gameObject.AddComponent<ShopEntryView>();Set(entry,"_button",button);Set(entry,"_label",text);template.gameObject.SetActive(false);
                Set(controller,"_shop",runtime);Set(controller,"_inventory",session);Set(controller,"_inventoryUi",inventoryUi);Set(controller,"_pause",pause);
                Set(controller,"_panel",panel.gameObject);Set(controller,"_offersRoot",offersRoot);Set(controller,"_ownedRoot",ownedRoot);Set(controller,"_entryPrefab",entry);
                Set(controller,"_summary",summary);Set(controller,"_details",details);Set(controller,"_status",status);Set(controller,"_quantity",quantity);Set(controller,"_containers",dropdown);Set(controller,"_toBackpack",backpack);
                Set(controller,"_openButton",open);Set(controller,"_closeButton",close);Set(controller,"_buyButton",buy);Set(controller,"_sellItemButton",sellItem);Set(controller,"_sellMaterialButton",sellMaterial);Set(controller,"_toggleAction",actionRef);
                if(!EditorSceneManager.SaveScene(scene,ScenePath))throw new IOException("新测试Scene保存失败。");
            }
            finally{EditorSceneManager.CloseScene(scene,true);if(original.IsValid() && original.isLoaded)SceneManager.SetActiveScene(original);}
            Report("Created",null);
        }
        private static RectTransform Scroll(Transform parent,string name,Vector2 position,Vector2 size)
        {
            var root=Rect(parent,name,position,size);root.gameObject.AddComponent<Image>().color=new Color(.04f,.065f,.095f);
            var viewport=Rect(root,"Viewport",Vector2.zero,size);viewport.gameObject.AddComponent<RectMask2D>();
            var content=Rect(viewport,"Content",Vector2.zero,new Vector2(size.x,0));var layout=content.gameObject.AddComponent<VerticalLayoutGroup>();layout.spacing=8;layout.childControlHeight=true;layout.childControlWidth=true;layout.childForceExpandHeight=false;layout.childForceExpandWidth=true;
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit=ContentSizeFitter.FitMode.PreferredSize;
            var scroll=root.gameObject.AddComponent<ScrollRect>();scroll.viewport=viewport;scroll.content=content;scroll.horizontal=false;scroll.movementType=ScrollRect.MovementType.Clamped;return content;
        }
        private static T Find<T>(Scene scene)where T:Component{foreach(var root in scene.GetRootGameObjects()){var found=root.GetComponentInChildren<T>(true);if(found!=null)return found;}return null;}
        private static RectTransform Rect(Transform parent,string name,Vector2 position,Vector2 size){var go=new GameObject(name,typeof(RectTransform));go.transform.SetParent(parent,false);var rect=go.GetComponent<RectTransform>();Position(rect,position,size);return rect;}
        private static void Position(RectTransform rect,Vector2 position,Vector2 size){rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(0,1);rect.anchoredPosition=position;rect.sizeDelta=size;}
        private static Text Label(Transform parent,string name,Vector2 position,Vector2 size,string value,int fontSize){var rect=Rect(parent,name,position,size);var text=rect.gameObject.AddComponent<Text>();text.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");text.fontSize=fontSize;text.text=value;text.color=new Color(.88f,.92f,.96f);text.raycastTarget=false;return text;}
        private static Button Button(Transform parent,string name,string value,Vector2 position,Vector2 size){var rect=Rect(parent,name,position,size);rect.gameObject.AddComponent<Image>().color=new Color(.12f,.2f,.29f);var button=rect.gameObject.AddComponent<Button>();var text=Label(rect,"Label",new Vector2(8,-4),new Vector2(size.x-16,size.y-8),value,20);text.alignment=TextAnchor.MiddleCenter;return button;}
        private static void Set(UnityEngine.Object target,string field,UnityEngine.Object value){var so=new SerializedObject(target);so.FindProperty(field).objectReferenceValue=value;so.ApplyModifiedPropertiesWithoutUndo();}
        private static void SetString(UnityEngine.Object target,string field,string value){var so=new SerializedObject(target);so.FindProperty(field).stringValue=value;so.ApplyModifiedPropertiesWithoutUndo();}
        private static void SetBool(UnityEngine.Object target,string field,bool value){var so=new SerializedObject(target);so.FindProperty(field).boolValue=value;so.ApplyModifiedPropertiesWithoutUndo();}
        private static void Report(string state,string error){Directory.CreateDirectory("Temp/SinglePlayer");File.WriteAllText("Temp/SinglePlayer/trade-sandbox-report.json",JsonUtility.ToJson(new ReportData{State=state,Error=error,Scene=ScenePath},true));}
        [Serializable]private sealed class ReportData{public string State,Error,Scene;}
    }
}
