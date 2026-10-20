#if UNITY_EDITOR
using System.Collections;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;

namespace Game.Run.Tests
{
    /// <summary>独立Review和内存资产验证玩家的整理意图，不污染用户存档。</summary>
    public sealed class ItemArrangementGlowPlayModeTests : BackpackWorkspacePlayModeTests
    {
        protected override string ReviewPath => Review;
        protected override string EvidencePath => "TestResults/SinglePlayer/M6-T1-ArrangementGlow";
        private ItemCatalog _catalog;
        private ItemDefinition _definition;
        protected override ItemCatalog CustomizeCatalog(ItemCatalog original)
        {
            if(!TestContext.CurrentContext.Test.Name.StartsWith("DraftRotation") && !TestContext.CurrentContext.Test.Name.StartsWith("GrabAnchor"))return original;
            _catalog=Object.Instantiate(original);
            var source=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Items/Containers/Definitions/Item_Container_Bottle.asset");
            _definition=Object.Instantiate(source);var definition=new SerializedObject(_definition);
            definition.FindProperty("_shapeWidth").intValue=2;definition.FindProperty("_shapeHeight").intValue=3;
            var cells=definition.FindProperty("_occupiedCells");cells.arraySize=6;for(int i=0;i<6;i++)cells.GetArrayElementAtIndex(i).vector2IntValue=new Vector2Int(i%2,i/2);
            definition.ApplyModifiedPropertiesWithoutUndo();var catalog=new SerializedObject(_catalog);var items=catalog.FindProperty("_definitions");
            for(int i=0;i<items.arraySize;i++)if(items.GetArrayElementAtIndex(i).objectReferenceValue==source)items.GetArrayElementAtIndex(i).objectReferenceValue=_definition;
            catalog.ApplyModifiedPropertiesWithoutUndo();return _catalog;
        }
        [UnityTearDown] public IEnumerator ReleaseDefinitions(){if(_catalog!=null)Object.Destroy(_catalog);if(_definition!=null)Object.Destroy(_definition);yield return null;}
        private InventoryGridView BagGrid()=>(InventoryGridView)new SerializedObject(Backpack).FindProperty("_gridView").objectReferenceValue;
        private NearbyItemGridView GroundGrid()=>(NearbyItemGridView)new SerializedObject(Backpack).FindProperty("_nearbyGrid").objectReferenceValue;
        private InventoryItemView View(string id, ItemLocationKind kind)
        {
            foreach(var view in Backpack.GetComponentsInChildren<InventoryItemView>())
                if(view.Snapshot.InstanceId==id && view.Snapshot.Location.Kind==kind)return view;
            Assert.Fail("缺少物品投影："+id);return null;
        }
        private InventoryItemView Ghost()=>(InventoryItemView)new SerializedObject(Backpack).FindProperty("_ghost").objectReferenceValue;
        private static Vector2 ScreenPoint(InventoryItemView view,Vector2 cellPoint)
        {
            float cell=view.Rect.rect.width/view.Snapshot.Rules.Shape.GetWidth(view.DisplayTurns);
            return RectTransformUtility.WorldToScreenPoint(null,view.Rect.TransformPoint(new Vector3(view.Rect.rect.xMin+cellPoint.x*cell,view.Rect.rect.yMax-cellPoint.y*cell)));
        }
        private static float ScreenWidth(InventoryItemView view)
        {
            var rect=view.Rect;
            return Vector2.Distance(RectTransformUtility.WorldToScreenPoint(null,rect.TransformPoint(new Vector3(rect.rect.xMin,0))),
                RectTransformUtility.WorldToScreenPoint(null,rect.TransformPoint(new Vector3(rect.rect.xMax,0))));
        }
        private void AssertGrabPoint(Vector2 grab,Vector2 pointer)
        {Assert.That(Vector2.Distance(ScreenPoint(Ghost(),grab),pointer),Is.LessThan(.35f),"Ghost必须保持按下时格子内的精确抓取点。");}

