using System;
using System.Collections.Generic;
using System.Reflection;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.Run.Tests
{
    /// <summary>
    /// 容量文字保留作者原 Rect，只恢复 Viewport 的 32 顶行：不能盖首格或移动作者外框。
    /// 读取正式 Prefab/Scene，在 inactive 副本调用真实 Refresh；不启动账号、输入、存档或 Gameplay。
    /// </summary>
    public sealed class InventoryCapacityHeaderLayoutTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly List<Scene> _scenes = new List<Scene>();
        private readonly List<GameObject> _roots = new List<GameObject>();
        private static readonly Vector2[] Viewports = { new Vector2(1280,720), new Vector2(1424,714),
            new Vector2(1920,1080), new Vector2(1920,1200), new Vector2(3440,1440) };

        [TearDown] public void Cleanup()
        {
            foreach (var root in _roots) if (root != null) Object.DestroyImmediate(root);
            foreach (var scene in _scenes) if (scene.IsValid() && scene.isLoaded) EditorSceneManager.ClosePreviewScene(scene);
            _roots.Clear(); _scenes.Clear();
        }

        [TestCase("Shared")]
        [TestCase("AuthorPrefab")]
        [TestCase("Hub")]
        [TestCase("Home")]
        public void OriginalTabReservesCapacityHeaderWithoutChangingAuthoredOuterLayout(string source)
        {
            var ui = Clone(source); var workspace = (BackpackWorkspaceView)Get(ui,"_backpackWorkspace");
            Assert.That(workspace, Is.Not.Null);
            var capacity = (Text)Get(workspace,"_capacity"); Assert.That(capacity, Is.Not.Null);
            var grid = (InventoryGridView)Get(ui,"_gridView"); Assert.That(grid, Is.Not.Null);
            var viewport = (RectTransform)grid.transform.parent;
            Assert.That(capacity.transform.parent, Is.SameAs(viewport.parent));
            Assert.That(viewport.GetComponent<RectMask2D>(), Is.Not.Null, "容量顶行外的 Content 必须仍由原 Viewport 裁剪。");
            var preserved = Capture(ui.transform, viewport);
            var item = Bag();
            // 只略过不在本合同内的道具模板替换；保持真实容量、钱包 Refresh 与原 View/Rect 接线。
            Set(workspace,"_tools",null);
            foreach (var size in Viewports)
            {
                LayoutCanvas(ui, size);
                workspace.Refresh(ui,new[]{item},1,item.InstanceId);
                AssertHeader(capacity.rectTransform,viewport);
                Assert.That(capacity.text, Is.EqualTo("已占 0 / "+item.Rules.BackpackWidth*item.Rules.BackpackHeight+" 格"));
                AssertPreserved(preserved);
                var labelPose = new Pose(capacity.rectTransform); var viewPose = new Pose(viewport);
                workspace.Refresh(ui,new[]{item},1,item.InstanceId);
                labelPose.AssertSame(capacity.rectTransform); viewPose.AssertSame(viewport);
            }
        }

        [Test] public void AlreadyLargerViewportInsetIsPreservedOnRepeatedRefresh()
        {
            var ui=Clone("Shared"); var workspace=(BackpackWorkspaceView)Get(ui,"_backpackWorkspace");
            var capacity=(Text)Get(workspace,"_capacity"); var grid=(InventoryGridView)Get(ui,"_gridView");
            var viewport=(RectTransform)grid.transform.parent; var max=viewport.offsetMax; max.y=-48; viewport.offsetMax=max;
            Set(workspace,"_tools",null); LayoutCanvas(ui,new Vector2(1424,714)); var item=Bag();
            var labelPose=new Pose(capacity.rectTransform);
            for(int i=0;i<3;i++) workspace.Refresh(ui,new[]{item},1,item.InstanceId);
            AssertHeader(capacity.rectTransform,viewport);
            labelPose.AssertSame(capacity.rectTransform);
            Assert.That(viewport.offsetMax.y,Is.EqualTo(-48).Within(.001f),"已有更大作者留白不能被扩大回 32。");
        }

        [TestCase("Warehouse")]
        [TestCase("Shop")]
        public void OriginalHubOtherWorkspacesKeepTheirOwnCaptionAndGeometry(string kind)
        {
            var ui=Clone(kind);
            Assert.That(Get(ui,"_backpackWorkspace"),Is.Null,"仓库/商店已退役 Tab 专用 Presenter 接线。");
            var legacy=ui.GetComponent<BackpackWorkspaceView>(); Assert.That(legacy,Is.Not.Null);
            Assert.That(Get(legacy,"_capacity"),Is.Null);
            var current=(WarehouseWorkspaceView)Get(ui,"_warehouseWorkspace"); Assert.That(current,Is.Not.Null);
            var caption=(Text)Get(current,"_bagCaption"); Assert.That(caption,Is.Not.Null);
            var item=Bag(); Set(legacy,"_tools",null); Set(current,"_tools",null);
            foreach(var size in Viewports)
            {
                LayoutCanvas(ui,size); var preserved=Capture(ui.transform);
                // 即使旧组件被误刷新，也不能按名称扫描别处、创建容量条或收缩商店/仓库的网格。
                legacy.Refresh(ui,new[]{item},1,item.InstanceId);
                current.Refresh(new[]{item},1,item.InstanceId);
                Assert.That(caption.text,Does.Contain("已占 0/"+item.Rules.BackpackWidth*item.Rules.BackpackHeight+" 格"));
                AssertPreserved(preserved);
            }
        }

        private SinglePlayerInventoryController Clone(string source)
        {
            var parent=new GameObject("Inactive original capacity fixture"); parent.SetActive(false); _roots.Add(parent);
            GameObject original;
            if(source=="Shared" || source=="AuthorPrefab")
                original=AssetDatabase.LoadAssetAtPath<GameObject>(source=="Shared"
                    ? "Assets/_Project/Resources/Presentation/CombatInventory.prefab"
                    : "Assets/_Project/Prefabs/SinglePlayer/PracticeInventoryWorkspace.prefab");
            else
            {
                var scene=EditorSceneManager.OpenPreviewScene(source=="Home"
                    ? "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Home.unity"
                    : "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity"); _scenes.Add(scene);
                original=null;
                foreach(var root in scene.GetRootGameObjects()) foreach(var candidate in root.GetComponentsInChildren<SinglePlayerInventoryController>(true))
                {
                    bool tab=Get(candidate,"_backpackWorkspace")!=null;
                    bool embedded=(bool)Get(candidate,"_embedded");
                    if((source=="Hub" || source=="Home") && tab || source=="Shop" && embedded || source=="Warehouse" && !tab && !embedded)
                    { Assert.That(original,Is.Null,"正式来源必须唯一。"); original=candidate.gameObject; }
                }
            }
            Assert.That(original,Is.Not.Null,source);
            var copy=Object.Instantiate(original,parent.transform,false);
            var ui=copy.GetComponentInChildren<SinglePlayerInventoryController>(true); Assert.That(ui,Is.Not.Null);
            // 显式空源禁止碰 Scene session；这里只读库存快照生成标题，不启动任何账户或权威生命周期。
            Set(ui,"_hasPresentationOverride",true); Set(ui,"_presentationSource",null);
            typeof(SinglePlayerInventoryController).GetMethod("ConfigureViewportLayout",Private).Invoke(ui,null);
            return ui;
        }

        private static void LayoutCanvas(SinglePlayerInventoryController ui,Vector2 viewport)
        {
            var canvas=ui.GetComponentInParent<Canvas>(true).rootCanvas; var scaler=canvas.GetComponent<CanvasScaler>();
            Assert.That(scaler.screenMatchMode,Is.EqualTo(CanvasScaler.ScreenMatchMode.Expand));
            float scale=Mathf.Min(viewport.x/scaler.referenceResolution.x,viewport.y/scaler.referenceResolution.y);
            // 与现有 UiViewportLayoutTests 相同：真实原 Rect 树计算 anchors，视口只决定设计面积与屏幕等比尺度。
            scaler.enabled=false; canvas.renderMode=RenderMode.WorldSpace;
            var rect=(RectTransform)canvas.transform; rect.localScale=Vector3.one;
            rect.sizeDelta=viewport/scale; rect.ForceUpdateRectTransforms();
        }
        private static void AssertHeader(RectTransform label,RectTransform viewport)
        {
            var region=(RectTransform)viewport.parent;
            var text=InParent(label,region); var content=InParent(viewport,region);
            Assert.That(label.rect.height,Is.GreaterThan(0),"容量文字必须仍有实际显示区域。");
            Assert.That(viewport.offsetMax.y,Is.LessThanOrEqualTo(-32+.001f));
            // 当前作者文字高 30、顶部 -2，正好占据 32 顶行。保留这个 Rect，
            // 不因旧接线工具曾用 24 高就缩小文字，也不强加额外 6 单位间隙。
            Assert.That(text.yMin-content.yMax,Is.GreaterThanOrEqualTo(-.01f),"文字底边不能进入首行可见区。");
            Assert.That(text.yMax,Is.LessThanOrEqualTo(region.rect.yMax+.01f));
            Assert.That(text.xMin,Is.GreaterThanOrEqualTo(region.rect.xMin-.01f));
            Assert.That(text.xMax,Is.LessThanOrEqualTo(region.rect.xMax+.01f));
            float overlapHeight=Mathf.Min(content.yMax,text.yMax)-Mathf.Max(content.yMin,text.yMin);
            Assert.That(overlapHeight,Is.LessThanOrEqualTo(.01f),"容量标题不能覆盖可见物品格；相接边界仅容许浮点投影误差。");
        }
        private static Rect InParent(RectTransform rect,RectTransform parent)
        {
            var corners=new Vector3[4];rect.GetWorldCorners(corners);
            var a=parent.InverseTransformPoint(corners[0]);var b=parent.InverseTransformPoint(corners[2]);
            return Rect.MinMaxRect(a.x,a.y,b.x,b.y);
        }
        private static Dictionary<RectTransform,Pose> Capture(Transform root,params RectTransform[] except)
        {
            var saved=new Dictionary<RectTransform,Pose>();
            foreach(var rect in root.GetComponentsInChildren<RectTransform>(true))
            {
                // Canvas 自身的设计面积由不同视口控制；其余作者根、周边区域和 Content local pose 均须保持。
                if(rect.GetComponent<Canvas>()!=null || Array.IndexOf(except,rect)>=0)continue;
                saved.Add(rect,new Pose(rect));
            }
            return saved;
        }
        private static void AssertPreserved(Dictionary<RectTransform,Pose> saved)
        { foreach(var pair in saved)pair.Value.AssertSame(pair.Key); }
        private readonly struct Pose
        {
            private readonly Vector2 _min,_max,_pivot,_position,_size;
            public Pose(RectTransform rect){_min=rect.anchorMin;_max=rect.anchorMax;_pivot=rect.pivot;_position=rect.anchoredPosition;_size=rect.sizeDelta;}
            public void AssertSame(RectTransform rect)
            {
                Assert.That(rect.anchorMin,Is.EqualTo(_min),rect.name);Assert.That(rect.anchorMax,Is.EqualTo(_max),rect.name);
                Assert.That(rect.pivot,Is.EqualTo(_pivot),rect.name);Assert.That(rect.anchoredPosition,Is.EqualTo(_position),rect.name);
                Assert.That(rect.sizeDelta,Is.EqualTo(_size),rect.name);
            }
        }
        private static InventoryItemSnapshot Bag()
        {
            var definition=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Items/Backpacks/Item_Backpack_StarterBag.asset");
            Assert.That(definition,Is.Not.Null); var args=new object[]{default(InventoryItemRules),null};
            Assert.That((bool)typeof(ItemDefinition).GetMethod("TryCreateRules",Private).Invoke(definition,args),Is.True,args[1] as string);
            return new InventoryItemSnapshot(new RuntimeItemState("capacity-bag",definition.ContentId,0),
                new ItemLocation(ItemLocationKind.EquippedBackpack,"capacity",0,0,0,0),definition,(InventoryItemRules)args[0]);
        }
        private static object Get(object target,string field)=>target.GetType().GetField(field,Private).GetValue(target);
        private static void Set(object target,string field,object value)=>target.GetType().GetField(field,Private).SetValue(target,value);
    }
}
