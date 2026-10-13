using System;
using System.Collections.Generic;
using System.IO;
using Game.Core;
using Game.Run;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.EditorTools
{
    /// <summary>Scene Authoring工具：补齐空身份；已保存的非空身份关联生成账，不能按遍历顺序重写。</summary>
    [InitializeOnLoad]
    public static class WorldItemSpawnIdTools
    {
        private const string RequestPath = "Temp/SinglePlayer/spawn-id-scan.request";
        private const string ReportPath = "TestResults/SinglePlayer/M6-T1-ItemPresentation/SpawnIds/current-scene.json";
        private const string UndoName = "补齐场景物品 SpawnId";
        private static double _nextPoll;
        static WorldItemSpawnIdTools() => EditorApplication.update += Poll;

        [MenuItem("Tools/Norn/物品/扫描当前场景并补齐 SpawnId（Undo，不保存）")]
        public static void ScanCurrentScene() => Show(Scan(SceneManager.GetActiveScene()));

        [MenuItem("Tools/Norn/物品/检查当前场景 SpawnId（只读）")]
        public static void InspectCurrentScene() => Show(Inspect(SceneManager.GetActiveScene()));

        public static SpawnIdScanReport Scan(Scene scene) => Execute(scene, true);
        public static SpawnIdScanReport Inspect(Scene scene) => Execute(scene, false);

        private static SpawnIdScanReport Execute(Scene scene, bool fillMissing)
        {
            ValidateScene(scene);
            var report = new SpawnIdScanReport { ScenePath = scene.path, SceneName = scene.name };
            var points = new List<WorldItemSpawnPoint>();
            var sessions = new List<SinglePlayerInventorySession>();
            foreach (var root in scene.GetRootGameObjects())
            {
                points.AddRange(root.GetComponentsInChildren<WorldItemSpawnPoint>(true));
                sessions.AddRange(root.GetComponentsInChildren<SinglePlayerInventorySession>(true));
            }
            report.Scanned = points.Count;
            int undoGroup = -1;
            var identities = new Dictionary<(string, string), List<WorldItemSpawnPoint>>();
            // 此处只在菜单点击执行，可分配收集结构；不会在Runtime每帧维护第二套物品注册表。
            foreach (var point in points)
            {
                if (string.IsNullOrWhiteSpace(point.SpawnId))
                {
                    report.EmptyIds++;
                    if (fillMissing)
                    {
                        if (undoGroup < 0)
                        {
                            Undo.IncrementCurrentGroup(); undoGroup = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName(UndoName);
                        }
                        Assign(point, Guid.NewGuid().ToString("N")); report.Generated++;
                    }
                    else report.AddIssue(point, "SpawnId为空，执行补齐扫描即可生成。");
                }
                if (point.Definition == null)
                { report.MissingDefinitions++; report.AddIssue(point, "缺少ItemDefinition；有编号仍不能注册物品。"); }
                var serialized = new SerializedObject(point);
                var session = serialized.FindProperty("_session").objectReferenceValue as SinglePlayerInventorySession;
                if (session == null && sessions.Count == 1) session = sessions[0];
                if (session == null)
                { report.MissingSessions++; report.AddIssue(point, sessions.Count > 1 ? "同Scene有多个Session，请明确指定Session。" : "缺少同Scene库存Session。"); }
                if (string.IsNullOrWhiteSpace(point.SceneKey) || session != null &&
                    (session.gameObject.scene != scene || point.SceneKey != session.WorldSceneKey))
                { report.SceneKeyMismatches++; report.AddIssue(point, "SceneKey为空、与Session不匹配，或Session属于其他Scene。"); }
                if (string.IsNullOrWhiteSpace(point.SpawnId)) continue;
                var key = (point.SceneKey, point.SpawnId);
                if (!identities.TryGetValue(key, out var group)) identities.Add(key, group = new List<WorldItemSpawnPoint>());
                group.Add(point);
            }
            foreach (var group in identities.Values)
            {
                if (group.Count < 2) continue;
                report.DuplicateGroups++;
                foreach (var point in group) report.AddIssue(point, "重复SpawnId：" + point.SpawnId + "；选择复制品单独重新编号，保留原点身份。");
            }
            if (undoGroup >= 0)
            {
                // RecordObject在帧末才差分；Flush使同帧测试/用户Undo也能恢复整批编号。
                Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(undoGroup);
                EditorSceneManager.MarkSceneDirty(scene);
            }
            report.SceneDirty = scene.isDirty;
            return report;
        }

        public static void RegenerateSelected(WorldItemSpawnPoint[] points)
        {
            if (points == null || points.Length == 0) return;
            // 先验证所有目标，再开始写，避免多选中混入Prefab Asset导致一半实例已改。
            foreach (var point in points)
                if (point == null || EditorUtility.IsPersistent(point)) throw new ArgumentException("只允许Scene实例；不能把生成身份写回共享Prefab Asset。");
                else ValidateScene(point.gameObject.scene);
            Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("重新编号选中物品复制品");
            foreach (var point in points) { Assign(point, Guid.NewGuid().ToString("N")); EditorSceneManager.MarkSceneDirty(point.gameObject.scene); }
            Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(group);
        }

        private static void Assign(WorldItemSpawnPoint point, string id)
        {
            Undo.RecordObject(point, UndoName);
            var serialized = new SerializedObject(point); serialized.FindProperty("_spawnId").stringValue = id;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            // Scene Prefab Instance的字段必须保存为override，原Prefab Asset保持空编号。
            PrefabUtility.RecordPrefabInstancePropertyModifications(point);
        }
        private static void ValidateScene(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) throw new ArgumentException("需要有效且已加载的Scene。");
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("仅在Edit Mode配置稳定SpawnId。");
        }
        private static void Show(SpawnIdScanReport report) => SpawnIdScanWindow.Open(report);

        private static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _nextPoll) return; _nextPoll = EditorApplication.timeSinceStartup + .5;
            if (!File.Exists(RequestPath) || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            string command = File.ReadAllText(RequestPath).Trim(); File.Delete(RequestPath);
            try
            {
                if (EditorUtility.scriptCompilationFailed) throw new InvalidOperationException("当前编译失败，不运行旧Assembly。");
                var scene = SceneManager.GetActiveScene();
                var report = command == "inspect" ? Inspect(scene) : command == "apply-current" ? Scan(scene) : throw new ArgumentException(command);
                Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)); File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
                if (command == "apply-current") Show(report);
            }
            catch (Exception e) { GameLog.Error(e.ToString(), "SpawnIds"); }
        }
    }

    [Serializable]
    public sealed class SpawnIdScanReport
    {
        public string Utc = DateTime.UtcNow.ToString("O");
        public string ScenePath, SceneName;
        public int Scanned, EmptyIds, Generated, DuplicateGroups, MissingDefinitions, MissingSessions, SceneKeyMismatches;
        public bool SceneDirty;
        public List<SpawnIdScanIssue> Issues = new List<SpawnIdScanIssue>();
        internal void AddIssue(WorldItemSpawnPoint point, string message) => Issues.Add(new SpawnIdScanIssue { Target = point, ObjectName = point.name, Message = message });
    }
    [Serializable]
    public sealed class SpawnIdScanIssue
    {
        public WorldItemSpawnPoint Target;
        public string ObjectName, Message;
    }
    internal sealed class SpawnIdScanWindow : EditorWindow
    {
        private SpawnIdScanReport _report;
        private Vector2 _scroll;
        internal static void Open(SpawnIdScanReport report)
        { var window = GetWindow<SpawnIdScanWindow>("物品编号扫描"); window._report = report; window.minSize = new Vector2(450, 260); window.Show(); }
        private void OnGUI()
        {
            if (_report == null) { EditorGUILayout.LabelField("请从Tools/Norn/物品执行扫描。"); return; }
            EditorGUILayout.LabelField(_report.SceneName, EditorStyles.boldLabel);
            EditorGUILayout.LabelField("扫描 " + _report.Scanned + " 件；新增 " + _report.Generated + " 个；重复组 " + _report.DuplicateGroups);
            EditorGUILayout.HelpBox("扫描仅补齐空编号，保留已有身份。修改已加入Undo，未自动保存Scene。重复编号请定位复制品后单独重新编号。", MessageType.Info);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var issue in _report.Issues)
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(issue.ObjectName, GUILayout.Width(140)) && issue.Target != null)
                { Selection.activeGameObject = issue.Target.gameObject; EditorGUIUtility.PingObject(issue.Target.gameObject); }
                EditorGUILayout.LabelField(issue.Message, EditorStyles.wordWrappedLabel); EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
            if (GUILayout.Button("重新扫描并补齐当前场景")) _report = WorldItemSpawnIdTools.Scan(SceneManager.GetActiveScene());
        }
    }
}