        protected override SinglePlayerProfile CustomizeProfile(SinglePlayerProfile profile,ItemCatalogIndex index)
        {
            if(!TestContext.CurrentContext.Test.Name.StartsWith("GrabAnchorFootprint"))return profile;
            profile.Items.TryMove("b0",ItemLocation.Warehouse);profile.Items.TryMove("b1",ItemLocation.Warehouse);
            var pumpkin=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Items/Food/Item_Food_Pumpkin.asset");
            Assert.That(profile.Items.TryAdd(new RuntimeItemState("footprint",pumpkin.ContentId,0),new GridPlacement(0,0,0).InBackpack("bag")),Is.True);
            return profile;
        }
        [UnityTest] public IEnumerator GrabAnchorFootprintMovesBeforeMouseEntersNextCell()
        {
            Backpack.OpenBackpack();yield return null;Canvas.ForceUpdateCanvases();
            foreach(float fraction in new[]{.1f,.5f,.9f})
            {
                Assert.That(Session.Profile.Items.TryMove("footprint",new GridPlacement(0,0,0).InBackpack("bag")).Success,Is.True);
                Backpack.RefreshPresentation();Canvas.ForceUpdateCanvases();var view=View("footprint",ItemLocationKind.Backpack);
                float cell=ScreenWidth(view)/view.Snapshot.Rules.Shape.GetWidth(0);var grab=new Vector2(fraction,fraction);
                var data=new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left,position=ScreenPoint(view,grab)};
                view.OnPointerDown(data);view.OnBeginDrag(data);data.position+=Vector2.down*(cell*.65f);view.OnDrag(data);
                AssertGrabPoint(grab,data.position);
                ExecuteEvents.Execute(BagGrid().gameObject,data,ExecuteEvents.dropHandler);view.OnEndDrag(data);yield return null;
                Session.ReadOnly.TryGetView("footprint",out var placed);
                Assert.That(placed.Location.Y,Is.EqualTo(1),"移动轮廓超过半格就吸附下一行，不能等鼠标跨格；抓取比例="+fraction);
            }
        }

