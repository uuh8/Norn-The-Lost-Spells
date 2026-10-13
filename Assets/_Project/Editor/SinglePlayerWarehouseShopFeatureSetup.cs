using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Game.Core;
using Game.Run;
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
    /// <summary>增量接线保留作者外围排布。正式场景仅单组Undo，保存只用于测试专用Review。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerWarehouseShopFeatureSetup
    {
        private const string Formal = "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity";
        private const string Review = "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub_WarehouseShopReview.unity";
        private const string Request = "Temp/SinglePlayer/warehouse-shop-feature.request";
        private const string Evidence = "TestResults/SinglePlayer/M6-T2-WarehouseShop";
        private const string UndoName = "仓库选择卡与分类网格及金币接线";
        private static bool _undo;
        static SinglePlayerWarehouseShopFeatureSetup()
        {
            EditorApplication.update += Poll;
        }
        private static void Poll()
        {
            if (!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            string command = File.ReadAllText(Request).Trim(); File.Delete(Request);
            try
            {
                if (EditorUtility.scriptCompilationFailed) throw new InvalidOperationException("编译失败，拒绝使用旧Assembly。");
                if (command == "inspect") Inspect();
                else if (command == "prepare") Prepare();
                else if (command == "apply-current") ApplyCurrent();
                else throw new ArgumentException(command);
            }
            catch (Exception e) { Write("setup-error.txt", e.ToString()); GameLog.Error(e.ToString(), "WarehouseShopFeature"); }
        }
        private static void Inspect()
        {
            var scene = SceneManager.GetActiveScene(); var text = new StringBuilder(scene.path + "\nDirty=" + scene.isDirty);
            foreach (var root in scene.GetRootGameObjects()) foreach (var ui in root.GetComponentsInChildren<SinglePlayerInventoryController>(true))
            {
                text.Append("\n\n" + ui.name + " enabled=" + ui.enabled);
                foreach (var field in new[] { "_panelRoot", "_gridView", "_wandsView", "_warehouseRoot", "_ghost" })
                    text.Append("\n" + field + "=" + Get<Object>(ui, field));
                if (ui.name == "InventoryCanvas_仓库") foreach (var child in ui.GetComponentsInChildren<RectTransform>(true))
                    if (child.GetComponent<InputField>() != null || child.GetComponent<Button>() != null || child.name.Contains("Wands")) text.Append("\n" + child.name + " " + child.anchorMin + " " + child.anchorMax);
            }
            Write("inspection.txt", text.ToString());
        }
        private static void Prepare()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++) if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("当前Scene有未保存修改，不代为保存或丢弃。");
            if (SceneManager.GetSceneByPath(Review).isLoaded) throw new InvalidOperationException("Review已打开，拒绝覆盖。");
            if (!File.Exists(Review)) { if (!AssetDatabase.CopyAsset(Formal, Review)) throw new IOException("复制Review失败。"); }
            else { File.Copy(Formal, Review, true); AssetDatabase.ImportAsset(Review, ImportAssetOptions.ForceUpdate); }
            var original = SceneManager.GetActiveScene(); var scene = EditorSceneManager.OpenScene(Review, OpenSceneMode.Additive);
            string originalHash = Hash();
            try
            {
                // new GameObject/DefaultControls默认归属Active Scene。先激活Review，避免临时控件
                // 在正式场景创建后跨Scene搬走，从而把未修改的正式场景也标成Dirty。
                SceneManager.SetActiveScene(scene); _undo = false; Apply(scene); EditorSceneManager.SaveScene(scene, Review); Write("prepared.txt", Review);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true); SceneManager.SetActiveScene(original);
                Write("review-isolation.txt", "FormalHashUnchanged=" + (Hash() == originalHash) + "\nFormalDirty=" + original.isDirty);
            }
        }
        [MenuItem("Tools/Norn/仓库/应用选择卡与分类网格及金币（Undo，不保存）")]
        private static void ApplyCurrent()
        {
            var scene = SceneManager.GetActiveScene(); if (scene.path != Formal) throw new InvalidOperationException("只处理当前正式据点。");
            string before = Hash(); Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName(UndoName);
            try
            {
                _undo = true; Apply(scene);
                var store = Window(scene, "InventoryCanvas_仓库"); Record(Get<GameObject>(store, "_panelRoot")); Get<GameObject>(store, "_panelRoot").SetActive(true);
                foreach (var name in new[] { "InventoryCanvas_背包", "InventoryCanvas_商店" }) { var ui = Window(scene, name); Record(Get<GameObject>(ui, "_panelRoot")); Get<GameObject>(ui, "_panelRoot").SetActive(false); }
                if (before != Hash()) throw new InvalidOperationException("正式场景磁盘意外变化。");
                Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(group); EditorSceneManager.MarkSceneDirty(scene);
                Write("current-applied.txt", "单组Undo；正式Scene未保存；SHA256=" + before);
            }
            catch { Undo.RevertAllDownToGroup(group); throw; }
            finally { _undo = false; }
        }
        private static void Apply(Scene scene)
        {
            var store = Window(scene, "InventoryCanvas_仓库"); var copy = Window(scene, "InventoryCanvas_商店");
            var panel = Get<GameObject>(store, "_panelRoot").transform; var left = panel.Find("LeftPart");
            ConfigureWands(store);
            var stock = Get<RectTransform>(store, "_warehouseRoot"); if (stock == null) throw new InvalidOperationException("仓库未配置原目录Content。");
            var grid = Catalog(store, stock); var workspace = Component<WarehouseWorkspaceView>(panel.gameObject);
            Set(store, "_warehouseWorkspace", workspace); Set(workspace, "_controller", store); Set(workspace, "_grid", grid);
            // 作者可能已将搜索框改成真正InputField，优先沿用；不重做用户设计的背景和外围Rect。
            InputField search = left.GetComponentInChildren<InputField>(true);
            if (search == null)
            {
                var old = left.Find("WarehouseDraft_Search"); search = CreateControl<InputField>(left, "WarehouseSearch_搜索物品", DefaultControls.CreateInputField(new DefaultControls.Resources()));
                if (old != null) { CopyRect((RectTransform)old, search.GetComponent<RectTransform>()); Hide(old); }
                else Anchors(search.GetComponent<RectTransform>(), .04f, .85f, .96f, .92f);
            }
            Set(workspace, "_search", search);
            var filters = left.Find("WarehouseDraft_Filters");
            var category = left.GetComponentInChildren<Dropdown>(true);
            int categoryButtons = 0;
            foreach (var button in left.GetComponentsInChildren<Button>(true))
            {
                var text = button.GetComponentInChildren<Text>(true); if (text == null) continue;
                int value = text.text == "全部" ? 0 : text.text == "法术" ? 1 : text.text == "材料" ? 3 : text.text == "法杖" ? 4 : text.text == "道具" ? 9 : -1;
                if (value < 0) continue;
                Record(button); for (int i = button.onClick.GetPersistentEventCount() - 1; i >= 0; i--) UnityEventTools.RemovePersistentListener(button.onClick, i);
                UnityEventTools.AddIntPersistentListener(button.onClick, workspace.SetCategoryFilter, value); categoryButtons++;
            }
            if (category == null && categoryButtons == 0)
            {
                category = CreateControl<Dropdown>(left, "WarehouseFilter_分类", DefaultControls.CreateDropdown(new DefaultControls.Resources()));
                if (filters != null) { CopyRect((RectTransform)filters, category.GetComponent<RectTransform>()); Hide(filters); }
                else Anchors(category.GetComponent<RectTransform>(), .04f, .77f, .96f, .84f);
                Record(category); category.ClearOptions(); category.AddOptions(new System.Collections.Generic.List<string> { "全部", "法术", "容器", "材料", "法杖", "背包", "食物", "草药", "炼金/消耗品" });
            }
            Set(workspace, "_category", category);
            if (categoryButtons > 0 && filters != null)
            {
                // 作者可能把原占位Text用作分类按钮父节点；只停用占位Text，不能隐藏其子按钮。
                var oldText = filters.GetComponent<Text>(); if (oldText != null) { Record(oldText); oldText.enabled = false; }
            }
            var organize = CreateControl<Button>(left, "WarehouseOrganize_整理", DefaultControls.CreateButton(new DefaultControls.Resources()));
            Anchors(organize.GetComponent<RectTransform>(), .70f, .02f, .96f, .075f); organize.GetComponentInChildren<Text>().text = "整理目录"; Set(workspace, "_organize", organize);
            Set(workspace, "_capacity", FindText(left, "WarehouseDraft_Header"));
            var gold = left.Find("GoldRegion"); var amount = FindText(gold, "Gold_Number"); Wallet(gold, store.Session, amount);
            var sourceIcon = gold.GetComponentInChildren<Image>(true); foreach (var image in gold.GetComponentsInChildren<Image>(true)) if (image.name == "Gold_Logo") sourceIcon = image;
            var equipment = panel.Find("MiddlePart/BackpackEquipmentPreview_装备预览");
            if (equipment != null) Preview(equipment, workspace);
            else
            {
                var logo = panel.Find("MiddlePart/Backpack_Logo"); if (logo == null) throw new InvalidOperationException("作者背包装备预览区域缺失。");
                var icon = logo.GetComponentInChildren<Image>(true); Set(workspace, "_bagIcon", icon); Set(workspace, "_bagFallback", icon.sprite);
                var caption = Label(logo, "CurrentBackpack_名称与容量"); Anchors(caption.rectTransform, .02f, .01f, .98f, .23f); caption.resizeTextMaxSize = 15; Set(workspace, "_bagCaption", caption);
            }
            ConfigureShop(scene, copy, sourceIcon != null ? sourceIcon.sprite : null);
            // 不能ForceUpdateCanvases：它会重排所有已打开Scene的Canvas，包含用户正式场景。
            // Review在Play Mode由Unity正常完成Layout，Editor只接线副本自身的对象。
        }
        private static void ConfigureWands(SinglePlayerInventoryController ui)
        {
            var view = Get<WandInventoryView>(ui, "_wandsView"); if (view == null) throw new InvalidOperationException("仓库WandInventoryView缺失。");
            var host = view.transform.Find("BackpackRuntime_Wands"); if (host == null) throw new InvalidOperationException("仓库BackpackRuntime_Wands缺失。");
            // 原三行只在仓库复制件删除；先记录引用，Undo才能恢复原面板数组。
            Record(view);
            var oldViewport = host.Find("Viewport");
            if (oldViewport != null) Remove(oldViewport.gameObject);
            var oldScroll = host.GetComponent<ScrollRect>(); if (oldScroll != null) Remove(oldScroll);
            var cards = Rect(host, "WarehouseWands_三个选择卡"); Anchors(cards, .015f, .64f, .985f, .99f);
            var title = Label(host, "SelectedWand_程序标题"); Anchors(title.rectTransform, .015f, .51f, .985f, .62f);
            var stats = Label(host, "SelectedWand_属性"); Anchors(stats.rectTransform, .015f, .36f, .985f, .50f); stats.resizeTextMaxSize = 16;
            var scrollHost = Rect(host, "SelectedWand_程序滚动区"); Anchors(scrollHost, .015f, .015f, .985f, .35f);
            var viewport = Rect(scrollHost, "Viewport"); Anchors(viewport, 0, .16f, 1, 1); Component<RectMask2D>(viewport.gameObject);
            var content = Rect(viewport, "ProgramContent_当前法杖槽"); TopLeft(content);
            var scroll = Component<ScrollRect>(scrollHost.gameObject); Record(scroll); scroll.enabled = true; scroll.viewport = viewport; scroll.content = content;
            scroll.horizontal = true; scroll.vertical = false; scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 48;
            var bar = CreateControl<Scrollbar>(scrollHost, "Scrollbar_程序横向", DefaultControls.CreateScrollbar(new DefaultControls.Resources()));
            Anchors(bar.GetComponent<RectTransform>(), 0, 0, 1, .14f); Record(bar); bar.direction = Scrollbar.Direction.LeftToRight; scroll.horizontalScrollbar = bar;
            Record(view); var data = new SerializedObject(view); data.FindProperty("_selectorLayout").boolValue = true; data.FindProperty("_wideLayout").boolValue = true; data.FindProperty("_panels").arraySize = 0; data.ApplyModifiedPropertiesWithoutUndo();
            Set(view, "_equipmentRoot", cards); Set(view, "_programRoot", content); Set(view, "_programTitle", title); Set(view, "_stats", stats);
        }
        private static void ConfigureShop(Scene scene, SinglePlayerInventoryController copy, Sprite coin)
        {
            var shop = Find<ShopController>(scene); var panel = Get<GameObject>(shop, "_panel").transform;
            var middle = panel.Find("ShopDraft_Middle_背包与仓库"); if (middle == null) throw new InvalidOperationException("商店当前三列区域未配置。");
            var carry = middle.Find("CarryRegion_携带背包"); var stockRegion = middle.Find("WarehouseRegion_仓库");
            var grid = Get<InventoryGridView>(copy, "_gridView"); var viewport = (RectTransform)grid.transform.parent;
            var oldView = Get<ShopBackpackView>(shop, "_backpackView");
            Text capacity = oldView != null ? Get<Text>(oldView, "_capacity") : null;
            if (oldView != null) { Set(shop, "_backpackView", null); Remove(oldView.transform.parent.gameObject); }
            // 搬作者复制件的真实格子视口，不再用只读镜像。其ScrollRect、滚动条和抓取点逻辑继续复用。
            if (_undo) Undo.SetTransformParent(viewport, carry, UndoName); else viewport.SetParent(carry, false);
            Anchors(viewport, .025f, .035f, .70f, .78f); Record(viewport.gameObject); viewport.gameObject.SetActive(true);
            Record(grid); var gridData = new SerializedObject(grid); gridData.FindProperty("_fitToViewport").boolValue = false; gridData.FindProperty("_cellSize").floatValue = 70; gridData.ApplyModifiedPropertiesWithoutUndo();
            var stock = Get<RectTransform>(shop, "_ownedRoot"); var catalog = Catalog(copy, stock);
            var workspace = Component<WarehouseWorkspaceView>(panel.gameObject); Set(copy, "_warehouseWorkspace", workspace); Set(workspace, "_controller", copy); Set(workspace, "_grid", catalog); Set(workspace, "_capacity", FindText(stockRegion, "Header"));
            if (capacity != null) Set(workspace, "_bagCapacity", capacity);
            var preview = carry.Find("BackpackPreview_装备预览"); Preview(preview, workspace);
            var placeholder = preview.Find("Placeholder"); if (placeholder != null) Hide(placeholder);
            var wallet = Rect(preview, "Wallet_当前金币"); Anchors(wallet, .03f, .01f, .97f, .22f); CoinWallet(wallet, copy.Session, coin);
            var goods = panel.Find("ShopDraft_Left_商品与购买预览/GoodsRegion_商店货品");
            var headerWallet = Rect(goods, "Wallet_金币"); Anchors(headerWallet, .54f, .90f, .98f, .995f); CoinWallet(headerWallet, copy.Session, coin);
            var header = FindText(goods, "Header"); if (header != null) Anchors(header.rectTransform, .03f, .90f, .52f, .995f);
            var bagTarget = Component<InventoryDropTarget>(preview.gameObject); Set(copy, "_backpackTarget", bagTarget);
            foreach (var field in new[] { "_toggleAction", "_rotateAction", "_cancelAction", "_splitAction", "_wandsView", "_spellStorage", "_nearbyGrid", "_groundRoot", "_groundTarget", "_workspace", "_backpackWorkspace", "_otherWorkspace", "_details" }) Set(copy, field, null);
            Set(copy, "_panelRoot", panel.gameObject); Set(copy, "_session", Get<SinglePlayerInventorySession>(shop, "_inventory"));
            Record(copy); var data = new SerializedObject(copy); data.FindProperty("_embedded").boolValue = true; data.FindProperty("_openOnStart").boolValue = false; data.ApplyModifiedPropertiesWithoutUndo(); copy.enabled = true;
            var ghost = Get<InventoryItemView>(copy, "_ghost"); Record(ghost.gameObject); ghost.name = "DragGhost_商店拖拽物品预览（勿删）"; ghost.gameObject.SetActive(false);
            Set(shop, "_embeddedInventory", copy);
            // 用户已明确允许清理过时冗余。真正交互网格已移出归档，业务ShopController保持原身份。
            foreach (var part in new[] { "LeftPart", "MiddlePart", "RightPart", "Logo" })
            { var obsolete = panel.Find(part); if (obsolete != null && !obsolete.gameObject.activeSelf) Remove(obsolete.gameObject); }
            foreach (var obsolete in copy.GetComponents<MonoBehaviour>())
                if (!obsolete.enabled && (obsolete is RaidPreparationView || obsolete is SpellAttachmentView || obsolete is RaidResultView || obsolete is ExtractionHud)) Remove(obsolete);
        }
        private static WarehouseCatalogGridView Catalog(SinglePlayerInventoryController ui, RectTransform content)
        {
            var fitter = content.GetComponent<ContentSizeFitter>(); if (fitter != null) Remove(fitter);
            var layout = content.GetComponent<LayoutGroup>(); if (layout != null) Remove(layout);
            var nearby = content.GetComponent<NearbyItemGridView>();
            if (nearby != null) { Set(ui, "_nearbyGrid", null); Remove(nearby); }
            TopLeft(content); var image = Component<Image>(content.gameObject); Record(image); image.color = Color.clear; image.raycastTarget = true;
            var scroll = content.GetComponentInParent<ScrollRect>(true); if (scroll == null) throw new InvalidOperationException("目录没有ScrollRect。");
            Record(scroll); scroll.enabled = true; scroll.content = content; scroll.vertical = true; scroll.horizontal = false; scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 48; scroll.verticalNormalizedPosition = 1;
            var viewport = scroll.viewport != null ? scroll.viewport : (RectTransform)scroll.transform;
            if (scroll.name == "Ground")
            {
                // 复制自附近物品的旧Root/Viewport保存着固定高度和偏移；只让内部滚动区
                // 填满作者已有WarehouseRegion，外围布局不动，避免内容覆盖金币栏。
                Anchors((RectTransform)scroll.transform, 0, 0, 1, 1);
                Anchors(viewport, .02f, .02f, .96f, .98f);
                if (scroll.horizontalScrollbar != null)
                { var oldBar = scroll.horizontalScrollbar.gameObject; scroll.horizontalScrollbar = null; Remove(oldBar); }
            }
            Component<RectMask2D>(viewport.gameObject);
            var view = Component<WarehouseCatalogGridView>(content.gameObject); Set(ui, "_warehouseGrid", view); Set(ui, "_warehouseRoot", content);
            // 可见空白边缘也属于仓库；清掉复制件Ground引用，不能把仓库空白误判为丢地面。
            Set(ui, "_groundTarget", null); Set(ui, "_groundRoot", null);
            Set(ui, "_warehouseTarget", Component<InventoryDropTarget>(viewport.gameObject));
            var oldTarget = content.GetComponent<InventoryDropTarget>(); if (oldTarget != null) Remove(oldTarget);
            var auto = Component<InventoryDragAutoScroll>(scroll.gameObject); Record(auto); auto.Configure(scroll);
            return view;
        }
        private static void Preview(Transform region, WarehouseWorkspaceView workspace)
        {
            if (region == null) throw new InvalidOperationException("背包装备预览区域缺失。");
            var icon = Component<Image>(Rect(region, "CurrentBackpack_装备图").gameObject); Anchors(icon.rectTransform, .08f, .42f, .92f, .97f); icon.raycastTarget = false;
            var caption = Label(region, "CurrentBackpack_名称与容量"); Anchors(caption.rectTransform, .03f, .23f, .97f, .41f); caption.resizeTextMaxSize = 16;
            var old = region.Find("Caption"); Hide(old); Set(workspace, "_bagIcon", icon); Set(workspace, "_bagCaption", caption);
        }
        private static void CoinWallet(Transform root, SinglePlayerInventorySession session, Sprite coin)
        {
            var icon = Component<Image>(Rect(root, "Coin_图标").gameObject); Anchors(icon.rectTransform, .01f, .10f, .25f, .90f); Record(icon); icon.sprite = coin; icon.preserveAspect = true; icon.raycastTarget = false;
            var amount = Label(root, "Coin_真实余额"); Anchors(amount.rectTransform, .27f, 0, 1, 1); amount.color = new Color(1, .83f, .24f); Wallet(root, session, amount);
        }
        private static void Wallet(Transform root, SinglePlayerInventorySession session, Text amount) { var wallet = Component<WalletView>(root.gameObject); Set(wallet, "_session", session); Set(wallet, "_amount", amount); }
        private static Text FindText(Transform root, string name) { if (root == null) return null; foreach (var text in root.GetComponentsInChildren<Text>(true)) if (text.name == name) return text; return null; }
        private static T CreateControl<T>(Transform parent, string name, GameObject candidate) where T : Component
        { var existing = parent.Find(name); if (existing != null) { Object.DestroyImmediate(candidate); return existing.GetComponent<T>(); } candidate.name = name; candidate.transform.SetParent(parent, false); if (_undo) Undo.RegisterCreatedObjectUndo(candidate, UndoName); return candidate.GetComponent<T>(); }
        private static RectTransform Rect(Transform parent, string name) { var old = parent.Find(name) as RectTransform; if (old != null) return old; var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(parent, false); if (_undo) Undo.RegisterCreatedObjectUndo(go, UndoName); return (RectTransform)go.transform; }
        private static Text Label(Transform parent, string name) { var label = Component<Text>(Rect(parent, name).gameObject); Record(label); label.font = AssetDatabase.GetBuiltinExtraResource<Font>("LegacyRuntime.ttf"); label.color = Color.white; label.raycastTarget = false; label.resizeTextForBestFit = true; label.resizeTextMinSize = 10; label.resizeTextMaxSize = 22; label.alignment = TextAnchor.MiddleLeft; return label; }
        private static void TopLeft(RectTransform rect) { Record(rect); rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1); rect.anchoredPosition = Vector2.zero; }
        private static void CopyRect(RectTransform source, RectTransform target) { Record(target); target.anchorMin = source.anchorMin; target.anchorMax = source.anchorMax; target.pivot = source.pivot; target.anchoredPosition = source.anchoredPosition; target.sizeDelta = source.sizeDelta; }
        private static void Anchors(RectTransform rect, float x0, float y0, float x1, float y1) { Record(rect); rect.anchorMin = new Vector2(x0, y0); rect.anchorMax = new Vector2(x1, y1); rect.offsetMin = rect.offsetMax = Vector2.zero; }
        private static T Component<T>(GameObject target) where T : Component { var result = target.GetComponent<T>(); return result != null ? result : _undo ? Undo.AddComponent<T>(target) : target.AddComponent<T>(); }
        private static T Find<T>(Scene scene) where T : Component { foreach (var root in scene.GetRootGameObjects()) { var value = root.GetComponentInChildren<T>(true); if (value != null) return value; } throw new InvalidOperationException(typeof(T).Name + "缺失。"); }
        private static SinglePlayerInventoryController Window(Scene scene, string name) { foreach (var root in scene.GetRootGameObjects()) foreach (var ui in root.GetComponentsInChildren<SinglePlayerInventoryController>(true)) if (ui.name == name) return ui; throw new InvalidOperationException(name + "缺失。"); }
        private static T Get<T>(Object target, string field) where T : Object => (T)new SerializedObject(target).FindProperty(field).objectReferenceValue;
        private static void Set(Object target, string field, Object value) { Record(target); var data = new SerializedObject(target); var property = data.FindProperty(field); if (property == null) throw new InvalidOperationException(target.name + ": " + field + "缺失。"); property.objectReferenceValue = value; data.ApplyModifiedPropertiesWithoutUndo(); }
        private static void Record(Object target) { if (_undo && target != null) Undo.RecordObject(target, UndoName); }
        private static void Hide(Transform target) { if (target == null) return; Record(target.gameObject); target.gameObject.SetActive(false); }
        private static void Remove(Object target) { if (_undo) Undo.DestroyObjectImmediate(target); else Object.DestroyImmediate(target); }
        private static string Hash() { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(Formal))).Replace("-", ""); }
        private static void Write(string name, string text) { Directory.CreateDirectory(Evidence); File.WriteAllText(Evidence + "/" + name, text); }
    }
}
