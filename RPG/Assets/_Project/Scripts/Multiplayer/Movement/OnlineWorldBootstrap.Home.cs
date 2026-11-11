using Game.Character;

namespace Game.Multiplayer
{
    public sealed partial class OnlineWorldBootstrap
    {
        private OnlineHomeWorldBindings _homeBindings;
        public OnlineHomeWorldBindings HomeBindings
        {
            get
            {
                if (_dungeonDefinition == null || !_dungeonDefinition.IsPeacefulHome || _worldContent == null) return null;
                if (_homeBindings == null) _homeBindings = _worldContent.GetComponentInChildren<OnlineHomeWorldBindings>(true);
                return _homeBindings;
            }
        }
        /// <summary>
        /// 原和平研究暂停是Gameplay时钟状态，独立于租约/故障Frozen。网络、HTTP和Owner结构编辑仍运行；
        /// Clock每帧清追赶债务，关闭研究后不会突然补模拟研究期间的物理/材料时间。
        /// </summary>
        public bool ResearchPaused => _dungeonDefinition != null && _dungeonDefinition.IsPeacefulHome
            && _combat != null && _combat.ResearchPaused;
    }
}
