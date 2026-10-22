using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// Teleport 的可变 Cooldown 不写回 BossDefinition。
    /// ScriptableObject 是 Authoring 模板，单局 Runtime 状态必须由 Controller 独占。
    /// </summary>
    public sealed class BossTeleportRuntimeState
    {
        public float CooldownRemaining { get; private set; }

        public void Tick(float deltaTime)
        {
            CooldownRemaining = Mathf.Max(
                0f,
                CooldownRemaining - Mathf.Max(0f, deltaTime));
        }

        public void MarkUsed(float cooldown)
        {
            CooldownRemaining = Mathf.Max(0f, cooldown);
        }
    }
}
