using Game.Core;
using Game.Run;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Character.Tests
{
    public sealed class N4TeleportReplicaTests
    {
        [Test]
        public void ReplicaTeleportPublishesVfxFactsWithoutMovingBossOrActivatingFsm()
        {
            var root = new GameObject("N4 teleport projection fixture"); root.SetActive(false);
            root.transform.position = new Vector3(31, 3, 7);
            var boss = root.AddComponent<WizardBossController>(); boss.ConfigureReplica();
            int received = 0; BossTeleportEvent last = default;
            void Consume(BossTeleportEvent fact) { received++; last = fact; }
            EventBus<BossTeleportEvent>.Subscribe(Consume);
            try
            {
                Vector3 source = root.transform.position, destination = new Vector3(46, 0, -5);
                for (uint stage = 0; stage < 4; stage++)
                {
                    boss.ApplyReplicatedTeleport(stage, source, destination);
                    Assert.That((uint)last.Stage, Is.EqualTo(stage));
                    Assert.That(last.SourcePosition, Is.EqualTo(source)); Assert.That(last.DestinationPosition, Is.EqualTo(destination));
                }
                boss.ApplyReplicatedTeleport(4, source, destination);
                boss.ApplyReplicatedTeleport(0, new Vector3(float.NaN, 0, 0), destination);
                Assert.That(received, Is.EqualTo(4));
                Assert.That(root.transform.position, Is.EqualTo(source), "网络表现不能成为第二个 Teleport 位移 Owner");
                Assert.That(boss.CurrentStateKind, Is.EqualTo(BossRuntimeStateKind.None)); Assert.That(boss.IsEncounterActive, Is.False);
            }
            finally { EventBus<BossTeleportEvent>.Unsubscribe(Consume); Object.DestroyImmediate(root); }
        }

        [Test]
        public void SinglePlayerBossRejectsNetworkTeleportProjection()
        {
            var root = new GameObject("N4 singleplayer teleport fixture"); root.SetActive(false);
            var boss = root.AddComponent<WizardBossController>(); int received = 0;
            void Consume(BossTeleportEvent fact) => received++;
            EventBus<BossTeleportEvent>.Subscribe(Consume);
            try { boss.ApplyReplicatedTeleport(0, Vector3.zero, Vector3.one); Assert.That(received, Is.Zero); }
            finally { EventBus<BossTeleportEvent>.Unsubscribe(Consume); Object.DestroyImmediate(root); }
        }
    }
}
