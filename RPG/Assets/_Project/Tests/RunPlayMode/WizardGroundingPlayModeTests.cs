#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Game.Character;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Game.Run.Tests
{
    /// <summary>在真实 Hub 对可见鞋底做测量；胶囊体、骨骼或阴影均不能代替实际网格接地。</summary>
    public sealed class WizardGroundingPlayModeTests
    {
        private const string Hub = "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity";
        private InputTestFixture _input;
        private bool _background;
        private Scene _hub;
        private readonly MemoryFiles _files = new MemoryFiles();

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _background = Application.runInBackground;
            Application.runInBackground = true;
            Time.timeScale = 1;
            _input = new InputTestFixture(); _input.Setup(); InputSystem.AddDevice<Keyboard>();
            SceneManager.sceneLoaded += OnLoaded;
            yield return EditorSceneManager.LoadSceneInPlayMode(Hub, new LoadSceneParameters(LoadSceneMode.Single));
            _hub = SceneManager.GetActiveScene();
            // Idle 动画和 CharacterController 均需先稳定，不能把出生帧的离地当成最终接地。
            yield return new WaitForSeconds(1.2f);
        }

        private void OnLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.path != Hub) return;
            foreach (var root in scene.GetRootGameObjects())
            foreach (var session in root.GetComponentsInChildren<SinglePlayerInventorySession>(true))
            {
                var data = new SerializedObject(session);
                var catalog = (ItemCatalog)data.FindProperty("_catalog").objectReferenceValue;
                var seeds = (SinglePlayerStartingInventory)data.FindProperty("_startingInventory").objectReferenceValue;
                // sceneLoaded 早于 Start；注入内存文件系统，测试不读取或覆盖玩家真实存档。
                Assert.That(session.ConfigureAndInitialize(catalog, seeds, "Temp/SinglePlayer/GroundingMemory", _files), Is.True);
            }
        }

        [UnityTest]
        public IEnumerator Idle_ActualShoeMeshStaysCloseToGround()
        {
            WizardController player = null;
            foreach (var root in _hub.GetRootGameObjects())
            {
                player = root.GetComponentInChildren<WizardController>();
                if (player != null) break;
            }
            Assert.That(player, Is.Not.Null);
            SkinnedMeshRenderer shoe = null;
            foreach (var renderer in player.GetComponentsInChildren<SkinnedMeshRenderer>())
                if (renderer.enabled && renderer.name == "Shoe1") { shoe = renderer; break; }
            Assert.That(shoe, Is.Not.Null, "必须测实际启用的 Shoe1，不能用骨骼高度代替鞋底。");
            var samples = new GroundingSample[4];
            var mesh = new Mesh();
            try
            {
                for (int i = 0; i < samples.Length; i++)
                {
                    yield return new WaitForEndOfFrame();
                    shoe.BakeMesh(mesh);
                    var vertices = mesh.vertices;
                    var sole = new Vector3(0, float.PositiveInfinity, 0);
                    for (int v = 0; v < vertices.Length; v++)
                    {
                        var world = shoe.transform.TransformPoint(vertices[v]);
                        if (world.y < sole.y) sole = world;
                    }
                    var hits = Physics.RaycastAll(sole + Vector3.up * .6f, Vector3.down, 4,
                        ~0, QueryTriggerInteraction.Ignore);
                    float ground = float.NegativeInfinity;
                    string groundName = null;
                    for (int h = 0; h < hits.Length; h++)
                    {
                        var hit = hits[h];
                        if (hit.collider.transform.IsChildOf(player.transform) || hit.point.y > sole.y + .03f) continue;
                        if (hit.point.y > ground) { ground = hit.point.y; groundName = hit.collider.name; }
                    }
                    Assert.That(float.IsNegativeInfinity(ground), Is.False, "鞋底下方必须命中真实场景 Collider。");
                    var cc = player.CharacterController;
                    var animator = player.Animator;
                    samples[i] = new GroundingSample
                    {
                        RootY = player.transform.position.y,
                        CapsuleBottomY = player.transform.TransformPoint(cc.center).y - cc.height * .5f,
                        BoundsMinY = cc.bounds.min.y,
                        ShoeMinY = sole.y, GroundY = ground, Gap = sole.y - ground,
                        ModelLocalY = animator.transform.localPosition.y,
                        AnimatorRootY = animator.rootPosition.y,
                        AnimatorStateHash = animator.GetCurrentAnimatorStateInfo(0).shortNameHash,
                        ApplyRootMotion = animator.applyRootMotion, GroundCollider = groundName
                    };
                    yield return new WaitForSeconds(.27f);
                }
                Directory.CreateDirectory("Temp/SinglePlayer");
                File.WriteAllText("Temp/SinglePlayer/wizard-grounding.json", JsonUtility.ToJson(new GroundingReport { Samples = samples }, true));
                for (int i = 0; i < samples.Length; i++)
                    Assert.That(samples[i].Gap, Is.InRange(-.045f, .045f),
                        "Idle 鞋底距真实地面的间隙超标；见 Temp/SinglePlayer/wizard-grounding.json sample " + i);
            }
            finally { UnityEngine.Object.Destroy(mesh); }
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            SceneManager.sceneLoaded -= OnLoaded;
            var cleanup = SceneManager.CreateScene("WizardGroundingCleanup");
            SceneManager.SetActiveScene(cleanup);
            if (_hub.IsValid() && _hub.isLoaded) yield return SceneManager.UnloadSceneAsync(_hub);
            _input?.TearDown(); _input = null;
            Application.runInBackground = _background; Time.timeScale = 1;
        }

        [Serializable] private sealed class GroundingReport { public GroundingSample[] Samples; }
        [Serializable] private struct GroundingSample
        {
            public float RootY, CapsuleBottomY, BoundsMinY, ShoeMinY, GroundY, Gap, ModelLocalY, AnimatorRootY;
            public int AnimatorStateHash;
            public bool ApplyRootMotion;
            public string GroundCollider;
        }
        private sealed class MemoryFiles : IProfileFileSystem
        {
            private readonly Dictionary<string, string> _data = new Dictionary<string, string>();
            public bool Exists(string path) => _data.ContainsKey(path);
            public string Read(string path) => _data[path];
            public void WriteFlushed(string path, string content) => _data[path] = content;
            public void Move(string source, string target) { _data[target] = _data[source]; _data.Remove(source); }
            public void Replace(string source, string target, string backup) { _data[backup] = _data[target]; Move(source, target); }
        }
    }
}
#endif
