using System;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 初始化时在显式 Root 下扫描一次 Authoring，随后只遍历固定数组。这样 Fixed Tick 不需要
    /// Find、LINQ、临时数组或 Closure；层级遍历顺序同时成为 Capacity Overflow 的确定性优先级。
    /// </summary>
    public sealed class FluidColliderProxyCollector
    {
        private readonly FluidColliderAuthoring[] _authorings;
        private readonly Collider[] _colliders;
        private readonly FluidColliderProxy[] _proxies;
        private readonly Matrix4x4[] _lastLocalToWorld;
        private readonly uint[] _lastDirtyVersions;
        private readonly bool[] _lastEligibility;

        public int ProxyCount { get; }
        public int OverflowCount { get; }
        public FluidColliderProxy[] Proxies => _proxies;

        public FluidColliderProxyCollector(Transform root, int capacity)
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            _authorings = new FluidColliderAuthoring[capacity];
            _colliders = new Collider[capacity];
            _proxies = new FluidColliderProxy[capacity];
            _lastLocalToWorld = new Matrix4x4[capacity];
            _lastDirtyVersions = new uint[capacity];
            _lastEligibility = new bool[capacity];

            // 这次返回数组的分配只发生在初始化边界；结果被压入固定容量数组，热路径不再扫描层级。
            FluidColliderAuthoring[] discovered =
                root.GetComponentsInChildren<FluidColliderAuthoring>(includeInactive: true);
            int proxyCount = 0;
            int overflowCount = 0;
            for (int index = 0; index < discovered.Length; index++)
            {
                FluidColliderAuthoring authoring = discovered[index];
                Collider collider = authoring != null ? authoring.ResolveCollider() : null;
                // Slot ownership只由“显式 Authoring + 受支持 Shape”决定；Enable/Active/Trigger 是可变状态，
                // 不能在首次扫描时把它们过滤掉，否则后来启用会改变后续对象的稳定 Index。
                if (!IsSupported(authoring, collider))
                    continue;

                if (proxyCount >= capacity)
                {
                    overflowCount++;
                    continue;
                }

                _authorings[proxyCount] = authoring;
                _colliders[proxyCount] = collider;
                bool eligible = IsEligible(authoring, collider);
                _proxies[proxyCount] = eligible
                    && FluidColliderProxy.TryCreate(collider, out FluidColliderProxy proxy)
                        ? proxy
                        : FluidColliderProxy.Disabled;
                _lastLocalToWorld[proxyCount] = collider.transform.localToWorldMatrix;
                _lastDirtyVersions[proxyCount] = authoring.DirtyVersion;
                _lastEligibility[proxyCount] = eligible;
                proxyCount++;
            }

            ProxyCount = proxyCount;
            OverflowCount = overflowCount;
        }

        /// <summary>
        /// Dynamic 只在 Matrix 变化时更新；任意 Authoring 都可用 MarkDirty 刷新 Collider 参数。
        /// 返回 true 表示调用者需要把固定 CPU 数组重新上传到已有 GraphicsBuffer。
        /// </summary>
        public bool RefreshChangedProxies()
        {
            bool changed = false;
            for (int index = 0; index < ProxyCount; index++)
            {
                FluidColliderAuthoring authoring = _authorings[index];
                Collider collider = _colliders[index];
                Matrix4x4 currentMatrix = collider.transform.localToWorldMatrix;
                bool eligible = IsEligible(authoring, collider);
                bool explicitlyDirty = authoring.DirtyVersion != _lastDirtyVersions[index];
                bool transformChanged = authoring.IsDynamic
                    && currentMatrix != _lastLocalToWorld[index];
                bool eligibilityChanged = eligible != _lastEligibility[index];
                if (!explicitlyDirty && !transformChanged && !eligibilityChanged)
                    continue;

                if (eligible
                    && FluidColliderProxy.TryCreate(collider, out FluidColliderProxy proxy))
                {
                    _proxies[index] = proxy;
                }
                else
                {
                    // 不压缩数组可保持其他 Proxy Index 稳定；GPU 遇到未知 Shape 值会直接跳过。
                    _proxies[index] = FluidColliderProxy.Disabled;
                }

                _lastLocalToWorld[index] = currentMatrix;
                _lastDirtyVersions[index] = authoring.DirtyVersion;
                _lastEligibility[index] = eligible;
                changed = true;
            }

            return changed;
        }

        private static bool IsEligible(FluidColliderAuthoring authoring, Collider collider)
        {
            return IsSupported(authoring, collider)
                && authoring.isActiveAndEnabled
                && collider.enabled
                && !collider.isTrigger
                && collider.gameObject.activeInHierarchy;
        }

        private static bool IsSupported(FluidColliderAuthoring authoring, Collider collider)
        {
            return authoring != null
                && collider != null
                && (collider is BoxCollider
                    || collider is SphereCollider
                    || collider is CapsuleCollider);
        }
    }
}
