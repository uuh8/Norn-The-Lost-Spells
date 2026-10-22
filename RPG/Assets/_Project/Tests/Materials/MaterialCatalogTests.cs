using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Materials.Tests
{
    public sealed class MaterialCatalogTests
    {
        private const string CatalogPath =
            "Assets/_Project/ScriptableObjects/Materials/MaterialCatalog_Default.asset";
        private MaterialDefinition _water;
        private MaterialDefinition _fire;
        private MaterialDefinition _poison;
        private MaterialCatalog _catalog;

        [SetUp]
        public void SetUp()
        {
            _water = CreateDefinition(MaterialId.Water, MaterialBehaviorKind.Liquid);
            _fire = CreateDefinition(MaterialId.Fire, MaterialBehaviorKind.ReactiveField);
            _poison = CreateDefinition(MaterialId.Poison, MaterialBehaviorKind.Liquid);
            _catalog = ScriptableObject.CreateInstance<MaterialCatalog>();
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_catalog);
            UnityEngine.Object.DestroyImmediate(_poison);
            UnityEngine.Object.DestroyImmediate(_fire);
            UnityEngine.Object.DestroyImmediate(_water);
        }

        [Test]
        public void CreateSnapshot_IndexesConfiguredDefinitionsAndLeavesReservedIdsUnsupported()
        {
            SetDefinitions(_catalog, _water, _fire, _poison);

            MaterialCatalogSnapshot snapshot = _catalog.CreateSnapshot();

            AssertDefinition(snapshot, MaterialId.Water, MaterialBehaviorKind.Liquid);
            AssertDefinition(snapshot, MaterialId.Fire, MaterialBehaviorKind.ReactiveField);
            AssertDefinition(snapshot, MaterialId.Poison, MaterialBehaviorKind.Liquid);
            Assert.That(snapshot.TryGet(MaterialId.Empty, out _), Is.False);
            Assert.That(snapshot.TryGet(MaterialId.Sticky, out _), Is.False);
            Assert.That(snapshot.TryGet((MaterialId)250, out _), Is.False);
        }

        [Test]
        public void CreateSnapshot_RejectsNullDefinition()
        {
            SetDefinitions(_catalog, _water, null);
            Assert.That(() => _catalog.CreateSnapshot(), Throws.TypeOf<InvalidOperationException>());
        }

        [Test]
        public void CreateSnapshot_RejectsDuplicateId()
        {
            MaterialDefinition duplicateWater = CreateDefinition(MaterialId.Water, MaterialBehaviorKind.Liquid);
            try
            {
                SetDefinitions(_catalog, _water, duplicateWater);
                Assert.That(() => _catalog.CreateSnapshot(), Throws.TypeOf<InvalidOperationException>());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(duplicateWater);
            }
        }

        [TestCase(MaterialId.Empty, MaterialBehaviorKind.Liquid)]
        [TestCase(MaterialId.Water, MaterialBehaviorKind.None)]
        [TestCase(MaterialId.Water, (MaterialBehaviorKind)99)]
        public void CreateSnapshot_RejectsInvalidDefinition(
            MaterialId id,
            MaterialBehaviorKind behavior)
        {
            MaterialDefinition invalid = CreateDefinition(id, behavior);
            try
            {
                SetDefinitions(_catalog, invalid);
                Assert.That(() => _catalog.CreateSnapshot(), Throws.TypeOf<InvalidOperationException>());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(invalid);
            }
        }

        [Test]
        public void DefaultCatalogAsset_RoundTripsCanonicalDefinitions()
        {
            MaterialCatalog catalog = AssetDatabase.LoadAssetAtPath<MaterialCatalog>(CatalogPath);
            Assert.That(catalog, Is.Not.Null);

            MaterialCatalogSnapshot snapshot = catalog.CreateSnapshot();
            AssertDefinition(snapshot, MaterialId.Water, MaterialBehaviorKind.Liquid);
            AssertDefinition(snapshot, MaterialId.Fire, MaterialBehaviorKind.ReactiveField);
            AssertDefinition(snapshot, MaterialId.Poison, MaterialBehaviorKind.Liquid);
            AssertDefinition(snapshot, MaterialId.Sticky, MaterialBehaviorKind.Liquid);
        }

        private static MaterialDefinition CreateDefinition(
            MaterialId id,
            MaterialBehaviorKind behavior)
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

        private static void AssertDefinition(
            MaterialCatalogSnapshot snapshot,
            MaterialId id,
            MaterialBehaviorKind behavior)
        {
            Assert.That(snapshot.TryGet(id, out MaterialDefinitionSnapshot definition), Is.True);
            Assert.That(definition.Id, Is.EqualTo(id));
            Assert.That(definition.Behavior, Is.EqualTo(behavior));
        }
    }
}
