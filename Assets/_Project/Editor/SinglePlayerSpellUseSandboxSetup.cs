using System;
using System.IO;
using Game.Core;
using Game.Run;
using Game.Skills;
using Game.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.EditorTools
{
    /// <summary>从旧测试 Scene 建新副本，所有修改只保存到新路径，Unity 负责生成 .meta。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerSpellUseSandboxSetup
    {
        private const string Root = "Assets/_Project/ScriptableObjects/Tests/SinglePlayerSpells";
        private const string ScenePath = "Assets/_Project/Scenes/Tests/SpellUseSandbox.unity";
        private const string Request = "Temp/SinglePlayer/create-spell-use-sandbox.request";
        static SinglePlayerSpellUseSandboxSetup() { EditorApplication.update += Poll; }
        private static void Poll()
        {
            if (!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            File.Delete(Request);
            try { Create(); } catch (Exception exception) { Report("Error", exception.ToString()); GameLog.Error(exception.ToString(), "SpellUseSandbox"); }
        }
        [MenuItem("Tools/Norn/Single Player/Create Spell Use Sandbox")]
        private static void Create()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { Report("Blocked", "当前在 Play Mode。"); return; }
            for (int i = 0; i < SceneManager.sceneCount; i++) if (SceneManager.GetSceneAt(i).isDirty)
            { Report("Blocked", "当前有未保存场景，未保存或丢弃。"); return; }
            if (File.Exists(ScenePath)) { Report("Existing", "已有独立场景，未覆盖。"); return; }
            if (AssetDatabase.IsValidFolder(Root)) throw new InvalidOperationException("目录已有部分资产，拒绝覆盖。");
            AssetDatabase.CreateFolder("Assets/_Project/ScriptableObjects/Tests", "SinglePlayerSpells");
            var originalCatalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/_Project/ScriptableObjects/Tests/SinglePlayerWands/Catalog.asset");
            var source = new SerializedObject(originalCatalog).FindProperty("_definitions");
            var definitions = new ItemDefinition[source.arraySize + 2];
            for (int i = 0; i < source.arraySize; i++)
            {
                var original = (ItemDefinition)source.GetArrayElementAtIndex(i).objectReferenceValue;
                var item = UnityEngine.Object.Instantiate(original); var data = new SerializedObject(item);
                data.FindProperty("_contentId").stringValue = original.ContentId.Replace("wandtest.", "spelltest.");
                if (original.SpellDefinition != null)
                { var spell = UnityEngine.Object.Instantiate(original.SpellDefinition);
                    AssetDatabase.CreateAsset(spell, Root + "/Spell_" + i + ".asset"); data.FindProperty("_spellDefinition").objectReferenceValue = spell; }
                if (original.WandDefinition != null)
                { var stats = UnityEngine.Object.Instantiate(original.WandDefinition);
                    AssetDatabase.CreateAsset(stats, Root + "/Stats_" + i + ".asset"); data.FindProperty("_wandDefinition").objectReferenceValue = stats; }
                data.ApplyModifiedPropertiesWithoutUndo(); AssetDatabase.CreateAsset(item, Root + "/Item_" + i + ".asset"); definitions[i] = item;
            }
            definitions[7] = Qualification("modifier", "伤害修正", SpellKind.Modify, 0);
            definitions[8] = Qualification("multicast", "双重施法", SpellKind.Multicast, 1);
            var catalog = ScriptableObject.CreateInstance<ItemCatalog>(); var catalogData = new SerializedObject(catalog);
            var entries = catalogData.FindProperty("_definitions"); entries.arraySize = definitions.Length;
            for (int i = 0; i < definitions.Length; i++) entries.GetArrayElementAtIndex(i).objectReferenceValue = definitions[i];
            catalogData.ApplyModifiedPropertiesWithoutUndo(); AssetDatabase.CreateAsset(catalog, Root + "/Catalog.asset");
            var seeds = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<SinglePlayerStartingInventory>("Assets/_Project/ScriptableObjects/Tests/SinglePlayerWands/StartingInventory.asset"));
            var seedData = new SerializedObject(seeds); var items = seedData.FindProperty("_items"); items.arraySize = 11;
            int[] sourceIndices = { 0, 1, 2, 3, 4, 5, 6, 4, 7, 8, 4 };
            for (int i = 0; i < items.arraySize; i++)
            {
                var seed = items.GetArrayElementAtIndex(i); var definition = definitions[sourceIndices[i]];
                seed.FindPropertyRelative("Definition").objectReferenceValue = definition;
                seed.FindPropertyRelative("HasUnlimitedCharges").boolValue = i == 5;
                seed.FindPropertyRelative("RemainingCharges").intValue = i == 8 ? 1 : i >= 4 && i != 5 ? i == 4 || i == 10 ? 3 : 2 : 0;
                if (i == 4) seed.FindPropertyRelative("SlotIndex").intValue = 1;
                if (i >= 8)
                {
                    seed.FindPropertyRelative("Location").intValue = (int)(i == 8 ? ItemLocationKind.WandSlot : ItemLocationKind.Warehouse);
                    seed.FindPropertyRelative("OwnerSeedIndex").intValue = i == 8 ? 1 : -1;
                    seed.FindPropertyRelative("SlotIndex").intValue = 0;
                }
            }
            seedData.ApplyModifiedPropertiesWithoutUndo(); AssetDatabase.CreateAsset(seeds, Root + "/StartingInventory.asset");
            if (!catalog.TryCreateIndex(out ItemCatalogIndex index, out string error) || !seeds.TryCreate(index, out _, out error)) throw new InvalidOperationException(error);
            Scene originalScene = SceneManager.GetActiveScene();
            Scene scene = EditorSceneManager.OpenScene("Assets/_Project/Scenes/Tests/WandSandbox.unity", OpenSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene); var session = Find<SinglePlayerInventorySession>(scene);
                var data = new SerializedObject(session); data.FindProperty("_catalog").objectReferenceValue = catalog;
                data.FindProperty("_startingInventory").objectReferenceValue = seeds; data.FindProperty("_sandboxName").stringValue = "SpellUseSandbox";
                data.FindProperty("_startSandboxRaid").boolValue = true; data.ApplyModifiedPropertiesWithoutUndo();
                BuildHud(session);
                if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new IOException("新次数场景保存失败。");
            }
            finally { SceneManager.SetActiveScene(originalScene); EditorSceneManager.CloseScene(scene, true); }
            Report("Created", null);
        }
        private static ItemDefinition Qualification(string id, string display, SpellKind kind, int extra)
        {
            var spell = ScriptableObject.CreateInstance<SpellDefinition>(); spell.Kind = kind; spell.ExtraDraws = extra; spell.ModDamageAddFlat = kind == SpellKind.Modify ? 10 : 0;
            AssetDatabase.CreateAsset(spell, Root + "/Spell_" + id + ".asset");
            var item = ScriptableObject.CreateInstance<ItemDefinition>(); var data = new SerializedObject(item);
            data.FindProperty("_contentId").stringValue = "spelltest." + id; data.FindProperty("_displayName").stringValue = display;
            data.FindProperty("_kind").intValue = (int)ItemKind.Spell; data.FindProperty("_spellDefinition").objectReferenceValue = spell;
            data.ApplyModifiedPropertiesWithoutUndo(); AssetDatabase.CreateAsset(item, Root + "/Item_" + id + ".asset"); return item;
        }
        private static T Find<T>(Scene scene) where T : Component
        { foreach (var root in scene.GetRootGameObjects()) { var item = root.GetComponentInChildren<T>(true); if (item != null) return item; } return null; }
        private static void BuildHud(SinglePlayerInventorySession session)
        {
            var root = new GameObject("Spell Use Feedback", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = root.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 40;
            var scaler = root.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1440, 900);
            var panel = Rect(root.transform, "Panel", new Vector2(-24, -24), new Vector2(490, 400));
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(1, 1); panel.gameObject.AddComponent<Image>().color = new Color(.04f, .08f, .13f, .94f);
            var hud = panel.gameObject.AddComponent<SpellUseHud>(); var data = new SerializedObject(hud); data.FindProperty("_session").objectReferenceValue = session;
            data.FindProperty("_label").objectReferenceValue = Label(panel, "Uses", new Vector2(16, -12), new Vector2(460, 240), "");
            data.FindProperty("_status").objectReferenceValue = Label(panel, "Status", new Vector2(16, -260), new Vector2(450, 35), "左键施法；I 打开整备后操作按钮");
            data.FindProperty("_begin").objectReferenceValue = Button(panel, "Begin", new Vector2(16, -305), "开始测试局");
            data.FindProperty("_end").objectReferenceValue = Button(panel, "End", new Vector2(245, -305), "结束测试局");
            Label(panel, "Help", new Vector2(16, -350), new Vector2(450, 42), "A：有限火球+最后一次修正　B：无限冰球\nC：有限 Trigger+有限 Payload；备用卡在仓库");
            data.ApplyModifiedPropertiesWithoutUndo();
        }
        private static Text Label(Transform parent, string name, Vector2 position, Vector2 size, string value)
        { var label = Rect(parent, name, position, size).gameObject.AddComponent<Text>(); label.fontSize = 18; label.color = Color.white; label.text = value; label.raycastTarget = false; return label; }
        private static Button Button(Transform parent, string name, Vector2 position, string value)
        { var rect = Rect(parent, name, position, new Vector2(215, 36)); var image = rect.gameObject.AddComponent<Image>(); image.color = new Color(.15f, .3f, .45f);
            var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = image; var label = Label(rect, "Label", new Vector2(8, -3), new Vector2(200, 30), value); return button; }
        private static RectTransform Rect(Transform parent, string name, Vector2 position, Vector2 size)
        { var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>(); rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1); rect.anchoredPosition = position; rect.sizeDelta = size; return rect; }
        private static void Report(string state, string message)
        { Directory.CreateDirectory("Temp/SinglePlayer"); File.WriteAllText("Temp/SinglePlayer/spell-use-sandbox-report.json", JsonUtility.ToJson(new Result { State = state, Message = message, Scene = ScenePath }, true)); }
        [Serializable] private sealed class Result { public string State, Message, Scene; }
    }
}
