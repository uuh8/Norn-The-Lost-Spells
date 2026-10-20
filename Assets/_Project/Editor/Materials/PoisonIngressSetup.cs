using Game.Core;
using Game.ElementField;
using Game.Materials;
using Game.Skills;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    internal static class PoisonIngressSetup
    {
        private const string Key = "Game.Materials.Task23.Setup.V1";
        private const string CatalogPath = "Assets/_Project/ScriptableObjects/Materials/MaterialCatalog_Default.asset";
        private const string RoutingPath = "Assets/_Project/ScriptableObjects/Materials/MaterialSimulationRouting_Default.asset";
        private const string ProjectionPath = "Assets/_Project/ScriptableObjects/Materials/MaterialStatusProjection_Default.asset";
        private const string WaterPrefabPath = "Assets/_Project/Art/Elemental/Prefabs/PF_ElementWaterProjectile.prefab";
        private const string PoisonPrefabPath = "Assets/_Project/Art/Elemental/Prefabs/PF_ElementPoisonProjectile.prefab";
        private const string WaterSpellPath = "Assets/_Project/ScriptableObjects/Spell/Definitions/Spell_Emit_WaterField.asset";
        private const string PoisonSpellPath = "Assets/_Project/ScriptableObjects/Spell/Definitions/Spell_Emit_PoisonProjectile.asset";
        private const string WandPath = "Assets/_Project/ScriptableObjects/Spell/Programs/Validation/Wand_Poison.asset";

        [MenuItem("Tools/Material World/Setup Poison Ingress")]
        private static void Run()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) { EditorApplication.delayCall += Run; return; }
            MaterialCatalog catalog = AssetDatabase.LoadAssetAtPath<MaterialCatalog>(CatalogPath);
            MaterialSimulationRoutingProfile routing = AssetDatabase.LoadAssetAtPath<MaterialSimulationRoutingProfile>(RoutingPath);
            if (catalog == null || routing == null) { GameLog.Error("[MATERIAL-TASK23-SETUP] Catalog/Routing missing.", "Editor"); return; }

            var routeData = new SerializedObject(routing);
            SerializedProperty routes = routeData.FindProperty("_routes");
            routes.arraySize = 2;
            SetRoute(routes.GetArrayElementAtIndex(0), MaterialId.Fire, MaterialSimulationBackendKind.ElementCell);
            SetRoute(routes.GetArrayElementAtIndex(1), MaterialId.Poison, MaterialSimulationBackendKind.GpuPbfLiquid);
            routeData.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(routing);

            MaterialStatusProjectionProfile projection = AssetDatabase.LoadAssetAtPath<MaterialStatusProjectionProfile>(ProjectionPath);
            if (projection == null) { projection = ScriptableObject.CreateInstance<MaterialStatusProjectionProfile>(); AssetDatabase.CreateAsset(projection, ProjectionPath); }
            var projectionData = new SerializedObject(projection);
            SerializedProperty bindings = projectionData.FindProperty("_bindings");
            bindings.arraySize = 3;
            SetProjection(bindings.GetArrayElementAtIndex(0), MaterialId.Water, Game.Combat.StatusKind.Wet, 10f);
            SetProjection(bindings.GetArrayElementAtIndex(1), MaterialId.Fire, Game.Combat.StatusKind.Burning, 10f);
            SetProjection(bindings.GetArrayElementAtIndex(2), MaterialId.Poison, Game.Combat.StatusKind.Poisoned, 10f);
            projectionData.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(projection);

            string[] worlds = AssetDatabase.FindAssets("t:ElementWorldProfile");
            for (int i = 0; i < worlds.Length; i++)
            {
                ElementWorldProfile profile = AssetDatabase.LoadAssetAtPath<ElementWorldProfile>(AssetDatabase.GUIDToAssetPath(worlds[i]));
                var data = new SerializedObject(profile);
                data.FindProperty("_materialStatusProjection").objectReferenceValue = projection;
                data.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(profile);
            }

            CreatePoisonContent();
            AssetDatabase.SaveAssets();
            SessionState.SetBool(Key, true);
            GameLog.Info($"[MATERIAL-TASK23-SETUP] SUCCESS Profiles={worlds.Length}", "Editor");
        }

        private static void CreatePoisonContent()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PoisonPrefabPath) == null)
                AssetDatabase.CopyAsset(WaterPrefabPath, PoisonPrefabPath);
            GameObject root = PrefabUtility.LoadPrefabContents(PoisonPrefabPath);
            try
            {
                ElementDepositOnImpact deposit = root.GetComponent<ElementDepositOnImpact>();
                var data = new SerializedObject(deposit);
                data.FindProperty("_materialKind").intValue = (byte)MaterialId.Poison;
                data.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, PoisonPrefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }

            if (AssetDatabase.LoadAssetAtPath<SpellDefinition>(PoisonSpellPath) == null)
                AssetDatabase.CopyAsset(WaterSpellPath, PoisonSpellPath);
            SpellDefinition spell = AssetDatabase.LoadAssetAtPath<SpellDefinition>(PoisonSpellPath);
            spell.name = "Spell_Emit_PoisonProjectile";
            spell.DisplayName = "毒液元素球";
            spell.Description = "发射毒液并在命中处写入共享 GPU PBF Liquid Pool。";
            spell.ProjectilePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PoisonPrefabPath);
            EditorUtility.SetDirty(spell);

            WandLoadout wand = AssetDatabase.LoadAssetAtPath<WandLoadout>(WandPath);
            if (wand == null) { wand = ScriptableObject.CreateInstance<WandLoadout>(); AssetDatabase.CreateAsset(wand, WandPath); }
            wand.Spells = new[] { spell };
            wand.BaseDraws = 1;
            EditorUtility.SetDirty(wand);
        }

        private static void SetRoute(SerializedProperty entry, MaterialId material, MaterialSimulationBackendKind backend)
        {
            entry.FindPropertyRelative("_material").intValue = (byte)material;
            entry.FindPropertyRelative("_backend").intValue = (byte)backend;
        }

        private static void SetProjection(SerializedProperty entry, MaterialId material, Game.Combat.StatusKind status, float amount)
        {
            entry.FindPropertyRelative("Material").intValue = (byte)material;
            entry.FindPropertyRelative("Status").intValue = (byte)status;
            entry.FindPropertyRelative("MaximumApplyPerExposureTick").floatValue = amount;
        }
    }
}
