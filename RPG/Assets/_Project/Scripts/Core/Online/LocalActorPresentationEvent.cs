using UnityEngine;

namespace Game.Core
{
    /// <summary>本机角色的只读表现绑定；GameObject 仅用于找到既有 Health/Mana/Status，不授予 Gameplay 写权限。</summary>
    public struct LocalActorPresentationEvent : IGameEvent
    {
        public GameObject Actor;
        public bool CrosshairVisible;
        public Transform CameraTarget;
    }

    /// <summary>共享背包只申请本机输入捕获；OwnerId 用 UI 实例 Id，不能暂停整个联机世界。</summary>
    public struct InventoryPresentationVisibilityEvent : IGameEvent
    {
        public int OwnerId;
        public bool Open;
    }

    /// <summary>保存最近一次表现绑定，解决角色和 Canvas 跨 Scene 创建顺序不同造成的漏订阅。</summary>
    public static class LocalActorPresentation
    {
        private static int _owner;
        public static GameObject Actor { get; private set; }
        public static bool CrosshairVisible { get; private set; }
        public static Transform CameraTarget { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() { _owner = 0; Actor = null; CrosshairVisible = false; CameraTarget = null; }

        public static void Bind(int owner, GameObject actor, bool crosshairVisible)
            => Bind(owner, actor, crosshairVisible, null);

        public static void Bind(int owner, GameObject actor, bool crosshairVisible, Transform cameraTarget)
        {
            if (owner == 0 || actor == null) return;
            _owner = owner; Actor = actor; CrosshairVisible = crosshairVisible; CameraTarget = cameraTarget;
            // 目标由角色组合层提供；Rendering 不必反向引用 Character 或按名字查找骨骼。
            EventBus<LocalActorPresentationEvent>.Publish(new LocalActorPresentationEvent
            { Actor = actor, CrosshairVisible = crosshairVisible, CameraTarget = cameraTarget });
        }

        public static void Clear(int owner)
        {
            // 卸载旧世界不能清掉较晚建立的新世界；Unity 销毁对象用 == 判断，不用 ?. / ??。
            if (_owner != owner) return;
            _owner = 0; Actor = null; CrosshairVisible = false; CameraTarget = null;
            EventBus<LocalActorPresentationEvent>.Publish(default);
        }
    }
}
