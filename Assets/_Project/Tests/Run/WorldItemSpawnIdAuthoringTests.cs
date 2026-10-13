#if UNITY_EDITOR
using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Run.Tests
{
    /// <summary>只在隔离Preview Scene检查Editor配置，不启动Session或修改用户场景/存档。</summary>
    public sealed class WorldItemSpawnIdAuthoringTests
    {
        private Scene _scene;
        private Type Tools => Type.GetType("Game.EditorTools.WorldItemSpawnIdTools, Game.PbfTask13.Editor");
        [SetUp] public void SetUp() => _scene = EditorSceneManager.NewPreviewScene();
        [TearDown] public void TearDown() => EditorSceneManager.ClosePreviewScene(_scene);

        [Test] public void ScanFillsAllEmptyIdsIncludingInactiveAndWhitespace()
        {
            var a = Point("A", null); var b = Point("B", "  "); b.gameObject.SetActive(false);
            var report = Scan();
            Assert.That(Field<int>(report, "Generated"), Is.EqualTo(2));
            Assert.That(Guid.TryParseExact(a.SpawnId, "N", out _), Is.True);
            Assert.That(Guid.TryParseExact(b.SpawnId, "N", out _), Is.True);
            Assert.That(a.SpawnId, Is.Not.EqualTo(b.SpawnId));
        }
        [Test] public void RepeatedScanPreservesExistingStableIdsAndIsNoOp()
        {
            var a = Point("Existing", "persisted.spawn"); var b = Point("New", null);
            Scan(); string generated = b.SpawnId;
            Assert.That(Field<int>(Scan(), "Generated"), Is.Zero);
            Assert.That(a.SpawnId, Is.EqualTo("persisted.spawn"));
            Assert.That(b.SpawnId, Is.EqualTo(generated));
        }
        [Test] public void DuplicateNonemptyIdsAreReportedWithoutRenumbering()
        {
            var a = Point("Original", "original"); var b = Point("Copy", "original");
            Assert.That(Field<int>(Scan(), "DuplicateGroups"), Is.EqualTo(1));
            Assert.That(a.SpawnId, Is.EqualTo("original")); Assert.That(b.SpawnId, Is.EqualTo("original"));
        }
        [Test] public void IdenticalIdsInDifferentSceneKeysDoNotConflict()
        {
            Point("Hub", "same"); var b = Point("OtherWorld", "same"); Set(b, "_sceneKey", "other.world");
            Assert.That(Field<int>(Scan(), "DuplicateGroups"), Is.Zero);
        }
        [Test] public void ScanLeavesOtherLoadedSceneUntouched()
        {
            var other = EditorSceneManager.NewPreviewScene();
            try
            {
                var go = new GameObject("Other"); SceneManager.MoveGameObjectToScene(go, other);
                var point = go.AddComponent<WorldItemSpawnPoint>(); Point("Local", null); Scan();
                Assert.That(point.SpawnId, Is.Null.Or.Empty);
            }
            finally { EditorSceneManager.ClosePreviewScene(other); }
        }
        [Test] public void ScanReportsMissingDefinitionAndMismatchedSceneKey()
        {
            var sessionObject = new GameObject("Session"); SceneManager.MoveGameObjectToScene(sessionObject, _scene);
            var session = sessionObject.AddComponent<SinglePlayerInventorySession>(); Set(session, "_worldSceneKey", "hub.map1");
            var point = Point("WrongKey", null); Set(point, "_sceneKey", "wrong.world");
            var report = Scan();
            Assert.That(Field<int>(report, "MissingDefinitions"), Is.EqualTo(1));
            Assert.That(Field<int>(report, "SceneKeyMismatches"), Is.EqualTo(1));
        }
        [Test] public void OneUndoRestoresEveryGeneratedId()
        {
            var a = Point("A", null); var b = Point("B", " "); Scan(); Undo.PerformUndo();
            Assert.That(a.SpawnId, Is.Null.Or.Empty); Assert.That(b.SpawnId, Is.EqualTo(" "));
        }
        [Test] public void PrefabInstanceGetsOverrideWhilePrefabAssetStaysBlank()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Items/Container_01.prefab");
            Assert.That(prefab, Is.Not.Null);
            string original = prefab.GetComponent<WorldItemSpawnPoint>().SpawnId;
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, _scene);
            var point = instance.GetComponent<WorldItemSpawnPoint>(); Set(point, "_spawnId", ""); Scan();
            Assert.That(point.SpawnId, Is.Not.Empty);
            Assert.That(new SerializedObject(point).FindProperty("_spawnId").prefabOverride, Is.True);
            Assert.That(prefab.GetComponent<WorldItemSpawnPoint>().SpawnId, Is.EqualTo(original));
        }
        [Test] public void InvalidSceneIsRejectedWithoutMutation()
        {
            Assert.That(Tools, Is.Not.Null, "批量SpawnId工具尚未实现。");
            var error = Assert.Throws<TargetInvocationException>(() => Tools.GetMethod("Scan").Invoke(null, new object[] { default(Scene) }));
            Assert.That(error.InnerException, Is.TypeOf<ArgumentException>());
        }
        [Test] public void ReadOnlyInspectDoesNotFillEmptyIds()
        {
            var point = Point("NotReady", null);
            Assert.That(Tools, Is.Not.Null);
            var report = Tools.GetMethod("Inspect").Invoke(null, new object[] { _scene });
            Assert.That(Field<int>(report, "EmptyIds"), Is.EqualTo(1));
            Assert.That(Field<int>(report, "Generated"), Is.Zero); Assert.That(point.SpawnId, Is.Null.Or.Empty);
        }
        [Test] public void RenumberSelectedCopyPreservesOriginalAndSupportsUndo()
        {
            var original = Point("Original", "persisted.spawn"); var copy = Point("Copy", "persisted.spawn");
            Assert.That(Tools, Is.Not.Null);
            Tools.GetMethod("RegenerateSelected").Invoke(null, new object[] { new[] { copy } });
            Assert.That(original.SpawnId, Is.EqualTo("persisted.spawn")); Assert.That(copy.SpawnId, Is.Not.EqualTo(original.SpawnId));
            Undo.PerformUndo(); Assert.That(copy.SpawnId, Is.EqualTo("persisted.spawn"));
        }
        [Test] public void MixedSelectionWithPrefabAssetRejectsBeforeChangingAnyInstance()
        {
            var point = Point("Valid", "stable");
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Items/Container_01.prefab").GetComponent<WorldItemSpawnPoint>();
            Assert.That(Tools, Is.Not.Null);
            var error = Assert.Throws<TargetInvocationException>(() => Tools.GetMethod("RegenerateSelected")
                .Invoke(null, new object[] { new[] { point, asset } }));
            Assert.That(error.InnerException, Is.TypeOf<ArgumentException>()); Assert.That(point.SpawnId, Is.EqualTo("stable"));
        }
        private object Scan()
        {
            // 测试不增加Runtime asmdef对Editor工具的引用；Reflection只发生在Editor测试中。
            Assert.That(Tools, Is.Not.Null, "批量SpawnId工具尚未实现。");
            return Tools.GetMethod("Scan", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { _scene });
        }
        private WorldItemSpawnPoint Point(string name, string id)
        {
            var go = new GameObject(name); SceneManager.MoveGameObjectToScene(go, _scene);
            var point = go.AddComponent<WorldItemSpawnPoint>(); Set(point, "_spawnId", id); return point;
        }
        private static void Set(UnityEngine.Object target, string name, string value)
        { var so = new SerializedObject(target); so.FindProperty(name).stringValue = value; so.ApplyModifiedPropertiesWithoutUndo(); }
        private static T Field<T>(object report, string name) => (T)report.GetType().GetField(name).GetValue(report);
    }
}
#endif
