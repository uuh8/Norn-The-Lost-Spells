using System;
using Game.Core;

namespace Game.Multiplayer
{
    /// <summary>
    /// 每个认证Actor独立的Token Bucket。硬预算在Protobuf解码前检查，软预算限制真正的库存操作；
    /// 两层分开才能让正常超频得到拒绝回执，同时不给恶意流量无限的字符串分配/可靠发送额度。
    /// </summary>
    public sealed class InventoryCommandRateBudget
    {
        private readonly double _burst, _perSecond;
        private int _connection;
        private uint _epoch;
        private double _tokens, _at;
        public InventoryCommandRateBudget(double burst, double perSecond) { _burst = burst; _perSecond = perSecond; }
        public bool TryTake(int connection, uint epoch, double now)
        {
            if (epoch == 0 || !double.IsFinite(now) || now < 0 || _epoch != 0 && epoch < _epoch) return false;
            if (_epoch == 0 || epoch > _epoch)
            { _connection = connection; _epoch = epoch; _at = now; _tokens = _burst; }
            if (_connection != connection || now < _at) return false;
            _tokens = Math.Min(_burst, _tokens + (now - _at) * _perSecond); _at = now;
            if (_tokens < 1) return false;
            _tokens--; return true;
        }
        public void Clear() { _connection = 0; _epoch = 0; _tokens = _at = 0; }
    }

    /// <summary>
    /// 同一个认证Actor的库存操作预算。选杖/搬卡/选工具仍共用原16突发、8次/秒；
    /// F采集与H倾倒各有16突发、10次/秒，承接原持键节奏而不让一种操作挤掉另一种。
    /// 三桶仅隔离操作额度，不隔离身份：整个Actor共用连接、Epoch和单调时钟栅栏。
    /// </summary>
    public sealed class InventoryCommandOperationBudget
    {
        // 在Channel.Awake一次创建，取额度只读写固定状态，不在命令热路径扩容或分配。
        private readonly InventoryCommandRateBudget _discrete = new InventoryCommandRateBudget(16, 8);
        private readonly InventoryCommandRateBudget _collect = new InventoryCommandRateBudget(16, 10);
        private readonly InventoryCommandRateBudget _pour = new InventoryCommandRateBudget(16, 10);
        private int _connection;
        private uint _epoch;
        private double _at;

        public bool TryTake(int connection, uint epoch, OnlineInventoryCommandKind kind, double now)
        {
            InventoryCommandRateBudget budget;
            switch (kind)
            {
                case OnlineInventoryCommandKind.SelectWand:
                case OnlineInventoryCommandKind.PlaceCard:
                case OnlineInventoryCommandKind.SelectTool:
                case OnlineInventoryCommandKind.BindTool:
                case OnlineInventoryCommandKind.DropContainer:
                case OnlineInventoryCommandKind.ThrowContainer:
                case OnlineInventoryCommandKind.ConfigureSpellAttachment:
                case OnlineInventoryCommandKind.ReleaseSolid:
                // 新通用移动仍消耗原离散桶，不改变F/H10Hz节奏或关闭认证/抗滥用门。
                case OnlineInventoryCommandKind.MoveOwned:
                case OnlineInventoryCommandKind.EquipWand:
                case OnlineInventoryCommandKind.DropItem:
                case OnlineInventoryCommandKind.PickupItem:
                case OnlineInventoryCommandKind.BeginWandResearch:
                case OnlineInventoryCommandKind.EndWandResearch:
                case OnlineInventoryCommandKind.PickupContainer: budget = _discrete; break;
                case OnlineInventoryCommandKind.CollectMaterial: budget = _collect; break;
                case OnlineInventoryCommandKind.PourContainer: budget = _pour; break;
                default: return false;
            }
            if (epoch == 0 || !double.IsFinite(now) || now < 0 || _epoch != 0 &&
                (epoch < _epoch || now < _at || epoch == _epoch && connection != _connection)) return false;
            if (_epoch == 0 || epoch > _epoch)
            {
                // 调用方必须先通过Room的当前认证连接门；这里只消费已验证的新代次。
                // 任一操作看到新Epoch就清全部桶，否则尚未使用的另一桶会接受旧连接迟到请求。
                ClearBuckets(); _connection = connection; _epoch = epoch;
            }
            // 合法身份的超频请求仍推进共同时间水位；切换操作不能拿旧时间补出额外额度。
            _at = now;
            return budget.TryTake(connection, epoch, now);
        }

        private void ClearBuckets() { _discrete.Clear(); _collect.Clear(); _pour.Clear(); }
        /// <summary>World解绑/替换、Server停止或组件禁用时一起释放额度和身份，避免下一场沿用旧栅栏。</summary>
        public void Clear() { ClearBuckets(); _connection = 0; _epoch = 0; _at = 0; }
    }

    public static class InventoryCommandDeliveryPolicy
    {
        /// <summary>资产、选择与连接各自独立；选容器不能靠强制重放收据或伪增资产版本触发快照。</summary>
        public static bool ShouldSendOwnerSnapshot(uint epoch, uint inventory, uint selection,
            uint sentEpoch, uint sentInventory, uint sentSelection)
            => epoch != sentEpoch || inventory != sentInventory || selection != sentSelection;

        /// <summary>
        /// 去重账本必须保持原回执，但原回执不授予本次额外发送预算。成功变更由版本/选中实例比较自动发布，
        /// 重复成功不用再发大快照；只有本次未限流的版本冲突需要主动修复客户端读源。
        /// </summary>
        public static bool ShouldResynchronize(OnlineInventoryCommandFailure budget, OnlineInventoryCommandFailure receipt)
            => budget != OnlineInventoryCommandFailure.RateLimited &&
                (receipt == OnlineInventoryCommandFailure.StaleVersion || receipt == OnlineInventoryCommandFailure.StaleProgram);
    }
}
