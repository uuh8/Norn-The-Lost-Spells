using Game.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>静态操作提示只随生命周期更新，不逐帧拼接Tick/坐标文本造成UI分配。</summary>
    public sealed class OnlineMovementHint : MonoBehaviour
    {
        private Text _text; private Font _font;
        private string _normalText;
        private float _feedbackUntil;
        private void Awake()
        {
            _text = GetComponent<Text>(); _font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "SimSun", "Arial" }, 20);
            _text.font = _font; _text.enabled = false;
        }
        private void OnEnable()
        { EventBus<OnlineMovementPresentationEvent>.Subscribe(OnView); EventBus<OnlineCastFeedbackEvent>.Subscribe(OnCastFeedback); }
        private void OnDisable()
        { EventBus<OnlineMovementPresentationEvent>.Unsubscribe(OnView); EventBus<OnlineCastFeedbackEvent>.Unsubscribe(OnCastFeedback); }
        private void OnView(OnlineMovementPresentationEvent view)
        {
            _text.enabled = view.Active;
            _normalText = view.Frozen ? "连接暂时中断，战斗已暂停；Esc打开联机菜单" : "WASD移动  ·  Space跳跃  ·  Shift冲刺  ·  鼠标左键施法  ·  Esc联机菜单";
            _feedbackUntil = 0; _text.text = _normalText;
        }
        private void OnCastFeedback(OnlineCastFeedbackEvent feedback)
        {
            // 低频收据才换用常量文本；不逐帧拼接网络状态或暴露服务器实现标识。
            _text.text = feedback.Failure switch
            {
                OnlineCastFailure.ResourceRejected => "当前法力或法术次数不足",
                OnlineCastFailure.EmptyOutput => "没有可执行的法术，请检查法杖和剩余次数",
                OnlineCastFailure.UnsupportedMaterial => "此法术的材料互动暂未支持联机",
                OnlineCastFailure.NotOwned or OnlineCastFailure.StaleProgram => "法杖状态已变化，请重新连接后重试",
                OnlineCastFailure.InvalidInput => "瞄准位置无效，请重新瞄准",
                OnlineCastFailure.Interrupted => "施法已取消",
                _ => "角色当前无法施法"
            };
            _feedbackUntil = Time.unscaledTime + 2f;
        }
        private void Update()
        {
            if (_feedbackUntil <= 0 || Time.unscaledTime < _feedbackUntil) return;
            _feedbackUntil = 0; _text.text = _normalText;
        }
        private void OnDestroy() { if (_font != null) Destroy(_font); }
    }
}
