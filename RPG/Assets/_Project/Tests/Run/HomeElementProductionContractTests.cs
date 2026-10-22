#if UNITY_EDITOR
using Game.ElementField;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Run.Tests
{
    public sealed class HomeElementProductionContractTests
    {
        private static T Find<T>(Scene scene)where T:Component
        {foreach(var root in scene.GetRootGameObjects()){var component=root.GetComponentInChildren<T>(true);if(component!=null)return component;}return null;}
        [Test] public void SavedHomeUsesProductionWorldRulesAndAllPhaseOwners()
        {
            var p7=EditorSceneManager.OpenPreviewScene("Assets/_Project/Scenes/P7_DemoRun.unity");
            var home=EditorSceneManager.OpenPreviewScene("Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Home.unity");
            try
            {
                var canonical=Find<ElementWorldRuntime>(p7);var actual=Find<ElementWorldRuntime>(home);Assert.That(actual,Is.Not.Null);
                var source=new SerializedObject(canonical);var target=new SerializedObject(actual);
                Assert.That(target.FindProperty("_profile").objectReferenceValue,Is.EqualTo(source.FindProperty("_profile").objectReferenceValue),"家应共享P7生产材料/后端/反应规则，而非旧容器模板");
                foreach(string field in new[]{"_fixedSolidRuntime","_gasRuntime","_gridLiquidRuntime","_conversionProfile"})Assert.That(target.FindProperty(field).objectReferenceValue,Is.Not.Null,field);
                foreach(string type in new[]{"GridLiquidFieldSurfaceRenderer","GasFieldVolumeRenderer","FixedSolidSurfaceRenderer"})
                    Assert.That(actual.GetComponent(type),Is.Not.Null,type);
            }
            finally{EditorSceneManager.ClosePreviewScene(home);EditorSceneManager.ClosePreviewScene(p7);}
        }
        [TestCase("ice")]
        [TestCase("dried")]
        public void SolidMaterialHasToolbeltThrowCapabilityAndVisibleModel(string name)
        {
            var path="Assets/_Project/ScriptableObjects/Items/Containers/Definitions/Item_Container_"+char.ToUpperInvariant(name[0])+name.Substring(1)+".asset";
            var item=AssetDatabase.LoadAssetAtPath<ItemDefinition>(path);Assert.That(item,Is.Not.Null,path);
            Assert.That(item.UseKind,Is.EqualTo(ItemUseKind.Throw));Assert.That(item.WorldModel,Is.Not.Null);
        }
    }
}
#endif
