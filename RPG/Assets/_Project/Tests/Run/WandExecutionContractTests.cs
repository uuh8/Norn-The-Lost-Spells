using System;
using Game.Core;
using NUnit.Framework;

namespace Game.Run.Tests
{
    // 先验证跨模块接缝缺失；Core只能带身份和版本，不能反向引用Skills的Graph。
    public sealed class WandExecutionContractTests
    {
        [Test]
        public void ResourceRequestCanRejectStaleExecutionAndInventoryIndependently()
        {
            Type request = typeof(CastResourceRequestedEvent);
            Assert.That(request.GetField("ExecutionRevision"), Is.Not.Null, "同程序的旧牌堆请求也必须可拒绝");
            Assert.That(request.GetField("ResourceVersion"), Is.Not.Null);
            Assert.That(request.GetField("InventoryVersion"), Is.Not.Null);
        }

        [Test]
        public void SessionArchiveCanOutliveReplaceableResourceServices()
        {
            Type archive = typeof(CastResourceService).Assembly.GetType("Game.Run.WandExecutionReplayArchive");
            Assert.That(archive, Is.Not.Null, "结算重建服务不能丢失已经提交的冻结记录");
        }

        [Test]
        public void V2TransactionIsExplicitInsteadOfChangingLegacyChildPayment()
        {
            Type service = typeof(CastResourceService).Assembly.GetType("Game.Run.WandExecutionTransactionService");
            Assert.That(service, Is.Not.Null, "保留Legacy入口，隔离验证V2的根预付事务");
        }
    }
}
