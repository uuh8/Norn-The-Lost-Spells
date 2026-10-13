using System;
using System.Collections.Generic;
using Game.Core;
using Game.Skills;
using NUnit.Framework;
using UnityEngine;
namespace Game.Run.Tests
{
    public class RaidTestFixture : InventoryTestFixture
    {
        protected SinglePlayerProfile Profile;
        protected ProfileSaveRepository Repository;
        protected RaidDefinition Definition;
        protected ProtectedSpellSelection Protection;
        protected MemoryRaidFiles Files;
        protected void Build(int charges = 3)
        {
            var store = Store(Define("bag", ItemKind.Backpack, bagWidth: 4, bagHeight: 3), Define("wand", ItemKind.Wand),
                Define("spell", ItemKind.Spell), Define("mod", ItemKind.Spell), Define("bottle", ItemKind.Container));
            store.Catalog.TryGet("mod", out var mod); mod.SpellDefinition.Kind = SpellKind.Modify;
            Add(store, "bag", "bag"); Add(store, "wand", "wand"); Add(store, "protected", "spell", charges);
            Add(store, "ordinary", "spell", 5); Add(store, "qualification", "mod", 1); Add(store, "hub", "spell", 8); Add(store, "bottle", "bottle");
            Assert.That(LoadoutOps.TryReplaceBackpack(store, "bag", "player", store.Version).Success, Is.True);
            Assert.That(LoadoutOps.TryEquipWand(store, "wand", "player", 0, store.Version).Success, Is.True);
            Assert.That(store.TryMove("protected", new ItemLocation(ItemLocationKind.WandSlot, "wand", 0, 0, 0, 0)).Success, Is.True);
            Assert.That(store.TryMove("ordinary", Bag(0,0)).Success, Is.True); Assert.That(store.TryMove("qualification", Bag(1,0)).Success, Is.True);
            Assert.That(store.TryMove("bottle", Bag(2,0)).Success, Is.True);
            Profile = new SinglePlayerProfile("player", store); Files = new MemoryRaidFiles();
            Repository = new ProfileSaveRepository("Temp/SinglePlayer/M4-Memory", store.Catalog, Files);
            Definition = Track(ScriptableObject.CreateInstance<RaidDefinition>()); Definition.WorldScenePath = "world";
            Protection = new ProtectedSpellSelection(); Assert.That(Protection.Select(Profile, "protected", true), Is.True);
        }
        protected void Depart(bool exploring = true)
        {
            Assert.That(new RaidPreparationService().TryPrepare(Profile, Repository, Definition, Protection, out var next).Success, Is.True);
            Profile = next; if (exploring) { Profile.PendingRaid.Phase = RaidPhase.Exploring; Assert.That(Repository.TrySave(Profile).Success, Is.True); }
        }
        protected void SetCharges(string id, int charges)
        {
            Profile.Items.TryGet(id, out var item); Profile.Items.TryGetLocation(id, out var location);
            Assert.That(ItemStackOps.TryChangeState(Profile.Items, new RuntimeItemState(id, item.ContentId, charges, false, item.StackStateKey,
                item.Composition, item.Material, item.Attachment), location, Profile.Items.Version).Success, Is.True);
        }
    }
    public sealed class MemoryRaidFiles : IProfileFileSystem
    {
        public readonly Dictionary<string,string> Data = new Dictionary<string,string>(); public bool FailWrite, FailReplace;
        public bool Exists(string p) => Data.ContainsKey(p);
        public string Read(string p) => Data[p];
        public void WriteFlushed(string p, string text) { if (FailWrite) throw new System.IO.IOException("injected write"); Data[p] = text; }
        public void Move(string a,string b) { if(FailReplace)throw new System.IO.IOException("injected move"); Data[b]=Data[a]; Data.Remove(a); }
        public void Replace(string a,string b,string backup) { if(FailReplace)throw new System.IO.IOException("injected replace"); Data[backup]=Data[b]; Data[b]=Data[a]; Data.Remove(a); }
    }
    public sealed class RaidPreparationServiceTests : RaidTestFixture
    {
        [Test] public void DepartureMovesUniqueRootsAndLeavesHubAssets()
        { Build(); Depart(); Assert.That(Profile.ActorId, Does.StartWith("raid.")); Profile.Items.TryGetLocation("hub", out var hub); Assert.That(hub, Is.EqualTo(ItemLocation.Warehouse));
            Assert.That(Profile.Items.Count, Is.EqualTo(7)); Assert.That(Profile.PendingRaid.Protects("ordinary"), Is.False); }
        [Test] public void ProtectionLocksOriginalCopiesAndCannotChangeInRaid()
        { Build(); Depart(); Assert.That(Protection.Select(Profile,"ordinary",true), Is.False); Assert.That(Protection.Select(Profile,"protected",false),Is.False); }
        [Test] public void ThirdProtectionAndWarehouseProtectionCannotDepart()
        { Build(); Assert.That(Protection.Select(Profile,"ordinary",true), Is.True); Assert.That(Protection.Select(Profile,"hub",true),Is.False);
            Protection.Clear(); Protection.Select(Profile,"hub",true); Assert.That(new RaidPreparationService().TryPrepare(Profile,Repository,Definition,Protection,out _).Success,Is.False); }
        [Test] public void WriteFailureDoesNotChangeSourceOrActor()
        { Build(); Files.FailWrite=true; Assert.That(new RaidPreparationService().TryPrepare(Profile,Repository,Definition,Protection,out var next).Success,Is.False);
            Assert.That(next,Is.Null); Assert.That(Profile.ActorId,Is.EqualTo("player")); Assert.That(Profile.PendingRaid,Is.Null); }
        [Test] public void LoadingFailureRollsBackOriginalInventoryAndProtection()
        { Build(); Depart(false); Assert.That(new RaidPreparationService().TryRollback(Profile,Repository,out var hub).Success,Is.True);
            Assert.That(hub.ActorId,Is.EqualTo("player")); Assert.That(hub.Items.Count,Is.EqualTo(7)); Assert.That(hub.PendingRaid,Is.Null); }
        [Test] public void ResumeDoesNotPayQualificationTwiceAndHubCannotBeMoved()
        { Build(); Profile.Items.TryMove("qualification",new ItemLocation(ItemLocationKind.WandSlot,"wand",0,0,0,0));
            // 程序槽已占用，先将原保护卡搬到背包，再装有限资格卡。
            Profile.Items.TryMove("protected",Bag(3,0)); Assert.That(Profile.Items.TryMove("qualification",new ItemLocation(ItemLocationKind.WandSlot,"wand",0,0,0,0)).Success,Is.True);
            Depart(); Profile.Items.TryGet("qualification",out var item); Assert.That(item.RemainingCharges,Is.Zero);
            var eligibility=new RaidSpellEligibility(Profile); eligibility.Resume(Profile.PendingRaid); Assert.That(eligibility.IsEligible("qualification"),Is.True);
            Assert.That(Profile.Items.TryMove("hub",Bag(0,1)).Success,Is.False); eligibility.TryEnd(out _); }
    }
}
