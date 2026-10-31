using System;
using System.IO;
using System.Reflection;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.Run.Tests
{
    /// <summary>
    /// 原 Tab 的导出与绑定边界。只检查 inactive Preview/内存 Source，不加载玩家档或创建线上执行工作区；
    /// 真正键盘 Tab/网络权限由独立 PlayMode 路径验证，不能从本组推断可见玩家验收已完成。
    /// </summary>
    public sealed class SharedInventoryPresentationTests
    {
        private const string SourcePath = "Assets/_Project/Prefabs/SinglePlayer/PracticeInventoryWorkspace.prefab";
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject _preview;
        private Scene _area;
        private CombatPresentationHost _host;

        [TearDown] public void Cleanup()
        {
            if (_host != null) Object.DestroyImmediate(_host.gameObject);
            _host = null;
            if (_preview != null) Exporter().GetMethod("ReleasePreview").Invoke(null, new object[] { _preview });
            _preview = null;
            if (_area.IsValid() && _area.isLoaded) EditorSceneManager.ClosePreviewScene(_area);
            LocalActorPresentation.Clear(97531);
        }

        [Test] public void PreviewReusesOriginalTabLayoutAndPersistentInputReferences()
        {
            var exporter = RequireExporter();
            byte[] before = File.ReadAllBytes(SourcePath);
            _preview = (GameObject)exporter.GetMethod("BuildPreview").Invoke(null, null);
            Assert.That(_preview.activeSelf, Is.False, "清理接线前不能触发原 Controller 的输入与 Start 生命周期。");
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(SourcePath).GetComponentInChildren<SinglePlayerInventoryController>(true);
            var clone = _preview.GetComponentInChildren<SinglePlayerInventoryController>(true);
            Assert.That(clone, Is.Not.Null);
            var original = new SerializedObject(source); var copied = new SerializedObject(clone);
            foreach (string field in new[] { "_toggleAction", "_rotateAction", "_cancelAction", "_splitAction", "_itemPrefab" })
                Assert.That(copied.FindProperty(field).objectReferenceValue, Is.EqualTo(original.FindProperty(field).objectReferenceValue),
                    "原 Input Actions/物品美术模板必须共用，不能再定义一套联机操作：" + field);
            var sourcePanel = (GameObject)original.FindProperty("_panelRoot").objectReferenceValue;
            var targetPanel = (GameObject)copied.FindProperty("_panelRoot").objectReferenceValue;
            Assert.That(targetPanel, Is.Not.Null);
            AssertLayouts(sourcePanel.transform, targetPanel.transform);
            Assert.That(File.ReadAllBytes(SourcePath), Is.EqualTo(before), "Preview 导出不得回写原工作区资源。");
        }

        [Test] public void PreviewDropsSinglePlayerAuthorityAndDuplicateHudWithoutBreakingControllerViews()
        {
            var exporter = RequireExporter(); _preview = (GameObject)exporter.GetMethod("BuildPreview").Invoke(null, null);
            var controllers = _preview.GetComponentsInChildren<SinglePlayerInventoryController>(true);
            Assert.That(controllers.Length, Is.EqualTo(1)); var data = new SerializedObject(controllers[0]);
            foreach (string field in new[] { "_session", "_hubAccess", "_pauseCoordinator", "_nearby", "_otherWorkspace" })
                Assert.That(data.FindProperty(field).objectReferenceValue, Is.Null, "线上 Scene UI 不能保留单机写入/暂停所有者：" + field);
            Assert.That(data.FindProperty("_openOnStart").boolValue, Is.False);
            foreach (string field in new[] { "_panelRoot", "_gridView", "_wandsView", "_backpackWorkspace", "_spellStorage", "_nearbyGrid", "_ghost" })
                Assert.That(data.FindProperty(field).objectReferenceValue, Is.Not.Null, "原只读 View 接线不能因去权威而丢失：" + field);
            Assert.That(_preview.GetComponentInChildren<SinglePlayerInventorySession>(true), Is.Null);
            Assert.That(_preview.GetComponentInChildren<PracticeInventoryWorkspace>(true), Is.Null);
            Assert.That(_preview.GetComponentInChildren<PracticeInventoryWorkspaceBinding>(true), Is.Null);
            Assert.That(_preview.GetComponentInChildren<EventSystem>(true), Is.Null);
            Assert.That(_preview.GetComponentInChildren<HeldItemHud>(true), Is.Null, "十格由共同 Host 拥有，Tab 不能再复制一份。");
        }

        [Test] public void SameSourceReplayKeepsOneSceneInventoryAndOpenTabWhileWrongActorIsRejected()
        {
            var method = typeof(CombatPresentationScope).GetMethod("TryBindOnlineInventory", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(method, Is.Not.Null, "Scope 缺少接入验证后库存读源的共同入口。");
            Assert.That(Resources.Load<GameObject>("Presentation/CombatInventory"), Is.Not.Null,
                "必须导出原 Tab Resource 后才可声明 Scene 自动装配，而不是只检查一个方法名。");
            Assert.That(CombatPresentationHost.Active, Is.Null, "测试不能抢占其他区域正在使用的 HUD。");
            float timeScale = Time.timeScale;
            _area = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("OnlineInventoryScope"); SceneManager.MoveGameObjectToScene(root, _area);
            var scope = root.AddComponent<CombatPresentationScope>();
            var setting = new SerializedObject(scope); setting.FindProperty("_online").boolValue = true; setting.ApplyModifiedPropertiesWithoutUndo();
            var actor = new GameObject("CurrentActor"); actor.SetActive(false); SceneManager.MoveGameObjectToScene(actor, _area);
            LocalActorPresentation.Bind(97531, actor, true);
            scope.Bind(new CombatPresentationContext { Actor = actor, WorldScene = _area, CrosshairVisible = true });
            _host = CombatPresentationHost.Active;
            var source = new EmptySource();
            Assert.That((bool)method.Invoke(scope, new object[] { actor, source }), Is.True);
            var inventory = root.GetComponentInChildren<SinglePlayerInventoryController>(true);
            Assert.That(inventory, Is.Not.Null); Assert.That(inventory.gameObject.scene, Is.EqualTo(_area));
            Assert.That(inventory.PresentationSource, Is.SameAs(source)); Assert.That(inventory.Session, Is.Null);
            typeof(SinglePlayerInventoryController).GetField("_open", Private).SetValue(inventory, true);
            Assert.That((bool)method.Invoke(scope, new object[] { actor, source }), Is.True);
            Assert.That(inventory.IsOpen, Is.True, "同一稳定 Source 重放不应调用 BindPresentationSource 关闭 Tab。");
            Assert.That(root.GetComponentsInChildren<SinglePlayerInventoryController>(true).Length, Is.EqualTo(1));
            var wrong = new GameObject("OtherActor"); SceneManager.MoveGameObjectToScene(wrong, _area);
            Assert.That((bool)method.Invoke(scope, new object[] { wrong, new EmptySource() }), Is.False);
            Assert.That(inventory.PresentationSource, Is.SameAs(source));
            scope.Clear();
            Assert.That(_host.Actor, Is.Null); Assert.That(_host.InventorySource, Is.Null);
            Assert.That(inventory == null, Is.True, "Scope 退出应释放 Scene UI，不能把原 Tab Controller 搬进 DDOL。");
            Assert.That(Time.timeScale, Is.EqualTo(timeScale));
        }

        private static Type Exporter()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            { var found = assembly.GetType("Game.Project.Editor.SharedInventoryPresentationTools"); if (found != null) return found; }
            return null;
        }

        [Test] public void ExportKeepsOriginalDragGhostCanvasGroupSoItCannotSwallowDropRaycasts()
        {
            _preview = RequireExporter().GetMethod("BuildPreview").Invoke(null, null) as GameObject;
            var controller = _preview.GetComponentInChildren<SinglePlayerInventoryController>(true);
            var ghost = (Component)typeof(SinglePlayerInventoryController).GetField("_ghost", Private).GetValue(controller);
            Assert.That(ghost, Is.Not.Null);
            var group = ghost.GetComponent<CanvasGroup>();
            Assert.That(group, Is.Not.Null, "CanvasGroup是原Ghost的UGUI输入行为，不是需要剥离的Gameplay权威。");
            Assert.That(group.blocksRaycasts, Is.False, "Ghost不能挡住程序槽/SpellStorage的Drop Raycast。");
            Assert.That(group.interactable, Is.False);
        }
        private static Type RequireExporter()
        {
            var type = Exporter(); Assert.That(type, Is.Not.Null, "缺少原 Tab 的单一 Editor 导出器，不能继续复制联机界面。");
            Assert.That(type.GetMethod("BuildPreview"), Is.Not.Null); Assert.That(type.GetMethod("ReleasePreview"), Is.Not.Null);
            Assert.That(type.GetMethod("ExportResource"), Is.Not.Null); Assert.That(type.GetMethod("VerifyResource"), Is.Not.Null); return type;
        }
        private static void AssertLayouts(Transform source, Transform target)
        {
            Assert.That(target.name, Is.EqualTo(source.name)); Assert.That(target.childCount, Is.EqualTo(source.childCount));
            if (source is RectTransform a && target is RectTransform b)
            {
                Assert.That(b.anchorMin, Is.EqualTo(a.anchorMin)); Assert.That(b.anchorMax, Is.EqualTo(a.anchorMax));
                Assert.That(b.pivot, Is.EqualTo(a.pivot)); Assert.That(b.anchoredPosition, Is.EqualTo(a.anchoredPosition));
                Assert.That(b.sizeDelta, Is.EqualTo(a.sizeDelta));
            }
            for (int i = 0; i < source.childCount; i++) AssertLayouts(source.GetChild(i), target.GetChild(i));
        }
        private sealed class EmptySource : IInventoryPresentationSource
        {
            public IInventoryReadOnly ReadOnly => null; public IToolbeltReadOnly ToolbeltReadOnly => null;
            public IWandLoadoutReadOnly WandsReadOnly => null; public ISpellUseReadOnly SpellUsesReadOnly => null;
            public IWalletReadOnly WalletReadOnly => null; public bool UseSpellProgrammingV2 => true;
            public bool CanAccessWarehouse => false; public string WorldSceneKey => "online"; public string EquippedBackpackId => null;
            public int SpellStorageCapacity => 0; public string InitializationMessage => "等待"; public bool HasUnsavedChanges => false;
            public bool TryCaptureWandResearch(string id, out WandResearchSnapshot snapshot) { snapshot = default; return false; }
            public bool TryCopyContainer(string itemId, MaterialAmount[] buffer, out int count, out ulong total, out ulong capacity)
            { count = 0; total = capacity = 0; return false; }
        }
    }
}
