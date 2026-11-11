using System.Reflection;
using NUnit.Framework;
using Shared = Game.NetworkProtocol;

namespace Game.Multiplayer.Tests
{
    /// <summary>测试真正DS恢复判定：暂时404和泛409不能授权更换未知正文，只有单调Epoch淘汰证明可重捕获。</summary>
    public sealed class RunMemberExitReceiptTests
    {
        [Test] public void FinalDeathRetiresCarriedContentsButKeepsBackpackShellAndRealGold()
        {
            var owned = new Shared.OnlineOwnedAssetSnapshot
            {
                Gold = 937, Items = new[]
                {
                    new Shared.OnlineOwnedItemSnapshot { InstanceId = "bag", ContentId = "bag", Revision = 3, LocationKind = 4 },
                    new Shared.OnlineOwnedItemSnapshot { InstanceId = "wand", ContentId = "wand", Revision = 8, LocationKind = 3 },
                    new Shared.OnlineOwnedItemSnapshot { InstanceId = "bottle", ContentId = "bottle", Revision = 4, LocationKind = 1,
                        ContainerState = new Shared.OnlineOwnedContainerStateSnapshot { SchemaVersion = 1,
                            Components = new[] { new Shared.OnlineOwnedMaterialAmountSnapshot { MaterialKey = 1, Quantity = 7 } } } },
                    new Shared.OnlineOwnedItemSnapshot { InstanceId = "solid", ContentId = "solid", Revision = 5, LocationKind = 1,
                        MaterialState = new Shared.OnlineOwnedMaterialStateSnapshot { SchemaVersion = 1, MaterialKey = 1, Quantity = 9 } }
                }
            };
            var method = typeof(RoomInstanceBootstrap).GetMethod("TryBuildFinalDeathFacts", BindingFlags.Static | BindingFlags.NonPublic);
            object[] args = { owned, null, null };
            Assert.That((bool)method.Invoke(null, args), Is.True);
            var retired = (Shared.OnlineRunAssetCheckpointRetired[])args[1];
            Assert.That(retired.Length, Is.EqualTo(3));
            Assert.That(System.Array.Exists(retired, x => x.InstanceId == "bag"), Is.False);
            Assert.That(System.Array.TrueForAll(retired, x => x.Reason == Shared.OnlineRunAssetRetiredReason.Destroyed), Is.True);
            var amounts = (Shared.OnlineOwnedMaterialAmountSnapshot[])args[2];
            Assert.That(amounts.Length, Is.EqualTo(1)); Assert.That(amounts[0].Quantity, Is.EqualTo(16ul));
            // 捕获只是固定证据，HTTP成功之前不改原库存/数量/金币，不能提前销毁再赌收据返回。
            Assert.That(owned.Items.Length, Is.EqualTo(4)); Assert.That(owned.Gold, Is.EqualTo(937));
            Assert.That(owned.Items[2].ContainerState.Components[0].Quantity, Is.EqualTo(7ul));
        }

        [Test] public void FinalDeathMaterialOverflowRejectsCandidateWithoutTruncatingFacts()
        {
            var owned = new Shared.OnlineOwnedAssetSnapshot { Items = new[]
            {
                new Shared.OnlineOwnedItemSnapshot { InstanceId = "a", ContentId = "solid", Revision = 1,
                    MaterialState = new Shared.OnlineOwnedMaterialStateSnapshot { SchemaVersion = 1, MaterialKey = 1, Quantity = ulong.MaxValue } },
                new Shared.OnlineOwnedItemSnapshot { InstanceId = "b", ContentId = "solid", Revision = 1,
                    MaterialState = new Shared.OnlineOwnedMaterialStateSnapshot { SchemaVersion = 1, MaterialKey = 1, Quantity = 1 } }
            } };
            var method = typeof(RoomInstanceBootstrap).GetMethod("TryBuildFinalDeathFacts", BindingFlags.Static | BindingFlags.NonPublic);
            object[] args = { owned, null, null };
            Assert.That((bool)method.Invoke(null, args), Is.False);
            Assert.That(args[1], Is.Null); Assert.That(args[2], Is.Null);
            Assert.That(owned.Items[0].MaterialState.Quantity, Is.EqualTo(ulong.MaxValue));
        }

        [TestCase(409L, "RunMemberExitConnectionSuperseded", 404L, true)]
        [TestCase(0L, "RunMemberExitConnectionSuperseded", 404L, false)]
        [TestCase(409L, "RunMemberExitStateConflict", 404L, false)]
        [TestCase(409L, "RunMemberExitConnectionSuperseded", 0L, false)]
        [TestCase(409L, "RunMemberExitConnectionSuperseded", 401L, false)]
        public void OnlyAuthenticatedPermanentSupersededProofAllowsNewCandidate(long post, string code, long query, bool expected)
        {
            var method = typeof(RoomInstanceBootstrap).GetMethod("CanReplaceRejectedMemberExit", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "缺少专用Epoch永久失效与认证404组合的恢复门。");
            Assert.That((bool)method.Invoke(null, new object[] { post, code, query }), Is.EqualTo(expected));
        }

        [TestCase(null, null, 0u, "00000008aaaaaaaaaaaaaaaaaaaaaaaa", 7u, false)]
        [TestCase("00000007aaaaaaaaaaaaaaaaaaaaaaaa", null, 7u, "00000007aaaaaaaaaaaaaaaaaaaaaaaa", 9u, true)]
        [TestCase("00000008bbbbbbbbbbbbbbbbbbbbbbbb", "00000007aaaaaaaaaaaaaaaaaaaaaaaa", 8u, "00000006cccccccccccccccccccccccc", 9u, false)]
        [TestCase("00000008bbbbbbbbbbbbbbbbbbbbbbbb", "00000007aaaaaaaaaaaaaaaaaaaaaaaa", 8u, "00000009dddddddddddddddddddddddd", 9u, true)]
        public void NewCommandRequiresRealEpochButKnownFixedCommandSurvivesReconnect(string current, string previous, uint origin, string incoming, uint epoch, bool expected)
        {
            var method = typeof(RoomInstanceBootstrap).GetMethod("CanAddressMemberExitCommand", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.That(method, Is.Not.Null, "有界候选历史必须拒绝伪造未来前缀和遗忘的隔代旧ID，不能靠无限HashSet。");
            Assert.That((bool)method.Invoke(null, new object[] { current, previous, origin, incoming, epoch }), Is.EqualTo(expected));
        }
    }
}
