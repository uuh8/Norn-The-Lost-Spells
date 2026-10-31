using UnityEngine;

namespace Game.Core
{
    /// <summary>
    /// 当前本机World的关卡HUD读源缓存。角色/Scene/网络不是此缓存的所有者；上层在成功安装可信进度后绑定，
    /// 纯HUD晚于网络出现时按WorldSceneHandle重放。Owner加Source引用双重释放，旧Runtime不能清较新的同World来源。
    /// </summary>
    public static class DungeonPresentation
    {
        private static int _owner, _world;
        private static IDungeonPresentationReadOnly _source;
        public static int WorldSceneHandle => Source != null ? _world : 0;
        public static IDungeonPresentationReadOnly Source => Alive(_source) != null && _source.TryCaptureProgress(out _) ? _source : null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() { _owner = _world = 0; _source = null; }

        public static bool Bind(int owner, int worldSceneHandle, IDungeonPresentationReadOnly source)
        {
            source = Alive(source);
            if (owner == 0 || worldSceneHandle == 0 || source == null || !source.TryCaptureProgress(out _)) return false;
            if (_owner == owner && _world == worldSceneHandle && ReferenceEquals(_source, source)) return true;
            _owner = owner; _world = worldSceneHandle; _source = source;
            EventBus<DungeonPresentationSourceChangedEvent>.Publish(new DungeonPresentationSourceChangedEvent
                { WorldSceneHandle = worldSceneHandle, Source = source });
            return true;
        }
        public static bool TryGet(int worldSceneHandle, out IDungeonPresentationReadOnly source)
        {
            source = worldSceneHandle != 0 && _world == worldSceneHandle ? Source : null;
            return source != null;
        }
        public static void Clear(int owner, IDungeonPresentationReadOnly source)
        {
            // 相同Bindings可以承接新的Runtime；只比Owner不够，必须连来源对象一起核对。
            if (owner == 0 || owner != _owner || !ReferenceEquals(source, _source)) return;
            _owner = _world = 0; _source = null;
            EventBus<DungeonPresentationSourceChangedEvent>.Publish(default);
        }
        private static IDungeonPresentationReadOnly Alive(IDungeonPresentationReadOnly source)
            => source is Object value && value == null ? null : source;
    }
}
