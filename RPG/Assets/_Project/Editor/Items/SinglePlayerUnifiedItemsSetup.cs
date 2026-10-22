using System;
using System.IO;
using Game.Character;
using Game.Core;
using Game.Run;
using Game.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.EditorTools
{
    /// <summary>统一资产和作者布局的接线；只保存SO/Prefab与隔离Review，正式Scene保留Undo/Dirty。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerUnifiedItemsSetup
    {
        private const string Request = "Temp/SinglePlayer/unified-items.request";
        private const string Hub = "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity";
        private const string Evidence = "TestResults/SinglePlayer/M6-T10-PortalAim";
        static SinglePlayerUnifiedItemsSetup() { EditorApplication.update += Poll; }
        private static void Poll()
        {
            if (!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            string command = File.ReadAllText(Request).Trim(); File.Delete(Request);
            try
            {
                if (EditorUtility.scriptCompilationFailed) throw new InvalidOperationException("编译失败");
                if (command == "prepare") Prepare();
                else if (command == "inspect") Inspect();
                else if (command == "apply-current") ApplyCurrent();
                else throw new ArgumentException(command);
            }
            catch (Exception error) { Write("setup-error.txt", error.ToString()); GameLog.Error(error.ToString(), "UnifiedItems"); }
        }
        private static void Write(string name, string body) { Directory.CreateDirectory(Evidence); File.WriteAllText(Evidence + "/" + name, DateTime.UtcNow.ToString("O") + "\n" + body); }
        private static void Set(Object target, string field, Object reference)
        { var data = new SerializedObject(target); data.FindProperty(field).objectReferenceValue = reference; data.ApplyModifiedPropertiesWithoutUndo(); }
        private static Sprite Frame()
        {
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath("Assets/_Project/Art/UI/Funtion_UI2/Frame_Selected.png"))
                if (asset is Sprite sprite) return sprite;
            throw new InvalidOperationException("Frame_Selected缺少Sprite子资产");
        }
        internal static void ConfigureHud(GameObject root, bool undo)
        {
            foreach (var hud in root.GetComponentsInChildren<HeldItemHud>(true))
            { if (undo) Undo.RecordObject(hud, "接入Frame_Selected"); Set(hud, "_selectedFrame", Frame()); }
        }
        private static void ConfigureScene(Scene scene, bool undo)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                ConfigureHud(root, undo);
                foreach (var workspace in root.GetComponentsInChildren<WarehouseWorkspaceView>(true)) ConfigureTools(workspace, undo);
                foreach (var portal in root.GetComponentsInChildren<HubPracticePortal>(true)) InstallPortalVisual(portal.gameObject, undo);
                foreach (var portal in root.GetComponentsInChildren<HubExpeditionPortal>(true)) InstallPortalVisual(portal.gameObject, undo);
                foreach (var collection in root.GetComponentsInChildren<MaterialCollectionController>(true))
                { if (undo) Undo.RecordObject(collection, "大容量采集速率"); var data = new SerializedObject(collection); data.FindProperty("_collectionRate").floatValue = 2000; data.ApplyModifiedPropertiesWithoutUndo(); }
                foreach (var text in root.GetComponentsInChildren<Text>(true))
                {
                    // 作者Logo已经承担标题；旧草稿工具的纯文字标题没有运行时消费者，删除它以免叠字。
                    if (text.name == "ShopDraft_Title")
                    { if (undo) Undo.DestroyObjectImmediate(text.gameObject); else Object.DestroyImmediate(text.gameObject); continue; }
                    string value = text.text.Replace("[E]", "[F]").Replace("E 交互", "F 交互").Replace("E采集", "F采集").Replace("E 采集", "F 采集");
                    if (value == text.text) continue; if (undo) Undo.RecordObject(text, "统一F提示"); text.text = value;
                }
            }
        }
        [MenuItem("Tools/Norn/物品/准备G瞄准与F门Review（不保存正式场景）")]
        private static void Prepare()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++) if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("Scene未保存，不代保存或丢弃");
            string dataRoot = "Assets/_Project/ScriptableObjects/";
            foreach (string path in new[] { "Items/Containers/Item_Container_01.asset", "Items/Containers/Item_Container_02.asset", "Items/Containers/Item_Container_03.asset", "Items/Containers/Definitions/Item_Container_Bottle.asset" })
            {
                var item = AssetDatabase.LoadAssetAtPath<ItemDefinition>(dataRoot + path); if (item == null) throw new InvalidOperationException(path);
                var data = new SerializedObject(item); int capacity = path.Contains("02") ? 30000 : path.Contains("03") ? 60000 : 15000;
                data.FindProperty("_containerCapacityGmu").intValue = capacity; data.ApplyModifiedPropertiesWithoutUndo(); AssetDatabase.SaveAssetIfDirty(item);
            }
            var use = AssetDatabase.LoadAssetAtPath<ContainerUseDefinition>(dataRoot + "Items/Containers/Use/UseDefinition.asset");
            var useData = new SerializedObject(use); useData.FindProperty("_pourRate").floatValue = 2000;
            useData.FindProperty("_collectionRate").floatValue = 2000;
            useData.FindProperty("_pourSpeed").floatValue = 8; useData.FindProperty("_pourLift").floatValue = 2;
            useData.FindProperty("_throwSpeed").floatValue = 24; useData.FindProperty("_throwLift").floatValue = 6;
            useData.FindProperty("_maximumFlightDistance").floatValue = 80; useData.ApplyModifiedPropertiesWithoutUndo(); AssetDatabase.SaveAssetIfDirty(use);
            for (int i = 0; i < 3; i++)
            {
                var wand = AssetDatabase.LoadAssetAtPath<ItemDefinition>(dataRoot + "Spell/Wands/Items/Item_Wand_Starter" + (char)('A' + i) + ".asset");
                var model = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/Wand/Wand0" + (i + 1) + ".prefab");
                if (wand == null || model == null) throw new InvalidOperationException("缺少三杖资产");
                Set(wand, "_worldModel", model); AssetDatabase.SaveAssetIfDirty(wand);
            }
            int icons = ItemIconBaker.BakeAll(true);
            // 只配置新档模板和新店供货，不修改任何已存在的玩家实例/Repository。
            var seeds = AssetDatabase.LoadAssetAtPath<SinglePlayerStartingInventory>(dataRoot + "World/Hub/Inventory/StartingInventory.asset");
            var seedData = new SerializedObject(seeds); var entries = seedData.FindProperty("_items");
            for (int i=0;i<entries.arraySize;i++)
            {
                var seed=entries.GetArrayElementAtIndex(i);var item=seed.FindPropertyRelative("Definition").objectReferenceValue as ItemDefinition;
                if(item==null||item.Kind!=ItemKind.Container)continue;var contents=seed.FindPropertyRelative("Materials");if(contents.arraySize>0)continue;
                var itemData=new SerializedObject(item);int mask=itemData.FindProperty("_containerMaterialsMask").intValue;byte key=1;while(key<=8&&(mask&(1<<key))==0)key++;
                contents.arraySize=1;var amount=contents.GetArrayElementAtIndex(0);amount.FindPropertyRelative("MaterialKey").intValue=key;amount.FindPropertyRelative("Quantity").ulongValue=(ulong)itemData.FindProperty("_containerCapacityGmu").longValue*2/3;
            }
            seedData.ApplyModifiedPropertiesWithoutUndo();AssetDatabase.SaveAssetIfDirty(seeds);
            var shop=AssetDatabase.LoadAssetAtPath<ShopDefinition>(dataRoot+"World/Hub/Trading/Shop.asset");
            foreach(var offer in shop.Offers)if(offer.IsMaterial && offer.Stock==100)offer.Stock=60000;
            EditorUtility.SetDirty(shop);AssetDatabase.SaveAssetIfDirty(shop);
            foreach (string guid in AssetDatabase.FindAssets("t:GameplayGuideDefinition", new[] { dataRoot }))
            {
                var guide = AssetDatabase.LoadAssetAtPath<GameplayGuideDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                for (int i = 0; i < guide.Pages.Length; i++)
                {
                    var page = guide.Pages[i];
                    page.Body = page.Body.Replace("E采集", "F采集").Replace("通过出口进入Boss地图", "通过出口返回据点P7入口外；Boss练习从据点P8入口进入");
                    guide.Pages[i] = page;
                }
                EditorUtility.SetDirty(guide); AssetDatabase.SaveAssetIfDirty(guide);
            }
            CreatePortalVisual();
            const string returnPath = "Assets/_Project/Prefabs/SinglePlayer/HubPracticeReturn.prefab";
            var returnRoot = PrefabUtility.LoadPrefabContents(returnPath);
            try { InstallPortalVisual(returnRoot, false); PrefabUtility.SaveAsPrefabAsset(returnRoot, returnPath); }
            finally { PrefabUtility.UnloadPrefabContents(returnRoot); }
            string review = "Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_UnifiedItemsReview.unity";
            if (SceneManager.GetSceneByPath(review).isLoaded) throw new InvalidOperationException("Review已打开");
            if (!File.Exists(review)) AssetDatabase.CopyAsset(Hub, review);
            else { File.Copy(Hub, review, true); AssetDatabase.ImportAsset(review, ImportAssetOptions.ForceUpdate); }
            var original = SceneManager.GetActiveScene(); var scene = EditorSceneManager.OpenScene(review, OpenSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene); ConfigureScene(scene, false);
                var prefab = SinglePlayerItemCarrySetup.Generate(scene);
                foreach (var root in scene.GetRootGameObjects())
                { var session = root.GetComponentInChildren<SinglePlayerInventorySession>(true); if (session != null) Set(session, "_practiceWorkspacePrefab", prefab); }
                EditorSceneManager.SaveScene(scene);
            }
            finally { SceneManager.SetActiveScene(original); EditorSceneManager.CloseScene(scene, true); }
            Write("prepared.txt", "Capacities=15000,30000,60000\nNewProfileContents=2/3 capacity; MaterialStock=60000\nThrow=24m/s +6m/s lift,80m limit\nWandIcons=" + icons + "\nPracticePrefab refreshed from author HUD\nFinalScene=" + SceneManager.GetActiveScene().path + "\nDirty=" + SceneManager.GetActiveScene().isDirty);
        }
        private static void ConfigureTools(WarehouseWorkspaceView workspace, bool undo)
        {
            var data = new SerializedObject(workspace);
            var controller = data.FindProperty("_controller").objectReferenceValue as SinglePlayerInventoryController;
            if (controller == null || data.FindProperty("_tools").objectReferenceValue != null) return;
            RectTransform carry = null;
            foreach (var rect in controller.GetComponentsInChildren<RectTransform>(true))
                if (rect.name == "CarryRegion_携带背包" || rect.name == "CarriedBackpackRegion_携带背包") { carry = rect; break; }
            if (carry == null) throw new InvalidOperationException("缺少当前携带区：" + controller.name);
            var row = new GameObject("EquippedTools_独立道具装备", typeof(RectTransform), typeof(ToolbeltView));
            if (undo) Undo.RegisterCreatedObjectUndo(row, "接入独立装备区");
            row.transform.SetParent(carry, false); var rowRect = (RectTransform)row.transform;
            rowRect.anchorMin = new Vector2(.025f, .01f); rowRect.anchorMax = new Vector2(.7f, .16f); rowRect.offsetMin = rowRect.offsetMax = Vector2.zero;
            var slots = new ToolbeltSlotView[HeldItemSlots.ToolCount];
            for (int i = 0; i < slots.Length; i++)
            {
                var cell = new GameObject("Tool" + HeldItemSlots.KeyLabel(i + 3), typeof(RectTransform), typeof(Image), typeof(ToolbeltSlotView)); cell.transform.SetParent(row.transform, false);
                var rect = (RectTransform)cell.transform; rect.anchorMin = new Vector2((float)i / slots.Length, 0); rect.anchorMax = new Vector2((float)(i + 1) / slots.Length, 1); rect.offsetMin = new Vector2(1, 0); rect.offsetMax = new Vector2(-1, 0);
                cell.GetComponent<Image>().color = new Color(.08f, .15f, .2f, .95f);
                var art = new GameObject("Icon", typeof(RectTransform), typeof(Image)); art.transform.SetParent(cell.transform, false);
                var image = art.GetComponent<Image>(); image.preserveAspect = true; image.raycastTarget = false;
                var artRect = image.rectTransform; artRect.anchorMin = new Vector2(.15f, .3f); artRect.anchorMax = new Vector2(.85f, 1); artRect.offsetMin = artRect.offsetMax = Vector2.zero;
                var label = new GameObject("Value", typeof(RectTransform), typeof(Text)); label.transform.SetParent(cell.transform, false);
                var text = label.GetComponent<Text>(); text.fontSize = 11; text.color = Color.white; text.alignment = TextAnchor.LowerCenter; text.raycastTarget = false;
                var labelRect = text.rectTransform; labelRect.anchorMin = Vector2.zero; labelRect.anchorMax = Vector2.one; labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;
                slots[i] = cell.GetComponent<ToolbeltSlotView>(); slots[i].Configure(i, image, text); slots[i].ConfigureCompact();
            }
            row.GetComponent<ToolbeltView>().Configure(slots);
            if (undo) Undo.RecordObject(workspace, "装备区接线"); Set(workspace, "_tools", row.GetComponent<ToolbeltView>());
            var grid = controller.GetComponentInChildren<InventoryGridView>(true);
            if (grid != null && grid.transform.parent is RectTransform viewport)
            { if (undo) Undo.RecordObject(viewport, "为已有装备留出可见空间"); var minimum = viewport.anchorMin; minimum.y = Mathf.Max(minimum.y, .18f); viewport.anchorMin = minimum; }
        }
        private static void CreatePortalVisual()
        {
            var original = SceneManager.GetActiveScene(); var p7 = SceneManager.GetSceneByPath("Assets/_Project/Scenes/P7_DemoRun.unity"); bool opened = !p7.isLoaded;
            if (opened) p7 = EditorSceneManager.OpenScene("Assets/_Project/Scenes/P7_DemoRun.unity", OpenSceneMode.Additive);
            var preview = EditorSceneManager.NewPreviewScene(); GameObject root = null;
            try
            {
                PortalVfxController source = null;
                foreach (var node in p7.GetRootGameObjects()) { source = node.GetComponentInChildren<PortalVfxController>(true); if (source != null) break; }
                if (source == null) throw new InvalidOperationException("P7缺少PortalVfxController");
                var data = new SerializedObject(source); root = new GameObject("UnifiedPortalVisual"); SceneManager.MoveGameObjectToScene(root, preview);
                root.AddComponent<PortalVisualPlacement>();
                var view = root.AddComponent<PortalVfxController>();
                foreach (string field in new[] { "_openPrefab", "_idlePrefab", "_closePrefab" }) Set(view, field, data.FindProperty(field).objectReferenceValue);
                var anchor = data.FindProperty("_visualRoot").objectReferenceValue as Transform;
                var visual = new GameObject("VisualRoot").transform; visual.SetParent(root.transform, false);
                if (anchor != null)
                { visual.localRotation = Quaternion.Inverse(source.transform.rotation) * anchor.rotation; visual.localPosition = source.transform.InverseTransformPoint(anchor.position); visual.localScale = anchor.localScale; }
                Set(view, "_visualRoot", visual);
                // Idle作为真实Prefab子项留在Editor；运行时复用它，避免另生一套圆环占位。
                var idlePrefab = data.FindProperty("_idlePrefab").objectReferenceValue as GameObject;
                var idle = (GameObject)PrefabUtility.InstantiatePrefab(idlePrefab, preview); idle.transform.SetParent(visual, false);
                Set(view, "_idleVisual", idle);
                ItemIconBaker.EnsureFolder("Assets/_Project/Resources/SinglePlayer");
                PrefabUtility.SaveAsPrefabAsset(root, "Assets/_Project/Resources/SinglePlayer/UnifiedPortalVisual.prefab");
            }
            finally
            { if (root != null) Object.DestroyImmediate(root); EditorSceneManager.ClosePreviewScene(preview); SceneManager.SetActiveScene(original); if (opened) EditorSceneManager.CloseScene(p7, true); }
        }
        [MenuItem("Tools/Norn/物品/应用G瞄准与F门（Undo，不保存）")]
        private static void ApplyCurrent()
        {
            var scene = SceneManager.GetActiveScene(); if (scene.path != Hub) throw new InvalidOperationException("请单独打开据点");
            Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("M6-T10 G瞄准与F门修正");
            ConfigureScene(scene, true); Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(group); EditorSceneManager.MarkSceneDirty(scene);
            Write("applied.txt", "正式Scene已接线，保留Undo/Dirty，由作者保存。SceneCount=" + SceneManager.sceneCount);
        }
        internal static void InstallPortalVisual(GameObject portal, bool undo)
        {
            Transform existing = portal.transform.Find("UnifiedPortalVisual");
            // 只清理本门里已替代的渲染/旧标签；权威Collider、路由和吸入锚点不随美术删除。
            foreach (var label in portal.GetComponentsInChildren<TextMesh>(true))
                if (existing == null || !label.transform.IsChildOf(existing)) Remove(label.gameObject, undo);
            foreach (var renderer in portal.GetComponentsInChildren<Renderer>(true))
            {
                if (existing != null && renderer.transform.IsChildOf(existing)) continue;
                var filter = renderer.GetComponent<MeshFilter>(); if (filter != null) Remove(filter, undo);
                Remove(renderer, undo);
            }
            foreach (var old in portal.GetComponentsInChildren<PortalVfxController>(true))
                if (existing == null || !old.transform.IsChildOf(existing)) Remove(old, undo);
            foreach (var child in portal.GetComponentsInChildren<Transform>(true))
                if (child != portal.transform && child != existing && child.childCount == 0 && child.GetComponents<Component>().Length == 1
                    && (child.name == "传送门占位环" || child.name == "Portal ring" || child.name == "VisualRoot")) Remove(child.gameObject, undo);
            if (existing == null)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Resources/SinglePlayer/UnifiedPortalVisual.prefab");
                if (prefab == null) throw new InvalidOperationException("请先准备统一蓝门Prefab");
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, portal.scene); existing = go.transform; existing.SetParent(portal.transform, false);
                if (undo) Undo.RegisterCreatedObjectUndo(go, "替换正式旧门");
            }
            if (undo) Undo.RecordObject(existing, "蓝门脚底与世界尺度"); existing.GetComponent<PortalVisualPlacement>().Place(portal.transform);
            foreach (var particle in existing.GetComponentsInChildren<ParticleSystem>(true))
                if (particle.transform.parent.GetComponent<ParticleSystem>() == null) particle.Simulate(.8f, true, true, false);
        }
        private static void Remove(Object target, bool undo) { if (undo) Undo.DestroyObjectImmediate(target); else Object.DestroyImmediate(target); }
        private static string PathOf(Transform node)
        { string path = node.name; while (node.parent != null) { node = node.parent; path = node.name + "/" + path; } return path; }
        private static void Inspect()
        {
            var output = new System.Text.StringBuilder();
            output.AppendLine("Scene=" + SceneManager.GetActiveScene().path + " Dirty=" + SceneManager.GetActiveScene().isDirty);
            output.AppendLine("SceneCount=" + SceneManager.sceneCount + " Playing=" + EditorApplication.isPlayingOrWillChangePlaymode + " Compiling=" + EditorApplication.isCompiling + " CompilationFailed=" + EditorUtility.scriptCompilationFailed);
            int missing = 0;
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                foreach (var node in root.GetComponentsInChildren<Transform>(true)) missing += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(node.gameObject);
                foreach (var portal in root.GetComponentsInChildren<HubPracticePortal>(true)) InspectPortal(portal.transform, output);
                foreach (var portal in root.GetComponentsInChildren<HubExpeditionPortal>(true)) InspectPortal(portal.transform, output);
                foreach (var view in root.GetComponentsInChildren<ToolbeltView>(true)) output.AppendLine("Tools=" + PathOf(view.transform) + " Active=" + view.gameObject.activeInHierarchy);
                foreach (var view in root.GetComponentsInChildren<WarehouseWorkspaceView>(true))
                {
                    var data = new SerializedObject(view); var icon = data.FindProperty("_bagIcon").objectReferenceValue as Image;
                    output.AppendLine("Workspace=" + PathOf(view.transform) + " BagIcon=" + (icon != null ? PathOf(icon.transform) : "null"));
                }
            }
            output.AppendLine("MissingScripts=" + missing);
            Write("inspection.txt", output.ToString());
        }
        private static void InspectPortal(Transform portal, System.Text.StringBuilder output)
        {
            var visual = portal.Find("UnifiedPortalVisual"); int legacy = 0;
            foreach (var renderer in portal.GetComponentsInChildren<MeshRenderer>(true)) if (visual == null || !renderer.transform.IsChildOf(visual)) legacy++;
            output.AppendLine("Portal=" + PathOf(portal) + " Shared=" + (visual != null) + " LegacyMeshRenderers=" + legacy
                + " TextMesh=" + portal.GetComponentsInChildren<TextMesh>(true).Length + " Placements=" + portal.GetComponentsInChildren<PortalVisualPlacement>(true).Length
                + " VisualCenter=" + (visual != null ? visual.position.ToString() : "null") + " Root=" + portal.position);
        }
    }
}
