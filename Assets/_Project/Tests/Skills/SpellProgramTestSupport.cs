using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Game.Skills.Tests
{
    public abstract class SpellProgramTestSupport
    {
        private readonly List<SpellDefinition> _created = new List<SpellDefinition>();
        [TearDown] public void DisposeDefinitions()
        {
            for (int i = 0; i < _created.Count; i++) if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            _created.Clear();
        }
        protected SpellDefinition Emit(float damage = 10, float cost = 0,
            PayloadTriggerMode trigger = PayloadTriggerMode.None)
        {
            var d = Make(SpellKind.Emit); d.BaseDamage = damage; d.ManaCost = cost; d.PayloadTrigger = trigger;
            d.DisplayName = "普通弹"; return d;
        }
        protected SpellDefinition Modify(float flat = 5, float cost = 0)
        {
            var d = Make(SpellKind.Modify); d.ModDamageAddFlat = flat; d.ManaCost = cost;
            d.DisplayName = "增伤"; return d;
        }
        protected SpellDefinition Multi(int draws = 2)
        { var d = Make(SpellKind.Multicast); d.ExtraDraws = draws - 1; d.DisplayName = "多重"; return d; }
        protected SpellDefinition Make(SpellKind kind)
        { var d = ScriptableObject.CreateInstance<SpellDefinition>(); d.Kind = kind; _created.Add(d); return d; }
        protected SpellProgram Compile(params SpellDefinition[] definitions)
        {
            Assert.That(SpellProgramCompiler.TryCompile(definitions, new SpellProgramCompileInputs(1),
                out var program, out var diagnostic), Is.True, diagnostic.Message);
            return program;
        }
    }
}
