using UnityEngine;

namespace Game.Core
{
    /// <summary>活动域失败与普通无效输入分开：容量失败意味着权威无法完整执行，必须停止并重建世界。</summary>
    public enum WorldActivityFailure : byte { None, InvalidActor, SourceCapacity, RegionCapacity }

    /// <summary>
    /// 装配层向材料世界提供已认证角色的位置；Core不持有Character或ElementField实现。
    /// 四槽身份由服务器装配层维护，不能接受客户端自报位置。死亡、断线或场景退出应清槽；
    /// 清槽只移除角色的接触查询范围，不能停止在途材料和远处反应。调用发生在材料Step之外。
    /// </summary>
    public interface IWorldAuthorityActivity
    {
        WorldActivityFailure LastActivityFailure { get; }
        bool TrySetActor(int slot, ulong actorId, Vector3 position);
        void ClearActor(int slot);
    }
}
