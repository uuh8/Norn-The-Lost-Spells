using System;

namespace Game.Core
{
    /// <summary>版本元数据的拒绝原因；BaselineRequired/MissingRevision供后续协调器决定是否申请新基线。</summary>
    public enum WorldReplicaRevisionFailure
    {
        None, WrongWorldEpoch, InvalidVersion, BaselineRequired, StaleGeneration, StaleRevision, MissingRevision
    }

    /// <summary>
    /// 一个固定世界、固定Chunk的已安装版本记录。Chunk地址由拥有它的Replica Store绑定，不能跨Chunk复用。
    /// 本类型只管理版本，不验证材料、页完整性、Checksum或连接身份，也不写材料数据、不发送ACK。
    /// 上层必须先完整验证候选并准备原子安装，再在同一主线程提交边界调用这里；
    /// 绝不能在收到Wire Header时就推进它，否则非法高版本会挡住后续合法数据。
    /// </summary>
    public sealed class WorldReplicaRevisionState
    {
        public ulong WorldEpoch { get; }
        public ulong Generation { get; private set; }
        public ulong Revision { get; private set; }
        public bool BaselineInstalled { get; private set; }
        public WorldReplicaRevisionFailure LastFailure { get; private set; }

        public WorldReplicaRevisionState(ulong worldEpoch)
        {
            if (worldEpoch == 0) throw new ArgumentOutOfRangeException(nameof(worldEpoch));
            WorldEpoch = worldEpoch;
        }

        /// <summary>
        /// 提交上层已完整验证的Baseline版本。新Generation允许自己的Revision从1开始；
        /// 同代旧基线不能回滚，重复同版本幂等。相同版本内容是否冲突由上层Checksum判定，不能在此省略。
        /// WorldEpoch固定，重连只换ConnectionEpoch；世界真正重建时由Owner创建新状态，而非接受任意更大的Epoch。
        /// </summary>
        public bool TryAcceptBaselineVersion(ulong worldEpoch, ulong generation, ulong revision)
        {
            if (worldEpoch != WorldEpoch) return Reject(WorldReplicaRevisionFailure.WrongWorldEpoch);
            if (generation == 0 || revision == 0) return Reject(WorldReplicaRevisionFailure.InvalidVersion);
            if (BaselineInstalled)
            {
                if (generation < Generation) return Reject(WorldReplicaRevisionFailure.StaleGeneration);
                if (generation == Generation && revision < Revision) return Reject(WorldReplicaRevisionFailure.StaleRevision);
            }
            Generation = generation; Revision = revision; BaselineInstalled = true;
            LastFailure = WorldReplicaRevisionFailure.None;
            return true;
        }

        /// <summary>
        /// Delta可合并多个提交版本，因此NewRevision只需严格大于BaseRevision，不要求相差1；
        /// 但Base必须恰好等于当前已安装版本。跨缺口、重叠和新代次都不能猜测应用部分数据。
        /// 失败仅更新诊断原因，已安装的Generation/Revision完全保留；后续合法候选仍可正常提交。
        /// </summary>
        public bool TryAcceptDeltaVersion(ulong worldEpoch, ulong generation, ulong baseRevision, ulong newRevision)
        {
            if (worldEpoch != WorldEpoch) return Reject(WorldReplicaRevisionFailure.WrongWorldEpoch);
            if (generation == 0 || baseRevision == 0 || newRevision <= baseRevision)
                return Reject(WorldReplicaRevisionFailure.InvalidVersion);
            if (!BaselineInstalled) return Reject(WorldReplicaRevisionFailure.BaselineRequired);
            if (generation < Generation) return Reject(WorldReplicaRevisionFailure.StaleGeneration);
            if (generation > Generation) return Reject(WorldReplicaRevisionFailure.BaselineRequired);
            if (newRevision <= Revision) return Reject(WorldReplicaRevisionFailure.StaleRevision);
            if (baseRevision != Revision) return Reject(WorldReplicaRevisionFailure.MissingRevision);
            // 比较采用完整无符号序关系，不做可能回绕的+1/差值；计数耗尽必须由权威换世界/代次或显式失败。
            Revision = newRevision; LastFailure = WorldReplicaRevisionFailure.None;
            return true;
        }

        private bool Reject(WorldReplicaRevisionFailure failure)
        { LastFailure = failure; return false; }
    }
}
