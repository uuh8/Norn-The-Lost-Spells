using System;
using System.Collections.Generic;
using Game.Character;
using Game.ElementField;
using Game.Run;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.EditorTools
{
    /// <summary>只读场景装配审计。返回证据，不修Scene、不初始化真实库存、不进入材料模拟。</summary>
    public static class SinglePlayerSceneAudit
    {
        [Serializable] public sealed class Report
        {
            public string Utc, Scene;
            public bool Dirty, InvertCulling;
            public int Cameras, Listeners, Worlds, ActiveRenderers, MissingScripts;
            public Vector3 Spawn;
            public List<string> Issues = new List<string>(), Roots = new List<string>(), Configurations = new List<string>();
        }

        public static Report Inspect(Scene scene, bool requireGameplay = true)
        {
            var report = new Report { Utc = DateTime.UtcNow.ToString("O"), Scene = scene.path, Dirty = scene.isDirty, InvertCulling = GL.invertCulling };
            WizardController player = null; HubInteractionController hub = null; SinglePlayerInventorySession session = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                report.Roots.Add(root.name);
                foreach (var camera in root.GetComponentsInChildren<Camera>(true)) if (camera.enabled && camera.gameObject.activeInHierarchy) report.Cameras++;
                foreach (var listener in root.GetComponentsInChildren<AudioListener>(true)) if (listener.enabled && listener.gameObject.activeInHierarchy) report.Listeners++;
                report.Worlds += root.GetComponentsInChildren<ElementWorldRuntime>(true).Length;
                var actor = root.GetComponentInChildren<WizardController>(true); if (actor != null) player = actor;
                var access = root.GetComponentInChildren<HubInteractionController>(true); if (access != null) hub = access;
                var inventory = root.GetComponentInChildren<SinglePlayerInventorySession>(true); if (inventory != null) session = inventory;
                foreach (var transform in root.GetComponentsInChildren<Transform>(true)) report.MissingScripts += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject);
                foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
                    if (component != null && component.GetType().Name == "Reflection_pass") report.Issues.Add("旧反射组件：" + component.name);
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                    report.ActiveRenderers++;
                    foreach (var material in renderer.sharedMaterials) if (material != null && material.shader != null && material.shader.name.StartsWith("beffio/", StringComparison.Ordinal))
                        report.Issues.Add("活动旧Shader：" + renderer.name + " / " + material.shader.name);
                }
            }
            if (report.MissingScripts != 0) report.Issues.Add("Missing Script数量：" + report.MissingScripts);
            if (!requireGameplay) return report;
            if (player == null || hub == null || session == null) { report.Issues.Add("玩家/据点交互/库存会话缺失"); return report; }
            report.Spawn = player.transform.position;
            if (report.Cameras != 1 || report.Listeners != 1 || report.Worlds != 1) report.Issues.Add("Camera/AudioListener/ElementWorld必须各一份");
            var data = new SerializedObject(session);
            if (data.FindProperty("_sandboxProfile").boolValue) report.Issues.Add("正式据点仍在使用Sandbox档位");
            foreach (string field in new[] { "_catalog", "_startingInventory" })
            {
                string path = AssetDatabase.GetAssetPath(data.FindProperty(field).objectReferenceValue); report.Configurations.Add(path);
                if (!path.StartsWith("Assets/_Project/ScriptableObjects/SinglePlayer/", StringComparison.Ordinal)) report.Issues.Add("正式配置路径错误：" + path);
            }
            if (hub.Definition == null || hub.Definition.Stations == null || hub.Definition.Stations.Length != 3) report.Issues.Add("三种交互点配置不完整");
            else
            {
                var kinds = new HashSet<HubStationKind>();
                foreach (var station in hub.Definition.Stations)
                {
                    if (!kinds.Add(station.Kind)) report.Issues.Add("重复交互种类：" + station.Kind);
                    if (!HasGround(scene, station.Position)) report.Issues.Add("交互点下方没有本Scene碰撞：" + station.DisplayName);
                }
                if (hub.Definition.ContainsExperiment(report.Spawn)) report.Issues.Add("出生点不应默认在实验区域");
            }
            if (!HasGround(scene, report.Spawn)) report.Issues.Add("出生点下方没有地面碰撞");
            return report;
        }

        public static bool HasGround(Scene scene, Vector3 point)
        {
            // Additive审计期间可能同时有两个地图，必须过滤Collider归属，不能误用用户Scene的地面。
            Physics.SyncTransforms(); var hits = Physics.RaycastAll(point + Vector3.up * 4, Vector3.down, 12, ~0, QueryTriggerInteraction.Ignore);
            foreach (var hit in hits) if (hit.collider.gameObject.scene == scene) return true;
            return false;
        }
    }
}
