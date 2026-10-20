using System;
using System.IO;
using Game.Core;
using Game.Run;
using Game.Skills;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.EditorTools
{
    /// <summary>只保存新灰盒 Scene。原场景不保存、不关闭；新增 Scene 在创建结束后移除。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerInventorySandboxSetup
    {
        private const string ScenePath = "Assets/_Project/Scenes/Tests/InventoryGridSandbox.unity";
        private const string AssetPath = "Assets/_Project/Tests/Fixtures/Run/InventoryGrid";
        private const string RequestPath = "Temp/SinglePlayer/create-grid-sandbox.request";
        private const string ReportPath = "Temp/SinglePlayer/grid-sandbox-report.json";
        private static double _nextPoll;
        static SinglePlayerInventorySandboxSetup() { EditorApplication.update += Poll; }

        private static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _nextPoll || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            _nextPoll = EditorApplication.timeSinceStartup + .5d;
            if (!File.Exists(RequestPath)) return;
            File.Delete(RequestPath);
            try { Generate(); }
            catch (Exception exception) { WriteReport("Error", exception.ToString()); GameLog.Error(exception.ToString(), "InventorySandbox"); }
        }

        [MenuItem("Tools/Norn/Single Player/Create Inventory Grid Sandbox")]
        private static void Generate()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
            { WriteReport("Blocked", "请退出 Play Mode/等待编译；没有保存场景。"); return; }
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isDirty) { WriteReport("Blocked", "有未保存场景；没有保存或丢弃。"); return; }
            if (File.Exists(ScenePath)) { WriteReport("Existing", "已有灰盒不覆盖；可手动打开。"); return; }
            EnsureFolder(AssetPath); EnsureFolder("Assets/_Project/Scenes/Tests");
            ItemCatalog catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/_Project/Tests/Fixtures/Run/InventoryGrid/Catalog.asset");
            if (catalog == null)
            {
                // 已有配置不覆盖；若上次创建中断留下半套资源，明确报错要求检查，而非静默重写。
                if (AssetDatabase.FindAssets("", new[] { AssetPath }).Length != 0)
                    throw new InvalidOperationException("灰盒资源目录已有资产但没有目录配置，请检查后重新生成。");
                var spell = ScriptableObject.CreateInstance<SpellDefinition>();
                AssetDatabase.CreateAsset(spell, "Assets/_Project/Tests/Fixtures/Run/InventoryGrid/Instruction.asset");
                ItemDefinition[] definitions =
                {
                    Define("sandbox.bag", ItemKind.Backpack, 1, 1, 1, null, null, 4, 4),
                    Define("sandbox.bar", ItemKind.Container, 2, 1, 1),
                    Define("sandbox.corner", ItemKind.Container, 2, 2, 1, new[] { Vector2Int.zero, new Vector2Int(0, 1), Vector2Int.one }),
                    Define("sandbox.small", ItemKind.Consumable, 1, 1, 1),
                    Define("sandbox.spell", ItemKind.Spell, 1, 1, 2, null, spell),
                    Define("sandbox.bottle", ItemKind.Container, 1, 1, 3)
                };
                catalog = ScriptableObject.CreateInstance<ItemCatalog>();
                var data = new SerializedObject(catalog);
                SerializedProperty entries = data.FindProperty("_definitions"); entries.arraySize = definitions.Length;
                for (int i = 0; i < definitions.Length; i++) entries.GetArrayElementAtIndex(i).objectReferenceValue = definitions[i];
                data.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.CreateAsset(catalog, "Assets/_Project/Tests/Fixtures/Run/InventoryGrid/Catalog.asset");
            }
            if (!catalog.TryCreateIndex(out _, out string error)) throw new InvalidOperationException(error);

            Scene original = SceneManager.GetActiveScene();
            Scene sandbox = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                // 新建对象默认进入 active Scene；先切到新 Scene，避免意外把灰盒塞进用户关卡。
                SceneManager.SetActiveScene(sandbox);
                var root = new GameObject("InventoryGridSandbox");
                var component = root.AddComponent<InventoryGridSandbox>();
                var data = new SerializedObject(component);
                data.FindProperty("_catalog").objectReferenceValue = catalog; data.ApplyModifiedPropertiesWithoutUndo();
                var cameraObject = new GameObject("Main Camera"); cameraObject.tag = "MainCamera";
                Camera camera = cameraObject.AddComponent<Camera>(); camera.orthographic = true; camera.orthographicSize = 4.5f;
                cameraObject.transform.position = new Vector3(3.5f, 10f, -1.5f);
                cameraObject.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                var light = new GameObject("Directional Light").AddComponent<Light>(); light.type = LightType.Directional;
                light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                if (!EditorSceneManager.SaveScene(sandbox, ScenePath)) throw new IOException("灰盒 Scene 保存失败。");
            }
            finally
            {
                if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original);
                EditorSceneManager.CloseScene(sandbox, true);
            }
            WriteReport("Created", null);
            GameLog.Info("已生成 InventoryGridSandbox；当前场景没有保存或替换。", "InventorySandbox");
        }

        private static ItemDefinition Define(string id, ItemKind kind, int width, int height, int stack,
            Vector2Int[] cells = null, SpellDefinition spell = null, int bagWidth = 1, int bagHeight = 1)
        {
            var definition = ScriptableObject.CreateInstance<ItemDefinition>();
            var data = new SerializedObject(definition);
            data.FindProperty("_contentId").stringValue = id; data.FindProperty("_displayName").stringValue = id;
            data.FindProperty("_kind").intValue = (int)kind; data.FindProperty("_spellDefinition").objectReferenceValue = spell;
            data.FindProperty("_shapeWidth").intValue = width; data.FindProperty("_shapeHeight").intValue = height;
            data.FindProperty("_maxStack").intValue = stack;
            data.FindProperty("_backpackWidth").intValue = bagWidth; data.FindProperty("_backpackHeight").intValue = bagHeight;
            if (cells == null)
            {
                cells = new Vector2Int[width * height];
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) cells[y * width + x] = new Vector2Int(x, y);
            }
            SerializedProperty shape = data.FindProperty("_occupiedCells"); shape.arraySize = cells.Length;
            for (int i = 0; i < cells.Length; i++) shape.GetArrayElementAtIndex(i).vector2IntValue = cells[i];
            data.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.CreateAsset(definition, "Assets/_Project/Tests/Fixtures/Run/InventoryGrid/" + id + ".asset");
            return definition;
        }
        private static void EnsureFolder(string path)
        {
            string[] parts = path.Split('/'); string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
        private static void WriteReport(string state, string error)
        {
            Directory.CreateDirectory("Temp/SinglePlayer");
            File.WriteAllText(ReportPath, JsonUtility.ToJson(new Report
            { State = state, Error = error, Utc = DateTime.UtcNow.ToString("O"), Scene = ScenePath,
                ActiveScene = SceneManager.GetActiveScene().path, SceneDirty = SceneManager.GetActiveScene().isDirty }, true));
        }
        [Serializable] private sealed class Report { public string State, Error, Utc, Scene, ActiveScene; public bool SceneDirty; }
    }
}
