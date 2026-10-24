using System;
using Game.NetworkProtocol;
using NUnit.Framework;

namespace Game.Multiplayer.Tests
{
    /// <summary>资产身份快照包含仓库/背包及安装闭包；不能沿用 N2 仅24个平面存储槽的包长门禁。</summary>
    public sealed class OnlineInventoryAdmissionBoundsTests
    {
        [TestCase(123, true)]
        [TestCase(124, false)]
        public void BoundedWholeProfileIncludesEquippedWandsAndTheirCards(int count, bool accepted)
        {
            var ready = new InstanceReadyReply { Members = new[]
            {
                new InstanceEntryMember { ActorId = 1, Profile = new OnlineProfileReply { PlayerId = "a", ContentManifestHash = "hash", Items = new OnlineItemReply[count] } },
                new InstanceEntryMember { ActorId = 2, Profile = new OnlineProfileReply { PlayerId = "b", ContentManifestHash = "hash" } }
            }};
            if (accepted) Assert.That(new OnlineInventorySession(ready, "hash").MemberCount, Is.EqualTo(2));
            else Assert.Throws<ArgumentException>(() => new OnlineInventorySession(ready, "hash"));
        }
    }
}
