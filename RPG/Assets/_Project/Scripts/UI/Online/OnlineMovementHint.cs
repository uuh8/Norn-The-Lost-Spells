using Game.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>静态操作提示只随生命周期更新，不逐帧拼接Tick/坐标文本造成UI分配。</summary>
    public sealed class OnlineMovementHint : MonoBehaviour
    {
        private Text _text; private Font _font;
        private void Awake()
        {
            _text = GetComponent<Text>(); _font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "SimSun", "Arial" }, 20);
            _text.font = _font; _text.enabled = false;
        }
        private void OnEnable() => EventBus<OnlineMovementPresentationEvent>.Subscribe(OnView);
        private void OnDisable() => EventBus<OnlineMovementPresentationEvent>.Unsubscribe(OnView);
        private void OnView(OnlineMovementPresentationEvent view)
        {
            _text.enabled = view.Active;
            _text.text = view.Frozen ? "连接暂时中断，运动已冻结；Esc打开联机菜单" : "移动与AI验证副本  ·  WASD移动  ·  Space跳跃  ·  Shift冲刺  ·  Esc联机菜单";
        }
        private void OnDestroy() { if (_font != null) Destroy(_font); }
    }
}
