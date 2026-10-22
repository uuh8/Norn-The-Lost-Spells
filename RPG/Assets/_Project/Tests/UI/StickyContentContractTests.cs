using System.Reflection;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.UI.Tests
{
    /// <summary>
    /// 锁定 Sticky UI 与 Projectile 的资源接线。这里验证“引用属于谁”，而不是只验证字段非空；
    /// 否则引用到场景里另一个有效 Image/Text 时，Null Check 仍会通过但画面不会更新。
    /// </summary>
    public sealed class StickyContentContractTests
    {
        private const string P7ScenePath = "Assets/_Project/Scenes/P7_DemoRun.unity";
        private const string P8ScenePath = "Assets/_Project/Scenes/P8_BossField.unity";
        private const string ProjectilePath =
            "Assets/_Project/Art/Elemental/Prefabs/PF_ElementStickyProjectile.prefab";

        [TestCase(P7ScenePath)]
        [TestCase(P8ScenePath)]
        public void EveryStatusViewReferencesItsOwnChildIconAndPercentageText(string scenePath)
        {
            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            try
            {
                PlayerStatusBar bar = FindInScene<PlayerStatusBar>(scene);
                Assert.That(bar, Is.Not.Null);
                var icons = new System.Collections.Generic.HashSet<int>();
                var texts = new System.Collections.Generic.HashSet<int>();
                AssertViewOwnsPresentation(bar, "_burningView", icons, texts);
                AssertViewOwnsPresentation(bar, "_wetView", icons, texts);
                AssertViewOwnsPresentation(bar, "_poisonedView", icons, texts);
                AssertViewOwnsPresentation(bar, "_stickyView", icons, texts);
                Assert.That(icons, Has.Count.EqualTo(4),
                    $"{scenePath} 的四种状态不能共享同一个 Image。共享后后写入的状态会覆盖先写入状态。");
                Assert.That(texts, Has.Count.EqualTo(4),
                    $"{scenePath} 的四种状态不能共享同一个百分比 Text。");
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static void AssertViewOwnsPresentation(
            PlayerStatusBar bar,
            string viewFieldName,
            System.Collections.Generic.HashSet<int> icons,
            System.Collections.Generic.HashSet<int> texts)
        {
            StatusIconView view = GetField<StatusIconView>(bar, viewFieldName);
            Assert.That(view, Is.Not.Null, $"{viewFieldName} 没有绑定 StatusIconView。");
            Image icon = GetField<Image>(view, "_icon");
            Text percentage = GetField<Text>(view, "_percentageText");

            Assert.That(icon, Is.Not.Null, $"{viewFieldName} 没有绑定 Image。");
            Assert.That(percentage, Is.Not.Null, $"{viewFieldName} 没有绑定百分比 Text。");
            Assert.That(icon.transform.IsChildOf(view.transform), Is.True,
                $"{viewFieldName} 的 Icon 必须属于自己的 Slot，不能引用相邻状态的子对象。");
            Assert.That(percentage.transform.IsChildOf(view.transform), Is.True,
                $"{viewFieldName} 的百分比必须属于自己的 Slot，不能引用相邻状态的子对象。");
            icons.Add(icon.GetInstanceID());
            texts.Add(percentage.GetInstanceID());
        }

        [Test]
        public void StickyProjectileUsesMeshCompatibleMaterialInsteadOfProceduralSurfaceShader()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ProjectilePath);
            Assert.That(prefab, Is.Not.Null);
            MeshRenderer renderer = prefab.GetComponent<MeshRenderer>();
            Assert.That(renderer, Is.Not.Null);
            Assert.That(renderer.sharedMaterial, Is.Not.Null);
            Assert.That(renderer.sharedMaterial.shader.name,
                Is.Not.EqualTo("Game/Elemental/Liquid Procedural"));
        }

        private static T GetField<T>(object owner, string fieldName) where T : Object
        {
            FieldInfo field = owner.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            return field.GetValue(owner) as T;
        }

        private static T FindInScene<T>(Scene scene) where T : Component
        {
            T[] components = Object.FindObjectsByType<T>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            for (int i = 0; i < components.Length; i++)
            {
                T component = components[i];
                if (component != null && component.gameObject.scene == scene)
                    return component;
            }
            return null;
        }
    }
}
