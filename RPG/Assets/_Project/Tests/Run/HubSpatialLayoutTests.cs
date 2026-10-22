using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Run.Tests
{
    public sealed class HubSpatialLayoutTests
    {
        private Scene _scene;
        [SetUp] public void Load() => _scene = EditorSceneManager.OpenPreviewScene(SessionState.GetString("Norn.HubAlignment.ReviewPath", "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity"));
        [TearDown] public void Close() { if (_scene.IsValid()) EditorSceneManager.ClosePreviewScene(_scene); }
        [Test] public void AllStationTextHasCameraFacingBehaviour()
        {
            int count = 0;
            foreach (var root in _scene.GetRootGameObjects())
                foreach(var node in root.GetComponentsInChildren<Transform>(true))
                if (node.name.EndsWith(" · E") || node.name == "实验区提示")
                    foreach (var text in node.GetComponentsInChildren<TextMesh>(true))
                    { count++; Assert.That(text.GetComponent("WorldSpaceLabelBillboard"), Is.Not.Null, node.name); }
            Assert.That(count, Is.GreaterThanOrEqualTo(3));
        }
        [Test] public void PersistentHintsAreAtScreenEdge()
        {
            int count = 0;
            foreach (var root in _scene.GetRootGameObjects())
                foreach (var text in root.GetComponentsInChildren<UnityEngine.UI.Text>(true))
                    if (text.name == "Prompt" || text.name == "ExtractionCondition" || text.name == "ExtractionTimer")
                    { count++; Assert.That(text.rectTransform.anchorMin.x, Is.EqualTo(0)); Assert.That(text.rectTransform.anchoredPosition.x, Is.LessThanOrEqualTo(32)); }
            Assert.That(count, Is.GreaterThanOrEqualTo(2));
        }
        [Test] public void ExpeditionIsAtSouthwestAndExperimentIsWestOfShop()
        {
            HubDefinition definition = null;
            foreach (var root in _scene.GetRootGameObjects())
            { var hub=root.GetComponentInChildren<HubInteractionController>(true); if(hub!=null)definition=hub.Definition; }
            Assert.That(definition,Is.Not.Null);
            Assert.That(definition.ExperimentCenter.x,Is.LessThan(-25));Assert.That(definition.ExperimentCenter.z,Is.GreaterThan(5));
            foreach(var station in definition.Stations) if(station.Kind==HubStationKind.Expedition)
            { Assert.That(station.Position.x,Is.LessThan(-25));Assert.That(station.Position.z,Is.LessThan(-30)); }
        }
    }
}
