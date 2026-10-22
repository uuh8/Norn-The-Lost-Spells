#if UNITY_EDITOR
using System.IO;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Run.Tests
{
    /// <summary>约束正式玩家入口，避免旧开局发卡与据点携带系统重新同时装配。</summary>
    public sealed class HubCenteredArchitectureTests
    {
        [Test]
        public void RuntimeAssetFilesUseUnderscoresInsteadOfDottedIdentifiers()
        {
            foreach (string file in Directory.GetFiles("Assets/_Project/ScriptableObjects", "*.asset", SearchOption.AllDirectories))
            {
                Assert.That(Path.GetFileNameWithoutExtension(file), Does.Not.Contain("."), file);
                foreach (string segment in Path.GetFileNameWithoutExtension(file).Split('_'))
                    Assert.That(segment.Length == 0 || !char.IsLower(segment[0]), Is.True, "英文分段采用 PascalCase：" + file);
            }
        }

        [TestCase("Assets/_Project/Scenes/P7_DemoRun.unity")]
        [TestCase("Assets/_Project/Scenes/P8_BossField.unity")]
        public void DungeonUsesCarriedInventoryWithoutLegacyStarterSession(string path)
        {
            Scene scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            try
            {
                bool bootstrap = false;
                foreach (GameObject root in scene.GetRootGameObjects())
                    foreach (MonoBehaviour component in root.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (component == null) continue;
                        string type = component.GetType().Name;
                        Assert.That(new[] { "RunSpellSession", "RunSessionCoordinator", "WandEditorController" }, Does.Not.Contain(type), path);
                        bootstrap |= component is PracticeInventorySceneBootstrap;
                    }
                Assert.That(bootstrap, Is.True, "直接 Editor 试玩与据点传送共用库存 Bootstrap。");
            }
            finally { EditorSceneManager.CloseScene(scene, true); }
        }

        [Test]
        public void MainMenuStartsAtHub()
        {
            Scene scene = EditorSceneManager.OpenScene("Assets/_Project/Scenes/P7_MainMenu.unity", OpenSceneMode.Additive);
            try
            {
                DemoMainMenuController menu = null;
                foreach (GameObject root in scene.GetRootGameObjects())
                    if ((menu = root.GetComponentInChildren<DemoMainMenuController>(true)) != null) break;
                Assert.That(menu, Is.Not.Null);
                Assert.That(new SerializedObject(menu).FindProperty("_hubSceneName").stringValue, Is.EqualTo("SinglePlayer_Hub"));
            }
            finally { EditorSceneManager.CloseScene(scene, true); }
        }

        [Test]
        public void ItemAuthoringReusesCanonicalFilesAndKeepsModelBasedContentIds()
        {
            var setup=System.Type.GetType("Game.EditorTools.SinglePlayerItemPickupSetup,Game.Project.Editor",true);
            var resolve=setup.GetMethod("DefinitionPath",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static);
            Assert.That(resolve,Is.Not.Null);int checkedCount=0;
            foreach(string file in Directory.GetFiles("Assets/_Project/Art/Prefabs/Item","*.prefab",SearchOption.AllDirectories))
            {
                string folder=Path.GetFileName(Path.GetDirectoryName(file));
                if(folder!="Food" && folder!="Herb" && folder!="Container")continue;
                string name=Path.GetFileNameWithoutExtension(file);
                string path=(string)resolve.Invoke(null,new object[]{folder,name});
                var definition=AssetDatabase.LoadAssetAtPath<ItemDefinition>(path);
                Assert.That(definition,Is.Not.Null,path);
                Assert.That(definition.ContentId,Is.EqualTo("item."+folder.ToLowerInvariant()+"."+name.ToLowerInvariant()));
                Assert.That(definition.WorldModel,Is.Not.Null,path);checkedCount++;
            }
            Assert.That(checkedCount,Is.EqualTo(18),"测试必须覆盖实际三类模型，不是空目录通过。");
        }
    }
}
#endif
