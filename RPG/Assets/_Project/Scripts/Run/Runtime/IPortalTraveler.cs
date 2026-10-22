using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// Portal Gameplay 与具体角色表现之间的最小合同。
    /// Game.Run 只发出“向哪里、用多久”的请求，不需要反向依赖 Game.Character。
    /// </summary>
    public interface IPortalTraveler
    {
        /// <summary>
        /// 首次接受 Transit 请求时返回 true；已经在 Transit 或配置无效时返回 false。
        /// </summary>
        bool BeginPortalTransit(Transform destination, float duration);
    }
}
