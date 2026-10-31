using UnityEngine;

namespace Game.Core
{
    /// <summary>
    /// 声明一个可以由共同战斗 HUD 接管的纯表现叶树。UI 与 Rendering 都依赖 Core，
    /// 因而用这个中性接口识别旧控件，不让 UI 反向引用具体 Rendering 脚本。
    /// 实现者必须返回只包含该 HUD 的节点，不能返回玩家、Session 或整张场景的 __UI 根。
    /// </summary>
    public interface ICombatHudElement
    {
        GameObject PresentationRoot { get; }
    }
}
