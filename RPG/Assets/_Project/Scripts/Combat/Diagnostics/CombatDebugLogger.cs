using UnityEngine;
using Game.Core;

namespace Game.Combat
{
    /// <summary>
    /// 【临时验证脚本】订阅战斗事件并经 GameLog 打印，用于 Scene 内手动验证核心闭环。
    /// 核心系统验证通过后可删除。
    /// </summary>
    public class CombatDebugLogger : MonoBehaviour
    {
        private void OnEnable()
        {
            EventBus<DamageReceivedEvent>.Subscribe(OnDamage);
            EventBus<DeathEvent>.Subscribe(OnDeath);
        }
        private void OnDisable()
        {
            EventBus<DamageReceivedEvent>.Unsubscribe(OnDamage);
            EventBus<DeathEvent>.Unsubscribe(OnDeath);
        }

        private void OnDamage(DamageReceivedEvent e)
        {
            GameLog.Info($"Target {e.TargetId} took " +
                         $"{e.Amount} {e.Type} dmg from {e.AttackerId}, " +
                         $"HP left {e.RemainingHp}", "Combat");
        }

        private void OnDeath(DeathEvent e)
        {
            GameLog.Info($"Target {e.TargetId} died (killer {e.AttackerId})", "Combat");
        }
    }
}
