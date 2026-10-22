using System;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Combat.Tests
{
    public sealed class MaterialReactionSemanticTests
    {
        private const string DefaultBindingsPath =
            "Assets/_Project/ScriptableObjects/Combat/Reactions/MaterialReactionBindings_Default.asset";
        private MaterialDefinition _water;
        private MaterialDefinition _fire;
        private MaterialDefinition _poison;
        private MaterialCatalog _materials;
        private MaterialReactionBindingProfile _bindings;

        [SetUp]
        public void SetUp()
        {
            _water = CreateDefinition(MaterialId.Water, MaterialBehaviorKind.Liquid);
            _fire = CreateDefinition(MaterialId.Fire, MaterialBehaviorKind.ReactiveField);
            _poison = CreateDefinition(MaterialId.Poison, MaterialBehaviorKind.Liquid);
            _materials = ScriptableObject.CreateInstance<MaterialCatalog>();
            _bindings = ScriptableObject.CreateInstance<MaterialReactionBindingProfile>();
            SetDefinitions(_materials, _water, _fire, _poison);
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_bindings);
            UnityEngine.Object.DestroyImmediate(_materials);
            UnityEngine.Object.DestroyImmediate(_poison);
            UnityEngine.Object.DestroyImmediate(_fire);
            UnityEngine.Object.DestroyImmediate(_water);
        }

        [Test]
        public void Catalog_ResolvesAnUnorderedPairToOneReaction()
        {
            SetBindings(_bindings, (MaterialId.Water, MaterialId.Fire, ElementReactionId.Extinguish));
            MaterialReactionCatalogSnapshot snapshot =
                _bindings.CreateSnapshot(_materials.CreateSnapshot());

            Assert.That(snapshot.TryResolve(MaterialId.Water, MaterialId.Fire, out ElementReactionId forward), Is.True);
            Assert.That(forward, Is.EqualTo(ElementReactionId.Extinguish));
            Assert.That(snapshot.TryResolve(MaterialId.Fire, MaterialId.Water, out ElementReactionId reverse), Is.True);
            Assert.That(reverse, Is.EqualTo(ElementReactionId.Extinguish));
            Assert.That(snapshot.TryResolve(MaterialId.Poison, MaterialId.Fire, out _), Is.False);
        }

        [Test]
        public void Catalog_RejectsDuplicateUnorderedPair()
        {
            SetBindings(
                _bindings,
                (MaterialId.Water, MaterialId.Fire, ElementReactionId.Extinguish),
                (MaterialId.Fire, MaterialId.Water, ElementReactionId.Extinguish));

            Assert.That(
                () => _bindings.CreateSnapshot(_materials.CreateSnapshot()),
                Throws.TypeOf<InvalidOperationException>());
        }

        [TestCase(MaterialId.Empty, MaterialId.Fire)]
        [TestCase(MaterialId.Water, MaterialId.Water)]
        [TestCase(MaterialId.Sticky, MaterialId.Fire)]
        public void Catalog_RejectsInvalidOrUnknownPair(MaterialId first, MaterialId second)
        {
            SetBindings(_bindings, (first, second, ElementReactionId.Extinguish));

            Assert.That(
                () => _bindings.CreateSnapshot(_materials.CreateSnapshot()),
                Throws.TypeOf<InvalidOperationException>());
        }

        [Test]
        public void Evaluator_ExtinguishConsumesHalfAsMuchWaterAndPreservesInputOrder()
        {
            var tuning = new ExtinguishTuning
            {
                FormalThreshold = 10f,
                LowRatePerSecond = 5f,
                FormalRatePerSecond = 50f,
                FireRemovedPerWater = 2f,
            };
            var waterFirst = new MaterialContactSnapshot(MaterialId.Water, 204, MaterialId.Fire, 204);
            var fireFirst = new MaterialContactSnapshot(MaterialId.Fire, 204, MaterialId.Water, 204);

            Assert.That(ElementReactionEvaluator.TryEvaluateMaterialContact(
                in waterFirst,
                1f,
                ElementReactionId.Extinguish,
                in tuning,
                out MaterialReactionResult a), Is.True);
            Assert.That(ElementReactionEvaluator.TryEvaluateMaterialContact(
                in fireFirst,
                1f,
                ElementReactionId.Extinguish,
                in tuning,
                out MaterialReactionResult b), Is.True);

            Assert.That(a.FirstConsumedGmu, Is.EqualTo(102));
            Assert.That(a.SecondConsumedGmu, Is.EqualTo(204));
            Assert.That(b.FirstConsumedGmu, Is.EqualTo(204));
            Assert.That(b.SecondConsumedGmu, Is.EqualTo(102));
        }

        [TestCase(10, 3, 1f, 6, 3)]
        [TestCase(204, 204, 1f, 204, 102)]
        [TestCase(204, 204, 0f, 0, 0)]
        [TestCase(255, 255, 0.25f, 51, 26)]
        public void Evaluator_GmuConversionUsesTwoToOneFireWaterRatio(
            int fireGmu,
            int waterGmu,
            float deltaTime,
            int expectedFireConsumed,
            int expectedWaterConsumed)
        {
            var extinguish = new ExtinguishTuning
            {
                FormalThreshold = 20f,
                LowRatePerSecond = 5f,
                FormalRatePerSecond = 40f,
                FireRemovedPerWater = 2f,
            };
            var tuning = new ElementReactionTuningSnapshot(
                extinguish,
                default,
                default,
                default,
                0f,
                0f);
            var contact = new MaterialContactSnapshot(
                MaterialId.Fire,
                fireGmu,
                MaterialId.Water,
                waterGmu);

            Assert.That(ElementReactionEvaluator.TryEvaluateMaterialContact(
                in contact,
                deltaTime,
                ElementReactionId.Extinguish,
                in tuning,
                out MaterialReactionResult result), Is.True);
            Assert.That(result.FirstConsumedGmu, Is.EqualTo(expectedFireConsumed));
            Assert.That(result.SecondConsumedGmu, Is.EqualTo(expectedWaterConsumed));
        }

        [Test]
        public void DefaultBindingAsset_ContainsWaterAndPoisonFireRules()
        {
            MaterialReactionBindingProfile profile =
                AssetDatabase.LoadAssetAtPath<MaterialReactionBindingProfile>(DefaultBindingsPath);
            Assert.That(profile, Is.Not.Null);

            MaterialReactionCatalogSnapshot snapshot = profile.CreateSnapshot(_materials.CreateSnapshot());
            Assert.That(snapshot.TryResolve(MaterialId.Water, MaterialId.Fire, out ElementReactionId reaction), Is.True);
            Assert.That(reaction, Is.EqualTo(ElementReactionId.Extinguish));
            Assert.That(snapshot.TryResolve(MaterialId.Poison, MaterialId.Fire, out ElementReactionId toxic), Is.True);
            Assert.That(toxic, Is.EqualTo(ElementReactionId.ToxicCombustion));
        }

        [Test]
        public void Evaluator_ToxicCombustionIsOrderIndependentAndConsumesOnlyPoison()
        {
            var toxic = new ToxicCombustionTuning
            { FireThreshold = 10f, PoisonThreshold = 10f, MaxPoisonConsume = 40f };
            var tuning = new ElementReactionTuningSnapshot(default, default, toxic, default, 0f, 0f);
            var poisonFirst = new MaterialContactSnapshot(MaterialId.Poison, 100, MaterialId.Fire, 100);
            var fireFirst = new MaterialContactSnapshot(MaterialId.Fire, 100, MaterialId.Poison, 100);
            Assert.That(ElementReactionEvaluator.TryEvaluateMaterialContact(
                in poisonFirst, 0f, ElementReactionId.ToxicCombustion, in tuning, out MaterialReactionResult a), Is.True);
            Assert.That(ElementReactionEvaluator.TryEvaluateMaterialContact(
                in fireFirst, 0f, ElementReactionId.ToxicCombustion, in tuning, out MaterialReactionResult b), Is.True);
            Assert.That((a.FirstConsumedGmu, a.SecondConsumedGmu), Is.EqualTo((40, 0)));
            Assert.That((b.FirstConsumedGmu, b.SecondConsumedGmu), Is.EqualTo((0, 40)));
        }

        [Test]
        public void Evaluator_AbsorbWaterConsumesWaterAndProducesStickyWithoutConsumingExistingSticky()
        {
            var tuning = new ElementReactionTuningSnapshot(
                default, default, default, default,
                new AbsorbWaterTuning { WaterConvertPerSecond = 40f }, 0f, 0f);
            var contact = new MaterialContactSnapshot(MaterialId.Water, 200, MaterialId.Sticky, 100);

            Assert.That(ElementReactionEvaluator.TryEvaluateMaterialContact(
                in contact, 1f, ElementReactionId.AbsorbWater, in tuning,
                out MaterialReactionResult result), Is.True);
            Assert.That(result.FirstConsumedGmu, Is.EqualTo(102));
            Assert.That(result.SecondConsumedGmu, Is.Zero);
            Assert.That(result.FirstProducedGmu, Is.Zero);
            Assert.That(result.SecondProducedGmu, Is.EqualTo(102));
        }

        [Test]
        public void Evaluator_IgniteGooConsumesStickyAndProducesFireOnFireSide()
        {
            var ignite = new IgniteGooTuning
            {
                FireThreshold = 10f,
                GooThreshold = 10f,
                WetInhibitThreshold = 10f,
                RequiredFireCapacity = 1f,
                GooConsumePerSecond = 20f,
                FirePerGoo = 1.5f,
            };
            var tuning = new ElementReactionTuningSnapshot(
                default, default, default, ignite, default, 0f, 0f);
            var contact = new MaterialContactSnapshot(MaterialId.Fire, 100, MaterialId.Sticky, 100);

            Assert.That(ElementReactionEvaluator.TryEvaluateMaterialContact(
                in contact, 1f, ElementReactionId.IgniteGoo, in tuning,
                out MaterialReactionResult result), Is.True);
            Assert.That(result.FirstConsumedGmu, Is.Zero);
            Assert.That(result.SecondConsumedGmu, Is.EqualTo(51));
            Assert.That(result.FirstProducedGmu, Is.EqualTo(77));
            Assert.That(result.SecondProducedGmu, Is.Zero);
        }

        [Test]
        public void Evaluator_RejectsMaterialPairThatDoesNotMatchReaction()
        {
            var tuning = new ExtinguishTuning
            {
                FormalThreshold = 10f,
                LowRatePerSecond = 5f,
                FormalRatePerSecond = 50f,
            };
            var contact = new MaterialContactSnapshot(MaterialId.Poison, 255, MaterialId.Fire, 255);

            Assert.That(ElementReactionEvaluator.TryEvaluateMaterialContact(
                in contact,
                1f,
                ElementReactionId.Extinguish,
                in tuning,
                out MaterialReactionResult result), Is.False);
            Assert.That(result.FirstConsumedGmu, Is.Zero);
            Assert.That(result.SecondConsumedGmu, Is.Zero);
        }

        private static MaterialDefinition CreateDefinition(MaterialId id, MaterialBehaviorKind behavior)
        {
            MaterialDefinition definition = ScriptableObject.CreateInstance<MaterialDefinition>();
            var serialized = new SerializedObject(definition);
            serialized.FindProperty("_id").intValue = (byte)id;
            serialized.FindProperty("_behavior").intValue = (byte)behavior;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return definition;
        }

        private static void SetDefinitions(MaterialCatalog catalog, params MaterialDefinition[] definitions)
        {
            var serialized = new SerializedObject(catalog);
            SerializedProperty property = serialized.FindProperty("_definitions");
            property.arraySize = definitions.Length;
            for (int i = 0; i < definitions.Length; i++)
                property.GetArrayElementAtIndex(i).objectReferenceValue = definitions[i];
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetBindings(
            MaterialReactionBindingProfile profile,
            params (MaterialId First, MaterialId Second, ElementReactionId Reaction)[] bindings)
        {
            var serialized = new SerializedObject(profile);
            SerializedProperty property = serialized.FindProperty("_bindings");
            property.arraySize = bindings.Length;
            for (int i = 0; i < bindings.Length; i++)
            {
                SerializedProperty item = property.GetArrayElementAtIndex(i);
                item.FindPropertyRelative("_first").intValue = (byte)bindings[i].First;
                item.FindPropertyRelative("_second").intValue = (byte)bindings[i].Second;
                item.FindPropertyRelative("_reaction").intValue = (byte)bindings[i].Reaction;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
