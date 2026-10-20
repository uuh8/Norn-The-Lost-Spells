using System.Reflection;
using Game.Core;
using Game.Skills;
using NUnit.Framework;

namespace Game.Run.Tests
{
    public sealed class OwnedWandGraphAdapterTests : WandExecutionTransactionTestFixture
    {
        [Test] public void FrozenGraphProvidesRuntimeLayersWithoutReinterpretingLegacyPayload()
        {
            Build(); var root = Request(CastResourceOperation.PrepareRoot); Assert.That(_casts.TryGetGraph(root.TicketId, out var graph), Is.True);
            var method = typeof(CastExecutionPlan).GetMethod("FromGraph", BindingFlags.Static | BindingFlags.Public);
            Assert.That(method, Is.Not.Null, "真实发射必须读取冻结Graph，不再解释SO后缀");
            var plan = (CastExecutionPlan)method.Invoke(null, new object[] { graph });
            Assert.That(plan.LayerCount, Is.EqualTo(graph.Scopes.Count)); Assert.That(plan.GetLayer(0).GetChild(0), Is.EqualTo(1));
            Assert.That(plan.GetLayer(0).ManaCost, Is.Zero); Assert.That(plan.GetLayer(1).GetCommand(0).Damage, Is.EqualTo(graph.Outputs[1].Command.Damage));
        }
        [Test] public void CoreRequestHasExplicitV2ModeForLegacyIsolation()
        { Assert.That(typeof(CastResourceRequestedEvent).GetField("UseV2"), Is.Not.Null); }
    }
}
