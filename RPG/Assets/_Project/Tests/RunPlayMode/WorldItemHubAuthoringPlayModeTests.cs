#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Security.Cryptography;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Game.Run.Tests
{
    /// <summary>直接读取用户已保存Hub，运行时验证默认试玩和Test模板；不保存或重排源Scene。</summary>
    public sealed class WorldItemHubAuthoringPlayModeTests
    {
        private const string Path="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity";
        private const string Evidence="TestResults/SinglePlayer/M6-T1-WorldItemLifecycle";
        private Scene _scene;
        private byte[] _hash;
        private bool _background;
        [UnityTest] public IEnumerator SavedHubStartsPreviewAndTestModelsMatchRuntimeWithoutSceneWrites()
        {
            _background=Application.runInBackground;Application.runInBackground=true;Time.timeScale=1;
            using(var sha=SHA256.Create())_hash=sha.ComputeHash(File.ReadAllBytes(Path));
            _scene=EditorSceneManager.LoadSceneInPlayMode(Path,new LoadSceneParameters(LoadSceneMode.Additive));
            for(int i=0;i<8;i++)yield return null;
            SinglePlayerInventorySession session=null;GameObject test=null;
            foreach(var root in _scene.GetRootGameObjects())
            {if(root.name=="Test")test=root;if(session==null)session=root.GetComponentInChildren<SinglePlayerInventorySession>(true);}
            Assert.That(session,Is.Not.Null);Assert.That(session.IsScenePreview,Is.True,session.InitializationMessage);
            Assert.That(test,Is.Not.Null,"本例验证用户保存的Test作者布局。");
            int checkedModels=0;var report=new System.Text.StringBuilder(DateTime.UtcNow.ToString("O")+"\n"+session.ProfileModeDescription+"\nVirtual key: "+session.ProfileStoragePath+"\n");
            foreach(var point in test.GetComponentsInChildren<WorldItemSpawnPoint>(true))
            {
                if(!point.enabled || !point.gameObject.activeInHierarchy || string.IsNullOrWhiteSpace(point.SpawnId))continue;
                var proxy=session.FindWorldProxy(point.RuntimeInstanceId);Assert.That(proxy,Is.Not.Null,point.name+" / "+point.SpawnId);
                Assert.That(point.AuthorVisualHidden,Is.True,point.name);Assert.That(proxy.transform.parent.name,Is.EqualTo("WorldItems_Runtime"));
                Assert.That(Vector3.Distance(proxy.transform.position,point.transform.position),Is.LessThan(.002f),point.name);
                var expected=RenderBounds(point.gameObject);var actual=RenderBounds(proxy.transform.Find("Visual").gameObject);
                Assert.That(Vector3.Distance(expected.size,actual.size),Is.LessThan(.005f),point.name+"的实际场景尺寸应被继承。");
                report.AppendLine(point.name+" / Scale "+point.transform.lossyScale+" / bounds "+actual.size+" / "+point.RuntimeInstanceId);checkedModels++;
            }
            Assert.That(checkedModels,Is.GreaterThan(0));Directory.CreateDirectory(Evidence);File.WriteAllText(Evidence+"/hub-authoring.txt",report.ToString());
            yield return new WaitForEndOfFrame();var image=ScreenCapture.CaptureScreenshotAsTexture();File.WriteAllBytes(Evidence+"/hub-initial-preview.png",image.EncodeToPNG());UnityEngine.Object.Destroy(image);
        }
        private static Bounds RenderBounds(GameObject root)
        {
            var renderers=root.GetComponentsInChildren<Renderer>(true);Assert.That(renderers.Length,Is.GreaterThan(0));
            var result=renderers[0].bounds;for(int i=1;i<renderers.Length;i++)result.Encapsulate(renderers[i].bounds);return result;
        }
        [UnityTearDown]public IEnumerator TearDown()
        {
            Time.timeScale=1;if(_scene.IsValid() && _scene.isLoaded)yield return SceneManager.UnloadSceneAsync(_scene);
            Application.runInBackground=_background;
            if(_hash!=null)using(var sha=SHA256.Create())Assert.That(sha.ComputeHash(File.ReadAllBytes(Path)),Is.EqualTo(_hash),"试玩不能改写用户Scene文件。");
        }
    }
}
#endif
