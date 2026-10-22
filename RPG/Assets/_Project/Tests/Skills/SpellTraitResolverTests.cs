using NUnit.Framework;

namespace Game.Skills.Tests
{
    public sealed class SpellTraitResolverTests
    {
        [Test]
        public void Resolve_TriggerEmit_ExposesTriggerAndStaticTraits()
        {
            SpellDefinition spell = UnityEngine.ScriptableObject.CreateInstance<SpellDefinition>();
            spell.Kind = SpellKind.Emit;
            spell.PayloadTrigger = PayloadTriggerMode.OnImpact;
            spell.BaseSpeed = 0f;

            SpellTraitFlags traits = SpellTraitResolver.Resolve(spell);

            Assert.That((traits & SpellTraitFlags.ImpactPayload) != 0, Is.True);
            UnityEngine.Object.DestroyImmediate(spell);
        }

        [Test]
        public void Resolve_Modifier_UsesAuthoritativeModifierFields()
        {
            SpellDefinition spell = UnityEngine.ScriptableObject.CreateInstance<SpellDefinition>();
            spell.Kind = SpellKind.Modify;
            spell.ModDamageAddFlat = 5f;
            spell.ModSpreadAddDegrees = 12f;

            SpellTraitFlags traits = SpellTraitResolver.Resolve(spell);

            Assert.That((traits & SpellTraitFlags.DamageModifier) != 0, Is.True);
            Assert.That((traits & SpellTraitFlags.Spread) != 0, Is.True);
            UnityEngine.Object.DestroyImmediate(spell);
        }
    }
}
