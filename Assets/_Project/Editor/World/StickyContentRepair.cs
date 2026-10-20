#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// 一次性、幂等地修复 Sticky Projectile 的内容资产。
    /// 由 AssetDatabase 创建 Material，让 Unity 自己维护 .meta/GUID；避免手写 YAML GUID 破坏引用。
    /// </summary>
    internal static class StickyContentRepair
    {
        private const string SourceMaterialPath =
            "Assets/_Project/Art/Elemental/Materials/M_ElementPoison_Projectile.mat";
        private const string StickyMaterialPath =
            "Assets/_Project/Art/Elemental/Materials/M_ElementSticky_Projectile.mat";
        private const string StickyProjectilePath =
            "Assets/_Project/Art/Elemental/Prefabs/PF_ElementStickyProjectile.prefab";

        [MenuItem("Tools/Game/Element/Repair Sticky Projectile Content")]
        private static void Repair()
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(StickyMaterialPath);
            if (material == null)
            {
                Material source = AssetDatabase.LoadAssetAtPath<Material>(SourceMaterialPath);
                if (source == null)
                    return;

                material = new Material(source)
                {
                    name = "M_ElementSticky_Projectile",
                };
                ConfigureAmber(material);
                AssetDatabase.CreateAsset(material, StickyMaterialPath);
            }

            GameObject root = PrefabUtility.LoadPrefabContents(StickyProjectilePath);
            if (root == null)
                return;

            try
            {
                MeshRenderer renderer = root.GetComponent<MeshRenderer>();
                if (renderer == null || renderer.sharedMaterial == material)
                    return;

                renderer.sharedMaterial = material;
                PrefabUtility.SaveAsPrefabAsset(root, StickyProjectilePath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void ConfigureAmber(Material material)
        {
            // ElementWater Shader 在普通 MeshRenderer 上使用 Mesh 顶点；这些属性只改变光照外观，
            // 与 GPU PBF Surface 的 StructuredBuffer/Procedural Draw 完全分离。
            material.SetColor("_DeepColor", new Color(0.49f, 0.17f, 0f, 1f));
            material.SetColor("_ShallowColor", new Color(1f, 0.76f, 0f, 1f));
            material.SetColor("_FresnelColor", new Color(1f, 0.57f, 0f, 1f));
            material.SetColor("_FoamColor", new Color(0.55f, 0.25f, 0f, 1f));
            material.SetFloat("_Smoothness", 0.9f);
            material.SetFloat("_WaveNormalStrength", 0.1f);
            material.SetFloat("_FoamStrength", 0.15f);
            EditorUtility.SetDirty(material);
        }
    }
}
#endif
