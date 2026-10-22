using Game.Combat;

namespace Game.ElementField
{
    /// <summary>
    /// P6 元素球使用的最薄 Projectile 类型。
    /// 飞行、Collision、伤害与 Impacted 事件仍由 Combat 的 ProjectileBase 统一负责；
    /// 元素沉积由同物体上的 ElementDepositOnImpact 组合完成，避免复制 Projectile 生命周期代码。
    /// </summary>
    public sealed class ElementOrbProjectile : ProjectileBase
    {
    }
}
