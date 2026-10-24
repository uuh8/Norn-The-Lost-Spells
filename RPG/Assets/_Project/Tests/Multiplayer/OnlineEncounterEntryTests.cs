using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEngine;

namespace Game.Multiplayer.Tests
{
    /// <summary>传送入场不是开战；只有已就绪、存活、合法且进入战斗区的玩家才触发首次 Boss encounter。</summary>
    public sealed class OnlineEncounterEntryTests
    {
        private sealed class Roster : ICombatActorRosterReadOnly
        {
            internal CombatActorSnapshot Actor;
            public int Count => 1;
            public bool TryGetActor(int index, out CombatActorSnapshot actor) { actor = Actor; return index == 0; }
        }
        [TestCase(0f, false, false)]
        [TestCase(20f, true, false)]
        [TestCase(8f, true, true)]
        public void LoadedActorMustEnterActivationRadius(float distance, bool ready, bool expected)
        {
            var type = typeof(OnlineCombatWorld).Assembly.GetType("Game.Multiplayer.OnlineEncounterEntry");
            Assert.That(type, Is.Not.Null, "需要显式入场/战斗区门，不能在账户认证完成时直接开 Boss。");
            var roster = new Roster { Actor = new CombatActorSnapshot
            { ActorId = 1, Position = Vector3.forward * distance, IsAlive = true, IsConnected = true, Targetable = ready } };
            bool accepted = (bool)type.GetMethod("CanActivate", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { roster, Vector3.zero, 10f });
            Assert.That(accepted, Is.EqualTo(expected));
        }
        [Test]
        public void AuthenticationDoesNotGrantTargetEligibility()
        {
            var slot = new NetworkActorRegistry.Slot { Connected = true };
            Assert.That(slot.Targetable, Is.False, "已认证但尚未加载的玩家不应被 AI 选中。");
            slot.SimulationReady = true;
            Assert.That(slot.Targetable, Is.True);
            slot.Connected = false;
            Assert.That(slot.Targetable, Is.False, "旧 ready 不能越过断线边界。");
        }
    }
}
