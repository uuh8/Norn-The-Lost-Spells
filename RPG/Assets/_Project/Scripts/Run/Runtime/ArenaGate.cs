using System;
using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// 战斗区域门的 Scene Side Effect Adapter。
    /// 它只负责同步 Collider 与视觉显隐，不判断 Encounter 是否完成，也不认识 Enemy。
    /// </summary>
    public sealed class ArenaGate : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("门关闭时启用、打开时禁用的阻挡 Collider。不要勾选 Is Trigger。")]
        private Collider[] _blockingColliders = Array.Empty<Collider>();

        [SerializeField]
        [Tooltip("门关闭时显示、打开时隐藏的视觉根节点。若 Collider 本身就是可见模型，可将模型子物体拖入。")]
        private GameObject[] _visualRoots = Array.Empty<GameObject>();

        public bool IsClosed { get; private set; }

        /// <summary>
        /// 同一个状态重复设置不会产生新的 Gameplay 结果，但仍同步所有引用，
        /// 这样在 Editor 中临时启停子物体后可以显式恢复一致状态。
        /// </summary>
        public void SetClosed(bool closed)
        {
            IsClosed = closed;

            for (int i = 0; i < _blockingColliders.Length; i++)
            {
                Collider blockingCollider = _blockingColliders[i];
                if (blockingCollider == null)
                {
                    GameLog.Warn($"ArenaGate '{name}' contains a missing blocking Collider.", "Run");
                    continue;
                }

                blockingCollider.enabled = closed;
            }

            for (int i = 0; i < _visualRoots.Length; i++)
            {
                GameObject visualRoot = _visualRoots[i];
                if (visualRoot == null)
                {
                    GameLog.Warn($"ArenaGate '{name}' contains a missing visual root.", "Run");
                    continue;
                }

                visualRoot.SetActive(closed);
            }
        }
    }
}
