#if UNITY_EDITOR
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Run.Tests
{
    public sealed class HomeSceneContractTests
    {
        private const string Home="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Home.unity";
        [Test] public void HomeHasStandaloneSavedSceneAndBuildEntry()
        {
            Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(Home),Is.Not.Null,"家需要独立作者场景");
            Assert.That(System.Array.Exists(EditorBuildSettings.scenes,s=>s.enabled&&s.path==Home),Is.True,"Build必须能进入家");
        }
    }
}
#endif
