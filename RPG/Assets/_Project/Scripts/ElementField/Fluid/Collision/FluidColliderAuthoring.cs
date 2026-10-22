using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 显式允许一个 Unity Collider 进入 GPU 流体世界。未挂该组件的碰撞体不会被扫描，
    /// 从而避免场景中所有 Physics 对象都意外增加每粒子 Collision 成本。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FluidColliderAuthoring : MonoBehaviour
    {
        [SerializeField] private Collider _collider;
        [Tooltip("Dynamic 会比较 Transform Matrix；Static 只在初始化或显式 MarkDirty 时重建。")]
        [SerializeField] private bool _isDynamic;

        private uint _dirtyVersion;

        public bool IsDynamic => _isDynamic;
        internal uint DirtyVersion => _dirtyVersion;

        public void MarkDirty()
        {
            unchecked
            {
                _dirtyVersion++;
            }
        }

        internal Collider ResolveCollider()
        {
            if (_collider == null)
                _collider = GetComponent<Collider>();
            return _collider;
        }

        private void Awake()
        {
            ResolveCollider();
        }

        private void OnEnable()
        {
            MarkDirty();
        }

        private void OnDisable()
        {
            // Static Proxy 也能通过生命周期边界失效；Collector 下次 Fixed Tick 不会继续保留幽灵碰撞。
            MarkDirty();
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            ResolveCollider();
            MarkDirty();
        }
#endif
    }
}
