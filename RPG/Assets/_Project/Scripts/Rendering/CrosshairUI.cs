using UnityEngine;
using Game.Core;

namespace Game.Rendering
{
    /// <summary>
    /// 准心 UI：订阅 CrosshairVisibilityEvent，按显隐信号开关准心 GameObject。
    /// 远程角色(弓手/法师)全程常驻准心由控制器发该事件驱动；与"蓄力瞄准取景"(AimStateChangedEvent)分离。
    /// 经 EventBus 与 gameplay 解耦——不引用 Game.Character。SetActive 一个 GameObject，无需 UGUI 程序集引用。
    /// </summary>
    public class CrosshairUI : MonoBehaviour
    {
        [SerializeField] private GameObject _crosshair; // 准心根物体（默认隐藏）

        private void Awake()
        {
            if (_crosshair != null) _crosshair.SetActive(false);
        }

        private void OnEnable()  => EventBus<CrosshairVisibilityEvent>.Subscribe(OnCrosshairVisibility);
        private void OnDisable() => EventBus<CrosshairVisibilityEvent>.Unsubscribe(OnCrosshairVisibility);

        private void OnCrosshairVisibility(CrosshairVisibilityEvent e)
        {
            if (_crosshair != null) _crosshair.SetActive(e.Visible);
        }
    }
}
