using Game.Core;
using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>
    /// 只配置联机材料复制的每连接发送窗口。窗口按DS权威玩家所在的参考块对齐，
    /// 与其相交的材料原生Chunk整块同步；它不修改单机配置，也不停止窗口外的DS模拟。
    /// 只保存参考块边长和半径，避免另存世界尺寸后产生两份互相漂移的数据。
    /// </summary>
    [CreateAssetMenu(menuName = "Norn/Online/World Replication Profile")]
    public sealed class OnlineWorldReplicationProfile : ScriptableObject
    {
        [Tooltip("独立网络参考块边长（米），必须为有限正数；不是材料原生格尺寸或模拟范围。")]
        [SerializeField, Min(0)] private float _referenceChunkWorldSize = 2f;
        [Tooltip("X/Z轴两侧邻接参考块数量。初值10配合2m边长，包含玩家所在块共42m。")]
        [SerializeField, Min(0)] private int _radiusXZChunks = 10;
        [Tooltip("Y轴两侧邻接参考块数量。初值3配合2m边长，包含玩家所在块共14m。")]
        [SerializeField, Min(0)] private int _radiusYChunks = 3;

        public float ReferenceChunkWorldSize => _referenceChunkWorldSize;
        public int RadiusXZChunks => _radiusXZChunks;
        public int RadiusYChunks => _radiusYChunks;

        /// <summary>
        /// Inspector约束不能拦截损坏或旧序列化数据，因此Bootstrap仍须显式验证。
        /// 复用Core范围规则检查尺寸与溢出，成功只表示配置有效；实际世界原点和玩家位置
        /// 仍由发送装配层逐次验证，不能用这里的零原点代替真实已提交几何。
        /// </summary>
        public bool Validate(out string error)
        {
            if (!WorldInterestCoordinator.TryBuildReferenceBounds(Vector3.zero, Vector3.zero,
                    _referenceChunkWorldSize, _radiusXZChunks, _radiusYChunks, out _))
            {
                error = "联机材料复制范围配置非法：参考块边长须为有限正数，半径须为非负数，且窗口不能溢出或塌缩。";
                return false;
            }
            error = string.Empty;
            return true;
        }
    }
}
