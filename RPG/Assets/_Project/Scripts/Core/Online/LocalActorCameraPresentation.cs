using UnityEngine;

namespace Game.Core
{
    /// <summary>原相机完成绑定的离散表现事实；不携带角色控制、网络或 Cinemachine 权限。</summary>
    public struct LocalActorCameraBoundEvent : IGameEvent
    {
        public GameObject Actor;
        public Camera Camera;
    }

    /// <summary>表现层注册实际 Camera，组合层经 Core 读取，避免 Multiplayer 反向依赖 Rendering。</summary>
    public static class LocalActorCameraPresentation
    {
        public static int Owner { get; private set; }
        public static GameObject Actor { get; private set; }
        public static Camera Camera { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() { Owner = 0; Actor = null; Camera = null; }

        public static void Bind(int owner, GameObject actor, Camera camera)
        {
            if (owner == 0 || actor == null || camera == null) return;
            Owner = owner; Actor = actor; Camera = camera;
            EventBus<LocalActorCameraBoundEvent>.Publish(new LocalActorCameraBoundEvent { Actor = actor, Camera = camera });
        }

        public static void Clear(int owner)
        {
            // Scene 卸载可能晚于新 Scene 完成绑定；旧 Owner 不能抹掉新 Camera。
            if (Owner != owner) return;
            Owner = 0; Actor = null; Camera = null;
            EventBus<LocalActorCameraBoundEvent>.Publish(default);
        }

        public static bool TryGet(GameObject actor, out Camera camera)
        {
            camera = null;
            // Unity Object 的销毁语义由 == 处理；不能只检查托管引用或使用 ??。
            if (actor == null || Actor == null || actor != Actor || Camera == null) return false;
            camera = Camera; return true;
        }
    }
}
