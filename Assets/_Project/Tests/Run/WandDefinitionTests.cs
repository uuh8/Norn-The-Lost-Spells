using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Run.Tests
{
    public sealed class WandDefinitionTests : InventoryTestFixture
    {
        [TestCase("_maxMana", 0)] [TestCase("_maxMana", float.NaN)] [TestCase("_maxMana", float.PositiveInfinity)]
        [TestCase("_manaRegenPerSecond", -1)] [TestCase("_manaRegenPerSecond", float.NaN)]
        [TestCase("_castCooldown", -1)] [TestCase("_castCooldown", float.PositiveInfinity)]
        public void InvalidFixedFloat_RejectsWholeConfiguration(string property, float value)
        { var wand = Track(ScriptableObject.CreateInstance<WandDefinition>()); var data = new SerializedObject(wand);
            data.FindProperty(property).floatValue = value; data.ApplyModifiedPropertiesWithoutUndo(); Assert.That(wand.TryFreeze(out _), Is.False); }
        [TestCase("_programCapacity")] [TestCase("_baseDraws")]
        public void NonPositiveProgramSize_RejectsConfiguration(string property)
        { var wand = Track(ScriptableObject.CreateInstance<WandDefinition>()); var data = new SerializedObject(wand);
            data.FindProperty(property).intValue = 0; data.ApplyModifiedPropertiesWithoutUndo(); Assert.That(wand.TryFreeze(out _), Is.False); }
        [Test] public void MissingDefinition_CannotFallbackToLegacyCapacity()
        { var item = Define("wand", ItemKind.Wand); var data = new SerializedObject(item); data.FindProperty("_wandDefinition").objectReferenceValue = null;
            data.ApplyModifiedPropertiesWithoutUndo(); Assert.Throws<AssertionException>(() => Store(item)); }
        [Test] public void ProgramRetainsEmptySlotsAndUsesFrozenBaseDraws()
        {
            var wand = Define("wand", ItemKind.Wand); var data = new SerializedObject(wand.WandDefinition);
            data.FindProperty("_programCapacity").intValue = 4; data.FindProperty("_baseDraws").intValue = 3; data.ApplyModifiedPropertiesWithoutUndo();
            var store = Store(wand, Define("spell", ItemKind.Spell)); Add(store, "a", "wand"); Add(store, "first", "spell", 2); Add(store, "last", "spell", 2);
            Assert.That(store.TryMove("first", new ItemLocation(ItemLocationKind.WandSlot, "a", 0, 0, 0, 0)).Success, Is.True);
            Assert.That(store.TryMove("last", new ItemLocation(ItemLocationKind.WandSlot, "a", 0, 0, 0, 3)).Success, Is.True);
            var service = new WandLoadoutService(new SinglePlayerProfile("player", store), "scope");
            Assert.That(service.ReadOnly.TryGetProgram("a", out WandProgramSnapshot program), Is.True);
            Assert.That(program.BaseDraws, Is.EqualTo(3)); Assert.That(program.Program.Count, Is.EqualTo(4));
            Assert.That(program.Program[1], Is.Null); Assert.That(program.Program[2], Is.Null); Assert.That(program.Program.GetInstanceId(3), Is.EqualTo("last"));
        }
        [Test] public void ZeroCooldownAndRegenAreValid()
        { var wand = Track(ScriptableObject.CreateInstance<WandDefinition>()); var data = new SerializedObject(wand);
            data.FindProperty("_castCooldown").floatValue = 0; data.FindProperty("_manaRegenPerSecond").floatValue = 0; data.ApplyModifiedPropertiesWithoutUndo();
            Assert.That(wand.TryFreeze(out WandStats stats), Is.True); var state = new WandRuntimeState("a", stats.MaxMana, stats.ManaRegenPerSecond, stats.CastCooldown);
            Assert.That(state.TrySpend(20, true), Is.True); Assert.That(state.TrySpend(20, true), Is.True); state.Tick(1); Assert.That(state.CurrentMana, Is.EqualTo(60)); }
    }
}
