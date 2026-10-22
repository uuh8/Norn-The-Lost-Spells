using Game.Core;
using Game.Materials;
using Game.Rendering;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.EditorTools
{
    internal static class PoisonSurfaceSetup
    {
        private const string Key = "Game.Materials.Task24.Setup.V1";
        private const string WaterProfile = "Assets/_Project/ScriptableObjects/ElementField/Fluid/LiquidRenderProfile_Water.asset";
        private const string PoisonProfile = "Assets/_Project/ScriptableObjects/ElementField/Fluid/LiquidRenderProfile_Poison.asset";
        private const string WaterMaterial = "Assets/_Project/Art/Elemental/Materials/M_ElementWater_PBF.mat";
        private const string PoisonMaterial = "Assets/_Project/Art/Elemental/Materials/M_ElementPoison_PBF.mat";
        private const string Sandbox = "Assets/_Project/Scenes/PBF_FluidSandbox.unity";

        [MenuItem("Tools/Material World/Setup Poison Surface")]
        private static void Run()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) { EditorApplication.delayCall += Run; return; }
            if (AssetDatabase.LoadAssetAtPath<LiquidRenderProfile>(PoisonProfile) == null)
                AssetDatabase.CopyAsset(WaterProfile, PoisonProfile);
            if (AssetDatabase.LoadAssetAtPath<Material>(PoisonMaterial) == null)
                AssetDatabase.CopyAsset(WaterMaterial, PoisonMaterial);
            Material poison = AssetDatabase.LoadAssetAtPath<Material>(PoisonMaterial);
            poison.name = "M_ElementPoison_PBF";
            SetColor(poison, "_ShallowColor", new Color(0.45f, 0.95f, 0.20f, 0.72f));
            SetColor(poison, "_DeepColor", new Color(0.05f, 0.28f, 0.03f, 0.88f));
            EditorUtility.SetDirty(poison);

            Scene scene = SceneManager.GetSceneByPath(Sandbox);
            bool openedHere = !scene.IsValid() || !scene.isLoaded;
            if (openedHere) scene = EditorSceneManager.OpenScene(Sandbox, OpenSceneMode.Additive);
            GpuLiquidSurfaceRenderer water = null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                GpuLiquidSurfaceRenderer[] renderers = root.GetComponentsInChildren<GpuLiquidSurfaceRenderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    var data = new SerializedObject(renderers[i]);
                    MaterialId target = (MaterialId)data.FindProperty("_targetMaterial").intValue;
                    if (target == MaterialId.Poison) { water = null; goto Save; }
                    if (target == MaterialId.Water) water = renderers[i];
                }
            }
            if (water != null)
            {
                GpuLiquidSurfaceRenderer poisonRenderer = water.gameObject.AddComponent<GpuLiquidSurfaceRenderer>();
                EditorUtility.CopySerialized(water, poisonRenderer);
                var data = new SerializedObject(poisonRenderer);
                data.FindProperty("_targetMaterial").intValue = (byte)MaterialId.Poison;
                data.FindProperty("_profile").objectReferenceValue = AssetDatabase.LoadAssetAtPath<LiquidRenderProfile>(PoisonProfile);
                data.FindProperty("_material").objectReferenceValue = poison;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
        Save:
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            if (openedHere) EditorSceneManager.CloseScene(scene, true);
            AssetDatabase.SaveAssets();
            SessionState.SetBool(Key, true);
            GameLog.Info("[MATERIAL-TASK24-SETUP] SUCCESS Poison surface uses shared GPU source.", "Editor");
        }

        private static void SetColor(Material material, string property, Color value)
        {
            if (material.HasProperty(property)) material.SetColor(property, value);
        }
    }
}
