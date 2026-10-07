using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace Game.Materials.Tests
{
    public sealed class MaterialIdentityContractTests
    {
        [Test]
        public void MaterialIds_PreserveSerializedAndGpuByteProtocol()
        {
            Assert.That((byte)MaterialId.Empty, Is.EqualTo(0));
            Assert.That((byte)MaterialId.Water, Is.EqualTo(1));
            Assert.That((byte)MaterialId.Fire, Is.EqualTo(2));
            Assert.That((byte)MaterialId.Poison, Is.EqualTo(3));
            Assert.That((byte)MaterialId.Sticky, Is.EqualTo(4));
        }

        [Test]
        public void FixedSolidAndGasHaveStableIdentitiesAndDistinctBehaviors()
        {
            string[] names = { "Ice", "DriedSlime", "Steam", "ToxicVapor" };
            for (int i = 0; i < names.Length; i++)
            {
                Assert.That(Enum.TryParse(names[i], out MaterialId material), Is.True,
                    "固体和气体必须是元素世界中可识别的真实材料。");
                Assert.That((byte)material, Is.EqualTo(5 + i));
            }
            Assert.That(Enum.TryParse("Solid", out MaterialBehaviorKind solid), Is.True);
            Assert.That((byte)solid, Is.EqualTo(3));
            Assert.That(Enum.TryParse("Gas", out MaterialBehaviorKind gas), Is.True);
            Assert.That((byte)gas, Is.EqualTo(4));
        }

        [Test]
        public void BehaviorKind_ContainsOnlyTheCurrentSemanticCategories()
        {
            Assert.That(
                Enum.GetValues(typeof(MaterialBehaviorKind)),
                Is.EqualTo(new[]
                {
                    MaterialBehaviorKind.None,
                    MaterialBehaviorKind.Liquid,
                    MaterialBehaviorKind.ReactiveField,
                    (MaterialBehaviorKind)3,
                    (MaterialBehaviorKind)4,
                }));
        }

        [Test]
        public void MaterialsAssembly_DoesNotReferenceUpperGameplayOrPresentationModules()
        {
            string[] references = typeof(MaterialId).Assembly
                .GetReferencedAssemblies()
                .Select(name => name.Name)
                .ToArray();

            Assert.That(references, Does.Not.Contain("Game.Combat"));
            Assert.That(references, Does.Not.Contain("Game.ElementField"));
            Assert.That(references, Does.Not.Contain("Game.Rendering"));
        }

        [Test]
        public void Definition_DoesNotOwnBackendRenderingStatusOrSolverParameters()
        {
            FieldInfo[] fields = typeof(MaterialDefinition).GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            Assert.That(fields.Select(field => field.Name), Is.EquivalentTo(new[] { "_id", "_behavior" }));
        }
    }
}
