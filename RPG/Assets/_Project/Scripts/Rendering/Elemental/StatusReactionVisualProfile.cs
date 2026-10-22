using UnityEngine;

namespace Game.Rendering
{
    /// <summary>
    /// Gameplay 只发布“发生了什么反应”，Profile 决定用哪个自研 Prefab 表现。
    /// 把映射放在 ScriptableObject 中，可以替换美术实现而不修改 Combat 规则或事件结构。
    /// </summary>
    [CreateAssetMenu(
        menuName = "Game/Rendering/Status Reaction Visual Profile",
        fileName = "StatusReactionVisualProfile")]
    public sealed class StatusReactionVisualProfile : ScriptableObject
    {
        // Continuous Visual 会在 Controller.Awake 中预实例化并反复 Play/Stop，适合有 Started/Resolved 生命周期的反应。
        [Header("Continuous Reaction Visuals")]
        [Tooltip("Extinguish 运行期间跟随状态载体的持续蒸汽 Prefab。")]
        public GameObject ExtinguishSteamPrefab;

        [Tooltip("Ignite Goo 运行期间跟随状态载体的持续燃烧 Prefab；Task 9 正式制作。")]
        public GameObject IgniteGooPrefab;

        [Header("Toxic Combustion (Task 9/10)")]
        // Toxic 的三个阶段需要不同生命周期：Wind-up 是持续槽，Resolved/Cancelled 是一次性 Burst。
        [Tooltip("毒爆 Wind-up 视觉；当前仅预留数据入口，不在 Task 6 实例化。")]
        public GameObject ToxicWindUpPrefab;

        [Tooltip("毒爆 Resolved 瞬时视觉；Task 10 将通过对象池播放。")]
        public GameObject ToxicResolvedPrefab;

        [Min(0.05f)]
        [Tooltip("每次毒爆 VFX 保持激活的秒数；应略大于 Particle System 的完整播放时长。")]
        public float ToxicResolvedLifetime = 2f;

        [Range(1, 16)]
        [Tooltip("预创建的毒爆 VFX 数量。池满时循环复用最早的槽位，避免战斗中 Instantiate/Destroy。")]
        public int ToxicBurstPoolCapacity = 4;

        [Tooltip("毒爆 Cancelled 瞬时视觉；Task 10 将通过对象池播放。")]
        public GameObject ToxicCancelledPrefab;
    }
}
