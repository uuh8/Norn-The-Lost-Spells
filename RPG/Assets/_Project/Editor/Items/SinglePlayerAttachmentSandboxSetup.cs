using System;
using System.IO;
using Game.Character;
using Game.Run;
using Game.UI;
using Game.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.EditorTools
{
    /// <summary>显式生成独立附着Scene；仅写新路径，拒绝dirty/Play，不保存当前工作Scene。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerAttachmentSandboxSetup
    {
        private const string Root = "Assets/_Project/Tests/Fixtures/Run/SinglePlayerAttachments";
        private const string ScenePath = "Assets/_Project/Scenes/Tests/AttachmentSandbox.unity";
        private const string Request = "Temp/SinglePlayer/create-attachment-sandbox.request";
        static SinglePlayerAttachmentSandboxSetup() { EditorApplication.update += Poll; }
        private static void Poll()
        {
            if (!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating || SessionState.GetBool("Norn.SinglePlayer.Tests.Running", false)) return;
            File.Delete(Request);
            try { Create(); } catch (Exception e) { Report("Error", e.ToString()); GameLog.Error(e.ToString(), "AttachmentSandbox"); }
        }
        [MenuItem("Tools/Norn/Single Player/Create Attachment Sandbox")]
        private static void Create()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorUtility.scriptCompilationFailed) { Report("Blocked", "当前Play或编译失败。"); return; }
            for (int i = 0; i < SceneManager.sceneCount; i++) if (SceneManager.GetSceneAt(i).isDirty) { Report("Blocked", "存在未保存Scene，没有保存/丢弃。"); return; }
            if (File.Exists(ScenePath)) { Report("Existing", null); return; }
            if (AssetDatabase.IsValidFolder(Root)) throw new InvalidOperationException("部分附着测试资产存在，未覆盖。");
            AssetDatabase.CreateFolder("Assets/_Project/Tests/Fixtures/Run", "SinglePlayerAttachments");
            const string sourceRoot = "Assets/_Project/Tests/Fixtures/Run/SinglePlayerContainers/";
            var seeds = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<SinglePlayerStartingInventory>("Assets/_Project/Tests/Fixtures/Run/SinglePlayerContainers/StartingInventory.asset"));
            AssetDatabase.CreateAsset(seeds, "Assets/_Project/Tests/Fixtures/Run/SinglePlayerAttachments/StartingInventory.asset");
            Scene original = SceneManager.GetActiveScene(); Scene scene = EditorSceneManager.OpenScene("Assets/_Project/Scenes/Tests/ContainerSandbox.unity", OpenSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene); var session = Find<SinglePlayerInventorySession>(scene); var ui = Find<SinglePlayerInventoryController>(scene);
                Set(session, "_startingInventory", seeds); SetString(session, "_sandboxName", "AttachmentSandbox");
                Set(Find<OwnedWandCastAdapter>(scene), "_materialWaitingMaterial", AssetDatabase.LoadAssetAtPath<Material>("Assets/_Project/Tests/Fixtures/Run/SinglePlayerContainers/M_ThrownBottle.asset"));
                var panel = (GameObject)new SerializedObject(ui).FindProperty("_panelRoot").objectReferenceValue;
                var ground = panel.transform.Find("Ground").GetComponent<RectTransform>(); ground.sizeDelta = new Vector2(430, 296);
                var details = panel.transform.Find("Details").GetComponent<RectTransform>(); details.anchoredPosition = new Vector2(856, -416); details.sizeDelta = new Vector2(430, 78);
                var controls = Rect(panel.transform, "SpellAttachmentControls", new Vector2(856, -504), new Vector2(430, 174));
                var description = Label(controls, "Description", Vector2.zero, new Vector2(430, 52), "点击具体法术配置附着");
                var sourcesGo = DefaultControls.CreateDropdown(new DefaultControls.Resources()); Place(sourcesGo, controls, "Sources", new Vector2(0, -58), new Vector2(270, 30));
                var doseGo = DefaultControls.CreateInputField(new DefaultControls.Resources()); Place(doseGo, controls, "Dose", new Vector2(286, -58), new Vector2(100, 30));
                var dose = doseGo.GetComponent<InputField>(); dose.contentType = InputField.ContentType.IntegerNumber; dose.text = "10";
                var apply = MakeButton(controls, "Apply", "开启/更新供料", new Vector2(0, -98));
                var clear = MakeButton(controls, "Clear", "关闭附着", new Vector2(180, -98));
                Label(controls, "Help", new Vector2(0, -136), new Vector2(430, 35), "剂量单位GMU；设置不扣料，真实产出才取料。");
                var feedback = Label(ui.transform, "AttachmentFeedback", new Vector2(22, -828), new Vector2(620, 52), "I整备 → 点击产出法术 → 选择瓶和剂量 → 施法消耗真实库存");
                var view = ui.gameObject.AddComponent<SpellAttachmentView>();
                Set(view, "_session", session); Set(view, "_inventory", ui); Set(view, "_controls", controls.gameObject);
                Set(view, "_worldHud", Find<ContainerCompositionView>(scene).GetComponentInParent<Canvas>().gameObject);
                Set(view, "_sources", sourcesGo.GetComponent<Dropdown>()); Set(view, "_dose", dose); Set(view, "_apply", apply); Set(view, "_clear", clear);
                Set(view, "_description", description); Set(view, "_feedback", feedback);
                controls.gameObject.SetActive(false);
                AssetDatabase.SaveAssetIfDirty(seeds);
                if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new IOException("附着测试Scene保存失败。");
            }
            finally { EditorSceneManager.CloseScene(scene, true); if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original); }
            Report("Created", null);
        }
        private static RectTransform Rect(Transform parent, string name, Vector2 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform)); Place(go, parent, name, position, size); return go.GetComponent<RectTransform>();
        }
        private static void Place(GameObject go, Transform parent, string name, Vector2 position, Vector2 size)
        { go.name = name; var rect = go.GetComponent<RectTransform>(); rect.SetParent(parent, false); rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1); rect.anchoredPosition = position; rect.sizeDelta = size; }
        private static Text Label(Transform parent, string name, Vector2 position, Vector2 size, string value)
        { var text = Rect(parent, name, position, size).gameObject.AddComponent<Text>(); text.text = value; text.fontSize = 16; text.color = Color.white; text.raycastTarget = false; return text; }
        private static Button MakeButton(Transform parent, string name, string label, Vector2 position)
        { var go = DefaultControls.CreateButton(new DefaultControls.Resources()); Place(go, parent, name, position, new Vector2(158, 30)); go.GetComponentInChildren<Text>().text = label; return go.GetComponent<Button>(); }
        private static T Find<T>(Scene scene) where T : Component
        { foreach (var root in scene.GetRootGameObjects()) { var component = root.GetComponentInChildren<T>(true); if (component != null) return component; } throw new InvalidOperationException(typeof(T).Name + "缺失。"); }
        private static void Set(UnityEngine.Object target, string field, UnityEngine.Object value)
        { var data = new SerializedObject(target); data.FindProperty(field).objectReferenceValue = value; data.ApplyModifiedPropertiesWithoutUndo(); }
        private static void SetString(UnityEngine.Object target, string field, string value)
        { var data = new SerializedObject(target); data.FindProperty(field).stringValue = value; data.ApplyModifiedPropertiesWithoutUndo(); }
        private static void Report(string state, string error)
        { Directory.CreateDirectory("Temp/SinglePlayer"); File.WriteAllText("Temp/SinglePlayer/attachment-sandbox-report.json", JsonUtility.ToJson(new Result { State = state, Error = error, Scene = ScenePath }, true)); }
        [Serializable] private sealed class Result { public string State, Error, Scene; }
    }
}
