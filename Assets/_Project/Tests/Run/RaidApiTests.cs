using NUnit.Framework;

namespace Game.Run.Tests
{
    public sealed class RaidApiTests
    {
        [TestCase("Game.Run.RaidPreparationService")]
        [TestCase("Game.Run.ExtractionProgress")]
        [TestCase("Game.Run.RaidSettlementService")]
        public void M4RequiresProductionService(string name)
        { Assert.That(typeof(SinglePlayerProfile).Assembly.GetType(name), Is.Not.Null, "缺少M4权威服务：" + name); }
    }
}
