using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.Combat;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.Run.Tests
{
    public sealed class BossHealthBarControllerTests
    {
        private readonly List<Object> _createdObjects = new List<Object>();
        private GameObject _hudRoot;
        private Text _nameText;
        private Text _phaseText;
        private Image _healthFill;
        private RectTransform _phase2Marker;
        private RectTransform _phase3Marker;
        private BossHealthBarController _controller;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            EventBus<BossHudInitializedEvent>.Clear();
            EventBus<BossPhaseChangedEvent>.Clear();
            EventBus<DamageReceivedEvent>.Clear();
            EventBus<DeathEvent>.Clear();
            EventBus<RunStateChangedEvent>.Clear();

            GameObject controllerObject = new GameObject("Boss HUD Controller");
            controllerObject.SetActive(false);
            _createdObjects.Add(controllerObject);
            _controller = controllerObject.AddComponent<BossHealthBarController>();

            // 阈值位置在共同UI父坐标系内计算，Fixture必须提供与生产HUD一致的RectTransform。
            _hudRoot = new GameObject("Boss HUD Root", typeof(RectTransform));
            _hudRoot.transform.SetParent(controllerObject.transform, false);
            _nameText = CreateUiComponent<Text>("Boss Name", _hudRoot.transform);
            _phaseText = CreateUiComponent<Text>("Phase", _hudRoot.transform);
            _healthFill = CreateUiComponent<Image>("Health Fill", _hudRoot.transform);
            _healthFill.type = Image.Type.Filled;
            _healthFill.rectTransform.sizeDelta = new Vector2(1000f, 100f);
            _phase2Marker = CreateUiComponent<Image>(
                "Phase 2 Marker",
                _hudRoot.transform).rectTransform;
            _phase3Marker = CreateUiComponent<Image>(
                "Phase 3 Marker",
                _hudRoot.transform).rectTransform;

            SetPrivateField(_controller, "_hudRoot", _hudRoot);
            SetPrivateField(_controller, "_bossNameText", _nameText);
            SetPrivateField(_controller, "_phaseText", _phaseText);
            SetPrivateField(_controller, "_healthFill", _healthFill);
            SetPrivateField(_controller, "_phase2ThresholdMarker", _phase2Marker);
            SetPrivateField(_controller, "_phase3ThresholdMarker", _phase3Marker);
            controllerObject.SetActive(true);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            EventBus<BossHudInitializedEvent>.Clear();
            EventBus<BossPhaseChangedEvent>.Clear();
            EventBus<DamageReceivedEvent>.Clear();
            EventBus<DeathEvent>.Clear();
            EventBus<RunStateChangedEvent>.Clear();

            for (int i = _createdObjects.Count - 1; i >= 0; i--)
            {
                if (_createdObjects[i] != null)
                {
                    Object.Destroy(_createdObjects[i]);
                }
            }

            _createdObjects.Clear();
            yield return null;
        }

        [UnityTest]
        public IEnumerator Initialize_ShowsDataAndPositionsPhaseMarkers()
        {
            Assert.That(_hudRoot.activeSelf, Is.False);

            PublishInitialization();
            yield return null;

            Assert.That(_hudRoot.activeSelf, Is.True);
            Assert.That(_nameText.text, Is.EqualTo("奥术大法师"));
            Assert.That(_phaseText.text, Is.EqualTo("Phase 1"));
            Assert.That(_healthFill.fillAmount, Is.EqualTo(1f).Within(0.001f));
            Assert.That(_phase2Marker.anchorMin.x, Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(_phase2Marker.anchoredPosition.x, Is.EqualTo(200f).Within(0.001f));
            Assert.That(_phase3Marker.anchorMin.x, Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(_phase3Marker.anchoredPosition.x, Is.EqualTo(-150f).Within(0.001f));
        }

        [UnityTest]
        public IEnumerator DamageAndPhaseEvents_FilterBossIdAndRefreshPresentation()
        {
            PublishInitialization();

            EventBus<DamageReceivedEvent>.Publish(new DamageReceivedEvent
            {
                TargetId = 999,
                RemainingHp = 100f,
            });
            Assert.That(_healthFill.fillAmount, Is.EqualTo(1f).Within(0.001f));

            EventBus<DamageReceivedEvent>.Publish(new DamageReceivedEvent
            {
                TargetId = 42,
                RemainingHp = 500f,
            });
            EventBus<BossPhaseChangedEvent>.Publish(new BossPhaseChangedEvent
            {
                BossId = 42,
                PreviousPhase = 1,
                CurrentPhase = 2,
                HealthRatio = 0.5f,
            });
            yield return null;

            Assert.That(_healthFill.fillAmount, Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(_phaseText.text, Is.EqualTo("Phase 2"));
        }

        [UnityTest]
        public IEnumerator BossDeathOrTerminalRun_HidesHud()
        {
            PublishInitialization();
            Assert.That(_hudRoot.activeSelf, Is.True);

            EventBus<DeathEvent>.Publish(new DeathEvent { TargetId = 42 });
            Assert.That(_hudRoot.activeSelf, Is.False);

            PublishInitialization();
            EventBus<RunStateChangedEvent>.Publish(new RunStateChangedEvent
            {
                PreviousState = RunState.Running,
                CurrentState = RunState.Failed,
                CurrentEncounterIndex = -1,
            });
            yield return null;

            Assert.That(_hudRoot.activeSelf, Is.False);
        }

        [UnityTest]
        public IEnumerator NamedHierarchy_AutoBindsButPreservesAuthoredBossHudLayout()
        {
            GameObject canvas = CreateRectObject("UICanvas", null);
            GameObject controllerObject =
                CreateRectObject("BossHUDController", canvas.transform);
            GameObject hudRoot =
                CreateRectObject("BossHUDRoot", controllerObject.transform);
            Text nameText = CreateUiComponent<Text>(
                "BossNameText",
                hudRoot.transform);
            Text phaseText = CreateUiComponent<Text>(
                "PhaseText",
                hudRoot.transform);
            GameObject healthBarRoot =
                CreateRectObject("HealthBarRoot", hudRoot.transform);
            Image background = CreateUiComponent<Image>(
                "Background",
                healthBarRoot.transform);
            Image healthFill = CreateUiComponent<Image>(
                "HealthFill",
                healthBarRoot.transform);
            RectTransform phase2Marker = CreateMarker(
                "Phase2MarkerRoot",
                "70%",
                healthBarRoot.transform);
            RectTransform phase3Marker = CreateMarker(
                "Phase3MarkerRoot",
                "35%",
                healthBarRoot.transform);

            // Task 7 的 HUD 由 Designer 在 Scene/Prefab 中完成纵向排版：
            // 名称与 Phase 在血条上方，百分比标签在血条下方。
            // Runtime 只拥有动态数据，不应在 Awake 中覆盖这些美术 Authoring 值。
            RectTransform healthRootRect =
                healthBarRoot.GetComponent<RectTransform>();
            RectTransform hudRect =
                hudRoot.GetComponent<RectTransform>();
            hudRect.anchoredPosition = new Vector2(0f, 425f);
            hudRect.sizeDelta = new Vector2(1366f, 230f);
            nameText.rectTransform.anchoredPosition =
                new Vector2(0f, -46f);
            phaseText.rectTransform.anchoredPosition =
                new Vector2(0f, -92f);
            healthRootRect.anchoredPosition =
                new Vector2(0f, -188f);
            healthRootRect.sizeDelta = new Vector2(1366f, 143f);
            healthFill.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            healthFill.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            healthFill.rectTransform.anchoredPosition =
                new Vector2(12f, 50f);
            healthFill.rectTransform.sizeDelta =
                new Vector2(1300f, 100f);
            phase2Marker.anchoredPosition =
                new Vector2(329f, -21f);
            phase3Marker.anchoredPosition =
                new Vector2(-182f, -21f);

            Vector2 authoredHudPosition = hudRect.anchoredPosition;
            Vector2 authoredHudSize = hudRect.sizeDelta;
            Vector2 authoredNamePosition =
                nameText.rectTransform.anchoredPosition;
            Vector2 authoredPhasePosition =
                phaseText.rectTransform.anchoredPosition;
            Vector2 authoredHealthPosition =
                healthRootRect.anchoredPosition;
            Vector2 authoredHealthSize = healthRootRect.sizeDelta;

            BossHealthBarController controller =
                controllerObject.AddComponent<BossHealthBarController>();
            Assert.That(controller, Is.Not.Null);
            yield return null;

            EventBus<BossHudInitializedEvent>.Publish(
                new BossHudInitializedEvent
                {
                    BossId = 84,
                    DisplayName = "自动绑定 Boss",
                    CurrentHp = 700f,
                    MaxHp = 1000f,
                    CurrentPhase = 2,
                    Phase2Threshold = 0.7f,
                    Phase3Threshold = 0.35f,
                });
            yield return null;

            Assert.That(hudRoot.activeSelf, Is.True);
            Assert.That(nameText.text, Is.EqualTo("自动绑定 Boss"));
            Assert.That(phaseText.text, Is.EqualTo("Phase 2"));
            Assert.That(healthFill.fillAmount, Is.EqualTo(0.7f).Within(0.001f));

            Assert.That(hudRect.anchoredPosition, Is.EqualTo(authoredHudPosition));
            Assert.That(hudRect.sizeDelta, Is.EqualTo(authoredHudSize));
            Assert.That(
                nameText.rectTransform.anchoredPosition,
                Is.EqualTo(authoredNamePosition));
            Assert.That(
                phaseText.rectTransform.anchoredPosition,
                Is.EqualTo(authoredPhasePosition));
            Assert.That(
                healthRootRect.anchoredPosition,
                Is.EqualTo(authoredHealthPosition));
            Assert.That(healthRootRect.sizeDelta, Is.EqualTo(authoredHealthSize));
            Assert.That(
                phase2Marker.anchorMin.x,
                Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(
                phase2Marker.anchoredPosition.x,
                Is.EqualTo(272f).Within(0.001f));
            Assert.That(
                phase2Marker.anchoredPosition.y,
                Is.EqualTo(50f).Within(0.001f));
            Assert.That(
                phase3Marker.anchorMin.x,
                Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(
                phase3Marker.anchoredPosition.x,
                Is.EqualTo(-183f).Within(0.001f));
            Assert.That(
                phase3Marker.anchoredPosition.y,
                Is.EqualTo(50f).Within(0.001f));

            AssertMarkerChildrenAreProgrammaticallyStacked(phase2Marker);
            AssertMarkerChildrenAreProgrammaticallyStacked(phase3Marker);
        }

        [UnityTest]
        public IEnumerator BossScenePolicy_HidesP7OnlyRootsButKeepsSharedWandRoot()
        {
            GameObject canvas = CreateRectObject("UICanvas", null);
            GameObject demoRunRoot =
                CreateRectObject("DemoRunUIRoot", canvas.transform);
            GameObject gameplayRoot =
                CreateRectObject("GameplayHUD", demoRunRoot.transform);
            GameObject notificationRoot =
                CreateRectObject("NotificationRoot", demoRunRoot.transform);
            GameObject wandRoot =
                CreateRectObject("WandEditorRoot", demoRunRoot.transform);
            GameObject wandPanel =
                CreateRectObject("PanelRoot", wandRoot.transform);
            WandEditorController wandEditor =
                wandRoot.AddComponent<WandEditorController>();
            SetPrivateField(wandEditor, "_panelRoot", wandPanel);
            demoRunRoot.SetActive(false);

            GameObject controllerObject =
                CreateRectObject("BossHUDController", canvas.transform);
            GameObject hudRoot =
                CreateRectObject("BossHUDRoot", controllerObject.transform);
            CreateUiComponent<Text>("BossNameText", hudRoot.transform);
            CreateUiComponent<Text>("PhaseText", hudRoot.transform);
            GameObject healthBarRoot =
                CreateRectObject("HealthBarRoot", hudRoot.transform);
            CreateUiComponent<Image>("Background", healthBarRoot.transform);
            CreateUiComponent<Image>("HealthFill", healthBarRoot.transform);
            CreateMarker("Phase2MarkerRoot", "70%", healthBarRoot.transform);
            CreateMarker("Phase3MarkerRoot", "35%", healthBarRoot.transform);

            controllerObject.AddComponent<BossHealthBarController>();
            yield return null;

            Assert.That(demoRunRoot.activeSelf, Is.True);
            Assert.That(gameplayRoot.activeSelf, Is.False);
            Assert.That(notificationRoot.activeSelf, Is.False);
            Assert.That(wandRoot.activeSelf, Is.True);
            Assert.That(wandPanel.activeSelf, Is.False);

            wandEditor.Toggle();
            Assert.That(wandPanel.activeSelf, Is.True,
                "P8 只能隐藏 P7 专属 HUD，不能让共享 WandEditorController 失去启用状态。");
            wandEditor.ForceClose();
        }

        private void PublishInitialization()
        {
            EventBus<BossHudInitializedEvent>.Publish(
                new BossHudInitializedEvent
                {
                    BossId = 42,
                    DisplayName = "奥术大法师",
                    CurrentHp = 1000f,
                    MaxHp = 1000f,
                    CurrentPhase = 1,
                    Phase2Threshold = 0.7f,
                    Phase3Threshold = 0.35f,
                });
        }

        private T CreateUiComponent<T>(string objectName, Transform parent)
            where T : Component
        {
            GameObject uiObject = new GameObject(
                objectName,
                typeof(RectTransform),
                typeof(CanvasRenderer));
            uiObject.transform.SetParent(parent, false);
            _createdObjects.Add(uiObject);
            return uiObject.AddComponent<T>();
        }

        private RectTransform CreateMarker(
            string markerName,
            string label,
            Transform parent)
        {
            GameObject markerRoot = CreateRectObject(markerName, parent);
            Image markerLine =
                CreateUiComponent<Image>("MarkerLine", markerRoot.transform);
            markerLine.rectTransform.sizeDelta = new Vector2(100f, 80f);
            markerLine.rectTransform.localScale =
                new Vector3(0.5f, 0.5f, 1f);
            Text markerLabel = CreateUiComponent<Text>(
                "MarkerLabel",
                markerRoot.transform);
            markerLabel.text = label;
            return markerRoot.GetComponent<RectTransform>();
        }

        private static void AssertMarkerChildrenAreProgrammaticallyStacked(
            RectTransform marker)
        {
            RectTransform markerLine =
                marker.Find("MarkerLine") as RectTransform;
            RectTransform markerLabel =
                marker.Find("MarkerLabel") as RectTransform;

            Assert.That(markerLine, Is.Not.Null);
            Assert.That(markerLabel, Is.Not.Null);
            Assert.That(
                markerLine.anchoredPosition,
                Is.EqualTo(Vector2.zero));
            Assert.That(
                markerLabel.anchoredPosition.x,
                Is.Zero.Within(0.001f));
            Assert.That(
                markerLabel.pivot,
                Is.EqualTo(new Vector2(0.5f, 1f)));
            Assert.That(
                markerLabel.anchoredPosition.y,
                Is.EqualTo(-24f).Within(0.001f),
                "MarkerLine显示高度80*0.5=40，中心到下沿为半高20，再留4px间距。");
        }

        private GameObject CreateRectObject(
            string objectName,
            Transform parent)
        {
            GameObject uiObject = new GameObject(
                objectName,
                typeof(RectTransform));
            if (parent != null)
            {
                uiObject.transform.SetParent(parent, false);
            }

            _createdObjects.Add(uiObject);
            return uiObject;
        }

        private static void SetPrivateField(
            object target,
            string fieldName,
            object value)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(target, value);
        }
    }
}
