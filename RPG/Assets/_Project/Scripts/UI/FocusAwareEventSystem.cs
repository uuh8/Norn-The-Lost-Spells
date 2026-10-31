using UnityEngine;
using UnityEngine.EventSystems;

namespace Game.UI
{
    /// <summary>
    /// 保留 UGUI EventSystem 的选择、导航、Raycast 和 InputModule 分发规则，
    /// 只修复“Scene 根停用期间错过窗口 Focus 回调”的恢复边界。
    /// 例如联机暂存 Hub 时，窗口后来已经回到前台，旧 m_HasFocus 却仍可能为 false；
    /// 此时鼠标与按钮都正常，InputSystemUIInputModule 仍会跳过本应处理的点击。
    /// -1000 与原包中 EventSystem 的执行顺序保持一致，不能依赖新脚本的默认顺序。
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class FocusAwareEventSystem : EventSystem
    {
        protected override void OnEnable()
        {
            // base.OnEnable 把本实例注册到 EventSystem 的活动列表，保留原 current / InputModule 生命周期。
            // 不能完全替换 base 的初始化，否则同一套 UGUI 控件会失去事件分发者。
            base.OnEnable();
            // UGUI 的 m_HasFocus 只随 OnApplicationFocus 更新，停用的 Hub 可能错过该回调。
            // 重新采样真实窗口状态，不伪造点击、不改 InputSystem 全局 Focus/后台策略，也不逐帧轮询。
            // OnApplicationFocus 是 UGUI 暴露给子类的受保护回调：这里调用原实现同步其缓存，
            // 而非通过反射修改私有字段；Application.isFocused 则提供当前真实窗口状态。
            // 只在重新启用这个离散边界采样，不把后台窗口强行标记为获得焦点。
            base.OnApplicationFocus(Application.isFocused);
        }
    }
}
