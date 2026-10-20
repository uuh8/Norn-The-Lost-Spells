using Game.Combat;
using Game.Core;
using Game.Materials;
using UnityEditor;

namespace Game.EditorTools
{
    internal static class ToxicReactionSetup
    {
        private const string Key = "Game.Materials.Task25.Setup.V1";
        private const string Path = "Assets/_Project/ScriptableObjects/Combat/Reactions/MaterialReactionBindings_Default.asset";
        [MenuItem("Tools/Material World/Setup Toxic Reaction")]
        private static void Run()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) { EditorApplication.delayCall += Run; return; }
            MaterialReactionBindingProfile profile = AssetDatabase.LoadAssetAtPath<MaterialReactionBindingProfile>(Path);
            if (profile == null) { GameLog.Error("[MATERIAL-TASK25-SETUP] Reaction binding asset missing.", "Editor"); return; }
            var data = new SerializedObject(profile);
            SerializedProperty bindings = data.FindProperty("_bindings");
            bindings.arraySize = 2;
            Set(bindings.GetArrayElementAtIndex(0), MaterialId.Water, MaterialId.Fire, ElementReactionId.Extinguish);
            Set(bindings.GetArrayElementAtIndex(1), MaterialId.Poison, MaterialId.Fire, ElementReactionId.ToxicCombustion);
            data.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            SessionState.SetBool(Key, true);
            GameLog.Info("[MATERIAL-TASK25-SETUP] SUCCESS Water/Fire + Poison/Fire.", "Editor");
        }

        private static void Set(SerializedProperty entry, MaterialId first, MaterialId second, ElementReactionId reaction)
        {
            entry.FindPropertyRelative("_first").intValue = (byte)first;
            entry.FindPropertyRelative("_second").intValue = (byte)second;
            entry.FindPropertyRelative("_reaction").intValue = (byte)reaction;
        }
    }
}