        [UnityTest] public IEnumerator GrabAnchorPreservesPressPointAcrossThresholdMovementScaleAndRotation()
        {
            Backpack.OpenBackpack();yield return null;Canvas.ForceUpdateCanvases();
            foreach(float scale in new[]{1f,.73f})
            {
                var view=View("b0",ItemLocationKind.Backpack);view.Rect.localScale=Vector3.one*scale;
                var grab=new Vector2(.24f,1.63f);float sourceWidth=ScreenWidth(view);uint version=Session.ReadOnly.Version;
                var data=new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left,position=ScreenPoint(view,grab)};
                view.OnPointerDown(data);data.position+=new Vector2(9,-6);view.OnBeginDrag(data);
                AssertGrabPoint(grab,data.position);Assert.That(ScreenWidth(Ghost()),Is.EqualTo(sourceWidth).Within(.35f));
                data.position+=new Vector2(137,-74);view.OnDrag(data);AssertGrabPoint(grab,data.position);
                for(int i=0;i<4;i++)
                {
                    int height=Ghost().Snapshot.Rules.Shape.GetHeight(Ghost().DisplayTurns);
                    grab=new Vector2(height-grab.y,grab.x);Backpack.RotateDrag();AssertGrabPoint(grab,data.position);
                }
                Assert.That(Session.ReadOnly.Version,Is.EqualTo(version));Backpack.CancelDrag();
            }
        }
        [UnityTest] public IEnumerator GrabAnchorNearbyUsesSourceSizeAndKeepsFractionalContact()
        {
            var dataGrid=new SerializedObject(GroundGrid());dataGrid.FindProperty("_cellSize").floatValue=BagGrid().CellSize*1.7f;dataGrid.ApplyModifiedPropertiesWithoutUndo();
            Backpack.OpenBackpack();yield return null;
            Session.ReadOnly.TryGetView("b0",out var source);Backpack.BeginDrag(source,Vector2.zero,null,false);Backpack.SubmitToTarget(InventoryDropKind.Ground);
            for(int i=0;i<5;i++)yield return null;Canvas.ForceUpdateCanvases();
            var view=View("b0",ItemLocationKind.WorldItem);float sourceWidth=ScreenWidth(view);var grab=new Vector2(1.31f,.72f);
            var data=new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left,position=ScreenPoint(view,grab)};
            view.OnPointerDown(data);data.position+=new Vector2(8,4);view.OnBeginDrag(data);
            AssertGrabPoint(grab,data.position);Assert.That(ScreenWidth(Ghost()),Is.EqualTo(sourceWidth).Within(.35f));
            data.position+=new Vector2(-72,53);view.OnDrag(data);AssertGrabPoint(grab,data.position);Backpack.CancelDrag();
        }
        [UnityTest] public IEnumerator GrabAnchorRotationKeepsContactAndDropsUsingRotatedGrabCell()
        {
            Backpack.OpenBackpack();yield return null;var view=View("b0",ItemLocationKind.Backpack);var grab=new Vector2(1.24f,2.6f);
            var data=new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left,position=ScreenPoint(view,grab)};
            view.OnPointerDown(data);view.OnBeginDrag(data);Backpack.RotateDrag();AssertGrabPoint(new Vector2(.4f,1.24f),data.position);
            BagGrid().GetComponentInParent<UnityEngine.UI.ScrollRect>().normalizedPosition=new Vector2(1,0);Canvas.ForceUpdateCanvases();
            var grid=(RectTransform)BagGrid().transform;
            data.position=RectTransformUtility.WorldToScreenPoint(null,grid.TransformPoint(new Vector3(
                grid.rect.xMin+(4+.4f)*BagGrid().CellSize,grid.rect.yMax-(5+1.24f)*BagGrid().CellSize)));
            view.OnDrag(data);BagGrid().OnDrop(data);view.OnEndDrag(data);
            yield return null;Session.ReadOnly.TryGetView("b0",out var placed);
            Assert.That(placed.Location.X,Is.EqualTo(4));Assert.That(placed.Location.Y,Is.EqualTo(5));Assert.That(placed.Location.QuarterTurns,Is.EqualTo(1));
        }
        [UnityTest] public IEnumerator OverlappedDragKeepsCandidateAndSelectionChangeRollsBack()
        {
            Backpack.OpenBackpack();yield return null;
            var grid=(InventoryGridView)new SerializedObject(Backpack).FindProperty("_gridView").objectReferenceValue;
            Session.ReadOnly.TryGetView("fire",out var blocker);Backpack.BeginDrag(blocker,Vector2.zero,null,false);Backpack.SubmitToBackpack(4,4,"bag");yield return null;
            Session.ReadOnly.TryGetView("b0",out var source);Session.ReadOnly.TryGetView("fire",out var target);
            Backpack.SelectItem("b0");Backpack.BeginDrag(source,Vector2.zero,null,false);
            Backpack.SubmitToBackpack(target.Location.X,target.Location.Y,"bag");yield return null;
            // 不相容容器不能合叠：资产不动，但UI保留红框候选，方便继续整理。
            Session.ReadOnly.TryGetView("b0",out var unchanged);Assert.That(unchanged.Location,Is.EqualTo(source.Location));
            Assert.That(View("b0",ItemLocationKind.Backpack).Rect.anchoredPosition,
                Is.EqualTo(new Vector2(target.Location.X*grid.CellSize,-target.Location.Y*grid.CellSize)));
            var color=View("b0",ItemLocationKind.Backpack).GetComponentInChildren<InventoryShapeOutlineGraphic>().color;
            Assert.That(color.r,Is.GreaterThan(.8f));Assert.That(color.g,Is.LessThan(.4f));
            Backpack.SelectItem("fire");yield return null;
            Assert.That(View("b0",ItemLocationKind.Backpack).Rect.anchoredPosition,
                Is.EqualTo(new Vector2(source.Location.X*grid.CellSize,-source.Location.Y*grid.CellSize)));
        }
        [UnityTest] public IEnumerator NearbyItemCanRotateWithoutChangingWorldAsset()
        {
            Backpack.OpenBackpack();yield return null;Backpack.DropSelected();
            Session.ReadOnly.TryGetView("b0",out var source);Backpack.BeginDrag(source,Vector2.zero,null,false);
            Backpack.SubmitToTarget(InventoryDropKind.Ground);yield return null;yield return null;
            Session.ReadOnly.TryGetView("b0",out var ground);Assert.That(ground.Location.Kind,Is.EqualTo(ItemLocationKind.WorldItem));
            Backpack.SelectItem("b0");Backpack.ClickItem("b0");yield return null;
            Assert.That(View("b0",ItemLocationKind.WorldItem).DisplayTurns,Is.EqualTo(1));
            Backpack.Close();Backpack.OpenBackpack();yield return null;
            Assert.That(View("b0",ItemLocationKind.WorldItem).DisplayTurns,Is.EqualTo(1));
            Session.ReadOnly.TryGetView("b0",out var retained);Assert.That(retained.Location,Is.EqualTo(ground.Location));
        }
        [UnityTest] public IEnumerator DraftRotationKeepsRedPoseThenLegalMoveCommits()
        {
            Backpack.OpenBackpack();yield return null;Session.ReadOnly.TryGetView("b0",out var source);
            Backpack.BeginDrag(source,Vector2.zero,null,false);Backpack.SubmitToBackpack(1,0,"bag");yield return null;
            Backpack.ClickItem("b0");yield return null;Session.ReadOnly.TryGetView("b0",out var real);
            Assert.That(real.Location.QuarterTurns,Is.Zero);Assert.That(View("b0",ItemLocationKind.Backpack).DisplayTurns,Is.EqualTo(1));Assert.That(Backpack.IsArrangementInvalid("b0"),Is.True);
            yield return new WaitForEndOfFrame();Capture("red-overlap-rotation.png");
            Backpack.BeginDrag(real,Vector2.zero,null,false,default,1);Backpack.SubmitToBackpack(0,5,"bag");yield return null;
            Session.ReadOnly.TryGetView("b0",out var committed);Assert.That(committed.Location.X,Is.Zero);Assert.That(committed.Location.Y,Is.EqualTo(5));Assert.That(committed.Location.QuarterTurns,Is.EqualTo(1));Assert.That(Backpack.IsArrangementInvalid("b0"),Is.False);
            yield return new WaitForEndOfFrame();Capture("legal-rotation-committed.png");
        }
        [UnityTest] public IEnumerator DraftRotationCloseCancelsConsecutiveInvalidPoses()
        {
            Backpack.OpenBackpack();yield return null;Session.ReadOnly.TryGetView("b0",out var source);
            Backpack.BeginDrag(source,Vector2.zero,null,false);Backpack.SubmitToBackpack(6,0,"bag");yield return null;
            Backpack.ClickItem("b0");yield return null;Assert.That(Backpack.IsArrangementInvalid("b0"),Is.True);
            Backpack.Close();Backpack.OpenBackpack();yield return null;
            Assert.That(View("b0",ItemLocationKind.Backpack).DisplayTurns,Is.Zero);Assert.That(Backpack.IsArrangementInvalid("b0"),Is.False);
        }
        [UnityTest] public IEnumerator NearbyReleaseUsesPointerCellAndRefreshKeepsPosition()
        {
            Backpack.OpenBackpack();yield return null;Canvas.ForceUpdateCanvases();Session.ReadOnly.TryGetView("b0",out var source);
            Backpack.BeginDrag(source,Vector2.zero,null,false);var grid=GroundGrid();Canvas.ForceUpdateCanvases();
            Vector2 pointer=RectTransformUtility.WorldToScreenPoint(null,grid.transform.TransformPoint(new Vector3(2*64+8,-3*64-8,0)));
            Backpack.SubmitToNearby(pointer,null);yield return null;yield return null;
            Assert.That(View("b0",ItemLocationKind.WorldItem).Rect.anchoredPosition,Is.EqualTo(new Vector2(128,-192)));
            Backpack.RefreshPresentation();Backpack.Close();Backpack.OpenBackpack();yield return null;
            Assert.That(View("b0",ItemLocationKind.WorldItem).Rect.anchoredPosition,Is.EqualTo(new Vector2(128,-192)));
            Session.ReadOnly.TryGetView("b0",out var world);uint version=Session.ReadOnly.Version;
            Backpack.BeginDrag(world,Vector2.zero,null,false);
            grid.GetComponentInParent<UnityEngine.UI.ScrollRect>().normalizedPosition=new Vector2(1,0);Canvas.ForceUpdateCanvases();
            pointer=RectTransformUtility.WorldToScreenPoint(null,grid.transform.TransformPoint(new Vector3(4*64+8,-5*64-8,0)));Backpack.SubmitToNearby(pointer,null);yield return null;
            Assert.That(View("b0",ItemLocationKind.WorldItem).Rect.anchoredPosition,Is.EqualTo(new Vector2(256,-320)));Assert.That(Session.ReadOnly.Version,Is.EqualTo(version));
        }
        [UnityTest] public IEnumerator PendingPickupShowsOnlyBackpackCandidateThenCancelRestoresGround()
        {
            Backpack.OpenBackpack();yield return null;Session.ReadOnly.TryGetView("b0",out var source);Backpack.BeginDrag(source,Vector2.zero,null,false);Backpack.SubmitToTarget(InventoryDropKind.Ground);yield return null;yield return null;
            Session.ReadOnly.TryGetView("b0",out var world);Session.ReadOnly.TryGetView("fire",out var blocker);Backpack.BeginDrag(world,Vector2.zero,null,false);Backpack.SubmitToBackpack(blocker.Location.X,blocker.Location.Y,"bag");yield return null;
            Assert.That(Backpack.IsPendingPickup("b0"),Is.True);int count=0;
            foreach(var view in Backpack.GetComponentsInChildren<InventoryItemView>())if(view.Snapshot.InstanceId=="b0")count++;
            Assert.That(count,Is.EqualTo(1));Session.ReadOnly.TryGetView("b0",out var retained);Assert.That(retained.Location,Is.EqualTo(world.Location));
            Backpack.SelectItem("fire");yield return null;Assert.That(View("b0",ItemLocationKind.WorldItem),Is.Not.Null);Assert.That(Backpack.IsPendingPickup("b0"),Is.False);
        }
        [UnityTest] public IEnumerator StableGlowRecordsScopedProfilerAndBrightFarProof()
        {
            Backpack.Close();Player.enabled=false;
            foreach(var root in Scene.GetRootGameObjects())foreach(var camera in root.GetComponentsInChildren<Camera>(true))camera.enabled=false;
            foreach(var root in Scene.GetRootGameObjects())foreach(var item in root.GetComponentsInChildren<WorldItemPickup>(true))item.gameObject.SetActive(false);
            var definition=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Items/Food/Item_Food_Apple.asset");
            Assert.That(Session.TryRegisterWorldItem(definition,Session.WorldSceneKey,"arrangement.glow",Player.transform.position+Vector3.right,Quaternion.identity),Is.True);yield return null;
            WorldItemPickup proxy=null;foreach(var root in Scene.GetRootGameObjects())foreach(var item in root.GetComponentsInChildren<WorldItemPickup>())proxy=item;
            Assert.That(proxy,Is.Not.Null);foreach(var node in proxy.GetComponentsInChildren<Transform>())node.gameObject.layer=30;
            var cameraObject=new GameObject("Glow daylight proof",typeof(Camera));UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cameraObject,Scene);
            var proof=cameraObject.GetComponent<Camera>();proof.cullingMask=1<<30;proof.backgroundColor=new Color(.45f,.55f,.65f);proof.clearFlags=CameraClearFlags.SolidColor;
            proof.orthographic=true;proof.allowHDR=false;proof.allowMSAA=false;proof.nearClipPlane=.01f;
            var bounds=proxy.GetComponentInChildren<Renderer>().bounds;proof.orthographicSize=bounds.size.magnitude*1.1f;
            proof.transform.position=bounds.center+new Vector3(.8f,.5f,-1).normalized*3;proof.transform.LookAt(bounds.center);Time.timeScale=0;
            bool recording=UnityEditorInternal.ProfilerDriver.enabled,cpu=UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            try
            {
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,true);UnityEditorInternal.ProfilerDriver.enabled=true;
                for(int i=0;i<20;i++)yield return null;int first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;
                for(int i=0;i<60;i++)yield return null;int last=UnityEditorInternal.ProfilerDriver.lastFrameIndex;
                WriteProfile("glow-profiler",first,last,new[]{"Norn.ItemGlow.Frustum","Norn.ItemGlow.RecordCapture","Norn.ItemGlow.RecordCompose"},"Visible isolated apple, 1920x1080 game view, 20 warmup frames excluded; RenderGraph API cost included in Record scopes");
                yield return new WaitForEndOfFrame();Capture("glow-bright-far.png");
            }
            finally{UnityEditorInternal.ProfilerDriver.enabled=recording;UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,cpu);}
        }
        [UnityTest] public IEnumerator NearbyOverlapRollsBackDisplayWithoutReversingCompletedDrop()
        {
            Backpack.OpenBackpack();yield return null;
            foreach(var id in new[]{"b0","b1"}){Session.ReadOnly.TryGetView(id,out var item);Backpack.BeginDrag(item,Vector2.zero,null,false);Backpack.SubmitToTarget(InventoryDropKind.Ground);yield return null;yield return null;}
            var grid=GroundGrid();grid.TryPlacement("b0",out var origin);grid.TryPlacement("b1",out var target);
            Backpack.SelectItem("b0");Session.ReadOnly.TryGetView("b0",out var ground);Backpack.BeginDrag(ground,Vector2.zero,null,false);Canvas.ForceUpdateCanvases();
            var pointer=RectTransformUtility.WorldToScreenPoint(null,grid.transform.TransformPoint(new Vector3(target.X*64+8,-target.Y*64-8,0)));
            Backpack.SubmitToNearby(pointer,null);yield return null;Assert.That(grid.IsInvalid("b0"),Is.True);
            Backpack.SelectItem("b1");yield return null;grid.TryPlacement("b0",out var restored);Assert.That(restored.X,Is.EqualTo(origin.X));Assert.That(restored.Y,Is.EqualTo(origin.Y));
            Session.ReadOnly.TryGetView("b0",out var retained);Assert.That(retained.Location,Is.EqualTo(ground.Location));
        }
        [UnityTest] public IEnumerator DraftSaveAndFocusLossRestoreLastLegalLocation()
        {
            Backpack.OpenBackpack();yield return null;Session.ReadOnly.TryGetView("b0",out var source);Session.ReadOnly.TryGetView("fire",out var blocker);
            Backpack.BeginDrag(source,Vector2.zero,null,false);Backpack.SubmitToBackpack(blocker.Location.X,blocker.Location.Y,"bag");yield return null;
            Assert.That(Backpack.IsArrangementInvalid("b0"),Is.True);Backpack.Save();yield return null;Assert.That(Backpack.IsArrangementInvalid("b0"),Is.False);
            Backpack.BeginDrag(source,Vector2.zero,null,false);Backpack.SubmitToBackpack(-1,0,"bag");yield return null;Assert.That(Backpack.IsArrangementInvalid("b0"),Is.True);
            Backpack.SendMessage("OnApplicationFocus",false);yield return null;Assert.That(Backpack.IsArrangementInvalid("b0"),Is.False);
            Session.ReadOnly.TryGetView("b0",out var retained);Assert.That(retained.Location,Is.EqualTo(source.Location));Backpack.SendMessage("OnApplicationFocus",true);
        }
        [UnityTest] public IEnumerator VisibleNearbyDropStaysAtReleaseScreenCell()
        {
            Backpack.OpenBackpack();yield return null;Session.ReadOnly.TryGetView("b0",out var source);Backpack.BeginDrag(source,Vector2.zero,null,false);Canvas.ForceUpdateCanvases();
            var grid=GroundGrid();var viewport=(RectTransform)grid.transform.parent;
            Vector2 pointer=RectTransformUtility.WorldToScreenPoint(null,viewport.TransformPoint(viewport.rect.center));Assert.That(grid.TryGetCell(pointer,null,out var cell),Is.True);
            Vector2 expected=RectTransformUtility.WorldToScreenPoint(null,grid.transform.TransformPoint(new Vector3(cell.x*64,-cell.y*64,0)));
            Backpack.SubmitToNearby(pointer,null);yield return null;yield return null;Canvas.ForceUpdateCanvases();
            Vector2 actual=RectTransformUtility.WorldToScreenPoint(null,View("b0",ItemLocationKind.WorldItem).Rect.position);
            Assert.That(Vector2.Distance(actual,expected),Is.LessThan(1),"区域内释放不应把新物品滚到左上");
        }
        [UnityTest] public IEnumerator NearbyExpansionPreservesCurrentPixelScrollOffset()
        {
            Backpack.OpenBackpack();yield return null;var grid=GroundGrid();
            grid.MoveItem("layout.padding",new InventoryShape(1,1,new[]{Vector2Int.zero}),new GridPlacement(9,12,0));Backpack.RefreshPresentation();Canvas.ForceUpdateCanvases();
            var scroll=grid.GetComponentInParent<UnityEngine.UI.ScrollRect>();scroll.normalizedPosition=new Vector2(.4f,.4f);yield return null;Canvas.ForceUpdateCanvases();
            var content=(RectTransform)grid.transform;Vector2 before=content.anchoredPosition;
            Session.ReadOnly.TryGetView("b0",out var source);Backpack.BeginDrag(source,Vector2.zero,null,false);Canvas.ForceUpdateCanvases();
            Assert.That(Vector2.Distance(content.anchoredPosition,before),Is.LessThan(1),"增加空白格时，不改变当前显示格的屏幕位置");Backpack.CancelDrag();
        }
    }
}
#endif
