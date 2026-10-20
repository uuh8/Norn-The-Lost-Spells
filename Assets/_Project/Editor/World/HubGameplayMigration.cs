using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>将已被携带库存替代的旧开场接线从场景移除；不重建场景或修改角色/环境布局。</summary>
    public static class HubGameplayMigration
    {
        public static void RemoveLegacyBindings()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("必须在 Edit Mode 迁移。");
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
                if (EditorSceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("保留未保存的作者场景。");
            var removed = new List<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:Scene", new[] { "Assets/_Project/Scenes" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                // 按实际组件识别旧设计，不按 P7、Review 等文件名推断是否可以删整张关卡。
                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                bool changed = false;
                try
                {
                    // RequireComponent 会阻止先删除被依赖者；必须先撤 Coordinator，再撤 Session。
                    foreach (GameObject root in scene.GetRootGameObjects())
                        foreach (MonoBehaviour component in root.GetComponentsInChildren<MonoBehaviour>(true))
                            if (component != null && component.GetType().Name == "RunSessionCoordinator")
                            {
                                removed.Add(path + " :: " + component.name + " / RunSessionCoordinator");
                                UnityEngine.Object.DestroyImmediate(component); changed = true;
                            }
                    foreach (GameObject root in scene.GetRootGameObjects())
                        foreach (MonoBehaviour component in root.GetComponentsInChildren<MonoBehaviour>(true))
                        {
                            if (component == null) continue;
                            string type = component.GetType().Name;
                            if (type == "WandEditorController")
                            {
                                removed.Add(path + " :: " + component.name + " / " + type);
                                UnityEngine.Object.DestroyImmediate(component.gameObject);
                                changed = true;
                            }
                            else if (type == "RunSpellSession")
                            {
                                removed.Add(path + " :: " + component.name + " / " + type);
                                UnityEngine.Object.DestroyImmediate(component);
                                changed = true;
                            }
                        }
                    if (changed)
                    {
                        EditorSceneManager.MarkSceneDirty(scene);
                        EditorSceneManager.SaveScene(scene);
                    }
                }
                finally { EditorSceneManager.CloseScene(scene, true); }
            }
            File.WriteAllLines("docs/superpowers/evidence/2026-10-19-project-layout/retired-scene-bindings.txt", removed);
        }
    }
}
