#if UNITY_EDITOR
using System.Collections;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.ElementField;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Game.Run.Tests
{
    /// <summary>用户保存后的区域Review与内存Profile；验证真实输入/投射物及滚动坐标，避免只测试方法是否返回true。</summary>
    public sealed class HubAreaPlayModeTests : BackpackWorkspacePlayModeTests
    {
        protected override string ReviewPath => "Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_AreaReview.unity";
        protected override string EvidencePath => "TestResults/SinglePlayer/M6-T1-Areas";
        private void Move(Vector3 point)
        {
            Player.CharacterController.enabled = false; Player.transform.position = point;
            Player.CharacterController.enabled = true; Physics.SyncTransforms();
        }
        private Transform Platform()
        { foreach (var root in Scene.GetRootGameObjects()) foreach (var node in root.GetComponentsInChildren<Transform>(true)) if (node.name == "实验平台 · 真实库存") return node; Assert.Fail("缺少实际实验平台"); return null; }
        private InventoryGridView BagGrid() => (InventoryGridView)new SerializedObject(Backpack).FindProperty("_gridView").objectReferenceValue;
        private Image Crosshair()
        { foreach (var root in Scene.GetRootGameObjects()) foreach (var image in root.GetComponentsInChildren<Image>(true)) if (image.name == "Crosshair") return image; Assert.Fail("缺少准星"); return null; }

        [UnityTest] public IEnumerator ShopAreaEdgeOpensWithFAndOutsideCannotOpen()
        {
            var hub = Find<HubInteractionController>(); var center = hub.Definition.Stations[1].Position;
            Player.enabled = false; Move(center + new Vector3(3, .4f, 0));
            Assert.That(hub.TryGetStation(out var station),Is.True,"实际区域查询先通过");Assert.That(station.Kind,Is.EqualTo(HubStationKind.Shop));
            int requests=0,results=0;bool success=false;
            void Request(HubInteractionRequestedEvent e){requests++;}
            void Result(HubInteractionResultEvent e){results++;success=e.Success;}
            EventBus<HubInteractionRequestedEvent>.Subscribe(Request);EventBus<HubInteractionResultEvent>.Subscribe(Result);
            try {Keys(Key.F);yield return null;Keys();}
            finally {EventBus<HubInteractionRequestedEvent>.Unsubscribe(Request);EventBus<HubInteractionResultEvent>.Unsubscribe(Result);}
            System.IO.Directory.CreateDirectory(EvidencePath);
            var nav=Find<HubNavigationView>();var input=Find<HubInteractInput>();
            System.IO.File.WriteAllText(EvidencePath+"/e-chain.txt","requests="+requests+" results="+results+" success="+success+" input="+input.isActiveAndEnabled+" navigation="+nav.isActiveAndEnabled+" timeScale="+Time.timeScale+" nav="+JsonUtility.ToJson(nav));
            Assert.That(requests,Is.EqualTo(1));Assert.That(results,Is.EqualTo(1));Assert.That(success,Is.True);
            Assert.That(nav.isActiveAndEnabled,Is.False,"保留用户隐藏提示Canvas，业务交互仍必须工作");
            var shop = Find<ShopController>(); Assert.That(shop.IsOpen, Is.True, "同一商店区域里离原点超过2m仍应开商店");
            shop.Close(); Move(center + new Vector3(20, .4f, 0)); Keys(Key.F); yield return null; Keys();
            Assert.That(shop.IsOpen, Is.False); shop.Open(); Assert.That(shop.IsOpen, Is.False, "区外按钮也不能绕过距离校验");
        }
        [UnityTest] public IEnumerator WarehouseRegionWorksWithoutPromptCanvasAndDisabledShopHasNoPointFallback()
        {
            var hub=Find<HubInteractionController>();Player.enabled=false;
            Move(hub.Definition.Stations[0].Position+new Vector3(3,.4f,2));Keys(Key.F);yield return null;Keys();
            Assert.That(Warehouse.IsOpen,Is.True);Assert.That(Warehouse.WarehouseMode,Is.True);Assert.That(Backpack.IsOpen,Is.False);
            Warehouse.Close();var area=(Behaviour)new SerializedObject(hub).FindProperty("_shopArea").objectReferenceValue;
            area.enabled=false;Move(hub.Definition.Stations[1].Position+Vector3.up*.4f);
            Assert.That(hub.TryGetStation(out _),Is.False,"已停用实际区域，不能退回旧2m点");
            Keys(Key.F);yield return null;Keys();Assert.That(Find<ShopController>().IsOpen,Is.False);
        }
        [UnityTest] public IEnumerator ExperimentBoxControlsPermissionAndSimulationWithoutDeletingWorld()
        {
            Player.enabled=false;var experiment=Find<ExperimentAreaController>();var area=(HubAreaVolume)new SerializedObject(experiment).FindProperty("_area").objectReferenceValue;
            Move(area.Center);yield return null;
            Assert.That(Session.WorldActionsAllowed,Is.True);var world=Find<ElementWorldRuntime>();Assert.That(world.SimulationSuspended,Is.False);
            area.Volume.enabled=false;yield return null;Assert.That(Session.WorldActionsAllowed,Is.False);Assert.That(world.SimulationSuspended,Is.True);
            area.Volume.enabled=true;yield return null;Assert.That(Session.WorldActionsAllowed,Is.True);Assert.That(world.SimulationSuspended,Is.False);
        }
        [UnityTest] public IEnumerator RegionQueriesAndExperimentUpdateHaveNoHotPathAllocation()
        {
            Player.enabled=false;var hub=Find<HubInteractionController>();Move(hub.Definition.Stations[1].Position+new Vector3(3,.4f,0));
            bool recording=UnityEditorInternal.ProfilerDriver.enabled;
            bool cpu=UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            try
            {
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,true);UnityEditorInternal.ProfilerDriver.enabled=true;
                for(int i=0;i<10;i++){hub.TryGetStation(out _);yield return null;}
                int first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;
                for(int i=0;i<60;i++){hub.TryGetStation(out _);yield return null;}
                int last=UnityEditorInternal.ProfilerDriver.lastFrameIndex;
                WriteProfile("regions-profiler",first,last,new[]{"Norn.Hub.RegionQuery","Norn.Hub.ExperimentUpdate"},"Editor TestRunner, 60 stable frames of explicit scene-region query and experiment Update; input/initialization excluded");
            }
            finally {UnityEditorInternal.ProfilerDriver.enabled=recording;UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,cpu);}
        }
        [UnityTest] public IEnumerator ActualResizedPlatformEdgeFiresThroughPlayerInput()
        {
            // Instantiate归属active Scene；测试必须明确当前Review，不能把生成到TestRunner初始Scene的弹误判成没出弹。
            SceneManager.SetActiveScene(Scene);
            Backpack.OpenBackpack(); Session.ReadOnly.TryGetView("fire", out var fire); Backpack.BeginDrag(fire, Vector2.zero, null, true);
            Backpack.SubmitToTarget(InventoryDropKind.ProgramSlot, "w0", 0); Backpack.Close();
            var bounds = Platform().GetComponent<Renderer>().bounds;
            Move(new Vector3(bounds.center.x - 2.8f, bounds.max.y + .05f, bounds.center.z)); yield return null;
            // 等待CharacterController真正落稳，覆盖“长期站台上”的玩家症状，而不只验证瞬移那一帧。
            yield return new WaitForSeconds(1);
            Assert.That(Session.WorldActionsAllowed,Is.True,"站稳后许可丢失：position="+Player.transform.position+" center="+Player.CharacterController.center+" height="+Player.CharacterController.height);
            int before = Projectiles(); Click(true); yield return null; Click(false);
            // 保持原角色动画释放路径，不直接调用SpellCaster代替玩家左键。
            float end = Time.realtimeSinceStartup + 1.5f; bool emitted = false;
            while (Time.realtimeSinceStartup < end) { if (Projectiles() > before) { emitted = true; break; } yield return null; }
            Assert.That(emitted, Is.True, "扩大平台内必须真正生成投射物，不能仅显示一份程序");
            System.IO.Directory.CreateDirectory(EvidencePath);yield return new WaitForEndOfFrame();Capture("experiment-shot.png");
        }
        private int Projectiles()
        { int count = 0; foreach (var root in Scene.GetRootGameObjects()) count += root.GetComponentsInChildren<ProjectileBase>().Length; return count; }
        [UnityTest] public IEnumerator CrosshairVisibleAtStartAndRestoredAfterBackpack()
        {
            var dot = Crosshair(); Assert.That(dot.isActiveAndEnabled, Is.True, "准星不能留在停用的旧HUD");
            var canvas = dot.GetComponentInParent<Canvas>(); Assert.That(canvas.enabled, Is.True);
            Backpack.OpenBackpack(); yield return null; Assert.That(canvas.enabled, Is.False);
            Backpack.Close(); yield return null; yield return null; Assert.That(canvas.enabled, Is.True); Assert.That(dot.gameObject.activeInHierarchy, Is.True);
            Vector2 point = RectTransformUtility.WorldToScreenPoint(null, dot.rectTransform.position);
            Assert.That(point.x, Is.EqualTo(Screen.width * .5f).Within(1)); Assert.That(point.y, Is.EqualTo(Screen.height * .5f).Within(1));
        }
        [UnityTest] public IEnumerator LargeGridScrollbarsReachLastCellAndDropUsesScrolledCoordinates()
        {
            Backpack.OpenBackpack(); yield return null; Canvas.ForceUpdateCanvases();
            var grid = BagGrid(); var scroll = grid.GetComponentInParent<ScrollRect>();
            var workspace=Backpack.GetComponent<BackpackWorkspaceView>();
            var capacity=(Text)new SerializedObject(workspace).FindProperty("_capacity").objectReferenceValue;
            var labelCorners=new Vector3[4];var viewportCorners=new Vector3[4];
            capacity.rectTransform.GetWorldCorners(labelCorners);scroll.viewport.GetWorldCorners(viewportCorners);
            Assert.That(labelCorners[0].y,Is.GreaterThan(viewportCorners[1].y),"容量文字必须独占顶行，不能盖住首排物品");
            Assert.That(grid.CellSize, Is.GreaterThanOrEqualTo(72)); Assert.That(scroll.horizontalScrollbar, Is.Not.Null); Assert.That(scroll.verticalScrollbar, Is.Not.Null);
            Assert.That(grid.GetComponent<RectTransform>().rect.height, Is.GreaterThan(scroll.viewport.rect.height));
            scroll.horizontalNormalizedPosition = 1; scroll.verticalNormalizedPosition = 0;
            yield return null; Canvas.ForceUpdateCanvases();
            var content = (RectTransform)grid.transform; var local = new Vector3(content.rect.xMin + grid.CellSize * 7.5f, content.rect.yMax - grid.CellSize * 7.5f, 0);
            var screen = RectTransformUtility.WorldToScreenPoint(null, content.TransformPoint(local));
            Assert.That(RectTransformUtility.RectangleContainsScreenPoint(scroll.viewport, screen), Is.True);
            Assert.That(grid.TryGetCell(screen, null, out var cell), Is.True); Assert.That(cell, Is.EqualTo(new Vector2Int(7,7)));
            Session.ReadOnly.TryGetView("fire", out var fire); Backpack.BeginDrag(fire, screen, null, true);
            ExecuteEvents.Execute(grid.gameObject, new PointerEventData(EventSystem.current) { position = screen }, ExecuteEvents.dropHandler);
            yield return null; Assert.That(Session.ReadOnly.TryGetView("fire", out var result), Is.True);
            Assert.That(result.Location.Kind, Is.EqualTo(ItemLocationKind.Backpack)); Assert.That(result.Location.X, Is.EqualTo(7)); Assert.That(result.Location.Y, Is.EqualTo(7));
            yield return new WaitForEndOfFrame();Capture("backpack-last-cell.png");
        }
    }
}
#endif
