using Game.Skills;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Run.Tests
{
    public sealed class SpellCatalogAliasTests:InventoryTestFixture
    {
        private void ShareSpell(ItemDefinition alias,SpellDefinition spell)
        {var data=new SerializedObject(alias);data.FindProperty("_spellDefinition").objectReferenceValue=spell;data.ApplyModifiedPropertiesWithoutUndo();}
        private ItemCatalog Catalog(ItemDefinition[] items,params ItemDefinition[] canonical)
        {
            var catalog=Track(ScriptableObject.CreateInstance<ItemCatalog>());var data=new SerializedObject(catalog);
            var entries=data.FindProperty("_definitions");entries.arraySize=items.Length;
            for(int i=0;i<items.Length;i++)entries.GetArrayElementAtIndex(i).objectReferenceValue=items[i];
            var choices=data.FindProperty("_canonicalSpellItems");Assert.That(choices,Is.Not.Null,"共享法术需要显式Authoring规范映射");choices.arraySize=canonical.Length;
            for(int i=0;i<canonical.Length;i++)choices.GetArrayElementAtIndex(i).objectReferenceValue=canonical[i];
            data.ApplyModifiedPropertiesWithoutUndo();return catalog;
        }
        [Test] public void ExplicitCanonicalKeepsBothForwardIdsAndRestoresExistingAliasInstance()
        {
            var original=Define("original",ItemKind.Spell);var alias=Define("alias",ItemKind.Spell);ShareSpell(alias,original.SpellDefinition);
            var catalog=Catalog(new[]{alias,original},original);Assert.That(catalog.TryCreateIndex(out var index,out var error),Is.True,error);
            Assert.That(index.TryFindSpell(original.SpellDefinition,out var reward),Is.True);Assert.That(reward,Is.SameAs(original));
            Assert.That(index.TryGet("alias",out var savedAlias),Is.True);Assert.That(savedAlias,Is.SameAs(alias));
            var store=new ItemOwnershipStore(index);Assert.That(store.TryAdd(new RuntimeItemState("old.instance","alias",8),ItemLocation.Warehouse),Is.True);
            var dto=ProfileSaveMapper.ToDto(new SinglePlayerProfile("player",store),0);
            Assert.That(ProfileSaveMapper.TryRestore(dto,index,out var restored).Success,Is.True);
            Assert.That(restored.Items.TryGet("old.instance",out var old),Is.True);Assert.That(old.ContentId,Is.EqualTo("alias"));Assert.That(old.RemainingCharges,Is.EqualTo(8));
        }
        [Test] public void MissingCanonicalStillRejectsAmbiguousRewards()
        {
            var a=Define("a",ItemKind.Spell);var b=Define("b",ItemKind.Spell);ShareSpell(b,a.SpellDefinition);
            Assert.That(Catalog(new[]{a,b}).TryCreateIndex(out var index,out _),Is.True);
            Assert.That(index.TryFindSpell(a.SpellDefinition,out _),Is.False,"不能用字典顺序暗中决定奖励身份。");
        }
        [Test] public void CanonicalMustBelongToCatalogAndCannotRepeatOneSpell()
        {
            var a=Define("a",ItemKind.Spell);var b=Define("b",ItemKind.Spell);ShareSpell(b,a.SpellDefinition);
            Assert.That(Catalog(new[]{a},b).TryCreateIndex(out var rejected,out _),Is.False);Assert.That(rejected,Is.Null);
            Assert.That(Catalog(new[]{a,b},a,b).TryCreateIndex(out rejected,out _),Is.False);Assert.That(rejected,Is.Null);
        }
    }
}
