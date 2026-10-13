#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using Game.Run;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Game.Run.Tests
{
    /// <summary>只加载独立草稿和内存资产；验证复制Canvas没有订阅Tab、E打开正确商店以及旧交易仍生效。</summary>
    public sealed class ShopDraftPlayModeTests : BackpackWorkspacePlayModeTests
    {
        protected override string ReviewPath => "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub_ShopDraftReview.unity";
        protected override string EvidencePath => "TestResults/SinglePlayer/M6-T1-ShopDraft";
        protected override SinglePlayerProfile CustomizeProfile(SinglePlayerProfile profile, ItemCatalogIndex index)
        {
            foreach(var root in Scene.GetRootGameObjects())foreach(var view in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                // 隔离作者已删除文字但遗留的旧HUD，红测试集中于商店入口而非无关NullReference。
                if(view is RaidResultView && Ref<Text>(view,"_result")==null)view.enabled=false;
                if(view is ExtractionHud && Ref<Text>(view,"_condition")==null)view.enabled=false;
            }
            return profile;
        }
        private static T Ref<T>(UnityEngine.Object target,string field) where T:UnityEngine.Object => (T)new SerializedObject(target).FindProperty(field).objectReferenceValue;
        private void Resolve()
        {
            foreach(var root in Scene.GetRootGameObjects())foreach(var ui in root.GetComponentsInChildren<SinglePlayerInventoryController>(true))
            {if(ui.name=="InventoryCanvas_背包")Backpack=ui;if(ui.name=="InventoryCanvas_仓库")Warehouse=ui;}
        }
        private void EnterShop()
        {
            Resolve();var hub=Find<HubInteractionController>();var area=Ref<HubAreaVolume>(hub,"_shopArea");
            Player.CharacterController.enabled=false;Player.transform.position=area.Center;Player.enabled=false;Physics.SyncTransforms();
        }
        [UnityTest] public IEnumerator EOpensUserShopCanvasAndTabReturnsToOnlyBackpack()
        {
            EnterShop();Keys(Key.E);yield return null;Keys();yield return null;
            var shop=Find<ShopController>();Assert.That(shop.IsOpen,Is.True,"商店入口控制器必须常驻启用");
            var panel=Ref<GameObject>(shop,"_panel");Assert.That(panel.GetComponentInParent<Canvas>().name,Is.EqualTo("InventoryCanvas_商店"));
            Assert.That(panel.activeInHierarchy,Is.True);Assert.That(Backpack.IsOpen,Is.False);Assert.That(Warehouse.IsOpen,Is.False);
            Assert.That(Find<HeldItemHud>().GetComponentInParent<Canvas>().enabled,Is.False);
            yield return new WaitForEndOfFrame();
            foreach(var field in new[]{"_buyButton","_sellItemButton","_sellMaterialButton","_closeButton"})
            {
                var button=Ref<Button>(shop,field);var label=button.GetComponentInChildren<Text>();
                var corners=new Vector3[4];label.rectTransform.GetWorldCorners(corners);
                foreach(var corner in corners)Assert.That(RectTransformUtility.RectangleContainsScreenPoint((RectTransform)button.transform,RectTransformUtility.WorldToScreenPoint(null,corner)),Is.True,"按钮文字必须在按钮内："+field);
            }
            Capture("shop-draft.png");
            Keys(Key.Tab);yield return null;Keys();yield return null;
            Assert.That(shop.IsOpen,Is.False);Assert.That(Backpack.IsOpen,Is.True);Assert.That(Warehouse.IsOpen,Is.False);
            Keys(Key.Escape);yield return null;Keys();yield return null;Assert.That(Time.timeScale,Is.EqualTo(1));
        }
        [UnityTest] public IEnumerator PurchaseStillUsesActualStockAndSharedAssets()
        {
            EnterShop();Keys(Key.E);yield return null;Keys();yield return null;
            var shop=Find<ShopController>();var session=Find<SinglePlayerShopSession>();Assert.That(shop.IsOpen,Is.True);
            ShopOfferSnapshot offer=default;bool found=false;
            for(int i=0;i<session.ReadOnly.Count;i++){var value=session.ReadOnly.GetOffer(i);if(!value.IsMaterial && value.BuyPrice>0 && value.BuyPrice<=248 && value.Stock>0){offer=value;found=true;break;}}
            Assert.That(found,Is.True,"内存钱包能够购买一份配置商品");
            long currency=session.ReadOnly.Currency;int count=Session.ReadOnly.Count;
            shop.SelectOffer(offer.OfferId);Ref<InputField>(shop,"_quantity").text="1";Ref<Button>(shop,"_buyButton").onClick.Invoke();yield return null;
            Assert.That(shop.Status,Does.Contain("已保存"));Assert.That(session.ReadOnly.Currency,Is.EqualTo(currency-offer.BuyPrice));
            Assert.That(Session.ReadOnly.Count,Is.EqualTo(count+1));
            yield return new WaitForEndOfFrame();Capture("shop-purchased.png");shop.Close();Assert.That(Time.timeScale,Is.EqualTo(1));
        }
        [UnityTest] public IEnumerator WarehouseStillOpensItsOwnCanvasAfterShopCloses()
        {
            EnterShop();Keys(Key.E);yield return null;Keys();yield return null;var shop=Find<ShopController>();Assert.That(shop.IsOpen,Is.True);
            Keys(Key.Escape);yield return null;Keys();yield return null;
            var warehouseArea=Ref<HubAreaVolume>(Find<HubInteractionController>(),"_warehouseArea");Player.transform.position=warehouseArea.Center;Physics.SyncTransforms();
            Keys(Key.E);yield return null;Keys();yield return null;
            Assert.That(shop.IsOpen,Is.False);Assert.That(Warehouse.IsOpen,Is.True);Assert.That(Backpack.IsOpen,Is.False);
            Assert.That(Ref<GameObject>(Warehouse,"_panelRoot").GetComponentInParent<Canvas>().name,Is.EqualTo("InventoryCanvas_仓库"));
            Keys(Key.Escape);yield return null;Keys();yield return null;Assert.That(Time.timeScale,Is.EqualTo(1));
        }
        [Serializable] private sealed class ProfileEvidence
        {
            public string Scope="Shop draft Review, initialized and paused, no transaction; Update scopes only";
            public int ValidFrames,UiSamples,SessionSamples;
            public long UiGcBytes,SessionGcBytes,MainThreadGcBytes;
        }
        [UnityTest] public IEnumerator StableDraftShopUpdateScopesHaveNoGcAlloc()
        {
            EnterShop();Keys(Key.E);yield return null;Keys();yield return null;Assert.That(Find<ShopController>().IsOpen,Is.True);
            bool recording=UnityEditorInternal.ProfilerDriver.enabled;
            bool cpu=UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            try
            {
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,true);UnityEditorInternal.ProfilerDriver.enabled=true;
                for(int i=0;i<10;i++)yield return null;
                int first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;
                for(int i=0;i<60;i++)yield return null;
                var evidence=new ProfileEvidence();int last=UnityEditorInternal.ProfilerDriver.lastFrameIndex;
                // RawFrameData分析在采样结束后进行；分析自身的分配不混入被测稳定帧。
                for(int frame=Math.Max(first,UnityEditorInternal.ProfilerDriver.firstFrameIndex);frame<=last;frame++)
                {
                    using var data=UnityEditorInternal.ProfilerDriver.GetRawFrameDataView(frame,0);if(!data.valid)continue;evidence.ValidFrames++;
                    for(int i=0;i<data.sampleCount;i++)
                    {
                        string name=data.GetSampleName(i);if(name=="GC.Alloc"&&data.GetSampleMetadataCount(i)>0)evidence.MainThreadGcBytes+=data.GetSampleMetadataAsLong(i,0);
                        bool ui=name=="Norn.Shop.UiUpdate",session=name=="Norn.Shop.SessionUpdate";if(!ui&&!session)continue;
                        if(ui)evidence.UiSamples++;else evidence.SessionSamples++;
                        double start=data.GetSampleStartTimeMs(i),end=start+data.GetSampleTimeMs(i);
                        for(int j=i+1;j<data.sampleCount;j++)
                        {
                            double time=data.GetSampleStartTimeMs(j);if(time>end)break;
                            if(time>=start&&data.GetSampleName(j)=="GC.Alloc"&&data.GetSampleMetadataCount(j)>0)
                            {long bytes=data.GetSampleMetadataAsLong(j,0);if(ui)evidence.UiGcBytes+=bytes;else evidence.SessionGcBytes+=bytes;}
                        }
                    }
                }
                Directory.CreateDirectory(EvidencePath);File.WriteAllText(EvidencePath+"/shop-profiler.json",JsonUtility.ToJson(evidence,true));
                Assert.That(evidence.ValidFrames,Is.GreaterThanOrEqualTo(50));Assert.That(evidence.UiSamples,Is.GreaterThan(0));Assert.That(evidence.SessionSamples,Is.GreaterThan(0));
                Assert.That(evidence.UiGcBytes,Is.Zero);Assert.That(evidence.SessionGcBytes,Is.Zero);
            }
            finally{UnityEditorInternal.ProfilerDriver.enabled=recording;UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,cpu);Find<ShopController>().Close();}
        }
    }
}
#endif
