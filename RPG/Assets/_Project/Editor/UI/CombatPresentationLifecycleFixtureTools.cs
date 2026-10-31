using System;
using Game.Combat;
using Game.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Project.Editor
{
    /// <summary>
    /// 显式生成最小生命周期Fixture，不打开或改写正式OnlineRoom/Hub。
    /// Editor生产程序集不引用测试程序集；只在此测试资源生成入口按完整名寻找测试MonoBehaviour。
    /// </summary>
    public static class CombatPresentationLifecycleFixtureTools
    {
        public const string ScenePath = "Assets/_Project/Tests/Fixtures/RuntimeComposition/EarlyCombatPresentation.unity";

        [MenuItem("Tools/Norn/Combat/Create Early Presentation Lifecycle Fixture")]
        public static void Create()
        {
            OnlineSceneSetup.RequireCleanEditor();
            Type publisher = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name != "Game.Run.PlayMode.Tests") continue;
                publisher = assembly.GetType("Game.Run.Tests.EarlyCombatPresentationPublisher"); break;
            }
            if (publisher == null || !typeof(MonoBehaviour).IsAssignableFrom(publisher))
                throw new InvalidOperationException("请先等待PlayMode测试程序集编译；缺少EarlyCombatPresentationPublisher。");
            const string parent = "Assets/_Project/Tests/Fixtures";
            if (!AssetDatabase.IsValidFolder(parent + "/RuntimeComposition")) AssetDatabase.CreateFolder(parent, "RuntimeComposition");
            var setup = EditorSceneManager.GetSceneManagerSetup(); Scene scene = default;
            try
            {
                scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                var owner = new GameObject("EarlyOnlineCombatScope"); SceneManager.MoveGameObjectToScene(owner, scene);
                var scope = owner.AddComponent<CombatPresentationScope>(); var data = new SerializedObject(scope);
                data.FindProperty("_online").boolValue = true; data.ApplyModifiedPropertiesWithoutUndo();
                var actor = new GameObject("EarlyLocalActor"); SceneManager.MoveGameObjectToScene(actor, scene);
                actor.AddComponent<HealthComponent>(); actor.AddComponent(publisher);
                // SaveScene让Unity生成场景与meta；不把测试Publisher加入任何正式Scene或Build Scene List。
                if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new InvalidOperationException("早期Scene生命周期Fixture保存失败。");
            }
            finally
            {
                if (scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
                EditorSceneManager.RestoreSceneManagerSetup(setup);
            }
        }
    }
}
