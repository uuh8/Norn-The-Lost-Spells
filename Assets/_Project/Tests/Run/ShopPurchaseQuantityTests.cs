using System;
using System.Reflection;
using Game.UI;
using NUnit.Framework;

namespace Game.Run.Tests
{
    public sealed class ShopPurchaseQuantityTests
    {
        private static object Call(string method, params object[] args)
        {
            var type = typeof(ShopController).Assembly.GetType("Game.UI.ShopPurchaseQuantity");
            Assert.That(type, Is.Not.Null, "购买数量必须统一考虑供货、金币和指定容器容量。");
            return type.GetMethod(method, BindingFlags.Public | BindingFlags.Static).Invoke(null, args);
        }
        [TestCase(10UL, 20L, 59L, false, 0UL, 2UL)]
        [TestCase(10UL, 20L, 19L, false, 0UL, 0UL)]
        [TestCase(1000UL, 1L, 1000L, false, 0UL, 128UL)]
        [TestCase(100UL, 2L, 1000L, true, 9UL, 9UL)]
        [TestCase(100UL, 2L, 1000L, true, 0UL, 0UL)]
        [TestCase(0UL, 2L, 1000L, false, 0UL, 0UL)]
        [TestCase(10UL, 0L, 1000L, false, 0UL, 0UL)]
        public void MaximumHonorsStockWalletAndContainer(ulong stock, long price, long money, bool material, ulong free, ulong expected)
        { Assert.That(Call("Maximum", stock, price, money, material, free), Is.EqualTo(expected)); }
        [Test]
        public void LargeContainerUsesNormalizedSliderWithoutRoundingFinalQuantity()
        {
            const ulong max = int.MaxValue;
            Assert.That(Call("FromProgress", 0f, max), Is.EqualTo(1UL));
            Assert.That(Call("FromProgress", 1f, max), Is.EqualTo(max));
            Assert.That(Call("FromProgress", .5f, max), Is.EqualTo(1073741824UL));
            Assert.That(Call("ToProgress", max, max), Is.EqualTo(1f));
            Assert.That(Call("FromProgress", 1f, 1UL), Is.EqualTo(1UL));
        }
        [Test]
        public void InvalidOrOverflowingTotalNeverBecomesAffordable()
        {
            object[] args = { long.MaxValue, 2UL, 0L };
            Assert.That(Call("TryCost", args), Is.False);
            args = new object[] { 20L, 3UL, 0L };
            Assert.That(Call("TryCost", args), Is.True);
            Assert.That(args[2], Is.EqualTo(60L));
        }
    }
}
