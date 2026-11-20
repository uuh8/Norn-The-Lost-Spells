using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;
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
            // 当前模板允许可配置测试金币；验证真实配置传递，不用旧零值前提重写作者资产。
            long configuredGold = new SerializedObject(seeds).FindProperty("_initialCurrency").longValue;
            Assert.That(configuredGold, Is.GreaterThanOrEqualTo(0));
            Assert.That(profile.Currency, Is.EqualTo(configuredGold));
            // 正式新档模板已允许带材料的容器；逐份比对作者配置，防止凭空补料、丢料或同类实例串料。
            // 比较无序多重集，不把Dictionary枚举顺序或新生成的InstanceId当作模板合同。
            var expectedContents = new List<string>();
            var configuredItems = new SerializedObject(seeds).FindProperty("_items");
            for (int i = 0; i < configuredItems.arraySize; i++)
            {
                var seed = configuredItems.GetArrayElementAtIndex(i);
                var definition = (ItemDefinition)seed.FindPropertyRelative("Definition").objectReferenceValue;
                Assert.That(index.TryGetRules(definition.ContentId, out var rules), Is.True);
                if (rules.Kind != ItemKind.Container) continue;
                var quantities = new ulong[8];
                var materials = seed.FindPropertyRelative("Materials");
                for (int j = 0; j < materials.arraySize; j++)
                {
                    var material = materials.GetArrayElementAtIndex(j);
                    int key = material.FindPropertyRelative("MaterialKey").intValue;
                    Assert.That(key, Is.InRange(1, 8));
                    quantities[key - 1] += material.FindPropertyRelative("Quantity").ulongValue;
                }
                expectedContents.Add(definition.ContentId + ":" + string.Join(",", quantities));
            }
            var actualContents = new List<string>();
            foreach (var entry in profile.Items)
            {
                if (entry.Item.Composition == null) continue;
                var quantities = new ulong[8];
                for (byte key = 1; key <= 8; key++) quantities[key - 1] = entry.Item.Composition.GetQuantity(key);
                actualContents.Add(entry.Item.ContentId + ":" + string.Join(",", quantities));
            }
            Assert.That(profile.Items.Count, Is.EqualTo(configuredItems.arraySize), "只创建模板声明的实例数量。");
            Assert.That(actualContents, Is.EquivalentTo(expectedContents), "每份容器的材料必须与正式模板相符，不能默认补满或清空。");
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

        [Test] public void FormalHubHasOneGameCameraNoElementWorldAndNoMissingScripts()
        {
            Load(); int cameras = 0, worlds = 0, listeners = 0, maps = 0;
            foreach (var root in _scene.GetRootGameObjects())
            {
                foreach (var camera in root.GetComponentsInChildren<Camera>(true)) if (camera.enabled) cameras++;
                worlds += root.GetComponentsInChildren<ElementWorldRuntime>(true).Length;
                listeners += root.GetComponentsInChildren<AudioListener>(true).Length;
                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                {
                    if (transform.name == "Map_1")
                    {
                        maps++;
                        Assert.That(transform.parent, Is.Not.Null, "正式地图保留在作者的__Level根下。");
                        Assert.That(transform.parent.name, Is.EqualTo("__Level"));
                    }
                    Assert.That(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject), Is.Zero);
                }
            }
            Assert.That(cameras, Is.EqualTo(1), "正式Hub只使用一台启用的游戏Camera。");
            // M6-T22已将实验材料世界迁至Home；Hub只负责整备与入口，不能重新装回并行模拟Owner。
            Assert.That(worlds, Is.Zero, "Hub不运行ElementWorld；Home世界合同由HomeElementProductionContractTests检查。");
            Assert.That(listeners, Is.EqualTo(1), "正式Hub只保留一个AudioListener。");
            Assert.That(maps, Is.EqualTo(1), "保留唯一的正式Map_1内容根。");
            Assert.That(Find<ContainerSandboxMaterialAuthoring>(), Is.Null);
        }

        [Test] public void ExistingLegacyReflectionIsInertUnderSrpWithAValidCurrentCamera()
        {
            Load(); Assert.That(GraphicsSettings.currentRenderPipeline, Is.Not.Null, "此门检查项目真实SRP环境，不切换全局RenderPipeline。");
            MonoBehaviour authored = null;
            foreach (var root in _scene.GetRootGameObjects())
                foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
                    if (component != null && component.GetType().Name == "Reflection_pass") { authored = component; break; }
            Assert.That(authored, Is.Not.Null, "使用正式Hub中现有兼容组件的真实类型。");
            var authoredRenderer = authored.GetComponent<Renderer>();
            Assert.That(authoredRenderer, Is.Not.Null); Assert.That(authoredRenderer.sharedMaterial, Is.Not.Null);
            var type = authored.GetType(); const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            var setupCamera = typeof(Camera).GetMethod("SetupCurrent", BindingFlags.Public | BindingFlags.Static);
            Assert.That(setupCamera, Is.Not.Null);
            Camera previousCamera = Camera.current;
            var probeRoot = new GameObject("Inactive SRP reflection guard probe"); probeRoot.SetActive(false);
            var cameraRoot = new GameObject("SRP reflection guard current camera"); cameraRoot.SetActive(false);
            SceneManager.MoveGameObjectToScene(probeRoot, _scene); SceneManager.MoveGameObjectToScene(cameraRoot, _scene);
            var material = new Material(authoredRenderer.sharedMaterial);
            Component probe = null;
            try
            {
                // 不调用Camera.Render、不修改正式Scene/共享材质；手工给出有效Camera，避免空Camera提前返回的假阳性。
                var renderer = probeRoot.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material; renderer.enabled = true;
                probe = probeRoot.AddComponent(type); type.GetField("activated").SetValue(probe, true);
                var camera = cameraRoot.AddComponent<Camera>(); setupCamera.Invoke(null, new object[] { camera });
                Assert.That(Camera.current, Is.SameAs(camera));
                string before = EditorJsonUtility.ToJson(material);
                var cameras = ObjectIds<Camera>(); var textures = ObjectIds<RenderTexture>();
                for (int i = 0; i < 2; i++)
                {
                    type.GetMethod("OnWillRenderObject", BindingFlags.Instance | BindingFlags.Public).Invoke(probe, null);
                    type.GetMethod("Update", fields).Invoke(probe, null);
                }
                Assert.That(((IDictionary)type.GetField("m_ReflectionCameras", fields).GetValue(probe)).Count, Is.Zero);
                Assert.That(type.GetField("m_ReflectionTexture", fields).GetValue(probe), Is.Null);
                Assert.That(ObjectIds<Camera>(), Is.EquivalentTo(cameras), "SRP早退不能创建额外反射Camera。");
                Assert.That(ObjectIds<RenderTexture>(), Is.EquivalentTo(textures), "SRP早退不能创建反射RenderTexture。");
                Assert.That(EditorJsonUtility.ToJson(material), Is.EqualTo(before), "Update也不得修改旧水面动画/反射材质字段。");
            }
            finally
            {
                setupCamera.Invoke(null, new object[] { previousCamera });
                // 负例若真的突破SRP门，也只回收本probe创建的对象，避免失败继续污染后续fixture。
                if (probe != null)
                {
                    var created = type.GetField("m_ReflectionCameras", fields).GetValue(probe) as IDictionary;
                    if (created != null)
                    {
                        foreach (var value in created.Values) if (value is Camera reflected && reflected != null) Object.DestroyImmediate(reflected.gameObject);
                        created.Clear();
                    }
                    if (type.GetField("m_ReflectionTexture", fields).GetValue(probe) is RenderTexture texture && texture != null) Object.DestroyImmediate(texture);
                }
                Object.DestroyImmediate(probeRoot); Object.DestroyImmediate(cameraRoot); Object.DestroyImmediate(material);
            }
        }

        private static HashSet<int> ObjectIds<T>() where T : Object
        {
            var ids = new HashSet<int>();
            foreach (var value in Resources.FindObjectsOfTypeAll<T>()) ids.Add(value.GetInstanceID());
            return ids;
        }

    }
}
