using System;
using System.Reflection;
using Game.NetworkProtocol;
using NUnit.Framework;

namespace Game.Multiplayer.Tests
{
    /// <summary>资产身份快照包含仓库/背包及安装闭包；不能沿用 N2 仅24个平面存储槽的包长门禁。</summary>
    public sealed class OnlineInventoryAdmissionBoundsTests
    {
        [TestCase(123, true)]
        [TestCase(172, true)]
        [TestCase(4120, true)]
        [TestCase(4121, false)]
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

        [TestCase(0, true)]
        [TestCase(24, true)]
        [TestCase(25, true)]
        [TestCase(38, true)]
        [TestCase(665, true)]
        [TestCase(4120, true)]
        [TestCase(4121, false)]
        public void AccountProfileRefreshAcceptsTheBoundedWholeProfileInsteadOfOnlyTwentyFourSlots(int count, bool accepted)
        {
            var profile = new OnlineProfileReply { ContentManifestHash = "hash", Items = new OnlineItemReply[count] };
            Assert.That(ValidAccountProfile(profile, "hash"), Is.EqualTo(accepted));
            Assert.That(profile.Items.Length, Is.EqualTo(count), "验证不截断真实资产，也不把空表补成初始库存。");
        }

        [TestCase("null-profile")]
        [TestCase("null-items")]
        [TestCase("manifest-mismatch")]
        public void AccountProfileRefreshRejectsMissingPayloadOrManifestMismatch(string defect)
        {
            var profile = new OnlineProfileReply { ContentManifestHash = "hash", Items = Array.Empty<OnlineItemReply>() };
            if (defect == "null-profile") profile = null;
            else if (defect == "null-items") profile.Items = null;
            else profile.ContentManifestHash = "other-hash";
            Assert.That(ValidAccountProfile(profile, "hash"), Is.False);
        }

        private static bool ValidAccountProfile(OnlineProfileReply profile, string expectedManifestHash)
        {
            // 私有纯验证保持在账户组合层，测试不创建Session、发HTTP或绕过EnterHub的本次Profile成功门。
            var method = typeof(OnlineAccountClient).GetMethod("IsValidProfileReply", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, "Refresh需要使用WholeProfile预算的纯验证，不能沿用24项平面槽上限。");
            return (bool)method.Invoke(null, new object[] { profile, expectedManifestHash });
        }
    }
}
