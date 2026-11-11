using System.Reflection;
using Game.Character;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Run.Tests
{
    /// <summary>正式容量和输入冲突的外部契约；不保存Scene、不接触真实玩家档。</summary>
    public sealed class BackpackAuthoringTests
    {
        [TestCase("Assets/_Project/ScriptableObjects/Items/Backpacks/Item_Backpack_Bag.asset")]
        [TestCase("Assets/_Project/ScriptableObjects/Items/Backpacks/Item_Backpack_StarterBag.asset")]
        public void FormalBackpackHasSixtyFourRealCells(string path)
        {
            var bag = AssetDatabase.LoadAssetAtPath<ItemDefinition>(path);
            Assert.That(bag, Is.Not.Null);
            var data = new SerializedObject(bag);
            Assert.That(data.FindProperty("_backpackWidth").intValue, Is.EqualTo(8));
            Assert.That(data.FindProperty("_backpackHeight").intValue, Is.EqualTo(8));
        }
        [Test] public void ProfileSupportsPersistentToolBindings()
        {
            // 六位道具引用从Schema6开始保存；后续增加物品生成账不能使此旧契约要求回退版本。
            Assert.That(ProfileSaveMapper.CurrentSchema, Is.GreaterThanOrEqualTo(6));
            Assert.That(typeof(SinglePlayerSaveDto).GetField("ToolBindings"), Is.Not.Null);
        }
        [Test] public void ReferenceBindingsHaveGameplayOwner()
        {
            // 首次RED使用反射，避免缺少新类型让整个项目无法编译、无法保留用户Scene。
            Assert.That(typeof(SinglePlayerProfile).Assembly.GetType("Game.Run.ToolbeltService"), Is.Not.Null);
        }
        [Test] public void BackpackHasDedicatedLayoutAndIndependentWandPanels()
        {
            var assembly=typeof(Game.UI.SinglePlayerInventoryController).Assembly;
            Assert.That(assembly.GetType("Game.UI.BackpackWorkspaceView"),Is.Not.Null);
            Assert.That(assembly.GetType("Game.UI.WandProgramPanelView"),Is.Not.Null);
            Assert.That(assembly.GetType("Game.UI.ToolbeltView"),Is.Not.Null);
        }
        [Test] public void ThrowAndSolidCollectionDoNotShareG()
        {
            var go = new GameObject("Backpack input contract");
            try
            {
                var use = go.AddComponent<ContainerUseController>();
                var collection = go.AddComponent<MaterialCollectionController>();
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                // 已确认的操作是 F 采集、G 投掷；原 MaterialCollection 的 _solid/J 已退役。
                // 仍检查真实 InputAction 的明确绑定及不冲突，不能只靠字段存在宣称输入正确。
                var throwField = typeof(ContainerUseController).GetField("_throw", flags);
                var collectField = typeof(MaterialCollectionController).GetField("_collect", flags);
                Assert.That(throwField, Is.Not.Null);
                Assert.That(collectField, Is.Not.Null);
                // 普通 EditMode 不保证执行 Awake，显式建立仅供此测试的 Actions。
                if (throwField.GetValue(use) == null)
                    typeof(ContainerUseController).GetMethod("Awake", flags).Invoke(use, null);
                if (collectField.GetValue(collection) == null)
                    typeof(MaterialCollectionController).GetMethod("Awake", flags).Invoke(collection, null);
                var throwing = (InputAction)throwField.GetValue(use);
                var collecting = (InputAction)collectField.GetValue(collection);
                Assert.That(throwing, Is.Not.Null);
                Assert.That(collecting, Is.Not.Null);
                Assert.That(throwing.bindings[0].path, Is.EqualTo("<Keyboard>/g"));
                Assert.That(collecting.bindings[0].path, Is.EqualTo("<Keyboard>/f"));
                foreach (var binding in collecting.bindings)
                    Assert.That(binding.path, Is.Not.EqualTo("<Keyboard>/g"), "采集不能消费投掷的 G 绑定。");
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
