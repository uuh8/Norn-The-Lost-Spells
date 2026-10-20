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
            // 正式一致性不能受上一次工具窗口留下的SessionState审阅路径影响。
            _hub = EditorSceneManager.OpenPreviewScene(Formal);
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
            var hub=Find<HubInteractionController>(_hub);var definition=hub.Definition;
            var areas=new System.Collections.Generic.HashSet<HubAreaVolume>();
            foreach(var station in definition.Stations)
            {
                if(station.Kind==HubStationKind.Shop) { Assert.That(station.Position.z,Is.GreaterThan(10),"商店移到上方Building_1门前。");Assert.That(station.Position.z,Is.LessThan(24.2f)); }
                if(station.Kind==HubStationKind.Warehouse) { Assert.That(station.Position.z,Is.LessThan(0),"仓库移到下方Building_3木棚。");Assert.That(station.Position.z,Is.GreaterThan(-12.54f)); }
                // 远征由实际Portal用例覆盖；这三项设施按已保存Box检验，不能恢复作者删去的E悬浮牌。
                if(station.Kind==HubStationKind.Expedition)continue;
                string field=station.Kind==HubStationKind.Shop?"_shopArea":station.Kind==HubStationKind.Warehouse?"_warehouseArea":"_researchArea";
                var area=(HubAreaVolume)new SerializedObject(hub).FindProperty(field).objectReferenceValue;
                Assert.That(area,Is.Not.Null,station.DisplayName);Assert.That(area.Volume.isTrigger,Is.True);Assert.That(area.gameObject.scene,Is.EqualTo(_hub));
                Assert.That(areas.Add(area),Is.True,"设施不能共用同一Box。");
                // 作者已调整Box中心/尺寸；Runtime以实际Box为准，旧Station点不是区域中心约束。
                if(station.Kind==HubStationKind.Shop){Assert.That(area.Center.z,Is.GreaterThan(10));Assert.That(area.Center.z,Is.LessThan(24.2f));}
                if(station.Kind==HubStationKind.Warehouse){Assert.That(area.Center.z,Is.LessThan(0));Assert.That(area.Center.z,Is.GreaterThan(-12.54f));}
                Assert.That(area.Volume.size.x,Is.GreaterThan(0));Assert.That(area.Volume.size.y,Is.GreaterThan(0));Assert.That(area.Volume.size.z,Is.GreaterThan(0));
                int matches=0;foreach(var root in _hub.GetRootGameObjects())foreach(var volume in root.GetComponentsInChildren<HubAreaVolume>(true))if(volume==area)matches++;
                Assert.That(matches,Is.EqualTo(1),"必须找到唯一真实区域，不能零匹配假通过。");
            }
            Assert.That(areas.Count,Is.EqualTo(3));
            for(int i=0;i<definition.Stations.Length;i++) for(int j=i+1;j<definition.Stations.Length;j++)
                Assert.That(Vector3.Distance(definition.Stations[i].Position,definition.Stations[j].Position),Is.GreaterThan(definition.InteractionRadius*2),"站点范围不可重叠，否则同一个E位置可能被最近点规则抢占。");
        }
    }
}
