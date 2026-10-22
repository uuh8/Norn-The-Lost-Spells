using System;
using Game.Combat;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.EditorTools
{
    /// <summary>
    /// 把“箭矢可读性”限定为 Arrow Prefab 的一次性 Authoring 操作。
    /// 不手写 Prefab YAML/GUID，交给 Unity 的 PrefabUtility 保存，避免破坏已有引用。
    /// </summary>
    internal static class ArrowVisibilityAuthoringUtility
    {
        private const string ArrowPrefabPath = "Assets/_Project/Art/Prefabs/Arrow.prefab";
        private const string TrailMaterialPath = "Assets/_Project/Art/Materials/Mat_TrailFlat.mat";

        [MenuItem("Tools/Norn/Projectile Visibility/Configure Arrow Prefab")]
        private static void ConfigureArrowPrefab()
        {
            Material trailMaterial = AssetDatabase.LoadAssetAtPath<Material>(TrailMaterialPath);
            if (trailMaterial == null)
                throw new InvalidOperationException($"缺少箭矢轨迹材质：{TrailMaterialPath}");

            GameObject arrowRoot = PrefabUtility.LoadPrefabContents(ArrowPrefabPath);
            if (arrowRoot == null)
                throw new InvalidOperationException($"缺少 Arrow Prefab：{ArrowPrefabPath}");

            try
            {
                TrailRenderer trail = arrowRoot.GetComponent<TrailRenderer>();
                if (trail == null)
                    trail = arrowRoot.AddComponent<TrailRenderer>();

                ConfigureTrail(trail, trailMaterial);

                ArrowVisibility visibility = arrowRoot.GetComponent<ArrowVisibility>();
                if (visibility == null)
                    visibility = arrowRoot.AddComponent<ArrowVisibility>();
                visibility.Configure(trail);

                PrefabUtility.SaveAsPrefabAsset(arrowRoot, ArrowPrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(arrowRoot);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog(
                "Arrow Visibility",
                "Arrow Prefab 已加入短轨迹提示，箭矢模型本身保持原始材质。请进入 P7/P8 验证可见性。",
                "确定");
        }

        private static void ConfigureTrail(TrailRenderer trail, Material material)
        {
            // Trail 仅保留约 0.2 秒：足够让余光捕捉运动，且不会把画面变成持续的光带。
            trail.sharedMaterial = material;
            trail.time = 0.22f;
            trail.minVertexDistance = 0.04f;
            trail.widthMultiplier = 1f;
            trail.widthCurve = new AnimationCurve(
                new Keyframe(0f, 0.12f),
                new Keyframe(1f, 0.015f));
            trail.alignment = LineAlignment.View;
            trail.textureMode = LineTextureMode.Stretch;
            trail.generateLightingData = false;
            trail.shadowCastingMode = ShadowCastingMode.Off;
            trail.receiveShadows = false;
            trail.emitting = true;
            trail.autodestruct = false;
        }
    }
}
