using UnityEngine;

namespace Game.Core
{
    /// <summary>
    /// 和平实验场对共用研究UI提供的只读空间事实。实现留在Character，UI不能沿接口推进World或写资源。
    /// CanResearch只回答当前角色是否仍位于原作者区域，不把研究窗口自己的输入捕获误判成失去权限。
    /// 真正开窗与编辑仍由DS检查同Owner、Epoch、原Box和当前会话事实。
    /// </summary>
    public interface IOnlineHomeResearchAccess
    {
        GameObject Actor { get; }
        int WorldSceneHandle { get; }
        ulong ActorId { get; }
        uint ConnectionEpoch { get; }
        bool CanResearch { get; }
    }

    public struct OnlineHomeResearchAccessChangedEvent : IGameEvent { public int WorldSceneHandle; }
    /// <summary>Character沿原F优先级选到研究区后发意图；UI转发库存会话命令，DS仍重验区域。</summary>
    public struct OnlineHomeResearchRequestedEvent : IGameEvent
    { public ulong ActorId; public uint ConnectionEpoch; public int WorldSceneHandle; }

    /// <summary>仅缓存当前本机的场景能力，复用原Combat Scope装配；这不是网络认证或Gameplay权限。</summary>
    public static class OnlineHomeResearchPresentation
    {
        private static int _owner;
        private static IOnlineHomeResearchAccess _access;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() { _owner = 0; _access = null; }
        public static void Bind(int owner, IOnlineHomeResearchAccess access)
        {
            if (owner == 0 || access == null || access.Actor == null) return;
            _owner = owner; _access = access;
            EventBus<OnlineHomeResearchAccessChangedEvent>.Publish(new OnlineHomeResearchAccessChangedEvent { WorldSceneHandle = access.WorldSceneHandle });
        }
        public static bool TryGet(int sceneHandle, GameObject actor, out IOnlineHomeResearchAccess access)
        {
            access = null;
            if (_access == null || _access is Object value && value == null || actor == null
                || _access.Actor != actor || _access.WorldSceneHandle != sceneHandle || !actor.scene.isLoaded) return false;
            access = _access; return true;
        }
        public static void Clear(int owner)
        {
            if (_owner != owner) return;
            int scene = _access != null && !(_access is Object value && value == null) ? _access.WorldSceneHandle : 0;
            _owner = 0; _access = null;
            EventBus<OnlineHomeResearchAccessChangedEvent>.Publish(new OnlineHomeResearchAccessChangedEvent { WorldSceneHandle = scene });
        }
    }
}
