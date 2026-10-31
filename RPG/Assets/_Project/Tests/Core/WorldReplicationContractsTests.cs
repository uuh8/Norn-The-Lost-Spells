using System;
using NUnit.Framework;

namespace Game.Core.Tests
{
    public sealed class WorldReplicationContractsTests
    {
        [Test]
        public void FixedBuffersAllocateOnlyAtConstructionAndDoNotAdvertiseAuthorityRestore()
        {
            // Reflection 保证缺合同的首轮 RED 是断言失败，不是测试程序集编译失败。
            Type type = typeof(GameLog).Assembly.GetType("Game.Core.WorldReplicaBuffer");
            Assert.That(type, Is.Not.Null, "N5 需要 caller-owned 有界值快照，不能返回材料 live 数组。");
            object buffer = Activator.CreateInstance(type, 8, 4, 100);
            Assert.That(((Array)type.GetProperty("Cells").GetValue(buffer)).Length, Is.EqualTo(8));
            Assert.That(((Array)type.GetProperty("Pending").GetValue(buffer)).Length, Is.EqualTo(4));
            Assert.That(((Array)type.GetProperty("Geometries").GetValue(buffer)).Length, Is.EqualTo(4));
            object header = type.GetProperty("Header").GetValue(buffer);
            Assert.That((bool)header.GetType().GetProperty("CanRestoreAuthorityRules").GetValue(header), Is.False);
        }
    }
}
