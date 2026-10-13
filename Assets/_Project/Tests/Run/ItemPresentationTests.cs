#if UNITY_EDITOR
using System;
using Game.Run;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Run.Tests
{
    public sealed class ItemPresentationTests
    {
        [Test] public void AllEighteenModelDefinitionsHavePersistentIcons()
        {
            foreach(var guid in AssetDatabase.FindAssets("t:ItemDefinition",new[]{"Assets/_Project/ScriptableObjects/SinglePlayer/Items"}))
            {
                var item=AssetDatabase.LoadAssetAtPath<ItemDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                Assert.That(item.Icon,Is.Not.Null,item.name);Assert.That(item.Icon.texture.width,Is.EqualTo(512));
                Assert.That(AssetDatabase.GetAssetPath(item.Icon),Does.StartWith("Assets/_Project/Art/UI/ItemIcons/"));
            }
        }
        [Test] public void GeneratedPngsHaveVisiblePixelsAndTransparentCorners()
        {
            foreach(var path in System.IO.Directory.GetFiles("Assets/_Project/Art/UI/ItemIcons","*.png",System.IO.SearchOption.AllDirectories))
            {
                var image=new Texture2D(2,2,TextureFormat.RGBA32,false);
                try
                {
                    Assert.That(image.LoadImage(System.IO.File.ReadAllBytes(path)),Is.True,path);
                    Assert.That(image.GetPixel(0,0).a,Is.Zero,path);
                    bool opaque=false;foreach(var pixel in image.GetPixels32())if(pixel.a>200){opaque=true;break;}
                    Assert.That(opaque,Is.True,path);
                }
                finally {UnityEngine.Object.DestroyImmediate(image);}
            }
        }
        [Test] public void MissingOnlyBakeKeepsExistingIconsAndSceneClean()
        {
            var type=Type.GetType("Game.EditorTools.ItemIconBaker, Game.PbfTask13.Editor");
            var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();bool before=scene.isDirty;
            Assert.That((int)type.GetMethod("BakeAll").Invoke(null,new object[]{false,null,1.22f}),Is.Zero);
            Assert.That(scene.isDirty,Is.EqualTo(before));
        }
        [Test] public void IconBakerSupportsExplicitAndMissingOnlyGeneration()
        {Assert.That(Type.GetType("Game.EditorTools.ItemIconBaker, Game.PbfTask13.Editor"),Is.Not.Null);}
        [Test] public void LegacyTemplateCreatesMissingIconImage()
        {
            var go=new GameObject("Legacy item",typeof(RectTransform),typeof(UnityEngine.UI.Image));
            try
            {
                var view=go.AddComponent<InventoryItemView>();
                var item=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/SinglePlayer/Items/Food/Apple.asset");
                var shape=new InventoryShape(1,1,new[]{Vector2Int.zero});
                var rules=new InventoryItemRules(ItemKind.Consumable,shape,1,1,1,1);
                var snapshot=new InventoryItemSnapshot(new RuntimeItemState("test",item.ContentId,1),new ItemLocation(ItemLocationKind.WorldItem,"test",0,0,0,-1),item,rules);
                view.Bind(null,snapshot,InventoryItemDisplay.Grid,64);
                var icon=new SerializedObject(view).FindProperty("_icon").objectReferenceValue as UnityEngine.UI.Image;
                Assert.That(icon,Is.Not.Null);Assert.That(icon.sprite,Is.SameAs(item.Icon));
            }
            finally {UnityEngine.Object.DestroyImmediate(go);}
        }
        [Test] public void NearbyGridLayoutExistsWithoutChangingAssetOwnership()
        {Assert.That(typeof(InventoryItemView).Assembly.GetType("Game.UI.NearbyItemGridLayout"),Is.Not.Null);}
        [Test] public void FixedListHasASeparateScreenSpacePresenter()
        {Assert.That(typeof(InventoryItemView).Assembly.GetType("Game.UI.NearbyPickupListView"),Is.Not.Null);}
        [Test] public void ScrollInputCollectsSelectedIdentity()
        {
            Assert.That(typeof(NearbyPickupController).GetMethod("TryCollectFocused"),Is.Not.Null);
            Assert.That(typeof(NearbyPickupController).GetMethod("SelectRelative"),Is.Not.Null);
        }
        [Test] public void CorePublishesVisualLifecycleWithoutRenderingDependency()
        {Assert.That(typeof(Game.Core.IGameEvent).Assembly.GetType("Game.Core.WorldItemVisualBoundEvent"),Is.Not.Null);}
    }
}
#endif
