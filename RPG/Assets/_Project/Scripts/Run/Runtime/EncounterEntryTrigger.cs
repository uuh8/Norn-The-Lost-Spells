using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// Encounter 入口的 Scene Adapter：把 Unity Physics 的空间进入消息转换为开战请求。
    /// 本组件只负责“谁进入了入口”，不负责激活敌人、控制 Gate 或推进整局顺序；
    /// 这些规则分别由 DemoEncounterController 与 DemoRunController 保持唯一真相来源。
    /// </summary>
    public sealed class EncounterEntryTrigger : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("进入该 Trigger 后尝试开始的战斗区域。只有已 Armed 的 Encounter 会接受请求。")]
        private DemoEncounterController _encounter;

        [SerializeField]
        [Tooltip("允许触发入口的 Layer。P7 场景中应只选择 Player，防止 Enemy 或 Projectile 误触。")]
        private LayerMask _playerLayers;

        private void OnTriggerEnter(Collider other)
        {
            if (_encounter == null || other == null)
            {
                return;
            }

            // LayerMask 是一个 bit field：第 n 位代表第 n 个 Unity Layer。
            // 1 << layer 把进入者的 Layer 转成单独的 bit，再与允许集合做 AND；
            // 结果为 0 表示该 Layer 不在集合中。该路径不创建对象，也没有字符串比较或 GC Alloc。
            int enteringLayerBit = 1 << other.gameObject.layer;
            if ((_playerLayers.value & enteringLayerBit) == 0)
            {
                return;
            }

            // 不在 Trigger 内维护“是否已触发”的第二份状态。
            // EncounterProgressTracker 会只接受 Armed -> Active 的首次迁移，
            // 因而玩家多个 Collider 或重复进入都不会重复产生开战 Side Effect。
            _encounter.TryBeginEncounter();
        }
    }
}
