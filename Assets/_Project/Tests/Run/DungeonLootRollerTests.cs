using System;
using NUnit.Framework;

namespace Game.Run.Tests
{
    public sealed class DungeonLootRollerTests
    {
        [TestCase(0d,0d,false)]
        [TestCase(1d,.999999d,true)]
        [TestCase(.25d,.25d,false)]
        [TestCase(.25d,.249999d,true)]
        public void ChanceHasStrictBoundary(double chance,double sample,bool expected)
        {
            // 先通过反射检查契约，使尚未实现的规则得到真正RED而非编译错误。
            var type=Type.GetType("Game.Run.DungeonLootRoller, Game.Run");
            Assert.That(type,Is.Not.Null,"地牢掉落应有可确定测试的纯抽样规则。");
            Assert.That(type.GetMethod("ShouldDrop").Invoke(null,new object[]{chance,sample}),Is.EqualTo(expected));
        }
        [TestCase(10,25,0d,10)]
        [TestCase(10,25,.999999d,25)]
        [TestCase(7,7,.5d,7)]
        public void QuantityIncludesBothConfiguredEndpoints(int min,int max,double sample,int expected)
        {
            var type=Type.GetType("Game.Run.DungeonLootRoller, Game.Run");Assert.That(type,Is.Not.Null);
            Assert.That(type.GetMethod("Quantity").Invoke(null,new object[]{min,max,sample}),Is.EqualTo(expected));
        }
    }
}
