using Game.Core;
using Game.ElementField;
using Game.Materials;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// Task 19 的最小 Authoring 工具：只创建默认 Route Profile 并绑定 P7 World Profile，
    /// 不重跑旧 PBF Setup，避免覆盖玩家已经调整过的 Simulation/Render 参数。
    /// </summary>
    internal static class MaterialSimulationRoutingSetup
    {
        private const string CatalogPath =
            "Assets/_Project/ScriptableObjects/Materials/MaterialCatalog_Default.asset";
        private const string RoutingPath =
            "Assets/_Project/ScriptableObjects/Materials/MaterialSimulationRouting_Default.asset";
        private const string WorldProfilePath =
            "Assets/_Project/ScriptableObjects/ElementField/Fluid/ElementWorldProfile_P7_PBF.asset";

        [MenuItem("Tools/Material World/Setup Routing")]
        private static void Run()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += Run;
                return;
            }

            MaterialCatalog catalog = AssetDatabase.LoadAssetAtPath<MaterialCatalog>(CatalogPath);
            ElementWorldProfile world = AssetDatabase.LoadAssetAtPath<ElementWorldProfile>(WorldProfilePath);
            if (catalog == null || world == null)
            {
                GameLog.Error("[MATERIAL-TASK19-SETUP] Catalog 或 P7 World Profile 缺失。", "Editor");
                return;
            }

            MaterialSimulationRoutingProfile routing =
                AssetDatabase.LoadAssetAtPath<MaterialSimulationRoutingProfile>(RoutingPath);
            if (routing == null)
            {
                routing = ScriptableObject.CreateInstance<MaterialSimulationRoutingProfile>();
                AssetDatabase.CreateAsset(routing, RoutingPath);
            }

            var routeData = new SerializedObject(routing);
            SerializedProperty routes = routeData.FindProperty("_routes");
            routes.arraySize = 2;
            SetRoute(routes.GetArrayElementAtIndex(0), MaterialId.Fire, MaterialSimulationBackendKind.ElementCell);
            SetRoute(routes.GetArrayElementAtIndex(1), MaterialId.Poison, MaterialSimulationBackendKind.Unsupported);
            routeData.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(routing);

            var worldData = new SerializedObject(world);
            worldData.FindProperty("_materialCatalog").objectReferenceValue = catalog;
            worldData.FindProperty("_materialSimulationRouting").objectReferenceValue = routing;
            worldData.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(world);

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(RoutingPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(WorldProfilePath, ImportAssetOptions.ForceSynchronousImport);
            GameLog.Info("[MATERIAL-TASK19-SETUP] SUCCESS", "Editor");
        }

        private static void SetRoute(
            SerializedProperty route,
            MaterialId material,
            MaterialSimulationBackendKind backend)
        {
            route.FindPropertyRelative("_material").intValue = (byte)material;
            route.FindPropertyRelative("_backend").intValue = (byte)backend;
        }
    }
}
