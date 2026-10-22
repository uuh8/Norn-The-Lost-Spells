#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Game.Run.Tests
{
    /// <summary>使用最新用户布局的独立副本和内存档，验证实际释放/点击对资产的结果。</summary>
    public sealed class ItemPresentationFeedbackPlayModeTests : BackpackWorkspacePlayModeTests
    {
        protected override string ReviewPath => "Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_ItemFeedbackReview.unity";
        protected override string EvidencePath => "TestResults/SinglePlayer/M6-T1-ItemPresentationFeedback";
        [UnitySetUp] public IEnumerator IsolateItems()
        {
            foreach (var root in Scene.GetRootGameObjects())
                foreach (var proxy in root.GetComponentsInChildren<WorldItemPickup>(true)) proxy.gameObject.SetActive(false);
            Backpack.Close(); Warehouse?.Close(); Player.enabled=false; yield return null;
        }
        private InventoryItemView BagView(string id)
        {
            foreach(var view in Backpack.GetComponentsInChildren<InventoryItemView>())
                if(view.Snapshot.InstanceId==id && view.Snapshot.Location.Kind==ItemLocationKind.Backpack) return view;
            Assert.Fail("未找到背包视图："+id); return null;
        }
        private static Vector2 Point(InventoryItemView view) => RectTransformUtility.WorldToScreenPoint(null,view.Rect.TransformPoint(new Vector3(8,-8,0)));
        private readonly List<Object> _assets=new List<Object>();
        private bool _windProof;
        private float[] _oldWind;
        private Vector4 _oldWindDirection;
        private static readonly string[] WindNames={"ASPW_WindToggle","ASPW_WindGrassSpeed","ASPW_WindGrassAmplitude","ASPW_WindGrassFlexibility","ASPW_WindGrassWavesAmplitude","ASPW_WindGrassWavesSpeed","ASPW_WindGrassWavesScale","ASPW_WindGrassTurbulence"};
        private bool ShapeCase=>TestContext.CurrentContext.Test.Name.StartsWith("FullBagLarge") || TestContext.CurrentContext.Test.Name.StartsWith("NonSquareClick") || TestContext.CurrentContext.Test.Name.StartsWith("BorderRotation") || TestContext.CurrentContext.Test.Name.StartsWith("StackClick");
        protected override ItemCatalog CustomizeCatalog(ItemCatalog original)
        {
            if(!ShapeCase)return original;
            int width=TestContext.CurrentContext.Test.Name.StartsWith("FullBagLarge")?4:2,height=width==4?5:3;
            var catalog=Object.Instantiate(original);_assets.Add(catalog);
            var definition=Object.Instantiate(AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Items/Food/Item_Food_Apple.asset"));_assets.Add(definition);
            var data=new SerializedObject(definition);data.FindProperty("_contentId").stringValue="feedback.large";
            data.FindProperty("_maxStack").intValue=2;
            data.FindProperty("_shapeWidth").intValue=width;data.FindProperty("_shapeHeight").intValue=height;
            var cells=data.FindProperty("_occupiedCells");cells.arraySize=width*height;
            for(int i=0;i<cells.arraySize;i++)cells.GetArrayElementAtIndex(i).vector2IntValue=new Vector2Int(i%width,i/width);
            data.ApplyModifiedPropertiesWithoutUndo();
            var catalogData=new SerializedObject(catalog);var definitions=catalogData.FindProperty("_definitions");int last=definitions.arraySize++;
            definitions.GetArrayElementAtIndex(last).objectReferenceValue=definition;catalogData.ApplyModifiedPropertiesWithoutUndo();
            return catalog;
        }
        protected override SinglePlayerProfile CustomizeProfile(SinglePlayerProfile profile,ItemCatalogIndex index)
        {
            if(!ShapeCase)return profile;
            bool fill=TestContext.CurrentContext.Test.Name.StartsWith("FullBagLarge");int width=fill?4:2,height=fill?5:3;
            int x=TestContext.CurrentContext.Test.Name.StartsWith("BorderRotation")?6:0,y=0;
            var store=new ItemOwnershipStore(index);
            var bag=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Items/Backpacks/Item_Backpack_Bag.asset");
            var bottle=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Items/Containers/Definitions/Item_Container_Bottle.asset");
            Assert.That(store.TryAdd(new RuntimeItemState("bag",bag.ContentId,0),new ItemLocation(ItemLocationKind.EquippedBackpack,"player",0,0,0,0)),Is.True);
            Assert.That(store.TryAdd(new RuntimeItemState("large","feedback.large",0),new ItemLocation(ItemLocationKind.Backpack,"bag",x,y,0,-1)),Is.True);
            if(fill)for(int cy=0;cy<8;cy++)for(int cx=0;cx<8;cx++)if(cx<x || cx>=x+width || cy<y || cy>=y+height)
                Assert.That(store.TryAdd(new RuntimeItemState("fill."+cx+"."+cy,bottle.ContentId,0),new ItemLocation(ItemLocationKind.Backpack,"bag",cx,cy,0,-1)),Is.True);
            return new SinglePlayerProfile("player",store);
        }
        private void LargeItem(int width,int height,bool fill=false,int x=0,int y=0,bool blocker=false)
        {
            Assert.That(Session.ReadOnly.TryGetView("large",out var item),Is.True);Assert.That(item.Rules.Shape.Width,Is.EqualTo(width));Assert.That(item.Rules.Shape.Height,Is.EqualTo(height));
            if(blocker)
            {
                Backpack.Close();
                var bottle=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Items/Containers/Definitions/Item_Container_Bottle.asset");
                Assert.That(Session.Profile.Items.TryAdd(new RuntimeItemState("block",bottle.ContentId,0),new ItemLocation(ItemLocationKind.Backpack,"bag",2,0,0,-1)),Is.True);
            }
        }
        [UnityTearDown] public IEnumerator ReleaseAssets()
        {
            if(_oldWind!=null){for(int i=0;i<WindNames.Length;i++)Shader.SetGlobalFloat(WindNames[i],_oldWind[i]);Shader.SetGlobalVector("ASPW_WindDirection",_oldWindDirection);_oldWind=null;}
            _windProof=false;foreach(var asset in _assets)Object.Destroy(asset);_assets.Clear();yield return null;
        }
        private NearbyItemGridView NearbyGrid()=>(NearbyItemGridView)new SerializedObject(Backpack).FindProperty("_nearbyGrid").objectReferenceValue;
        private void ReleaseThroughRaycast(InventoryItemView view,Vector2 destination)
        {
            var data=new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left,position=Point(view),pointerDrag=view.gameObject};
            ExecuteEvents.Execute(view.gameObject,data,ExecuteEvents.beginDragHandler);data.position=destination;
            ExecuteEvents.Execute(view.gameObject,data,ExecuteEvents.dragHandler);
            var hits=new List<RaycastResult>();EventSystem.current.RaycastAll(data,hits);Assert.That(hits.Count,Is.GreaterThan(0));
            var target=ExecuteEvents.GetEventHandler<IDropHandler>(hits[0].gameObject);Assert.That(target,Is.Not.Null,"实际Raycast没有Drop接收者："+hits[0].gameObject.name);
            ExecuteEvents.Execute(target,data,ExecuteEvents.dropHandler);ExecuteEvents.Execute(view.gameObject,data,ExecuteEvents.endDragHandler);
        }
        [UnityTest] public IEnumerator EmptyNearbyGridFillsViewportAndHasSpareRow()
        {
            Backpack.OpenBackpack(); yield return null; Canvas.ForceUpdateCanvases();
            var grid=(NearbyItemGridView)new SerializedObject(Backpack).FindProperty("_nearbyGrid").objectReferenceValue;
            var viewport=(RectTransform)grid.transform.parent;
            Assert.That(((RectTransform)grid.transform).rect.height,Is.GreaterThan(viewport.rect.height));
            Assert.That(grid.GetComponentsInChildren<Image>().Length,Is.GreaterThan(2*grid.Columns));
        }
        [UnityTest] public IEnumerator OutsideReleaseDropsSameInstanceAndPreservesContainerContent()
        {
            Backpack.OpenBackpack(); yield return null; var view=BagView("b0"); ulong total=Bottle("b0");
            var data=new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left,position=Point(view)};
            view.OnBeginDrag(data); Assert.That(Backpack.IsDragging,Is.True);
            data.position=new Vector2(-50,-50); view.OnDrag(data); view.OnEndDrag(data); yield return null;
            Assert.That(Session.ReadOnly.TryGetView("b0",out var item),Is.True);
            Assert.That(item.Location.Kind,Is.EqualTo(ItemLocationKind.WorldItem)); Assert.That(Bottle("b0"),Is.EqualTo(total));
        }
        [UnityTest] public IEnumerator FirstClickSelectsAndFurtherClicksRotateClockwise()
        {
            Backpack.OpenBackpack(); yield return null; var data=new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left};
            uint before=Session.ReadOnly.Version; BagView("b0").OnPointerClick(data);
            Assert.That(Backpack.SelectedItemId,Is.EqualTo("b0")); Assert.That(Session.ReadOnly.Version,Is.EqualTo(before));
            for(int turn=1;turn<=4;turn++)
            {
                BagView("b0").OnPointerClick(data); yield return null;
                Session.ReadOnly.TryGetView("b0",out var item); Assert.That(item.Location.QuarterTurns,Is.EqualTo(turn%4));
                Assert.That(Bottle("b0"),Is.EqualTo(100));
            }
        }
        [UnityTest] public IEnumerator InsideInvalidReleaseAndShortcutOutsideKeepAssets()
        {
            Backpack.OpenBackpack(); yield return null; var view=BagView("b0"); Session.ReadOnly.TryGetView("b0",out var original);
            var data=new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left,position=Point(view)};
            view.OnBeginDrag(data); view.OnEndDrag(data); yield return null;
            Session.ReadOnly.TryGetView("b0",out var retained); Assert.That(retained.Location,Is.EqualTo(original.Location));
            Backpack.BeginShortcutDrag("b0",Vector2.zero,null); data.position=new Vector2(-50,-50); view.OnEndDrag(data);
            Session.ReadOnly.TryGetView("b0",out retained); Assert.That(retained.Location,Is.EqualTo(original.Location));
        }
        [UnityTest] public IEnumerator HighlightKeepsOriginalSurfaceMaterial()
        {
            var definition=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Items/Food/Item_Food_Apple.asset");
            var original=definition.WorldModel.GetComponentInChildren<Renderer>().sharedMaterial; string before=EditorJsonUtility.ToJson(original);
            Assert.That(Session.TryRegisterWorldItem(definition,Session.WorldSceneKey,"feedback.apple",Player.transform.position+Vector3.right,Quaternion.identity),Is.True);
            yield return null;
            foreach(var root in Scene.GetRootGameObjects()) foreach(var proxy in root.GetComponentsInChildren<WorldItemPickup>())
            {
                if(!Session.ReadOnly.TryGetView(proxy.ItemId,out var item) || item.ContentId!=definition.ContentId) continue;
                Assert.That(proxy.GetComponentInChildren<Renderer>().sharedMaterial,Is.SameAs(original));
                Assert.That(EditorJsonUtility.ToJson(original),Is.EqualTo(before)); yield break;
            }
            Assert.Fail("真实苹果代理未生成。");
        }
        [UnityTest] public IEnumerator FullBagLargeItemDropsThroughNearbyBlankAndCanBeRecovered()
        {
            LargeItem(4,5,true);Backpack.OpenBackpack();yield return null;Canvas.ForceUpdateCanvases();
            var grid=NearbyGrid();var viewport=(RectTransform)grid.transform.parent;
            viewport.sizeDelta=new Vector2(200,viewport.sizeDelta.y);Backpack.RefreshPresentation();Canvas.ForceUpdateCanvases();
            Vector2 destination=RectTransformUtility.WorldToScreenPoint(null,viewport.TransformPoint(viewport.rect.center));
            ReleaseThroughRaycast(BagView("large"),destination);yield return null;
            Session.ReadOnly.TryGetView("large",out var dropped);Assert.That(dropped.Location.Kind,Is.EqualTo(ItemLocationKind.WorldItem));
            Assert.That(grid.Rows,Is.GreaterThanOrEqualTo(6));
            InventoryItemView ground=null;foreach(var v in grid.GetComponentsInChildren<InventoryItemView>())if(v.Snapshot.InstanceId=="large")ground=v;
            Assert.That(ground,Is.Not.Null);Assert.That(ground.Rect.rect.width,Is.EqualTo(256));Assert.That(ground.Rect.rect.height,Is.EqualTo(320));
            var scroll=grid.GetComponentInParent<ScrollRect>();var content=(RectTransform)grid.transform;
            // 自由附近区预留空白，Content末端不再等于物品末端；滚到这件物品的右下边而非垃圾区尽头。
            float overflowY=content.rect.height-viewport.rect.height,overflowX=content.rect.width-viewport.rect.width;
            if(overflowY>0)scroll.verticalNormalizedPosition=1-Mathf.Clamp01((-ground.Rect.anchoredPosition.y+ground.Rect.rect.height-viewport.rect.height)/overflowY);
            if(overflowX>0)scroll.horizontalNormalizedPosition=Mathf.Clamp01((ground.Rect.anchoredPosition.x+ground.Rect.rect.width-viewport.rect.width)/overflowX);
            yield return null;Canvas.ForceUpdateCanvases();
            // 最后一格中心位于Viewport内，不能只有前两行能查看。
            var lastCell=ground.Rect.TransformPoint(new Vector3(224,-288,0));var local=viewport.InverseTransformPoint(lastCell);
            Assert.That(viewport.rect.Contains(local),Is.True);yield return new WaitForEndOfFrame();Capture("large-nearby-last-row.png");
            Assert.That(Find<NearbyPickupController>().TryCollect("large"),Is.True);yield return null;
            Session.ReadOnly.TryGetView("large",out var recovered);Assert.That(recovered.Location.Kind,Is.EqualTo(ItemLocationKind.Backpack));
        }
        [UnityTest] public IEnumerator NearbyOccupiedItemAlsoAcceptsDropWithoutDuplicateEndDrag()
        {
            Backpack.OpenBackpack();yield return null;Session.ReadOnly.TryGetView("b1",out var b1);
            Backpack.BeginDrag(b1,Vector2.zero,null,false);Backpack.SubmitToTarget(InventoryDropKind.Ground);yield return null;
            var grid=NearbyGrid();InventoryItemView ground=null;foreach(var view in grid.GetComponentsInChildren<InventoryItemView>())if(view.Snapshot.InstanceId=="b1")ground=view;
            Assert.That(ground,Is.Not.Null);ReleaseThroughRaycast(BagView("b0"),Point(ground));yield return null;
            Session.ReadOnly.TryGetView("b0",out var b0);Assert.That(b0.Location.Kind,Is.EqualTo(ItemLocationKind.WorldItem));
            Assert.That(Bottle("b0"),Is.EqualTo(100));Assert.That(Bottle("b1"),Is.EqualTo(100));
        }
        [UnityTest] public IEnumerator NonSquareClickSwapsFootprintAndCollisionKeepsOldOrientation()
        {
            LargeItem(2,3);Backpack.OpenBackpack();yield return null;var click=new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left};
            BagView("large").OnPointerClick(click);BagView("large").OnPointerClick(click);yield return null;
            var view=BagView("large");float cell=Backpack.GetComponentInChildren<InventoryGridView>().CellSize;
            Assert.That(view.Rect.rect.width/cell,Is.EqualTo(3));Assert.That(view.Rect.rect.height/cell,Is.EqualTo(2));
            for(int i=0;i<3;i++){BagView("large").OnPointerClick(click);yield return null;}
            LargeItem(2,3,blocker:true);Backpack.OpenBackpack();yield return null;uint before=Session.ReadOnly.Version;
            Backpack.SelectItem("large");BagView("large").OnPointerClick(click);yield return null;
            Session.ReadOnly.TryGetView("large",out var unchanged);Assert.That(unchanged.Location.QuarterTurns,Is.Zero);Assert.That(Session.ReadOnly.Version,Is.EqualTo(before));
        }
        [UnityTest] public IEnumerator BorderRotationAndDragClickCannotMutateOriginalPlacement()
        {
            LargeItem(2,3,x:6);Backpack.OpenBackpack();yield return null;var view=BagView("large");var data=new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left,position=Point(view)};
            view.OnPointerClick(data);uint before=Session.ReadOnly.Version;view.OnPointerClick(data);yield return null;
            Session.ReadOnly.TryGetView("large",out var item);Assert.That(item.Location.QuarterTurns,Is.Zero);Assert.That(Session.ReadOnly.Version,Is.EqualTo(before));
            view.OnBeginDrag(data);view.OnEndDrag(data);view.OnPointerClick(data);yield return null;
            Assert.That(Session.ReadOnly.Version,Is.EqualTo(before));
        }
        [UnityTest] public IEnumerator StackClickRotatesTogetherAndShiftOutsideDropsOnlyOneInstance()
        {
            Session.Profile.Items.TryGetLocation("large",out var location);
            Assert.That(Session.Profile.Items.TryAdd(new RuntimeItemState("second","feedback.large",0),location),Is.True);
            Backpack.OpenBackpack();yield return null;
            int beforeCount=Session.ReadOnly.Count;
            var data=new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left};
            BagView("large").OnPointerClick(data);BagView("large").OnPointerClick(data);yield return null;
            Session.ReadOnly.TryGetView("large",out var first);Session.ReadOnly.TryGetView("second",out var second);
            Assert.That(first.Location.QuarterTurns,Is.EqualTo(1));Assert.That(second.Location,Is.EqualTo(first.Location));
            Keys(UnityEngine.InputSystem.Key.LeftShift);yield return null;var view=BagView("large");data.position=Point(view);view.OnBeginDrag(data);
            data.position=new Vector2(-50,-50);view.OnEndDrag(data);Keys();yield return null;
            Session.ReadOnly.TryGetView("large",out first);Session.ReadOnly.TryGetView("second",out second);
            Assert.That(first.Location.Kind,Is.EqualTo(ItemLocationKind.WorldItem));Assert.That(second.Location.Kind,Is.EqualTo(ItemLocationKind.Backpack));
            Assert.That(second.Location.QuarterTurns,Is.EqualTo(1));Assert.That(Session.ReadOnly.Count,Is.EqualTo(beforeCount));
        }
        [UnityTest] public IEnumerator NearbyEdgeDragKeepsScrollPositionWithoutAssetMutation()
        {
            Backpack.OpenBackpack();yield return null;var grid=NearbyGrid();var scroll=grid.GetComponentInParent<ScrollRect>();
            var viewport=scroll.viewport;var point=RectTransformUtility.WorldToScreenPoint(null,viewport.TransformPoint(new Vector3(viewport.rect.center.x,viewport.rect.yMin+1,0)));
            uint before=Session.ReadOnly.Version;Session.ReadOnly.TryGetView("b0",out var item);Backpack.BeginDrag(item,point,null,false);
            Vector2 position=scroll.normalizedPosition;for(int i=0;i<30;i++)yield return null;
            Assert.That(scroll.normalizedPosition,Is.EqualTo(position));
            Assert.That(Backpack.IsDragging,Is.True);Assert.That(Session.ReadOnly.Version,Is.EqualTo(before));Backpack.CancelDrag();
        }
        [UnityTest] public IEnumerator StableNearbyEdgeDragHasNoScopedGc()
        {
            bool recording=UnityEditorInternal.ProfilerDriver.enabled;bool cpu=UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            try
            {
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,true);UnityEditorInternal.ProfilerDriver.enabled=true;
                Backpack.OpenBackpack();yield return null;var grid=NearbyGrid();var viewport=(RectTransform)grid.transform.parent;
                var pointer=RectTransformUtility.WorldToScreenPoint(null,viewport.TransformPoint(new Vector3(viewport.rect.center.x,viewport.rect.yMin+1,0)));
                Session.ReadOnly.TryGetView("b0",out var item);Backpack.BeginDrag(item,pointer,null,false);
                for(int i=0;i<10;i++)yield return null;int first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;
                for(int i=0;i<60;i++)yield return null;int last=UnityEditorInternal.ProfilerDriver.lastFrameIndex;
                WriteProfile("nearby-edge-drag-profiler",first,last,new[]{"Norn.Inventory.InteractionTick","Norn.HeldItemHud.Update"},"Paused stationary edge drag, autoscroll disabled; warmup excluded");
                Backpack.CancelDrag();
            }
            finally{UnityEditorInternal.ProfilerDriver.enabled=recording;UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,cpu);}
        }
        private static Texture2D ReadFrame()
        {var image=new Texture2D(Screen.width,Screen.height,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,Screen.width,Screen.height),0,0);image.Apply();return image;}
        private static int Difference(Color32 a,Color32 b)=>Mathf.Abs(a.r-b.r)+Mathf.Abs(a.g-b.g)+Mathf.Abs(a.b-b.b);
        [UnityTest] public IEnumerator OutlineChangesOnlySilhouetteForFoodContainerAndHerbAtTwoAngles()
        {
            Backpack.Close();foreach(var root in Scene.GetRootGameObjects())
            {foreach(var canvas in root.GetComponentsInChildren<Canvas>(true))canvas.enabled=false;foreach(var camera in root.GetComponentsInChildren<Camera>(true))camera.enabled=false;}
            var cameraNode=new GameObject("Outline pixel proof",typeof(Camera));UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cameraNode,Scene);
            var proof=cameraNode.GetComponent<Camera>();proof.cullingMask=1<<30;proof.clearFlags=CameraClearFlags.SolidColor;proof.backgroundColor=new Color(.04f,.05f,.06f);
            proof.orthographic=true;proof.allowHDR=false;proof.allowMSAA=false;proof.nearClipPlane=.01f;proof.farClipPlane=100;
            var metrics=new System.Text.StringBuilder();
            foreach(string relative in new[]{"Food/Apple","Container/Container_01","Herb/Herb_Flower_01"})
            {
                var definition=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Items/"+relative+".asset");
                Assert.That(Session.TryRegisterWorldItem(definition,Session.WorldSceneKey,"proof."+relative,Player.transform.position+Vector3.right,Quaternion.identity),Is.True);
                yield return null;WorldItemPickup current=null;
                foreach(var root in Scene.GetRootGameObjects())foreach(var proxy in root.GetComponentsInChildren<WorldItemPickup>())
                    if(Session.ReadOnly.TryGetView(proxy.ItemId,out var item) && item.ContentId==definition.ContentId)current=proxy;
                Assert.That(current,Is.Not.Null);foreach(var child in current.GetComponentsInChildren<Transform>())child.gameObject.layer=30;
                var renderers=current.GetComponentsInChildren<MeshRenderer>();var layers=new List<MeshRenderer>();Bounds bounds=renderers[0].bounds;
                foreach(var renderer in renderers){bounds.Encapsulate(renderer.bounds);if(renderer.name=="Pickup outline" || renderer.name=="Pickup silhouette mask")layers.Add(renderer);}
                Assert.That(layers.Count,Is.GreaterThanOrEqualTo(2));
                proof.orthographicSize=Mathf.Max(.01f,bounds.size.magnitude*.65f);float distance=Mathf.Max(2,bounds.size.magnitude*3);
                for(int angle=0;angle<2;angle++)
                {
                    proof.transform.position=bounds.center+Quaternion.Euler(0,angle*90,0)*new Vector3(.8f,.5f,-1).normalized*distance;proof.transform.LookAt(bounds.center);
                    foreach(var layer in layers)layer.enabled=false;yield return null;yield return new WaitForEndOfFrame();var off=ReadFrame();
                    foreach(var layer in layers)layer.enabled=true;yield return null;yield return new WaitForEndOfFrame();var on=ReadFrame();
                    try
                    {
                        var a=off.GetPixels32();var b=on.GetPixels32();int w=off.width,h=off.height;var background=a[0];var foreground=new bool[a.Length];
                        for(int i=0;i<a.Length;i++)foreground[i]=Difference(a[i],background)>15;
                        // 只比较模型投影附近，HUD等独立系统不属于本次材质/轮廓判据。
                        Vector2 min=new Vector2(w,h),max=Vector2.zero;
                        for(int corner=0;corner<8;corner++)
                        {
                            Vector3 world=bounds.center+Vector3.Scale(bounds.extents,new Vector3((corner&1)==0?-1:1,(corner&2)==0?-1:1,(corner&4)==0?-1:1));
                            Vector2 screen=proof.WorldToScreenPoint(world);min=Vector2.Min(min,screen);max=Vector2.Max(max,screen);
                        }
                        int minX=Mathf.Max(3,Mathf.FloorToInt(min.x)-32),maxX=Mathf.Min(w-3,Mathf.CeilToInt(max.x)+32);
                        int minY=Mathf.Max(3,Mathf.FloorToInt(min.y)-32),maxY=Mathf.Min(h-3,Mathf.CeilToInt(max.y)+32);
                        int changed=0,interior=0,interiorChanged=0,farChanges=0;
                        for(int y=minY;y<maxY;y++)for(int x=minX;x<maxX;x++)
                        {
                            int p=y*w+x;bool deep=foreground[p];
                            for(int dy=-3;dy<=3 && deep;dy++)for(int dx=-3;dx<=3;dx++)if(!foreground[(y+dy)*w+x+dx]){deep=false;break;}
                            bool differs=Difference(a[p],b[p])>3;
                            if(differs)
                            {
                                changed++;bool near=false;
                                // 新光晕半径12px，Sharp外扩2.5px，允许滤波足迹但必须有有限边界。
                                for(int dy=-17;dy<=17 && !near;dy++)for(int dx=-17;dx<=17;dx++)
                                    if(x+dx>=0 && x+dx<w && y+dy>=0 && y+dy<h && foreground[(y+dy)*w+x+dx]){near=true;break;}
                                if(!near)farChanges++;
                            }
                            if(deep){interior++;if(differs)interiorChanged++;}
                        }
                        string name=relative.Replace('/','-')+(_windProof?"-wind":"")+"-"+angle;
                        System.IO.Directory.CreateDirectory(EvidencePath);System.IO.File.WriteAllBytes(EvidencePath+"/"+name+"-off.png",off.EncodeToPNG());System.IO.File.WriteAllBytes(EvidencePath+"/"+name+"-on.png",on.EncodeToPNG());
                        metrics.AppendLine(name+": changed="+changed+", interior="+interior+", interiorChanged="+interiorChanged+", farChanges="+farChanges);
                        System.IO.File.WriteAllText(EvidencePath+(_windProof?"/outline-wind-pixels.txt":"/outline-pixels.txt"),metrics.ToString());
                        Assert.That(interior,Is.GreaterThan(50),name+"不能用空画面证明表面颜色保留");
                        Assert.That(changed,Is.GreaterThan(10),name+"必须实际绘制可见轮廓");
                        Assert.That(interiorChanged,Is.Zero,name+"物体中央像素不得被高亮着色");
                        Assert.That(farChanges,Is.Zero,name+"光晕不能落在距离可见物体边缘17像素之外");
                    }
                    finally{Object.Destroy(off);Object.Destroy(on);}
                }
                if(relative.StartsWith("Herb"))
                {
                    var wall=GameObject.CreatePrimitive(PrimitiveType.Cube);UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(wall,Scene);wall.layer=30;
                    wall.transform.position=proof.transform.position+proof.transform.forward*distance*.4f;wall.transform.rotation=proof.transform.rotation;
                    wall.transform.localScale=new Vector3(proof.orthographicSize*proof.aspect*4,proof.orthographicSize*4,.1f);
                    var material=new Material(Shader.Find("Universal Render Pipeline/Unlit"));_assets.Add(material);material.SetColor("_BaseColor",new Color(.1f,.12f,.14f));wall.GetComponent<Renderer>().sharedMaterial=material;
                    foreach(var layer in layers)layer.enabled=false;yield return null;yield return new WaitForEndOfFrame();var off=ReadFrame();
                    foreach(var layer in layers)layer.enabled=true;yield return null;yield return new WaitForEndOfFrame();var on=ReadFrame();
                    try{var a=off.GetPixels32();var b=on.GetPixels32();int changed=0;for(int i=0;i<a.Length;i++)if(Difference(a[i],b[i])>3)changed++;
                        Assert.That(changed,Is.Zero,"墙后物品不得透出轮廓");Capture(_windProof?"outline-wind-wall.png":"outline-wall.png");}
                    finally{Object.Destroy(off);Object.Destroy(on);}
                }
            }
        }
        [UnityTest] public IEnumerator GrassWindKeepsOutlineAttachedToTrueSilhouette()
        {
            _windProof=true;_oldWind=new float[WindNames.Length];_oldWindDirection=Shader.GetGlobalVector("ASPW_WindDirection");
            for(int i=0;i<WindNames.Length;i++){_oldWind[i]=Shader.GetGlobalFloat(WindNames[i]);Shader.SetGlobalFloat(WindNames[i],1);}
            Shader.SetGlobalFloat("ASPW_WindGrassAmplitude",2);Shader.SetGlobalFloat("ASPW_WindGrassFlexibility",2);Shader.SetGlobalVector("ASPW_WindDirection",new Vector4(1,0,1,0));
            // 固定Shader时间便于off/on逐像素对比；原草药启用的Vertex wind仍在当前姿态生效。
            Time.timeScale=0;yield return OutlineChangesOnlySilhouetteForFoodContainerAndHerbAtTwoAngles();
        }
    }
}
#endif
