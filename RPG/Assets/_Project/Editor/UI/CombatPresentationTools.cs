using System;
using Game.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Project.Editor
{
    /// <summary>
    /// 战斗区域的 Editor 装配边界。单机使用现有 Run/练习工作区自动登记；联机区域只安装一个
    /// 读取认证后角色事实的 Scope，不复制 HP、背包或输入控制器，也不创建第二套网络 UI。
    /// </summary>
    public static class CombatPresentationTools
    {
        public static void InstallOnline(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) throw new InvalidOperationException("战斗区域必须已加载。");
            CombatPresentationScope scope = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                var found = root.GetComponentsInChildren<CombatPresentationScope>(true);
                for (int i = 0; i < found.Length; i++)
                {
                    if (scope != null) throw new InvalidOperationException("同一区域存在多个战斗表现 Scope，拒绝重复接线。");
                    scope = found[i];
                }
            }
            if (scope == null)
            {
                var root = new GameObject("CombatPresentationScope");
                SceneManager.MoveGameObjectToScene(root, scene);
                scope = root.AddComponent<CombatPresentationScope>();
            }
            // SerializedObject 将 Editor 配置写入实际 Scene；运行时无需反射，也不会根据场景名称判断玩法模式。
            var data = new SerializedObject(scope);
            data.FindProperty("_online").boolValue = true;
            data.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
