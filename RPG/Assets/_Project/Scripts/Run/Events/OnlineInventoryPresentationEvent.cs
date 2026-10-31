using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// 当前本机 Actor 的线上库存只读来源。UI 只取得表现接口，不获得 Profile、Store 或网络连接；
    /// Actor 引用仅作场景/实例身份匹配，不能据此推导某个远端玩家的资产权限。
    /// </summary>
    public struct OnlineInventoryPresentationEvent : IGameEvent
    {
        public GameObject Actor;
        public IInventoryPresentationSource Source;
    }

    /// <summary>
    /// 重放最新读源，解决网络快照先于 UI OnEnable 到达的次序差异。缓存只属于一个本机 Owner；
    /// 旧场景 Replica 的迟到 Destroy 只能清自己的记录，不能把新世界已经绑定的读源清空。
    /// </summary>
    public static class OnlineInventoryPresentation
    {
        private static int _owner;
        private static GameObject _actor;
        private static IInventoryPresentationSource _source;
        public static GameObject Actor => _actor != null ? _actor : null;
        public static IInventoryPresentationSource Source => Actor != null ? Alive(_source) : null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() { _owner = 0; _actor = null; _source = null; }

        public static void Bind(int owner, GameObject actor, IInventoryPresentationSource source)
        {
            if (owner == 0 || actor == null || Alive(source) == null) return;
            // 同 Source 对象内部原子换值由离散 Inventory/Toolbelt 事件通知；不重发绑定来打断原 Tab/拖拽。
            if (_owner == owner && _actor == actor && ReferenceEquals(_source, source)) return;
            _owner = owner; _actor = actor; _source = source;
            EventBus<OnlineInventoryPresentationEvent>.Publish(new OnlineInventoryPresentationEvent { Actor = actor, Source = source });
        }

        public static void Clear(int owner)
        {
            if (owner == 0 || owner != _owner) return;
            _owner = 0; _actor = null; _source = null;
            EventBus<OnlineInventoryPresentationEvent>.Publish(default);
        }

        private static IInventoryPresentationSource Alive(IInventoryPresentationSource source)
        {
            // 接口类型不会自动使用 Unity 的 destroyed-object == 运算符，显式拦截遗留 wrapper。
            return source is Object value && value == null ? null : source;
        }
    }
}
