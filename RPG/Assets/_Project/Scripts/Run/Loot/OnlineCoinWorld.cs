using System;
using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>同Run的NPC身份Coin事实，不是Item/GMU；已拾墓碑始终保留，不复用身份。</summary>
    public readonly struct OnlineCoinRecord
    {
        public readonly ulong Id, CollectorActorId;
        public readonly long Amount;
        public readonly Vector3 Position;
        public readonly bool Collected;
        public OnlineCoinRecord(ulong id, long amount, Vector3 position, bool collected = false, ulong collectorActorId = 0)
        { Id = id; Amount = amount; Position = position; Collected = collected; CollectorActorId = collectorActorId; }
    }

    /// <summary>
    /// DS主线程Coin账本，固定12 NPC身份含已拾墓碑。注册只接原死亡抽样结果，不重新抽奖。
    /// 成功记账到Owner后仅写固定数组墓碑；中间没有事件或await，不给多人碰触重复入账的机会。
    /// Collider/认证连接由组合层验证，本层继续验证Run、Actor、Owner与溢出边界。
    /// </summary>
    public sealed class OnlineCoinWorld : IDisposable
    {
        public const int MaximumCoins = 12;
        private readonly OnlineCoinRecord[] _records = new OnlineCoinRecord[MaximumCoins];
        private readonly Func<bool> _canWrite;
        private bool _disposed;
        private uint _version = 1;
        public string RunId { get; }
        public int Count { get; private set; }
        public uint Version => _version;
        public bool IsDisposed => _disposed;

        public OnlineCoinWorld(string runId, Func<bool> canWrite)
        {
            if (string.IsNullOrWhiteSpace(runId) || canWrite == null) throw new ArgumentException("Coin世界需要可信Run及只读权威写门。");
            RunId = runId; _canWrite = canWrite;
        }

        public bool TryRegister(ulong id, long amount, Vector3 position)
        {
            if (_disposed || !Index(id, out int slot) || amount <= 0 || !Finite(position)) return false;
            var previous = _records[slot];
            // 重试只确认相同原始事实；逐值Equals避免Vector3近似相等掩盖冲突，也不复活已拾记录。
            if (previous.Id != 0) return previous.Amount == amount && previous.Position.Equals(position);
            if (!_canWrite() || _version == uint.MaxValue || Count == MaximumCoins) return false;
            _records[slot] = new OnlineCoinRecord(id, amount, position); Count++; _version++; return true;
        }

        public bool TryCollect(ulong id, ulong actorId, OnlineOwnedWorkspace owner)
        {
            if (_disposed || !Index(id, out int slot) || owner == null || actorId < 1 || actorId > OnlineActorLimits.PlayerCount
                || owner.ActorId != actorId || owner.RunId != RunId || !owner.IsActiveAndEnabled || !_canWrite()
                || _version == uint.MaxValue) return false;
            var coin = _records[slot];
            if (coin.Id == 0 || coin.Collected || !owner.TryCreditCoins(coin.Amount)) return false;
            // 所有失败均在Credit前；此处仅有不能失败的值复制，禁止加入外部回调。
            _records[slot] = new OnlineCoinRecord(coin.Id, coin.Amount, coin.Position, true, actorId); _version++; return true;
        }

        public bool TryGet(ulong id, out OnlineCoinRecord record)
        {
            record = default;
            if (!Index(id, out int slot) || _records[slot].Id == 0) return false;
            record = _records[slot]; return true;
        }

        /// <summary>稳定NPC顺序的值复制；调用方预分配12条缓冲，不借出内部可写数组。</summary>
        public int CopyTo(OnlineCoinRecord[] target)
        {
            if (target == null || target.Length < Count) throw new ArgumentException("Coin复制缓冲不足。");
            int at = 0;
            for (int i = 0; i < _records.Length; i++) if (_records[i].Id != 0) target[at++] = _records[i];
            return at;
        }
        private static bool Index(ulong id, out int slot)
        {
            slot = -1;
            if (id < OnlineActorLimits.FirstNpcActorId || id - OnlineActorLimits.FirstNpcActorId >= MaximumCoins) return false;
            slot = (int)(id - OnlineActorLimits.FirstNpcActorId); return true;
        }
        private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        // 生命周期终止仅撤销新写入；保留最终墓碑供只读收尾，不补还Owner余额。
        public void Dispose() => _disposed = true;
    }
}
