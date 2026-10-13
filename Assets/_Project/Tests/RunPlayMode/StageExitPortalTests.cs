using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using Game.Skills;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Run.Tests
{
    /// <summary>
    /// Portal 是全法术解锁的第二道保险：玩家可以跳过世界奖励，但不能带着残缺 Library 进入地图二。
    /// </summary>
    public sealed class StageExitPortalTests
    {
        private readonly List<UnityEngine.Object> _createdObjects =
            new List<UnityEngine.Object>();

        [SetUp]
        public void SetUp()
        {
            EventBus<EncounterCompletedEvent>.Clear();
            EventBus<PortalStateChangedEvent>.Clear();
            EventBus<SceneTransitionRequestedEvent>.Clear();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            for (int i = _createdObjects.Count - 1; i >= 0; i--)
            {
                if (_createdObjects[i] != null)
                {
                    Object.Destroy(_createdObjects[i]);
                }
            }

            _createdObjects.Clear();
            yield return null;
            EventBus<EncounterCompletedEvent>.Clear();
            EventBus<PortalStateChangedEvent>.Clear();
            EventBus<SceneTransitionRequestedEvent>.Clear();
        }

        [UnityTest]
        public IEnumerator MatchingCompletion_EnterUnlocksAllAndRequestsBossFieldExactlyOnce()
        {
            SpellDefinition fire = CreateSpell("Fire");
            SpellDefinition water = CreateSpell("Water");
            SpellLibrary startingLibrary = CreateLibrary(fire);
            SpellLibrary fullLibrary = CreateLibrary(fire, water);
            WandLoadout wand = CreateWand(fire);

            GameObject sessionObject = CreateGameObject("P8 Run Session Root");
            sessionObject.SetActive(false);
            RunSpellSession session = sessionObject.AddComponent<RunSpellSession>();
            session.ConfigureAndInitialize(startingLibrary, wand);
            RunSessionCoordinator coordinator =
                sessionObject.AddComponent<RunSessionCoordinator>();

            GameObject pauseObject = CreateGameObject("Run Pause Coordinator");
            RunPauseCoordinator pauseCoordinator =
                pauseObject.AddComponent<RunPauseCoordinator>();

            GameObject portalObject = CreateGameObject("Stage Exit Portal");
            portalObject.SetActive(false);
            Collider trigger = portalObject.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            StageExitPortal portal = portalObject.AddComponent<StageExitPortal>();
            SetPrivateField(portal, "_session", session);
            SetPrivateField(portal, "_sessionCoordinator", coordinator);
            SetPrivateField(portal, "_pauseCoordinator", pauseCoordinator);
            SetPrivateField(portal, "_fullLibrary", fullLibrary);
            SetPrivateField(portal, "_encounterIndex", 3);
            SetPrivateField(portal, "_targetSceneName", "P8_BossField");
            SetPrivateField(portal, "_playerLayers", (LayerMask)(1 << 8));
            portal.CloseDuration = 0.05f;
            GameObject travelerAnchorObject = CreateGameObject("Traveler Anchor");
            portal.TravelerAnchor = travelerAnchorObject.transform;
            portal.TravelerTransitDuration = 0.03f;
            portalObject.SetActive(true);

            int transitionCount = 0;
            int closingCount = 0;
            string requestedScene = null;
            EventBus<SceneTransitionRequestedEvent>.Subscribe(e =>
            {
                transitionCount++;
                requestedScene = e.SceneName;
            });
            EventBus<PortalStateChangedEvent>.Subscribe(e =>
            {
                if (e.PortalId == portal.PortalId &&
                    e.State == PortalVfxState.Closing)
                {
                    closingCount++;
                }
            });

            Assert.That(portal.IsAvailable, Is.False);
            Assert.That(trigger.enabled, Is.False);
            EventBus<EncounterCompletedEvent>.Publish(new EncounterCompletedEvent
            {
                EncounterIndex = 3,
                EncounterCount = 4
            });

            Assert.That(portal.IsAvailable, Is.True);
            Assert.That(trigger.enabled, Is.True);

            GameObject enemy = CreateGameObject("Enemy");
            enemy.layer = 9;
            Collider enemyCollider = enemy.AddComponent<BoxCollider>();
            Assert.That(portal.TryEnter(enemyCollider), Is.False);
            Assert.That(session.RuntimeLibrary.Available, Is.EqualTo(new[] { fire }));
            Assert.That(transitionCount, Is.Zero,
                "非 Player Collider 不能解锁法术或请求 Scene Transition。");

            GameObject player = CreateGameObject("Player");
            player.layer = 8;
            Collider playerCollider = player.AddComponent<BoxCollider>();
            StageExitPortalTravelerSpy traveler =
                player.AddComponent<StageExitPortalTravelerSpy>();
            traveler.AcceptRequest = false;

            Assert.That(portal.TryEnter(playerCollider), Is.True);
            Assert.That(traveler.BeginCount, Is.EqualTo(1));
            var center=portal.transform.Find("UnifiedPortalVisual");
            Assert.That(center,Is.Not.Null);
            Assert.That(traveler.Destination, Is.SameAs(center),"公共蓝门的视觉中心应优先于旧Anchor；解锁/暂停/单次加载语义保持。");
            Assert.That(traveler.Duration, Is.EqualTo(0.03f));
            Assert.That(session.RuntimeLibrary.Available, Is.EqualTo(new[] { fire, water }));
            Assert.That(coordinator.Stage, Is.EqualTo(RunStage.BossField));
            Assert.That(coordinator.EntryMode, Is.EqualTo(RunEntryMode.EnterBossField));
            Assert.That(closingCount, Is.EqualTo(1),
                "有效进入应立即播放一次 Portal Close。");
            Assert.That(transitionCount, Is.Zero,
                "Portal Close 尚未播放完成时不能开始 Fade。");
            Assert.That(
                pauseCoordinator.HasReason(RunPauseReason.SceneTransition),
                Is.True,
                "Close 播放期间必须立即冻结 Gameplay，不能等 Fade 才阻止输入。");
            Assert.That(Time.timeScale, Is.Zero);
            Assert.That(portal.TryEnter(playerCollider), Is.False);
            Assert.That(traveler.BeginCount, Is.EqualTo(1),
                "重复 Collider Enter 不能启动第二条角色吸入流程。");

            yield return new WaitForSecondsRealtime(0.08f);

            Assert.That(requestedScene, Is.EqualTo("P8_BossField"));
            Assert.That(transitionCount, Is.EqualTo(1));
            Assert.That(portal.TryEnter(playerCollider), Is.False);
            Assert.That(transitionCount, Is.EqualTo(1),
                "多个 Player Collider 或重复 Trigger 不能发出第二次 Scene Load 请求。");
        }

        [UnityTest]
        public IEnumerator ClosingVisual_UsesUnscaledParticleTimeWhileGameplayIsPaused()
        {
            GameObject portalObject = CreateGameObject("Stage Exit Portal");
            portalObject.SetActive(false);
            portalObject.AddComponent<BoxCollider>().isTrigger = true;
            StageExitPortal portal = portalObject.AddComponent<StageExitPortal>();
            portalObject.SetActive(true);

            GameObject closePrefab = CreateGameObject("Close Prefab");
            ParticleSystem rootParticles = closePrefab.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule rootMain = rootParticles.main;
            rootMain.useUnscaledTime = false;

            GameObject child = CreateGameObject("Close Child");
            child.transform.SetParent(closePrefab.transform, false);
            ParticleSystem childParticles = child.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule childMain = childParticles.main;
            childMain.useUnscaledTime = false;
            closePrefab.SetActive(false);

            GameObject vfxObject = CreateGameObject("Portal VFX");
            vfxObject.SetActive(false);
            PortalVfxController controller =
                vfxObject.AddComponent<PortalVfxController>();
            SetPrivateField(controller, "_portal", portal);
            SetPrivateField(controller, "_visualRoot", vfxObject.transform);
            SetPrivateField(controller, "_closePrefab", closePrefab);
            vfxObject.SetActive(true);

            EventBus<PortalStateChangedEvent>.Publish(new PortalStateChangedEvent
            {
                PortalId = portal.PortalId,
                State = PortalVfxState.Closing
            });

            GameObject closeInstance =
                GetPrivateField<GameObject>(controller, "_closeInstance");
            Assert.That(closeInstance, Is.Not.Null);
            Assert.That(closeInstance.activeSelf, Is.True);

            ParticleSystem[] particles =
                closeInstance.GetComponentsInChildren<ParticleSystem>(true);
            Assert.That(particles, Has.Length.EqualTo(2));
            for (int i = 0; i < particles.Length; i++)
            {
                Assert.That(
                    particles[i].main.useUnscaledTime,
                    Is.True,
                    "Gameplay timeScale=0 时，Close 粒子必须使用 unscaled time 才能推进。");
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator ClosingVisual_CancelsPendingOpenToIdleTransition()
        {
            GameObject portalObject = CreateGameObject("Stage Exit Portal");
            portalObject.SetActive(false);
            portalObject.AddComponent<BoxCollider>().isTrigger = true;
            StageExitPortal portal = portalObject.AddComponent<StageExitPortal>();
            portalObject.SetActive(true);

            GameObject openPrefab = CreateGameObject("Open Prefab");
            GameObject idlePrefab = CreateGameObject("Idle Prefab");
            GameObject closePrefab = CreateGameObject("Close Prefab");
            openPrefab.SetActive(false);
            idlePrefab.SetActive(false);
            closePrefab.SetActive(false);

            GameObject vfxObject = CreateGameObject("Portal VFX");
            vfxObject.SetActive(false);
            PortalVfxController controller =
                vfxObject.AddComponent<PortalVfxController>();
            SetPrivateField(controller, "_portal", portal);
            SetPrivateField(controller, "_visualRoot", vfxObject.transform);
            SetPrivateField(controller, "_openPrefab", openPrefab);
            SetPrivateField(controller, "_idlePrefab", idlePrefab);
            SetPrivateField(controller, "_closePrefab", closePrefab);
            SetPrivateField(controller, "_openDuration", 0.05f);
            vfxObject.SetActive(true);

            EventBus<PortalStateChangedEvent>.Publish(new PortalStateChangedEvent
            {
                PortalId = portal.PortalId,
                State = PortalVfxState.Opening
            });
            EventBus<PortalStateChangedEvent>.Publish(new PortalStateChangedEvent
            {
                PortalId = portal.PortalId,
                State = PortalVfxState.Closing
            });

            yield return new WaitForSecondsRealtime(0.08f);

            GameObject idleInstance =
                GetPrivateField<GameObject>(controller, "_idleInstance");
            GameObject closeInstance =
                GetPrivateField<GameObject>(controller, "_closeInstance");
            Assert.That(idleInstance.activeSelf, Is.False,
                "Closing 已接管视觉后，旧 Open Coroutine 不能再切回 Idle。");
            Assert.That(closeInstance.activeSelf, Is.True);
        }

        private SpellDefinition CreateSpell(string displayName)
        {
            SpellDefinition spell = ScriptableObject.CreateInstance<SpellDefinition>();
            spell.DisplayName = displayName;
            _createdObjects.Add(spell);
            return spell;
        }

        private SpellLibrary CreateLibrary(params SpellDefinition[] spells)
        {
            SpellLibrary library = ScriptableObject.CreateInstance<SpellLibrary>();
            library.Available = spells;
            _createdObjects.Add(library);
            return library;
        }

        private WandLoadout CreateWand(params SpellDefinition[] spells)
        {
            WandLoadout wand = ScriptableObject.CreateInstance<WandLoadout>();
            wand.Spells = spells;
            wand.BaseDraws = 1;
            _createdObjects.Add(wand);
            return wand;
        }

        private GameObject CreateGameObject(string objectName)
        {
            GameObject gameObject = new GameObject(objectName);
            _createdObjects.Add(gameObject);
            return gameObject;
        }

        private static void SetPrivateField<T>(object target, string fieldName, T value)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"找不到测试字段 {fieldName}。");
            field.SetValue(target, value);
        }

        private static T GetPrivateField<T>(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"找不到测试字段 {fieldName}。");
            return (T)field.GetValue(target);
        }
    }

    /// <summary>
    /// 测试替身只记录 Game.Run 发出的 Portal Transit 合同，不依赖 Game.Character 的具体表现实现。
    /// </summary>
    public sealed class StageExitPortalTravelerSpy : MonoBehaviour, IPortalTraveler
    {
        public int BeginCount { get; private set; }
        public Transform Destination { get; private set; }
        public float Duration { get; private set; }
        public bool AcceptRequest { get; set; } = true;

        public bool BeginPortalTransit(Transform destination, float duration)
        {
            BeginCount++;
            Destination = destination;
            Duration = duration;
            return AcceptRequest;
        }
    }
}
