using System;
using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Run.Tests
{
    public sealed class HeldItemInputContractTests : InventoryTestFixture
    {
        [Test] public void UnifiedInputHasReleaseGateAndOwnsSixToolKeys()
        {
            var type = typeof(Game.Character.WizardController).Assembly.GetType("Game.Character.HeldItemInputController");
            Assert.That(type,Is.Not.Null,"统一左键需要明确的手持输入边界。");
            var go=Track(new GameObject("held input contract"));
            var input=go.AddComponent(type);
            type.GetMethod("Awake",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(input,null);
            var actions=(InputAction[])type.GetField("_select",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(input);
            for(int i=0;i<6;i++)Assert.That(actions[i].bindings[0].path,Is.EqualTo("<Keyboard>/"+(i+4)));
            Assert.That(type.GetProperty("CanPour").GetValue(input),Is.False,"没有真实会话不能撒出。");
        }
        [TestCase(false)][TestCase(true)] public void BindingAloneMarksSessionDirtyAndSaveOutcomePreservesIt(bool fail)
        {
            var store=Store(Define("bag",ItemKind.Backpack,bagWidth:8,bagHeight:8),Define("bottle",ItemKind.Container));
            Add(store,"bag","bag");Add(store,"b","bottle");
            store.TryMove("bag",new ItemLocation(ItemLocationKind.EquippedBackpack,"player",0,0,0,0));store.TryMove("b",Bag());
            var profile=new SinglePlayerProfile("player",store);
            var go=Track(new GameObject("session contract"));var session=go.AddComponent<SinglePlayerInventorySession>();
            typeof(SinglePlayerInventorySession).GetMethod("OnEnable",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(session,null);
            var files=new MemoryRaidFiles();var repository=new ProfileSaveRepository("Temp/SinglePlayer/BackpackTests/"+Guid.NewGuid().ToString("N"),store.Catalog,files);
            try
            {
            Assert.That(session.Initialize(profile,repository),Is.True);Assert.That(session.Save().Success,Is.True);
            Assert.That(session.HasUnsavedChanges,Is.False);
            EventBus<ToolbeltBindingRequestedEvent>.Publish(new ToolbeltBindingRequestedEvent
            {InventoryId=session.ReadOnly.InventoryId,ToolSlot=0,ItemId="b",ExpectedVersion=0});
            Assert.That(session.HasUnsavedChanges,Is.True);
            files.FailWrite=fail;Assert.That(session.Save().Success,Is.EqualTo(!fail));Assert.That(session.HasUnsavedChanges,Is.EqualTo(fail));
            }
            finally { typeof(SinglePlayerInventorySession).GetMethod("OnDisable",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(session,null); }
        }
    }
}
