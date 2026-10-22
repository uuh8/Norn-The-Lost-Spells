using Game.Core;

namespace Game.Run
{
    /// <summary>
    /// Gameplay 只声明“希望进入哪张 Scene”；Fade、输入遮挡和 LoadSceneAsync
    /// 由依赖 Game.Run 的 Game.UI 执行，避免形成 Game.Run -> Game.UI 反向依赖。
    /// </summary>
    public struct SceneTransitionRequestedEvent : IGameEvent
    {
        public string SceneName;
    }
}
