using System;

namespace Game.Core
{
    /// <summary>版本元数据的拒绝原因；BaselineRequired/MissingRevision供后续协调器决定是否申请新基线。</summary>
    public enum WorldReplicaRevisionFailure
    {
        None, WrongWorldEpoch, InvalidVersion, BaselineRequired, StaleGeneration, StaleRevision, MissingRevision
    }

    /// <summary>
    /// 一个固定世界、固定Chunk的已安装版本记录。Chunk地址由拥有它的Replica Store绑定；
    /// 普通退出/重连不能清版本，跨Chunk复用必须经过可信Owner显式退休旧Key的协议边界。
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
        /// 仅供可信缓存Owner在旧Key已经完成协议退休后，原位复用预分配版本对象；WorldEpoch不变。
        /// 这不是普通Reset/重连API：调用前必须已验证当前连接的单调SubscriptionEpoch、可靠Leave，
        /// 取消候选并释放旧Key全部只读数量，且保证旧生命周期/页不能再次到达该版本Owner。
        /// 本类没有连接/订阅身份，无法自行证明这些前提，因此由Store/Assembler的退休守卫与协调器
        /// 共同负责；收到Wire Header、普通Evict或ConnectionEpoch更新时绝不能直接调用。
        /// 只清值、不创建新对象，使长期移动材料经过新Chunk时仍可复用固定容量工作区。
        /// </summary>
        public void ResetForTrustedKeyRetirement()
        {
            Generation = Revision = 0;
            BaselineInstalled = false;
            LastFailure = WorldReplicaRevisionFailure.None;
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
