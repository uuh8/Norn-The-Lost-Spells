using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEngine;
namespace Game.Run.Tests
{
    public sealed class PracticeWorldActionsTests:InventoryTestFixture
    {
        [TestCase(false)][TestCase(true)]public void PracticePermissionDoesNotDependOnCopiedHubExperimentFlag(bool hubFlag)
        {
            var store=Store(Define("bag",ItemKind.Backpack));Add(store,"bag","bag");
            var actor=Track(new GameObject("Practice actor"));var session=actor.AddComponent<SinglePlayerInventorySession>();
            JsonUtility.FromJsonOverwrite("{\"_allowWorldExperiments\":"+(hubFlag?"true":"false")+"}",session);
            var profile=new SinglePlayerProfile("player",store);
            var method=typeof(SinglePlayerInventorySession).GetMethod("ConfigurePractice",BindingFlags.Instance|BindingFlags.NonPublic);
            Assert.That((bool)method.Invoke(session,new object[]{profile,new ProfileSaveRepository("Temp/SinglePlayer/PracticePermissions",store.Catalog),true,actor.transform,"p7",new Dictionary<(string,string),string>()}),Is.True);
            Assert.That(profile.PendingRaid,Is.Null,"练习工作区不是PendingRaid，不能只靠PendingRaid放行");
            Assert.That(session.WorldActionsAllowed,Is.True,"副本要能真实提交施法、采集和工具请求");
            profile.RaidActionsFrozen=true;
            Assert.That(session.RaidWriteLocked,Is.True,"世界上下文放行不能绕过转场冻结");
        }
        [Test]public void HubExperimentFlagStillDisablesHubWorldActions()
        {
            var store=Store(Define("bag",ItemKind.Backpack));var session=Track(new GameObject("Hub session")).AddComponent<SinglePlayerInventorySession>();
            JsonUtility.FromJsonOverwrite("{\"_allowWorldExperiments\":false}",session);
            Assert.That(session.Initialize(new SinglePlayerProfile("player",store),new ProfileSaveRepository("Temp/SinglePlayer/HubPermissions",store.Catalog)),Is.True);
            Assert.That(session.WorldActionsAllowed,Is.False);
        }
    }
}
