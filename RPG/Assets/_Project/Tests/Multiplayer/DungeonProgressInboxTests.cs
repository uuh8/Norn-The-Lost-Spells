using System;
using System.Reflection;
using Game.Core;
using NUnit.Framework;

namespace Game.Multiplayer.Tests
{
    /// <summary>网络候选尚未通过领域校验，不能只凭较高Revision覆盖前一个合法进度。</summary>
    public sealed class DungeonProgressInboxTests
    {
        private object _inbox;
        private MethodInfo _add, _take;
        [SetUp] public void RequireBoundedInbox()
        {
            var type = typeof(DungeonProgressNetworkChannel).Assembly.GetType("Game.Multiplayer.DungeonProgressInbox");
            Assert.That(type, Is.Not.Null, "需要有界候选Inbox，领域拒绝非法高Revision后仍可处理同帧其他合法包。");
            _inbox = Activator.CreateInstance(type, true); _add = type.GetMethod("TryEnqueue"); _take = type.GetMethod("TryDequeue");
        }
        [Test] public void HigherUnvalidatedRevisionDoesNotReplaceEarlierCandidate()
        {
            Assert.That(Add(11), Is.True); Assert.That(Add(100), Is.True);
            Assert.That(Take().Revision, Is.EqualTo(11)); Assert.That(Take().Revision, Is.EqualTo(100));
            Assert.That(_take.Invoke(_inbox, new object[] { null }), Is.False);
        }
        [Test] public void CapacityIsBoundedAndOverflowCannotEraseAcceptedCandidate()
        {
            for (uint i = 1; i <= 8; i++) Assert.That(Add(i), Is.True);
            Assert.That(Add(999), Is.False, "接收突发不能无限分配，也不能让溢出候选擦除已排队合法事实。");
            for (uint i = 1; i <= 8; i++) Assert.That(Take().Revision, Is.EqualTo(i));
            Assert.That(Add(9), Is.True); Assert.That(Take().Revision, Is.EqualTo(9));
        }
        private bool Add(uint revision) => (bool)_add.Invoke(_inbox, new object[] { new OnlineDungeonProgress { Revision = revision } });
        private OnlineDungeonProgress Take()
        {
            var args = new object[] { null }; Assert.That(_take.Invoke(_inbox, args), Is.True);
            return (OnlineDungeonProgress)args[0];
        }
    }
}
