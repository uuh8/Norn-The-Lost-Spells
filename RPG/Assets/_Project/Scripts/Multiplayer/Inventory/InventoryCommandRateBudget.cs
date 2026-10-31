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

    public static class InventoryCommandDeliveryPolicy
    {
        /// <summary>
        /// 去重账本必须保持原回执，但原回执不授予本次额外发送预算。成功变更由版本/选中实例比较自动发布，
        /// 重复成功不用再发大快照；只有本次未限流的版本冲突需要主动修复客户端读源。
        /// </summary>
        public static bool ShouldResynchronize(OnlineInventoryCommandFailure budget, OnlineInventoryCommandFailure receipt)
            => budget != OnlineInventoryCommandFailure.RateLimited &&
                (receipt == OnlineInventoryCommandFailure.StaleVersion || receipt == OnlineInventoryCommandFailure.StaleProgram);
    }
}
