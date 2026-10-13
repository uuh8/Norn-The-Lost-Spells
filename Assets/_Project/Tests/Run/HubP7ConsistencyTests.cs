using Game.Character;
using Game.UI;
using NUnit.Framework;
using Unity.Cinemachine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace Game.Run.Tests
{
    /// <summary>读取磁盘最终Scene配置；Prefab相同仍可能被Scene override改变，必须逐项比较最终值。</summary>
    public sealed class HubP7ConsistencyTests
    {
        private Scene _p7, _hub;
        private const string Formal = "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity";
        [SetUp] public void Load()
        {
            _p7 = EditorSceneManager.OpenPreviewScene("Assets/_Project/Scenes/P7_DemoRun.unity");
            _hub = EditorSceneManager.OpenPreviewScene(SessionState.GetString("Norn.HubAlignment.ReviewPath", Formal));
        }
        [TearDown] public void Cleanup()
        {
            if (_p7.IsValid()) EditorSceneManager.ClosePreviewScene(_p7);
            if (_hub.IsValid()) EditorSceneManager.ClosePreviewScene(_hub);
        }
        private static T Find<T>(Scene scene) where T : Component
        { foreach (var root in scene.GetRootGameObjects()) { var value = root.GetComponentInChildren<T>(true); if (value != null) return value; } return null; }
        private static void EqualSettings(Object source, Object target)
        {
            Assert.That(source, Is.Not.Null); Assert.That(target, Is.Not.Null, source.GetType().Name);
            var expected = new SerializedObject(source); var actual = new SerializedObject(target);
            var property = expected.GetIterator();
            while (property.NextVisible(true))
            {
                if (!property.propertyPath.StartsWith("_") || property.propertyType == SerializedPropertyType.Generic) continue;
                // 局部Scene引用必须重绑自身对象；外部资产引用与所有参数仍必须保持一致。
                if (property.propertyType == SerializedPropertyType.ObjectReference && property.objectReferenceValue != null && !EditorUtility.IsPersistent(property.objectReferenceValue)) continue;
                var value = actual.FindProperty(property.propertyPath);
                Assert.That(value, Is.Not.Null, property.propertyPath);
                Assert.That(SerializedProperty.DataEquals(property, value), Is.True, source.GetType().Name + "." + property.propertyPath);
            }
        }
        [Test] public void PlayerMovementJumpDashAndAimMatchP7FinalOverrides()
        {
            var expected = Find<WizardController>(_p7); var actual = Find<WizardController>(_hub);
            EqualSettings(expected, actual); EqualSettings(expected.GetComponent<GroundChecker>(), actual.GetComponent<GroundChecker>());
            var a = expected.GetComponent<CharacterController>(); var b = actual.GetComponent<CharacterController>();
            Assert.That(b.height, Is.EqualTo(a.height)); Assert.That(b.radius, Is.EqualTo(a.radius)); Assert.That(b.center, Is.EqualTo(a.center));
            Assert.That(b.slopeLimit, Is.EqualTo(a.slopeLimit)); Assert.That(b.stepOffset, Is.EqualTo(a.stepOffset)); Assert.That(b.skinWidth, Is.EqualTo(a.skinWidth));
            Assert.That(actual.GetComponentInChildren<Animator>(true).runtimeAnimatorController, Is.EqualTo(expected.GetComponentInChildren<Animator>(true).runtimeAnimatorController));
            Assert.That(actual.transform.Find("CameraRoot").localPosition, Is.EqualTo(expected.transform.Find("CameraRoot").localPosition));
        }
        [Test] public void ThirdPersonLensAndObstacleHandlingMatchP7()
        {
            Assert.That(Find<CinemachineFollow>(_hub), Is.Null, "普通Follow和ThirdPersonFollow不能混用。");
            var expected = Find<CinemachineThirdPersonFollow>(_p7); var actual = Find<CinemachineThirdPersonFollow>(_hub);
            Assert.That(actual, Is.Not.Null);
            Assert.That(EditorJsonUtility.ToJson(actual), Does.Contain("ShoulderOffset"));
            Assert.That(actual.Damping, Is.EqualTo(expected.Damping)); Assert.That(actual.ShoulderOffset, Is.EqualTo(expected.ShoulderOffset));
            Assert.That(actual.VerticalArmLength, Is.EqualTo(expected.VerticalArmLength)); Assert.That(actual.CameraDistance, Is.EqualTo(expected.CameraDistance));
            Assert.That(actual.CameraSide, Is.EqualTo(expected.CameraSide));
            // 此处只比较避让参数，JSON不包括组件自身的GameObject引用，避免Scene identity造成假差异。
            Assert.That(JsonUtility.ToJson(actual.AvoidObstacles), Is.EqualTo(JsonUtility.ToJson(expected.AvoidObstacles)));
            Assert.That(JsonUtility.ToJson(Find<CinemachineCamera>(_hub).Lens), Is.EqualTo(JsonUtility.ToJson(Find<CinemachineCamera>(_p7).Lens)));
            Assert.That(Find<CinemachineCamera>(_hub).Target.TrackingTarget, Is.EqualTo(Find<WizardController>(_hub).transform.Find("CameraRoot")));
        }
        [Test] public void BackpackUsesOverlayTabAndShopHasOnlyStationOpening()
        {
            var ui = Find<SinglePlayerInventoryController>(_hub); var data = new SerializedObject(ui);
            var reference = (InputActionReference)data.FindProperty("_toggleAction").objectReferenceValue;
            Assert.That(reference.action.bindings[0].path, Is.EqualTo("<Keyboard>/tab"));
            Assert.That(ui.GetComponentInParent<Canvas>().renderMode, Is.EqualTo(RenderMode.ScreenSpaceOverlay));
            var shop = new SerializedObject(Find<ShopController>(_hub));
            Assert.That(shop.FindProperty("_toggleAction").objectReferenceValue, Is.Null);
            var button = (UnityEngine.UI.Button)shop.FindProperty("_openButton").objectReferenceValue;
            Assert.That(button == null || !button.gameObject.activeSelf, Is.True, "商店用近距离E站点入口。");
        }
        [Test] public void WarehouseAndShopMatchUserBuildingsWithSeparateInteractionRanges()
        {
            var definition=Find<HubInteractionController>(_hub).Definition;
            foreach(var station in definition.Stations)
            {
                if(station.Kind==HubStationKind.Shop) { Assert.That(station.Position.z,Is.GreaterThan(10),"商店移到上方Building_1门前。");Assert.That(station.Position.z,Is.LessThan(24.2f)); }
                if(station.Kind==HubStationKind.Warehouse) { Assert.That(station.Position.z,Is.LessThan(0),"仓库移到下方Building_3木棚。");Assert.That(station.Position.z,Is.GreaterThan(-12.54f)); }
                int markers=0;
                foreach(var root in _hub.GetRootGameObjects())foreach(var node in root.GetComponentsInChildren<Transform>(true)) if(node.name==station.DisplayName+" · E")
                {Assert.That(Vector3.Distance(node.position,station.Position),Is.LessThan(.0001f),"父层级转换存在float舍入，标牌仍须位于真实交互点。");markers++;}
                Assert.That(markers,Is.EqualTo(1),"分组后也必须找到唯一真实站点，不能零匹配假通过。");
            }
            for(int i=0;i<definition.Stations.Length;i++) for(int j=i+1;j<definition.Stations.Length;j++)
                Assert.That(Vector3.Distance(definition.Stations[i].Position,definition.Stations[j].Position),Is.GreaterThan(definition.InteractionRadius*2),"站点范围不可重叠，否则同一个E位置可能被最近点规则抢占。");
        }
    }
}
