using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Game.Core;
using Game.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.EditorTools
{
    /// <summary>只添加确认层和缺失反馈接线。Review保存隔离副本；作者Scene只增量Undo，不能重套旧布局。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerShopPurchaseDialogSetup
    {
        private const string Formal="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity";
        private const string Review="Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_ShopQuickPurchaseReview.unity";
        private const string Request="Temp/SinglePlayer/shop-purchase-dialog.request";
        private const string Evidence="TestResults/SinglePlayer/M6-T7-PurchaseDialog";
        private const string UndoName="添加商店购买数量确认框";
        private static bool _undo;
        static SinglePlayerShopPurchaseDialogSetup()=>EditorApplication.update+=Poll;
        private static void Poll()
        {
            if(!File.Exists(Request)||EditorApplication.isCompiling||EditorApplication.isUpdating||EditorApplication.isPlayingOrWillChangePlaymode)return;
            string command=File.ReadAllText(Request).Trim();File.Delete(Request);
            try
            {
                if(EditorUtility.scriptCompilationFailed)throw new InvalidOperationException("编译失败，拒绝使用旧Assembly。");
                if(command=="prepare")Prepare();else if(command=="apply-current")ApplyCurrent();
                else if(command=="inspect")Audit(SceneManager.GetActiveScene(),"inspection.txt");else throw new ArgumentException(command);
            }
            catch(Exception error){Write("setup-error.txt",error.ToString());GameLog.Error(error.ToString(),"ShopPurchaseDialog");}
        }
        private static void Prepare()
        {
            for(int i=0;i<SceneManager.sceneCount;i++)if(SceneManager.GetSceneAt(i).isDirty)throw new InvalidOperationException("请保存要保留的修改；不代为保存/丢弃。");
            if(SceneManager.GetSceneByPath(Review).isLoaded)throw new InvalidOperationException("Review已打开，拒绝覆盖。");
            var original=SceneManager.GetActiveScene();string hash=Hash();
            if(!File.Exists(Review)){if(!AssetDatabase.CopyAsset(Formal,Review))throw new IOException("复制Review失败。");}
            else{File.Copy(Formal,Review,true);AssetDatabase.ImportAsset(Review,ImportAssetOptions.ForceUpdate);}
            var scene=EditorSceneManager.OpenScene(Review,OpenSceneMode.Additive);
            try
            {
                // 新GameObject默认属于Active Scene，先切到Review，否则会把作者Scene标脏。
                SceneManager.SetActiveScene(scene);_undo=false;Apply(scene);EditorSceneManager.SaveScene(scene,Review);
                Audit(scene,"review-layout.txt");
            }
            finally
            {
                EditorSceneManager.CloseScene(scene,true);SceneManager.SetActiveScene(original);
                Write("review-isolation.txt","FormalHashUnchanged="+(hash==Hash())+"\nFormalDirty="+original.isDirty);
            }
        }
        [MenuItem("Tools/Norn/商店/添加购买确认框（Undo，不保存）")]
        private static void ApplyCurrent()
        {
            var scene=SceneManager.GetActiveScene();if(scene.path!=Formal)throw new InvalidOperationException("只增量处理正式据点。");
            string hash=Hash();Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName(UndoName);
            try
            {
                _undo=true;Apply(scene);Undo.FlushUndoRecordObjects();Undo.CollapseUndoOperations(group);
                if(hash!=Hash())throw new InvalidOperationException("正式Scene磁盘意外改变。");
                EditorSceneManager.MarkSceneDirty(scene);Audit(scene,"current-layout.txt");
                Write("current-applied.txt","正式磁盘未写入，单组Undo。\nSHA256="+hash);
            }
            catch{Undo.RevertAllDownToGroup(group);throw;}
            finally{_undo=false;}
        }
        private static void Apply(Scene scene)
        {
            var shop=Find<ShopController>(scene);if(shop==null)throw new InvalidOperationException("缺少ShopController。");
            var panel=Get<GameObject>(shop,"_panel");if(panel==null||Get<Button>(shop,"_buyButton")==null)throw new InvalidOperationException("保留现有商店Panel及购买按钮，缺少引用请先接线。");
            if(Get<ShopPurchaseDialog>(shop,"_purchaseDialog")!=null)return;
            var root=Rect(panel.transform,"ConfirmPurchaseDialog_购买确认",new Color(0,0,0,.65f));Place(root,0,0,1,1);
            // AddComponent会立刻OnEnable，先关闭根节点，字段接完以后才允许运行。
            root.gameObject.SetActive(false);
            var frame=Rect(root,"Panel_购买物品",new Color(.88f,.86f,.79f));
            frame.anchorMin=frame.anchorMax=new Vector2(.5f,.5f);frame.sizeDelta=new Vector2(720,530);
            var ink=new Color(.15f,.19f,.25f);var gold=new Color(.72f,.46f,.10f);
            Label(frame,"Title","购买物品",.05f,.88f,.95f,.98f,28,ink,TextAnchor.MiddleCenter);
            var info=Rect(frame,"GoodsInfo_商品信息",new Color(.42f,.47f,.51f));Place(info,.06f,.63f,.94f,.86f);
            var icon=Rect(info,"Icon_商品图标",Color.white).GetComponent<Image>();Place(icon.rectTransform,.02f,.08f,.23f,.92f);icon.preserveAspect=true;icon.raycastTarget=false;
            var name=Label(info,"Name_商品名称","商品",.26f,.60f,.98f,.96f,25,Color.white);
            var desc=Label(info,"Description_关键说明","",.26f,.02f,.98f,.61f,19,Color.white);
            Label(frame,"QuantityTitle","购买数量",.1f,.54f,.9f,.62f,20,ink,TextAnchor.MiddleCenter);
            var amount=Label(frame,"Quantity_数量","1 份",.1f,.47f,.9f,.55f,30,ink,TextAnchor.MiddleCenter);
            var minus=Button(frame,"Minus_减少","−",.07f,.38f,.16f,.46f,ink);
            var plus=Button(frame,"Plus_增加","+",.84f,.38f,.93f,.46f,ink);
            var sliderRoot=Rect(frame,"QuantitySlider_购买数量",new Color(.65f,.66f,.64f));Place(sliderRoot,.20f,.405f,.80f,.435f);
            var slider=Add<Slider>(sliderRoot.gameObject);slider.minValue=0;slider.maxValue=1;slider.wholeNumbers=false;
            var fillArea=Rect(sliderRoot,"FillArea",Color.clear);Place(fillArea,0,.1f,1,.9f);fillArea.GetComponent<Image>().raycastTarget=false;
            var fill=Rect(fillArea,"Fill",gold);Place(fill,0,0,1,1);fill.GetComponent<Image>().raycastTarget=false;
            var handleArea=Rect(sliderRoot,"HandleArea",Color.clear);Place(handleArea,0,0,1,1);handleArea.GetComponent<Image>().raycastTarget=false;
            var handle=Rect(handleArea,"Handle",new Color(.24f,.32f,.43f));handle.sizeDelta=new Vector2(24,30);
            slider.fillRect=fill;slider.handleRect=handle;slider.targetGraphic=handle.GetComponent<Image>();
            var limits=Label(frame,"Limits_数量范围","1 — 10",.20f,.35f,.80f,.40f,16,ink,TextAnchor.MiddleCenter);
            var stock=Label(frame,"Stock_剩余供货","",.08f,.28f,.92f,.35f,19,ink,TextAnchor.MiddleCenter);
            var cost=Label(frame,"Cost_金币消耗","",.08f,.20f,.53f,.29f,23,ink,TextAnchor.MiddleCenter);
            var wallet=Label(frame,"Wallet_当前金币","",.54f,.20f,.94f,.29f,20,ink,TextAnchor.MiddleCenter);
            var target=Label(frame,"Target_去向与容器","",.06f,.14f,.94f,.21f,18,ink,TextAnchor.MiddleCenter);
            var feedback=Label(frame,"Feedback_失败原因","",.06f,.10f,.94f,.15f,16,new Color(.62f,.15f,.12f),TextAnchor.MiddleCenter);
            var cancel=Button(frame,"Cancel_取消","取消",.07f,.025f,.47f,.105f,new Color(.29f,.34f,.41f));
            var confirm=Button(frame,"Confirm_购买","购买",.53f,.025f,.93f,.105f,gold);
            var dialog=Add<ShopPurchaseDialog>(root.gameObject);
            Set(dialog,"_controller",shop);Set(dialog,"_icon",icon);Set(dialog,"_name",name);Set(dialog,"_description",desc);
            Set(dialog,"_quantityLabel",amount);Set(dialog,"_limits",limits);Set(dialog,"_stock",stock);Set(dialog,"_cost",cost);Set(dialog,"_wallet",wallet);
            Set(dialog,"_target",target);Set(dialog,"_feedback",feedback);Set(dialog,"_slider",slider);
            Set(dialog,"_minus",minus);Set(dialog,"_plus",plus);Set(dialog,"_cancel",cancel);Set(dialog,"_confirm",confirm);Set(shop,"_purchaseDialog",dialog);
            if(Get<Text>(shop,"_status")==null)
            {
                // 简短反馈放在标题旁的空位；不调整作者商品区、按钮或中列网格。
                var status=Label(panel.transform,"PurchaseStatus_交易反馈","",.35f,.935f,.76f,.985f,18,new Color(.98f,.85f,.49f),TextAnchor.MiddleCenter);
                Set(shop,"_status",status);
            }
        }
        private static RectTransform Rect(Transform parent,string name,Color color)
        {
            var go=new GameObject(name,typeof(RectTransform),typeof(Image));go.transform.SetParent(parent,false);
            if(_undo)Undo.RegisterCreatedObjectUndo(go,UndoName);go.GetComponent<Image>().color=color;return(RectTransform)go.transform;
        }
        private static void Place(RectTransform rect,float x0,float y0,float x1,float y1)
        {rect.anchorMin=new Vector2(x0,y0);rect.anchorMax=new Vector2(x1,y1);rect.offsetMin=rect.offsetMax=Vector2.zero;}
        private static Text Label(Transform parent,string name,string caption,float x0,float y0,float x1,float y1,int size,Color color,TextAnchor alignment=TextAnchor.UpperLeft)
        {
            var go=new GameObject(name,typeof(RectTransform),typeof(Text));go.transform.SetParent(parent,false);if(_undo)Undo.RegisterCreatedObjectUndo(go,UndoName);
            // LegacyRuntime.ttf属于Runtime内置资源；AssetDatabase的ExtraResource查找域不包含它。
            var text=go.GetComponent<Text>();Place(text.rectTransform,x0,y0,x1,y1);text.text=caption;text.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize=size;text.color=color;text.alignment=alignment;text.raycastTarget=false;return text;
        }
        private static Button Button(Transform parent,string name,string caption,float x0,float y0,float x1,float y1,Color color)
        {
            var rect=Rect(parent,name,color);Place(rect,x0,y0,x1,y1);var button=Add<Button>(rect.gameObject);button.targetGraphic=rect.GetComponent<Image>();
            Label(rect,"Label",caption,0,0,1,1,24,Color.white,TextAnchor.MiddleCenter);return button;
        }
        private static T Add<T>(GameObject target)where T:Component=>_undo?Undo.AddComponent<T>(target):target.AddComponent<T>();
        private static void Set(Object target,string field,Object value)
        {if(_undo)Undo.RecordObject(target,UndoName);var data=new SerializedObject(target);data.FindProperty(field).objectReferenceValue=value;data.ApplyModifiedPropertiesWithoutUndo();}
        private static T Get<T>(Object target,string field)where T:Object=>(T)new SerializedObject(target).FindProperty(field)?.objectReferenceValue;
        private static T Find<T>(Scene scene)where T:Component{foreach(var root in scene.GetRootGameObjects()){var value=root.GetComponentInChildren<T>(true);if(value!=null)return value;}return null;}
        private static string Hash(){using var sha=SHA256.Create();return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(Formal)));}
        private static void Audit(Scene scene,string name)
        {
            var shop=Find<ShopController>(scene);var panel=Get<GameObject>(shop,"_panel");var text=new StringBuilder(scene.path+"\nDirty="+scene.isDirty);
            foreach(var child in panel.GetComponentsInChildren<Transform>(true))text.Append("\n"+child.name);
            text.Append("\nDialogWired="+(Get<ShopPurchaseDialog>(shop,"_purchaseDialog")!=null));Write(name,text.ToString());
        }
        private static void Write(string name,string text){Directory.CreateDirectory(Evidence);File.WriteAllText(Evidence+"/"+name,text);}
    }
}
