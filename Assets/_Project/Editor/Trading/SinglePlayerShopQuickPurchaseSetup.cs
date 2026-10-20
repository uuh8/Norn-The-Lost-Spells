using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Game.Core;
using Game.UI;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.EditorTools
{
    /// <summary>只增量整理商店；Review 隔离保存，正式据点仅单组 Undo，保护用户中列布局。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerShopQuickPurchaseSetup
    {
        private const string Formal = "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity";
        private const string Review = "Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_ShopQuickPurchaseReview.unity";
        private const string Request = "Temp/SinglePlayer/shop-quick-purchase.request";
        private const string Evidence = "TestResults/SinglePlayer/M6-T4-QuickPurchase";
        private const string UndoName = "商店快捷购买与单一详情区域";
        private static bool _undo;
        static SinglePlayerShopQuickPurchaseSetup() => EditorApplication.update += Poll;
        private static void Poll()
        {
            if (!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            string command = File.ReadAllText(Request).Trim(); File.Delete(Request);
            try
            {
                if (EditorUtility.scriptCompilationFailed) throw new InvalidOperationException("编译失败，拒绝使用旧 Assembly。");
                if (command == "copy-baseline") Prepare(false);
                else if (command == "prepare") Prepare(true);
                else if (command == "apply-current") ApplyCurrent();
                else if (command == "inspect") Audit(SceneManager.GetActiveScene(), "inspection.txt");
                else throw new ArgumentException(command);
            }
            catch (Exception exception) { Write("setup-error.txt", exception.ToString()); GameLog.Error(exception.ToString(), "ShopQuickPurchase"); }
        }
        private static void Prepare(bool apply)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++) if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("Scene 有未保存修改，不代为保存或丢弃。");
            if (SceneManager.GetSceneByPath(Review).isLoaded) throw new InvalidOperationException("Review 已打开，拒绝覆盖。");
            if (!File.Exists(Review)) { if (!AssetDatabase.CopyAsset(Formal, Review)) throw new IOException("复制 Review 失败。"); }
            else { File.Copy(Formal, Review, true); AssetDatabase.ImportAsset(Review, ImportAssetOptions.ForceUpdate); }
            var original = SceneManager.GetActiveScene(); string before = Hash();
            var scene = EditorSceneManager.OpenScene(Review, OpenSceneMode.Additive);
            try
            {
                // new GameObject 默认属于 Active Scene；激活副本后创建，避免把正式 Scene 标脏。
                SceneManager.SetActiveScene(scene); _undo = false;
                if (apply) { Apply(scene); EditorSceneManager.SaveScene(scene, Review); }
                Audit(scene, "review-layout.txt"); Write("prepared.txt", "Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_ShopQuickPurchaseReview.unity\\nApplied=" + apply);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true); SceneManager.SetActiveScene(original);
                Write("review-isolation.txt", "FormalHashUnchanged=" + (before == Hash()) + "\nFormalDirty=" + original.isDirty);
            }
        }
        [MenuItem("Tools/Norn/商店/应用快捷购买布局（Undo，不保存）")]
        private static void ApplyCurrent()
        {
            var scene = SceneManager.GetActiveScene(); if (scene.path != Formal) throw new InvalidOperationException("只处理当前正式据点。");
            string before = Hash(); Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName(UndoName);
            try
            {
                _undo = true; Apply(scene);
                var panel = Get<GameObject>(Find<ShopController>(scene), "_panel"); Record(panel); panel.SetActive(true);
                foreach (var root in scene.GetRootGameObjects()) foreach (var ui in root.GetComponentsInChildren<SinglePlayerInventoryController>(true))
                    if (ui.name == "InventoryCanvas_背包" || ui.name == "InventoryCanvas_仓库") { var other = Get<GameObject>(ui, "_panelRoot"); Record(other); other.SetActive(false); }
                if (before != Hash()) throw new InvalidOperationException("正式 Scene 磁盘发生意外变化。");
                Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(group); EditorSceneManager.MarkSceneDirty(scene);
                Audit(scene, "current-layout.txt"); Write("current-applied.txt", "单组 Undo；未保存正式 Scene。\nSHA256=" + before);
            }
            catch { Undo.RevertAllDownToGroup(group); throw; }
            finally { _undo = false; }
        }
        private static void Apply(Scene scene)
        {
            var shop = Find<ShopController>(scene); if (shop == null) throw new InvalidOperationException("缺少 ShopController。");
            // 作者已删除旧数量区或已采用确认框时，不允许旧菜单重建过时左栏。
            if(Get<InputField>(shop,"_quantity")==null || Get<ShopPurchaseDialog>(shop,"_purchaseDialog")!=null)
                throw new InvalidOperationException("旧快捷购买布局已停用。请使用 Tools/Norn/商店/添加购买确认框（Undo，不保存），保留作者现有布局。");
            var panel = Get<GameObject>(shop, "_panel").transform;
            var left = panel.Find("ShopDraft_Left_商品与购买预览") ?? panel.Find("Shop_Left_商品与快捷购买");
            var right = panel.Find("ShopDraft_Right_详情与交易") ?? panel.Find("Shop_Right_物品详情");
            var goods = left?.Find("GoodsRegion_商店货品"); var details = right?.Find("DetailsRegion_物品详情");
            if (goods == null || details == null) throw new InvalidOperationException("需要已保存的三列商店草稿，拒绝重建作者布局。");
            var existing = left.Find("QuickPurchaseRegion_快捷购买");
            if (existing != null && Get<Object>(shop, "_quickPurchasePopup") != null) { Audit(scene, "already-applied.txt"); return; }
            var footer = Rect(left, "QuickPurchaseRegion_快捷购买", new Color(.36f, .24f, .08f)); Anchors(footer, 0, 0, 1, .30f);
            // 控件先搬走再删旧区域，SerializedObject 引用仍指向同一对象，不会丢失交易参数。
            var quantity = Get<InputField>(shop, "_quantity"); Move(quantity.transform, footer, .49f, .79f, .97f, .98f); Record(quantity); quantity.text = "1";
            Label(footer, "QuantityCaption", "数量 / GMU", .03f, .79f, .47f, .98f, 18);
            Move(Get<Dropdown>(shop, "_containers").transform, footer, .03f, .57f, .97f, .76f);
            var summary = Get<Text>(shop, "_summary"); Move(summary.transform, footer, .03f, .31f, .97f, .55f); Style(summary, 16);
            var status = Get<Text>(shop, "_status"); Move(status.transform, footer, .03f, .16f, .97f, .31f); Style(status, 17);
            var buy = Get<Button>(shop, "_buyButton"); Move(buy.transform, footer, .03f, .01f, .97f, .15f); Caption(buy, "购买物品", 21);
            Record(buy.targetGraphic); buy.targetGraphic.color = new Color(.75f, .44f, .08f);
            var close = Get<Button>(shop, "_closeButton"); var authoredClose = panel.Find("Close")?.GetComponent<Button>();
            if (authoredClose != null && authoredClose != close)
            {
                // 复用作者已有的金框关闭按钮，移除盖在它上面的旧草稿按钮。
                // 原工具曾加 persistent Close；现在交由 OnEnable 订阅，避免一次 click 调用两次。
                Record(authoredClose);
                for (int i = authoredClose.onClick.GetPersistentEventCount() - 1; i >= 0; i--)
                    if (authoredClose.onClick.GetPersistentTarget(i) == shop && authoredClose.onClick.GetPersistentMethodName(i) == "Close") UnityEventTools.RemovePersistentListener(authoredClose.onClick, i);
                Set(shop, "_closeButton", authoredClose); Remove(close.gameObject);
            }
            else { Move(close.transform, panel, .83f, .91f, .98f, .975f); Caption(close, "Esc 关闭", 20); }
            var obsoleteToggle = Get<Toggle>(shop, "_toBackpack"); Set(shop, "_toBackpack", null); if (obsoleteToggle != null) Remove(obsoleteToggle.gameObject);
            var cart = left.Find("CartRegion_购买预览"); if (cart != null) Remove(cart.gameObject);
            Anchors((RectTransform)goods, 0, .315f, 1, 1);
            var detailText = Get<Text>(shop, "_details"); Move(detailText.transform, details, .04f, .04f, .96f, .88f);
            Record(detailText); detailText.text = "物品详情待后续开发"; Style(detailText, 20);
            Anchors((RectTransform)details, 0, 0, 1, 1);
            for (int i = right.childCount - 1; i >= 0; i--) if (right.GetChild(i) != details) Remove(right.GetChild(i).gameObject);
            Record(left.gameObject); left.name = "Shop_Left_商品与快捷购买";
            Record(right.gameObject); right.name = "Shop_Right_物品详情";
            SetBool(shop, "_detailsPlaceholder", true); SetBool(shop, "_quickPurchase", true);
            // 弹层只创建一次；运行时透明根节点不拦其他商品，只有小菜单内部接受 Raycast。
            var root = Rect(panel, "QuickPurchasePopup_鼠标购买", new Color(0, 0, 0, 0)); Anchors(root, 0, 0, 1, 1);
            var menu = Rect(root, "Menu_购买", new Color(.06f, .11f, .18f, .98f)); Record(menu);
            menu.anchorMin = menu.anchorMax = new Vector2(.5f, .5f); menu.pivot = new Vector2(0, 1); menu.sizeDelta = new Vector2(220, 76); menu.anchoredPosition = Vector2.zero;
            var buttonRect = Rect(menu, "Buy_购买", new Color(.67f, .39f, .06f)); Anchors(buttonRect, .04f, .12f, .96f, .88f);
            var popupButton = Add<Button>(buttonRect.gameObject); popupButton.targetGraphic = buttonRect.GetComponent<Image>();
            Label(buttonRect, "Label", "购买", .02f, .02f, .98f, .98f, 24).alignment = TextAnchor.MiddleCenter;
            var popup = Add<ShopQuickPurchasePopup>(root.gameObject);
            Set(popup, "_controller", shop); Set(popup, "_menu", menu); Set(popup, "_buy", popupButton); Set(shop, "_quickPurchasePopup", popup);
            Record(root.gameObject); root.gameObject.SetActive(false);
            // 不使用 ForceUpdateCanvases；它会重排全部打开 Scene，而不只处理副本。
        }
        private static void Audit(Scene scene, string file)
        {
            var shop = Find<ShopController>(scene); var panel = shop != null ? Get<GameObject>(shop, "_panel") : null;
            var report = new StringBuilder(scene.path + "\nDirty=" + scene.isDirty);
            if (panel != null) foreach (var child in panel.GetComponentsInChildren<Transform>(true)) report.Append("\n" + child.name);
            Write(file, report.ToString());
        }
        private static T Find<T>(Scene scene) where T : Component { foreach (var root in scene.GetRootGameObjects()) { var value = root.GetComponentInChildren<T>(true); if (value != null) return value; } return null; }
        private static T Get<T>(Object target, string field) where T : Object => (T)new SerializedObject(target).FindProperty(field)?.objectReferenceValue;
        private static void Set(Object target, string field, Object value) { Record(target); var data = new SerializedObject(target); var property = data.FindProperty(field); if (property == null) throw new InvalidOperationException("缺少字段 " + field); property.objectReferenceValue = value; data.ApplyModifiedPropertiesWithoutUndo(); }
        private static void SetBool(Object target, string field, bool value) { Record(target); var data = new SerializedObject(target); data.FindProperty(field).boolValue = value; data.ApplyModifiedPropertiesWithoutUndo(); }
        private static void Record(Object target) { if (_undo && target != null) Undo.RecordObject(target, UndoName); }
        private static T Add<T>(GameObject target) where T : Component => _undo ? Undo.AddComponent<T>(target) : target.AddComponent<T>();
        private static void Remove(Object target) { if (_undo) Undo.DestroyObjectImmediate(target); else Object.DestroyImmediate(target); }
        private static void Move(Transform target, Transform parent, float x0, float y0, float x1, float y1) { if (_undo) Undo.SetTransformParent(target, parent, UndoName); else target.SetParent(parent, false); Anchors((RectTransform)target, x0, y0, x1, y1); Record(target.gameObject); target.gameObject.SetActive(true); }
        private static void Anchors(RectTransform rect, float x0, float y0, float x1, float y1) { Record(rect); rect.anchorMin = new Vector2(x0, y0); rect.anchorMax = new Vector2(x1, y1); rect.pivot = new Vector2(.5f, .5f); rect.offsetMin = rect.offsetMax = Vector2.zero; }
        private static RectTransform Rect(Transform parent, string name, Color color) { var go = new GameObject(name, typeof(RectTransform), typeof(Image)); go.transform.SetParent(parent, false); go.GetComponent<Image>().color = color; if (_undo) Undo.RegisterCreatedObjectUndo(go, UndoName); return (RectTransform)go.transform; }
        private static Text Label(Transform parent, string name, string caption, float x0, float y0, float x1, float y1, int size) { var go = new GameObject(name, typeof(RectTransform), typeof(Text)); go.transform.SetParent(parent, false); if (_undo) Undo.RegisterCreatedObjectUndo(go, UndoName); var label = go.GetComponent<Text>(); Anchors(label.rectTransform, x0, y0, x1, y1); label.font = AssetDatabase.GetBuiltinExtraResource<Font>("LegacyRuntime.ttf"); label.text = caption; Style(label, size); return label; }
        private static void Style(Text text, int size) { Record(text); text.fontSize = size; text.color = new Color(.95f, .96f, 1); text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Truncate; text.raycastTarget = false; }
        private static void Caption(Button button, string caption, int size) { var label = button.GetComponentInChildren<Text>(true); Record(label); label.text = caption; Style(label, size); label.alignment = TextAnchor.MiddleCenter; Anchors(label.rectTransform, .03f, .03f, .97f, .97f); }
        private static string Hash() { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(Formal))).Replace("-", ""); }
        private static void Write(string name, string text) { Directory.CreateDirectory(Evidence); File.WriteAllText(Evidence + "/" + name, text); }
    }
}
