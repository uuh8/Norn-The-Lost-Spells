#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Game.Run;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Game.Run.Tests
{
    /// <summary>Authoring契约先证明旧工作区缺少HUD与直接试玩入口；不加载大地图。</summary>
    public sealed class PracticeModernUiContractTests
    {
        private static Type SetupType => Type.GetType("Game.EditorTools.SinglePlayerPracticeUiSetup,Game.Project.Editor",true);
        [TestCase("Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity", "", false)]
        [TestCase("Assets/_Project/Scenes/P7_DemoRun.unity", "", false)]
        [TestCase("Assets/_Project/Scenes/P8_BossField.unity", "", false)]
        [TestCase("Assets/_Project/Scenes/P7_DemoRun.unity", "Assets/_Project/Scenes/P8_BossField.unity", true)]
        [TestCase("Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity", "Assets/_Project/Scenes/P7_DemoRun.unity", true)]
        [TestCase("Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity", "Assets/_Project/Scenes/Tests/Hub/P8_BossField_ModernUiReview.unity", true)]
        public void IndependentMapPlayRejectsOnlyMultipleGameplayScenes(string first,string second,bool expected)
        {
            // Editor 工具不能成为 Run 的运行时依赖；反射只在测试端检查正式地图与 Review 的同一边界。
            var guard=SetupType.GetMethod("HasIndependentSceneConflict",BindingFlags.Static|BindingFlags.NonPublic);
            Assert.That(guard,Is.Not.Null);
            Assert.That(guard.Invoke(null,new object[]{new[]{first,second}}),Is.EqualTo(expected));
        }
        [Test] public void RetiredBatchCommandLeavesLoadedScenesAndFormalFilesUnchanged()
        {
            const string request="Temp/SinglePlayer/practice-modern-ui.request";
            Assert.That(File.Exists(request),Is.False,"不覆盖玩家或其他工具尚未处理的请求");
            var original=SceneManager.GetActiveScene();int count=SceneManager.sceneCount;
            var handles=new int[count];var dirty=new bool[count];
            for(int i=0;i<count;i++){handles[i]=SceneManager.GetSceneAt(i).handle;dirty[i]=SceneManager.GetSceneAt(i).isDirty;}
            var paths=new[]{"Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity","Assets/_Project/Scenes/P7_DemoRun.unity","Assets/_Project/Scenes/P8_BossField.unity"};
            var contents=new byte[paths.Length][];
            for(int i=0;i<paths.Length;i++)contents[i]=File.ReadAllBytes(paths[i]);
            try
            {
                Directory.CreateDirectory("Temp/SinglePlayer");File.WriteAllText(request,"apply-maps");
                LogAssert.Expect(LogType.Error,new Regex("已移除同时打开两张正式地图"));
                SetupType.GetMethod("Poll",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);
                Assert.That(File.Exists(request),Is.False);
                Assert.That(SceneManager.sceneCount,Is.EqualTo(count));
                Assert.That(SceneManager.GetActiveScene().handle,Is.EqualTo(original.handle));
                for(int i=0;i<count;i++)
                {Assert.That((int)SceneManager.GetSceneAt(i).handle,Is.EqualTo(handles[i]));Assert.That(SceneManager.GetSceneAt(i).isDirty,Is.EqualTo(dirty[i]));}
                for(int i=0;i<paths.Length;i++)CollectionAssert.AreEqual(contents[i],File.ReadAllBytes(paths[i]),paths[i]);
            }
            finally{if(File.Exists(request))File.Delete(request);}
        }
        [Test] public void SharedWorkspaceContainsModernHeldHudAndWhiteDot()
        {
            var asset=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/SinglePlayer/PracticeInventoryWorkspace.prefab");
            Assert.That(asset,Is.Not.Null);
            Assert.That(asset.GetComponentInChildren<HeldItemHud>(true),Is.Not.Null,"练习缺少同源十格手持HUD");
            Assert.That(asset.transform.Find("PracticeContainerHUD"),Is.Not.Null,"共用容器数量HUD也应带入练习图");
            var dot=asset.transform.Find("PracticeHeldHUD/ScreenCenterDot");
            Assert.That(dot,Is.Not.Null);
            var rect=dot.GetComponent<RectTransform>();
            Assert.That(rect.sizeDelta,Is.EqualTo(new Vector2(5,5)));
        }
        [Test] public void DirectPlayBootstrapAndRewardBridgeExist()
        {
            Assert.That(typeof(PracticeInventoryWorkspace).Assembly.GetType("Game.Run.PracticeInventorySceneBootstrap"),Is.Not.Null);
            Assert.That(typeof(PracticeInventoryWorkspace).Assembly.GetType("Game.Run.PracticeSpellRewardAdapter"),Is.Not.Null);
        }
        [Test] public void EveryFullLibrarySpellHasOneCanonicalDefinitionAndKnownSaveAliases()
        {
            var catalog=AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/_Project/ScriptableObjects/World/Hub/Inventory/Catalog.asset");
            var library=AssetDatabase.LoadAssetAtPath<Game.Skills.SpellLibrary>("Assets/_Project/ScriptableObjects/Spell/Libraries/SpellLibrary_Player.asset");
            Assert.That(library,Is.Not.Null);Assert.That(catalog,Is.Not.Null);
            var entries=new SerializedObject(catalog).FindProperty("_definitions");
            foreach(var spell in library.Available)
            {
                int count=0;
                for(int i=0;i<entries.arraySize;i++)
                {
                    var item=(ItemDefinition)entries.GetArrayElementAtIndex(i).objectReferenceValue;
                    if(item.SpellDefinition!=spell)continue;
                    // 去重不能让已经发放的物品ID失效；仅这两个明确旧ID允许作为兼容别名。
                    if(item.ContentId=="norn.v2.double"||item.ContentId=="norn.v2.damage")
                    {Assert.That(item.Icon,Is.SameAs(spell.Icon));Assert.That(item.DisplayName,Is.EqualTo(spell.DisplayName));continue;}
                    count++;
                }
                Assert.That(count,Is.EqualTo(1),spell.name+" 需要可持有的物品映射");
            }
        }
    }
}
#endif
