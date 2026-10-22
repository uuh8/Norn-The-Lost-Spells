using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.Combat;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Game.Run.Tests
{
    public sealed class BossTeleportVfxControllerTests
    {
        private readonly List<Object> _createdObjects = new List<Object>();

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            EventBus<BossTeleportEvent>.Clear();
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
        public IEnumerator TelegraphToArrival_UsesTwoPrecreatedPortalChannels()
        {
            HealthComponent bossHealth = CreateBossHealth();
            BossTeleportVfxController controller =
                CreateController(bossHealth);
            Vector3 source = new Vector3(1f, 0f, 2f);
            Vector3 destination = new Vector3(8f, 0f, -3f);
            Vector3 visualOffset =
                Vector3.up * controller.VisualHeightOffset;

            EventBus<BossTeleportEvent>.Publish(
                new BossTeleportEvent
                {
                    BossId = bossHealth.Id,
                    Stage = BossTeleportStage.Telegraph,
                    SourcePosition = source,
                    DestinationPosition = destination,
                });

            Assert.That(
                controller.VisualHeightOffset,
                Is.EqualTo(1.5f).Within(0.0001f));
            AssertVisual(
                controller,
                "Source Open",
                true,
                source + visualOffset);
            AssertVisual(
                controller,
                "Destination Open",
                true,
                destination + visualOffset);

            yield return new WaitForSecondsRealtime(0.02f);

            AssertVisual(
                controller,
                "Source Idle",
                true,
                source + visualOffset);
            AssertVisual(
                controller,
                "Destination Idle",
                true,
                destination + visualOffset);

            EventBus<BossTeleportEvent>.Publish(
                new BossTeleportEvent
                {
                    BossId = bossHealth.Id,
                    Stage = BossTeleportStage.Departed,
                    SourcePosition = source,
                    DestinationPosition = destination,
                });

            AssertVisual(
                controller,
                "Source Close",
                true,
                source + visualOffset);
            AssertVisual(
                controller,
                "Destination Idle",
                true,
                destination + visualOffset);

            EventBus<BossTeleportEvent>.Publish(
                new BossTeleportEvent
                {
                    BossId = bossHealth.Id,
                    Stage = BossTeleportStage.Arrived,
                    SourcePosition = source,
                    DestinationPosition = destination,
                });

            AssertVisual(
                controller,
                "Destination Close",
                true,
                destination + visualOffset);
        }

        [UnityTest]
        public IEnumerator EventForOtherBoss_DoesNotShowVisuals()
        {
            HealthComponent bossHealth = CreateBossHealth();
            BossTeleportVfxController controller =
                CreateController(bossHealth);

            EventBus<BossTeleportEvent>.Publish(
                new BossTeleportEvent
                {
                    BossId = bossHealth.Id + 1,
                    Stage = BossTeleportStage.Telegraph,
                    SourcePosition = Vector3.zero,
                    DestinationPosition = Vector3.one,
                });

            yield return null;

            AssertVisual(controller, "Source Open", false, Vector3.zero);
            AssertVisual(controller, "Destination Open", false, Vector3.zero);
        }

        private HealthComponent CreateBossHealth()
        {
            GameObject boss = new GameObject("VFX Boss");
            HealthComponent health = boss.AddComponent<HealthComponent>();
            _createdObjects.Add(boss);
            return health;
        }

        private BossTeleportVfxController CreateController(
            HealthComponent bossHealth)
        {
            GameObject open = new GameObject("Open Prefab");
            GameObject idle = new GameObject("Idle Prefab");
            GameObject close = new GameObject("Close Prefab");
            _createdObjects.Add(open);
            _createdObjects.Add(idle);
            _createdObjects.Add(close);

            GameObject root = new GameObject("Boss Teleport VFX");
            root.SetActive(false);
            BossTeleportVfxController controller =
                root.AddComponent<BossTeleportVfxController>();
            SetPrivateField(controller, "_bossHealth", bossHealth);
            SetPrivateField(controller, "_openPrefab", open);
            SetPrivateField(controller, "_idlePrefab", idle);
            SetPrivateField(controller, "_closePrefab", close);
            SetPrivateField(controller, "_openDuration", 0.01f);
            SetPrivateField(controller, "_closeDuration", 0.01f);
            SetPrivateField(controller, "_visualHeightOffset", 1.5f);
            root.SetActive(true);
            _createdObjects.Add(root);
            return controller;
        }

        private static void AssertVisual(
            BossTeleportVfxController controller,
            string name,
            bool expectedActive,
            Vector3 expectedPosition)
        {
            Transform visual = controller.transform.Find(name);
            Assert.That(visual, Is.Not.Null, name);
            Assert.That(visual.gameObject.activeSelf, Is.EqualTo(expectedActive));
            if (expectedActive)
            {
                Assert.That(visual.position, Is.EqualTo(expectedPosition));
            }
        }

        private static void SetPrivateField(
            object target,
            string fieldName,
            object value)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, fieldName);
            field.SetValue(target, value);
        }
    }
}
