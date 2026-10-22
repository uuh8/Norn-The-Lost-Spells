using System.Collections;
using Game.Combat;
using Game.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Rendering.Tests
{
    public sealed class ReactionBurstVisualControllerTests
    {
        private GameObject _root;
        private GameObject _template;
        private StatusReactionVisualProfile _profile;
        private ReactionBurstVisualController _controller;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            yield return new EnterPlayMode();
            _template = new GameObject("ToxicBurstTemplate");
            _template.AddComponent<ParticleSystem>();
            _template.SetActive(false);
            _profile = ScriptableObject.CreateInstance<StatusReactionVisualProfile>();
            _profile.ToxicResolvedPrefab = _template;
            _profile.ToxicResolvedLifetime = 10f;
            _profile.ToxicBurstPoolCapacity = 2;

            _root = new GameObject("GlobalReactionVisuals");
            _root.SetActive(false);
            _controller = _root.AddComponent<ReactionBurstVisualController>();
            _controller.ConfigureForTests(_profile);
            _root.SetActive(true);
            yield return null;
        }

        [UnityTest]
        public IEnumerator ToxicResolved_PlaysAtWorldPositionWithoutTargetFiltering()
        {
            Vector3 position = new Vector3(3f, 2f, -4f);
            Publish(ElementReactionId.ToxicCombustion, ElementReactionPhase.Resolved, position, 0);
            yield return null;

            Assert.AreEqual(2, _controller.ToxicPoolSize);
            Assert.AreEqual(1, _controller.ActiveToxicBurstCount);
            Assert.AreEqual(position, _controller.LastToxicBurstPosition);
        }

        [UnityTest]
        public IEnumerator NonResolvedOrDifferentReaction_IsIgnored()
        {
            Publish(ElementReactionId.ToxicCombustion, ElementReactionPhase.Started, Vector3.one, 1);
            Publish(ElementReactionId.Extinguish, ElementReactionPhase.Resolved, Vector3.one, 0);
            yield return null;
            Assert.AreEqual(0, _controller.ActiveToxicBurstCount);
        }

        [UnityTest]
        public IEnumerator PoolFull_ReusesSlotWithoutCreatingNewInstance()
        {
            int childCount = _root.transform.childCount;
            Publish(ElementReactionId.ToxicCombustion, ElementReactionPhase.Resolved, Vector3.right, 1);
            Publish(ElementReactionId.ToxicCombustion, ElementReactionPhase.Resolved, Vector3.up, 2);
            Publish(ElementReactionId.ToxicCombustion, ElementReactionPhase.Resolved, Vector3.forward, 0);
            yield return null;

            Assert.AreEqual(childCount, _root.transform.childCount);
            Assert.AreEqual(2, _controller.ActiveToxicBurstCount);
            Assert.AreEqual(Vector3.forward, _controller.LastToxicBurstPosition);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            EventBus<ElementReactionEvent>.Clear();
            if (_root != null) Object.Destroy(_root);
            if (_template != null) Object.Destroy(_template);
            if (_profile != null) Object.Destroy(_profile);
            yield return null;
            if (Application.isPlaying) yield return new ExitPlayMode();
        }

        private static void Publish(
            ElementReactionId reaction,
            ElementReactionPhase phase,
            Vector3 worldPosition,
            int targetId)
        {
            EventBus<ElementReactionEvent>.Publish(new ElementReactionEvent
            {
                TargetId = targetId,
                Reaction = reaction,
                Phase = phase,
                WorldPosition = worldPosition,
                NormalizedStrength = 1f,
            });
        }
    }
}
