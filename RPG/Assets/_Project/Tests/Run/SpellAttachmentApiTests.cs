using NUnit.Framework;
using Game.Skills;

namespace Game.Run.Tests
{
    /// <summary>先证明当前生产入口缺失；后续用真实资源与世界测试取代“仅类型存在”的完成判断。</summary>
    public sealed class SpellAttachmentApiTests
    {
        [Test] public void ConcreteCardAttachmentServiceExists() =>
            Assert.That(typeof(SinglePlayerProfile).Assembly.GetType("Game.Run.SpellAttachmentService"), Is.Not.Null);
        [Test] public void MaterialOutputOwnershipServiceExists() =>
            Assert.That(typeof(SinglePlayerProfile).Assembly.GetType("Game.Run.SpellMaterialOutputService"), Is.Not.Null);
        [Test] public void EachOutputMapsToItsOriginalProgramSlot() =>
            Assert.That(typeof(CastExecutionLayer).GetMethod("GetOutputSlot"), Is.Not.Null);
    }
}
