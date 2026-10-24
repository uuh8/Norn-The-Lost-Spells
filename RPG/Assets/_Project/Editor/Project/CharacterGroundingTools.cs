using System;
using Game.Core;
using UnityEditor;
using UnityEngine;

namespace Game.Project.Editor
{
    /// <summary>修正经真实 Hub BakeMesh 证实的 Wizard 视觉间隙；不改变 CharacterController 的运动语义。</summary>
    public static class CharacterGroundingTools
    {
        private const string Wizard = "Assets/_Project/Art/Prefabs/WizardPlayer.prefab";

        [MenuItem("Tools/Norn/Character/Align Wizard Feet")]
        public static void AlignWizardFeet()
        {
            OnlineSceneSetup.RequireCleanEditor();
            var root = PrefabUtility.LoadPrefabContents(Wizard);
            try
            {
                var controller = root.GetComponent<CharacterController>();
                var animator = root.GetComponentInChildren<Animator>(true);
                if (controller == null || animator == null || animator.transform.name != "MagicWand01")
                    throw new InvalidOperationException("Wizard Prefab 的物理根或 MagicWand01 模型结构已改变，需重新测量。");
                var position = animator.transform.localPosition;
                float target = -controller.skinWidth;
                // 真实 Hub 测得胶囊底高于地面 .08m，鞋底约等于模型原点；抵消 Skin 的视觉间隙。
                // 只接受已测量的旧 .1m 或本工具的结果，防止后来美术改版时误覆盖新的绑定。
                if (!Mathf.Approximately(position.y, .1f) && !Mathf.Approximately(position.y, target))
                    throw new InvalidOperationException("Wizard 模型偏移不属于已验证基线，请先重跑鞋底 BakeMesh 测量。");
                position.y = target;
                animator.transform.localPosition = position;
                PrefabUtility.SaveAsPrefabAsset(root, Wizard);
                GameLog.Info("Wizard 模型 local Y = " + target + "；CC 高度、Skin、台阶与重力均保留。", "CharacterGrounding");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
    }
}
