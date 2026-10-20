#if UNITY_EDITOR
using Game.Run;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.Run.Tests
{
    public sealed class ItemPickupAuthoringTests
    {
        internal static System.Collections.Generic.IEnumerable<string> ModelDefinitionGuids()
        {
            // 此契约针对既有18份模型道具；背包和实验用容器有自己的图标/Shape契约。
            foreach(var guid in AssetDatabase.FindAssets("t:ItemDefinition",new[]{"Assets/_Project/ScriptableObjects/Items"}))
            {
                string path=AssetDatabase.GUIDToAssetPath(guid);
                string folder=System.IO.Path.GetDirectoryName(path).Replace('\\','/');
                if(folder=="Assets/_Project/ScriptableObjects/Items/Food"||folder=="Assets/_Project/ScriptableObjects/Items/Herb"
                    ||folder=="Assets/_Project/ScriptableObjects/Items/Containers")yield return guid;
            }
        }
        [Test] public void AllEighteenDefinitionsHaveRealModelsAndValidShapeRules()
        {
            var guids=new System.Collections.Generic.List<string>(ModelDefinitionGuids());Assert.That(guids.Count,Is.EqualTo(18));
            foreach(var guid in guids){var item=AssetDatabase.LoadAssetAtPath<ItemDefinition>(AssetDatabase.GUIDToAssetPath(guid));Assert.That(item.WorldModel,Is.Not.Null,item.name);Assert.That(item.TryCreateRules(out _,out var error),Is.True,error);}
        }
        [Test] public void ReviewHasNoFoldAndUsesUserPickupSpriteAndAutoScroll()
        {
            var scene=EditorSceneManager.OpenPreviewScene("Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_ItemReview.unity");
            try
            {
                int panels=0,prompts=0,scrolls=0;
                foreach(var root in scene.GetRootGameObjects())
                {
                    foreach(var panel in root.GetComponentsInChildren<WandProgramPanelView>(true)){panels++;Assert.That(panel.transform.Find("Fold"),Is.Null);Assert.That(panel.transform.Find("Expanded").gameObject.activeSelf,Is.True);}
                    scrolls+=root.GetComponentsInChildren<InventoryDragAutoScroll>(true).Length;
                    foreach(var prompt in root.GetComponentsInChildren<WorldPickupPromptView>(true))
                    {prompts++;var image=prompt.GetComponentInChildren<UnityEngine.UI.Image>(true);Assert.That(AssetDatabase.GetAssetPath(image.sprite),Is.EqualTo("Assets/_Project/Art/UI/Funtion_UI2/PickupItemEntry.png"));}
                }
                Assert.That(panels,Is.EqualTo(3));Assert.That(prompts,Is.EqualTo(1));Assert.That(scrolls,Is.EqualTo(1));
            }
            finally{EditorSceneManager.ClosePreviewScene(scene);}
        }
    }
}
#endif
