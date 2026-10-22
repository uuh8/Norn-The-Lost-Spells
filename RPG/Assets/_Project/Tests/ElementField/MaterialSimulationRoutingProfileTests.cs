using System;
using System.Reflection;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.ElementField.Tests
{
    public sealed class MaterialSimulationRoutingProfileTests
    {
        private MaterialDefinition[] _definitions;
        private MaterialCatalog _catalog;
        private MaterialSimulationRoutingProfile _profile;

        [SetUp]
        public void SetUp()
        {
            _definitions = new[]
            {
                Definition(MaterialId.Water, MaterialBehaviorKind.Liquid),
                Definition(MaterialId.Fire, MaterialBehaviorKind.ReactiveField),
                Definition(MaterialId.Poison, MaterialBehaviorKind.Liquid),
            };
            _catalog = ScriptableObject.CreateInstance<MaterialCatalog>();
            SetField(_catalog, "_definitions", _definitions);
            _profile = ScriptableObject.CreateInstance<MaterialSimulationRoutingProfile>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_profile);
            Object.DestroyImmediate(_catalog);
            for (int i = 0; i < _definitions.Length; i++) Object.DestroyImmediate(_definitions[i]);
        }

        [TestCase(WaterSimulationMode.LegacyCell, MaterialSimulationBackendKind.ElementCell)]
        [TestCase(WaterSimulationMode.GpuPbf, MaterialSimulationBackendKind.GpuPbfLiquid)]
        public void SnapshotCombinesWaterDecisionWithExplicitRoutes(
            WaterSimulationMode mode,
            MaterialSimulationBackendKind expectedWaterBackend)
        {
            Configure(
                new MaterialSimulationRoute(MaterialId.Fire, MaterialSimulationBackendKind.ElementCell),
                new MaterialSimulationRoute(MaterialId.Poison, MaterialSimulationBackendKind.Unsupported));
            WaterSimulationCompatibilityDecision decision = WaterSimulationCompatibilityPolicy.Resolve(mode, true);

            MaterialSimulationRouteSnapshot snapshot = MaterialSimulationRouteSnapshot.Create(
                _catalog.CreateSnapshot(), _profile, in decision);

            AssertRoute(snapshot, MaterialId.Water, expectedWaterBackend);
            AssertRoute(snapshot, MaterialId.Fire, MaterialSimulationBackendKind.ElementCell);
            AssertRoute(snapshot, MaterialId.Poison, MaterialSimulationBackendKind.Unsupported);
            Assert.That(snapshot.TryResolve(MaterialId.Sticky, out _), Is.False);
            Assert.That(snapshot.TryResolve((MaterialId)255, out _), Is.False);
        }

        [Test]
        public void LiquidMayBindPbfButReactiveFieldMayNot()
        {
            Configure(new MaterialSimulationRoute(MaterialId.Poison, MaterialSimulationBackendKind.GpuPbfLiquid));
            WaterSimulationCompatibilityDecision decision = WaterSimulationCompatibilityPolicy.Resolve(WaterSimulationMode.LegacyCell, true);
            Assert.That(() => MaterialSimulationRouteSnapshot.Create(_catalog.CreateSnapshot(), _profile, in decision), Throws.Nothing);

            Configure(new MaterialSimulationRoute(MaterialId.Fire, MaterialSimulationBackendKind.GpuPbfLiquid));
            Assert.That(
                () => MaterialSimulationRouteSnapshot.Create(_catalog.CreateSnapshot(), _profile, in decision),
                Throws.TypeOf<InvalidOperationException>());
        }

        [Test]
        public void DuplicateWaterOwnedUnknownOrInvalidRouteIsRejected()
        {
            WaterSimulationCompatibilityDecision decision = WaterSimulationCompatibilityPolicy.Resolve(WaterSimulationMode.LegacyCell, true);
            Configure(
                new MaterialSimulationRoute(MaterialId.Fire, MaterialSimulationBackendKind.ElementCell),
                new MaterialSimulationRoute(MaterialId.Fire, MaterialSimulationBackendKind.Unsupported));
            AssertInvalid(decision);
            Configure(new MaterialSimulationRoute(MaterialId.Water, MaterialSimulationBackendKind.ElementCell));
            AssertInvalid(decision);
            Configure(new MaterialSimulationRoute(MaterialId.Sticky, MaterialSimulationBackendKind.ElementCell));
            AssertInvalid(decision);
            Configure(new MaterialSimulationRoute(MaterialId.Fire, (MaterialSimulationBackendKind)255));
            AssertInvalid(decision);
        }

        [Test]
        public void NullCatalogOrProfileIsRejected()
        {
            WaterSimulationCompatibilityDecision decision = WaterSimulationCompatibilityPolicy.Resolve(WaterSimulationMode.LegacyCell, true);
            Assert.That(() => MaterialSimulationRouteSnapshot.Create(null, _profile, in decision), Throws.TypeOf<ArgumentNullException>());
            Assert.That(() => MaterialSimulationRouteSnapshot.Create(_catalog.CreateSnapshot(), null, in decision), Throws.TypeOf<ArgumentNullException>());
        }

        private void AssertInvalid(WaterSimulationCompatibilityDecision decision)
        {
            Assert.That(
                () => MaterialSimulationRouteSnapshot.Create(_catalog.CreateSnapshot(), _profile, in decision),
                Throws.TypeOf<InvalidOperationException>());
        }

        private void Configure(params MaterialSimulationRoute[] routes)
        {
            var serialized = new SerializedObject(_profile);
            SerializedProperty entries = serialized.FindProperty("_routes");
            Assert.That(entries, Is.Not.Null);
            entries.arraySize = routes.Length;
            for (int i = 0; i < routes.Length; i++)
            {
                SerializedProperty entry = entries.GetArrayElementAtIndex(i);
                entry.FindPropertyRelative("_material").intValue = (byte)routes[i].Material;
                entry.FindPropertyRelative("_backend").intValue = (byte)routes[i].Backend;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void AssertRoute(IMaterialSimulationRouteReadOnly routes, MaterialId material, MaterialSimulationBackendKind expected)
        {
            Assert.That(routes.TryResolve(material, out MaterialSimulationBackendKind actual), Is.True);
            Assert.That(actual, Is.EqualTo(expected));
        }

        private static MaterialDefinition Definition(MaterialId id, MaterialBehaviorKind behavior)
        {
            MaterialDefinition definition = ScriptableObject.CreateInstance<MaterialDefinition>();
            SetField(definition, "_id", id);
            SetField(definition, "_behavior", behavior);
            return definition;
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"缺少字段 {name}。");
            field.SetValue(target, value);
        }
    }
}
