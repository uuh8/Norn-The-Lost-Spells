using Game.Combat;
using Game.Core;
using Game.ElementField;
using Game.Materials;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// 只迁移 Task 21 新增的 Reaction Binding 引用，不触碰玩家已经调好的 PBF/Rendering 参数。
    /// </summary>
    internal static class MaterialReactionBindingSetup
    {
        private const string CatalogPath =
            "Assets/_Project/ScriptableObjects/Materials/MaterialCatalog_Default.asset";
        private const string FolderPath =
            "Assets/_Project/ScriptableObjects/Combat/Reactions";
        private const string BindingPath = "Assets/_Project/ScriptableObjects/Combat/Reactions/MaterialReactionBindings_Default.asset";

        [MenuItem("Tools/Material World/Setup Reaction Bindings")]
        private static void Run()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += Run;
                return;
            }

            MaterialCatalog materials = AssetDatabase.LoadAssetAtPath<MaterialCatalog>(CatalogPath);
            if (materials == null)
            {
                GameLog.Error("[MATERIAL-TASK21-SETUP] 默认 Material Catalog 缺失。", "Editor");
                return;
            }

            EnsureFolders();
            MaterialReactionBindingProfile bindings =
                AssetDatabase.LoadAssetAtPath<MaterialReactionBindingProfile>(BindingPath);
            if (bindings == null)
            {
                bindings = ScriptableObject.CreateInstance<MaterialReactionBindingProfile>();
                AssetDatabase.CreateAsset(bindings, BindingPath);
            }

            var bindingData = new SerializedObject(bindings);
            SerializedProperty entries = bindingData.FindProperty("_bindings");
            entries.arraySize = 2;
            SerializedProperty entry = entries.GetArrayElementAtIndex(0);
            entry.FindPropertyRelative("_first").intValue = (byte)MaterialId.Water;
            entry.FindPropertyRelative("_second").intValue = (byte)MaterialId.Fire;
            entry.FindPropertyRelative("_reaction").intValue = (byte)ElementReactionId.Extinguish;
            SerializedProperty toxic = entries.GetArrayElementAtIndex(1);
            toxic.FindPropertyRelative("_first").intValue = (byte)MaterialId.Poison;
            toxic.FindPropertyRelative("_second").intValue = (byte)MaterialId.Fire;
            toxic.FindPropertyRelative("_reaction").intValue = (byte)ElementReactionId.ToxicCombustion;
            bindingData.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(bindings);

            string[] worldGuids = AssetDatabase.FindAssets("t:ElementWorldProfile");
            for (int i = 0; i < worldGuids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(worldGuids[i]);
                ElementWorldProfile world = AssetDatabase.LoadAssetAtPath<ElementWorldProfile>(path);
                var worldData = new SerializedObject(world);
                worldData.FindProperty("_materialReactionBindings").objectReferenceValue = bindings;
                worldData.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(world);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(BindingPath, ImportAssetOptions.ForceSynchronousImport);
            GameLog.Info(
                $"[MATERIAL-TASK21-SETUP] SUCCESS Profiles={worldGuids.Length}",
                "Editor");
        }

        private static void EnsureFolders()
        {
            const string combat = "Assets/_Project/ScriptableObjects/Combat";
            if (!AssetDatabase.IsValidFolder(combat))
                AssetDatabase.CreateFolder("Assets/_Project/ScriptableObjects", "Combat");
            if (!AssetDatabase.IsValidFolder(FolderPath))
                AssetDatabase.CreateFolder(combat, "Reactions");
        }
    }
}
