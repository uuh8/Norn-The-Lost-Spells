#if UNITY_EDITOR
using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Run.Tests
{
    public sealed class InventoryDragAutoScrollTests
    {
        [TestCase(1f)]
        [TestCase(.54f)]
        [TestCase(1.5f)]
        public void DragStartsInsideWideScreenEdgeBand(float scale)
        {
            float distance=ScrollDistance(scale,64);
            Assert.That(distance,Is.GreaterThan(1),"离边缘64屏幕像素就应开始滚动，无需碰到Scrollbar。");
        }

        [Test] public void ApproachingEdgeAcceleratesAndCenterStaysStill()
        {
            float inner=ScrollDistance(1,80),outer=ScrollDistance(1,10);
            Assert.That(inner,Is.GreaterThan(0));
            Assert.That(outer,Is.GreaterThan(inner*2));
            Assert.That(ScrollDistance(1,200),Is.EqualTo(0).Within(.01f));
        }

        [Test] public void ScreenSpeedIsFastAndIndependentOfCanvasScale()
        {
            float normal=ScrollDistance(1,1),small=ScrollDistance(.54f,1),large=ScrollDistance(1.5f,1);
            Assert.That(normal/.02f,Is.GreaterThan(900),"极靠近边缘的速度应接近1000屏幕像素/秒。");
            Assert.That(small,Is.EqualTo(normal).Within(.1f));
            Assert.That(large,Is.EqualTo(normal).Within(.1f));
        }

        // 用屏幕距离观察实际Content移动，避免只验证normalizedPosition而漏掉Canvas缩放的手感差异。
        private static float ScrollDistance(float scale,float edgeDistance)
        {
            var root=new GameObject("Screen scroll proof",typeof(RectTransform),typeof(Canvas));
            try
            {
                root.GetComponent<Canvas>().renderMode=RenderMode.ScreenSpaceOverlay;
                var node=new GameObject("Viewport",typeof(RectTransform),typeof(ScrollRect));node.transform.SetParent(root.transform,false);
                var viewport=(RectTransform)node.transform;viewport.sizeDelta=new Vector2(500,500);viewport.localScale=Vector3.one*scale;
                var contentNode=new GameObject("Content",typeof(RectTransform));contentNode.transform.SetParent(node.transform,false);
                var content=(RectTransform)contentNode.transform;content.anchorMin=content.anchorMax=content.pivot=new Vector2(0,1);content.sizeDelta=new Vector2(1500,1500);
                var scroll=node.GetComponent<ScrollRect>();scroll.viewport=viewport;scroll.content=content;scroll.movementType=ScrollRect.MovementType.Clamped;
                var driver=node.AddComponent<Game.UI.InventoryDragAutoScroll>();driver.Configure(scroll);
                Canvas.ForceUpdateCanvases();scroll.normalizedPosition=new Vector2(0,1);
                var pointer=RectTransformUtility.WorldToScreenPoint(null,viewport.TransformPoint(new Vector3(viewport.rect.xMax,viewport.rect.center.y)));
                pointer.x-=edgeDistance;
                var before=RectTransformUtility.WorldToScreenPoint(null,content.position);
                driver.Tick(pointer,null,true,.02f);
                var after=RectTransformUtility.WorldToScreenPoint(null,content.position);
                return Mathf.Abs(after.x-before.x);
            }
            finally{UnityEngine.Object.DestroyImmediate(root);}
        }

        [Test] public void StationaryPointerScrollsWhilePausedAndStopsWhenInactive()
        {
            var type=typeof(Game.UI.InventoryGridView).Assembly.GetType("Game.UI.InventoryDragAutoScroll");
            Assert.That(type,Is.Not.Null,"拖住物品停在边缘必须有持续滚动入口。");
            var root=new GameObject("Scroll test",typeof(RectTransform),typeof(Canvas));
            try
            {
                root.GetComponent<Canvas>().renderMode=RenderMode.ScreenSpaceOverlay;
                var viewport=new GameObject("Viewport",typeof(RectTransform),typeof(ScrollRect));viewport.transform.SetParent(root.transform,false);
                var vr=(RectTransform)viewport.transform;vr.sizeDelta=new Vector2(200,200);
                var content=new GameObject("Content",typeof(RectTransform));content.transform.SetParent(viewport.transform,false);
                var cr=(RectTransform)content.transform;cr.anchorMin=cr.anchorMax=cr.pivot=new Vector2(0,1);cr.sizeDelta=new Vector2(600,600);
                var scroll=viewport.GetComponent<ScrollRect>();scroll.viewport=vr;scroll.content=cr;scroll.movementType=ScrollRect.MovementType.Clamped;
                Canvas.ForceUpdateCanvases();scroll.horizontalNormalizedPosition=0;scroll.verticalNormalizedPosition=1;
                var component=viewport.AddComponent(type);type.GetMethod("Configure").Invoke(component,new object[]{scroll});
                Vector2 pointer=RectTransformUtility.WorldToScreenPoint(null,vr.TransformPoint(new Vector3(vr.rect.xMax-1,vr.rect.yMin+1)));
                var tick=type.GetMethod("Tick");float before=scroll.verticalNormalizedPosition;
                Time.timeScale=0;
                for(int i=0;i<10;i++)tick.Invoke(component,new object[]{pointer,null,true,.05f});
                Assert.That(scroll.verticalNormalizedPosition,Is.LessThan(before));Assert.That(scroll.horizontalNormalizedPosition,Is.GreaterThan(0));
                float stopped=scroll.verticalNormalizedPosition;tick.Invoke(component,new object[]{pointer,null,false,.5f});
                Assert.That(scroll.verticalNormalizedPosition,Is.EqualTo(stopped));
            }
            finally{Time.timeScale=1;UnityEngine.Object.DestroyImmediate(root);}
        }
    }
}
#endif
