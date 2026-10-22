#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Game.Run.Tests
{
    /// <summary>真正驱动Session.Start而非注入Initialize，验证默认模式和同Play换Scene的状态边界。</summary>
    public sealed class EditorScenePreviewSessionPlayModeTests
    {
        private Scene _scene,_previous;
        private string _key;
        private SinglePlayerInventorySession _session;
        private ItemCatalog _catalog;
        private SinglePlayerStartingInventory _seeds;
        [UnitySetUp]public IEnumerator Setup()
        {
            _previous=SceneManager.GetActiveScene();_scene=SceneManager.CreateScene("PreviewLifecycle_"+Guid.NewGuid().ToString("N"));SceneManager.SetActiveScene(_scene);
            _key="PreviewTest_"+Guid.NewGuid().ToString("N");
            _catalog=AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/_Project/ScriptableObjects/World/Hub/Inventory/Catalog.asset");
            _seeds=AssetDatabase.LoadAssetAtPath<SinglePlayerStartingInventory>("Assets/_Project/ScriptableObjects/World/Hub/Inventory/StartingInventory.asset");
            _session=MakeSession();yield return null;yield return null;Assert.That(_session.ReadOnly,Is.Not.Null,_session.InitializationMessage);
        }
        private SinglePlayerInventorySession MakeSession()
        {
            var result=new GameObject("Preview inventory").AddComponent<SinglePlayerInventorySession>();var data=new SerializedObject(result);
            data.FindProperty("_catalog").objectReferenceValue=_catalog;data.FindProperty("_startingInventory").objectReferenceValue=_seeds;
            data.FindProperty("_sandboxName").stringValue=_key;data.ApplyModifiedPropertiesWithoutUndo();return result;
        }
        [UnityTest]public IEnumerator StartDefaultsToIsolatedPreviewAndSamePlayReopenKeepsState()
        {
            Assert.That(_session.IsScenePreview,Is.True);string path=_session.ProfileStoragePath;Assert.That(File.Exists(path),Is.False);
            long original=_session.Currency;_session.Profile.Currency+=123;Assert.That(_session.Save().Success,Is.True);
            UnityEngine.Object.Destroy(_session.gameObject);yield return null;_session=MakeSession();yield return null;yield return null;
            Assert.That(_session.Currency,Is.EqualTo(original+123));Assert.That(File.Exists(path),Is.False,"真实Repository的提交也只能写入内存边界。");
            EditorScenePreviewFileSystem.ResetForPlay();UnityEngine.Object.Destroy(_session.gameObject);yield return null;
            // Destroy会保存本轮临时档；真正SubsystemRegistration发生在上一轮结束以后，按相同顺序模拟。
            EditorScenePreviewFileSystem.ResetForPlay();_session=MakeSession();yield return null;yield return null;
            Assert.That(_session.Currency,Is.EqualTo(original));Assert.That(File.Exists(path),Is.False);
        }
        [UnityTest]public IEnumerator ExplicitSavedRepositoryStillLoadsPersistentState()
        {
            string directory=Path.GetFullPath("Temp/SinglePlayer/PreviewSavedMode/"+Guid.NewGuid().ToString("N"));
            try
            {
                var first=new GameObject("Saved inventory").AddComponent<SinglePlayerInventorySession>();
                Assert.That(first.ConfigureAndInitialize(_catalog,_seeds,directory),Is.True);Assert.That(first.IsScenePreview,Is.False);
                first.Profile.Currency+=321;long expected=first.Currency;Assert.That(first.Save().Success,Is.True);UnityEngine.Object.Destroy(first.gameObject);yield return null;
                var second=new GameObject("Saved reopen").AddComponent<SinglePlayerInventorySession>();
                Assert.That(second.ConfigureAndInitialize(_catalog,_seeds,directory),Is.True);Assert.That(second.Currency,Is.EqualTo(expected));Assert.That(second.IsScenePreview,Is.False);
                UnityEngine.Object.Destroy(second.gameObject);yield return null;
            }
            finally
            {
                string root=Path.GetFullPath("Temp/SinglePlayer/PreviewSavedMode")+Path.DirectorySeparatorChar;
                if(directory.StartsWith(root,StringComparison.OrdinalIgnoreCase) && Directory.Exists(directory))Directory.Delete(directory,true);
            }
        }
        [UnityTest]public IEnumerator InitialGoldOnlySeedsNewPreviewAndNextPlayResetsPickedIdentity()
        {
            var template=UnityEngine.Object.Instantiate(_seeds);
            try
            {
                var data=new SerializedObject(template);data.FindProperty("_initialCurrency").longValue=5000;data.ApplyModifiedPropertiesWithoutUndo();
                UnityEngine.Object.Destroy(_session.gameObject);yield return null;EditorScenePreviewFileSystem.ResetForPlay();_seeds=template;
                _session=MakeSession();yield return null;yield return null;Assert.That(_session.Currency,Is.EqualTo(5000));
                _catalog.TryCreateIndex(out var index,out _);index.TryGet("item.food.pumpkin",out var pumpkin);
                var item=RuntimeItemState.Create(pumpkin.ContentId);Assert.That(_session.Profile.Items.TryAdd(item,ItemLocation.Warehouse),Is.True);
                _session.Profile.Currency=4321;Assert.That(_session.Save().Success,Is.True);string path=_session.ProfileStoragePath;
                UnityEngine.Object.Destroy(_session.gameObject);yield return null;_session=MakeSession();yield return null;yield return null;
                Assert.That(_session.Currency,Is.EqualTo(4321));Assert.That(_session.Profile.Items.TryGet(item.InstanceId,out _),Is.True);
                UnityEngine.Object.Destroy(_session.gameObject);yield return null;EditorScenePreviewFileSystem.ResetForPlay();
                _session=MakeSession();yield return null;yield return null;
                Assert.That(_session.Currency,Is.EqualTo(5000));Assert.That(_session.Profile.Items.TryGet(item.InstanceId,out _),Is.False);
                Assert.That(File.Exists(path),Is.False);
            }
            finally{UnityEngine.Object.Destroy(template);}
        }
        [UnityTearDown]public IEnumerator TearDown(){Time.timeScale=1;SceneManager.SetActiveScene(_previous);yield return SceneManager.UnloadSceneAsync(_scene);}
    }
}
#endif
