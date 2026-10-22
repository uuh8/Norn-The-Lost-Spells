using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Game.Character.Tests
{
    public class SpellCasterTraceLevelTests
    {
        [Test]
        public void TraceLevel_InEditor_ReturnsConfiguredLevel()
        {
            var gameObject = new GameObject("SpellCasterTraceLevelTests");
            try
            {
                SpellCaster caster = gameObject.AddComponent<SpellCaster>();
                FieldInfo field = typeof(SpellCaster).GetField(
                    "_traceLevel",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                Assert.That(field, Is.Not.Null);
                field.SetValue(caster, CastTraceLevel.Detailed);

                Assert.That(caster.TraceLevel, Is.EqualTo(CastTraceLevel.Detailed));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }
    }
}
