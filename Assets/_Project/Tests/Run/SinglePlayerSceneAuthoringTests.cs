using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Game.Character;
using Game.ElementField;

namespace Game.Run.Tests
{
    /// <summary>直接检查最终装配资产，不用灰盒成功结果冒充正式地图接线。</summary>
    public sealed class SinglePlayerSceneAuthoringTests
    {
        private const string HubPath = "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity";
        private Scene _scene;

        private void Load()
        {
            Assert.That(System.IO.File.Exists(HubPath), Is.True, "正式据点Scene尚未创建。");
            _scene = EditorSceneManager.OpenScene(HubPath, OpenSceneMode.Additive);
        }

        private T Find<T>() where T : Component
        {
            foreach (var root in _scene.GetRootGameObjects())
            { var component = root.GetComponentInChildren<T>(true); if (component != null) return component; }
            return null;
        }

        [TearDown] public void Cleanup()
        { if (_scene.IsValid() && _scene.isLoaded) EditorSceneManager.CloseScene(_scene, true); }

        [Test] public void FormalHubHasProductionInventoryAndIndependentConfiguration()
        {
            Load(); var session = Find<SinglePlayerInventorySession>();
            Assert.That(session, Is.Not.Null); var serialized = new SerializedObject(session);
            Assert.That(serialized.FindProperty("_sandboxProfile").boolValue, Is.False);
            foreach (string name in new[] { "_catalog", "_startingInventory" })
            {
                var asset = serialized.FindProperty(name).objectReferenceValue;
                Assert.That(AssetDatabase.GetAssetPath(asset), Does.StartWith("Assets/_Project/ScriptableObjects/World/Hub/Inventory/"));
                foreach (string dependency in AssetDatabase.GetDependencies(AssetDatabase.GetAssetPath(asset), true))
                    Assert.That(dependency, Does.Not.StartWith("Assets/_Project/Tests/Fixtures/Run/"), dependency);
            }
            var catalog = (ItemCatalog)serialized.FindProperty("_catalog").objectReferenceValue;
            var seeds = (SinglePlayerStartingInventory)serialized.FindProperty("_startingInventory").objectReferenceValue;
            Assert.That(catalog.TryCreateIndex(out var index, out string error), Is.True, error);
            Assert.That(seeds.TryCreate(index, out var profile, out error), Is.True, error);
            Assert.That(profile.Currency, Is.Zero);
            foreach (var entry in profile.Items) if (entry.Item.Composition != null)
                Assert.That(entry.Item.Composition.Total, Is.Zero, "新档不能从测试样本获得免费材料。");
        }

        [Test] public void FormalHubHasFourDistinctReachableStationsAndExplicitExperiment()
        {
            Load(); var interaction = Find<HubInteractionController>(); Assert.That(interaction, Is.Not.Null);
            var definition = (HubDefinition)new SerializedObject(interaction).FindProperty("_definition").objectReferenceValue;
            Assert.That(definition.Stations.Length, Is.EqualTo(4));
            for (int i = 0; i < definition.Stations.Length; i++)
            {
                Assert.That(definition.TryFindNearest(definition.Stations[i].Position, out var station), Is.True);
                Assert.That(station.Kind, Is.EqualTo(definition.Stations[i].Kind));
                for (int j = i + 1; j < definition.Stations.Length; j++) Assert.That(definition.Stations[i].Kind, Is.Not.EqualTo(definition.Stations[j].Kind));
            }
            Assert.That(definition.ContainsExperiment(definition.ExperimentCenter), Is.True);
            Assert.That(definition.ContainsExperiment(Find<WizardController>().transform.position), Is.False, "出生在普通整备区，不默认进入实验。");
        }

        [Test] public void FormalHubHasOneGameCameraOneWorldAndNoLegacyReflection()
        {
            Load(); int cameras = 0, worlds = 0, listeners = 0, maps = 0;
            foreach (var root in _scene.GetRootGameObjects())
            {
                foreach (var camera in root.GetComponentsInChildren<Camera>(true)) if (camera.enabled) cameras++;
                worlds += root.GetComponentsInChildren<ElementWorldRuntime>(true).Length;
                listeners += root.GetComponentsInChildren<AudioListener>(true).Length;
                foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
                    if (component != null) Assert.That(component.GetType().Name, Is.Not.EqualTo("Reflection_pass"));
                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                {
                    if(transform.name=="Map_1")maps++;
                    Assert.That(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject), Is.Zero);
                }
            }
            Assert.That(cameras, Is.EqualTo(1)); Assert.That(worlds, Is.EqualTo(1));
            Assert.That(listeners, Is.EqualTo(1)); Assert.That(maps, Is.EqualTo(1));
            Assert.That(Find<ContainerSandboxMaterialAuthoring>(), Is.Null);
        }

    }
}
