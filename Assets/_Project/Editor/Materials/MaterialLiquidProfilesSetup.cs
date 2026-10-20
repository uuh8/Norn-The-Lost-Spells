using Game.Core;
using Game.ElementField;
using Game.Materials;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    internal static class MaterialLiquidProfilesSetup
    {
        private const string Key = "Game.Materials.Task22.Setup.V1";
        private const string CatalogPath = "Assets/_Project/ScriptableObjects/Materials/MaterialCatalog_Default.asset";
        private const string SimulationPath = "Assets/_Project/ScriptableObjects/ElementField/Fluid/LiquidSimulationProfile_Water.asset";
        private const string WaterPath = "Assets/_Project/ScriptableObjects/ElementField/Fluid/LiquidMaterialProfile_Water.asset";
        private const string PoisonPath = "Assets/_Project/ScriptableObjects/ElementField/Fluid/LiquidMaterialProfile_Poison.asset";

        [MenuItem("Tools/Material World/Setup Liquid Materials")]
        private static void Run()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += Run;
                return;
            }
            MaterialCatalog catalog = AssetDatabase.LoadAssetAtPath<MaterialCatalog>(CatalogPath);
            LiquidSimulationProfile simulation = AssetDatabase.LoadAssetAtPath<LiquidSimulationProfile>(SimulationPath);
            if (catalog == null || simulation == null)
            {
                GameLog.Error("[MATERIAL-TASK22-SETUP] Catalog 或 LiquidSimulationProfile 缺失。", "Editor");
                return;
            }

            LiquidMaterialProfile water = GetOrCreate(WaterPath);
            LiquidMaterialProfile poison = GetOrCreate(PoisonPath);
            // Task 22 收口后 LiquidMaterialProfile 是唯一参数源；重跑迁移工具也不得再从已移除的 global 字段覆盖玩家调参。
            if (AssetDatabase.GetAssetPath(water) == WaterPath && new SerializedObject(water).FindProperty("_material").intValue == 0)
                Configure(water, MaterialId.Water, 8, 1f, 1000f, 0.08f, 0.001f, 12f, 1f, 0.2f, 0.02f);
            if (new SerializedObject(poison).FindProperty("_material").intValue == 0)
                Configure(poison, MaterialId.Poison, 4, 1.1f, 1050f, 0.16f, 0.0015f, 14f, 1f, 0.2f, 0.025f);

            var source = new SerializedObject(simulation);

            source.FindProperty("_materialCatalog").objectReferenceValue = catalog;
            SerializedProperty materials = source.FindProperty("_liquidMaterials");
            materials.arraySize = 2;
            materials.GetArrayElementAtIndex(0).objectReferenceValue = water;
            materials.GetArrayElementAtIndex(1).objectReferenceValue = poison;
            source.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(simulation);
            AssetDatabase.SaveAssets();
            SessionState.SetBool(Key, true);
            GameLog.Info("[MATERIAL-TASK22-SETUP] SUCCESS Water+Poison rows bound to one Runtime profile.", "Editor");
        }

        private static LiquidMaterialProfile GetOrCreate(string path)
        {
            LiquidMaterialProfile value = AssetDatabase.LoadAssetAtPath<LiquidMaterialProfile>(path);
            if (value != null) return value;
            value = ScriptableObject.CreateInstance<LiquidMaterialProfile>();
            AssetDatabase.CreateAsset(value, path);
            return value;
        }

        private static void Configure(
            LiquidMaterialProfile profile,
            MaterialId material,
            int scale,
            float mass,
            float density,
            float viscosity,
            float pressure,
            float cohesion,
            float cohesionRatio,
            float maximumCohesionSpeed,
            float sleepDensityError)
        {
            var data = new SerializedObject(profile);
            data.FindProperty("_material").intValue = (byte)material;
            data.FindProperty("_amountUnitsPerParticle").intValue = scale;
            data.FindProperty("_particleMass").floatValue = mass;
            data.FindProperty("_restDensity").floatValue = density;
            data.FindProperty("_viscosity").floatValue = viscosity;
            data.FindProperty("_artificialPressure").floatValue = pressure;
            data.FindProperty("_cohesionStrength").floatValue = cohesion;
            data.FindProperty("_cohesionRestDistanceRatio").floatValue = cohesionRatio;
            data.FindProperty("_maximumCohesionDeltaSpeed").floatValue = maximumCohesionSpeed;
            data.FindProperty("_sleepDensityErrorThreshold").floatValue = sleepDensityError;
            data.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(profile);
        }
    }
}
