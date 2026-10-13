using Game.Core;
using Game.Run;
using UnityEngine;
using UnityEngine.UI;
namespace Game.UI
{
    /// <summary>只读撤离进度。角色在出口按F启动，UI不拥有输入或切图权限。</summary>
    public sealed class ExtractionHud : MonoBehaviour
    {
        [SerializeField] private SinglePlayerInventorySession _session;
        [SerializeField] private SinglePlayerInventoryController _inventory;
        [SerializeField] private Text _condition, _timer;
        private readonly string[] _seconds = new string[301], _amounts = new string[1025];
        private void Awake()
        {
            // UGUI Text最终需要string；提前缓存整数显示，连续倒计时不插值/new字符串。
            for (int i = 0; i < _seconds.Length; i++) _seconds[i] = "撤离倒计时 " + i + " 秒 · 离开重计";
            for (int i = 0; i < _amounts.Length; i++) _amounts[i] = "本局真实采集 " + i + " GMU";
        }
        private void OnEnable() { EventBus<ExtractionProgressChangedEvent>.Subscribe(OnProgress); EventBus<RaidStateChangedEvent>.Subscribe(OnState); }
        private void OnDisable() { EventBus<ExtractionProgressChangedEvent>.Unsubscribe(OnProgress); EventBus<RaidStateChangedEvent>.Unsubscribe(OnState); }
        private void OnProgress(ExtractionProgressChangedEvent e)
        {
            if (e.InventoryId != _session.ReadOnly?.InventoryId) return;
            _condition.text = e.Collected <= 1024 ? _amounts[(int)e.Collected] : "本局真实采集 ≥1024 GMU";
            _timer.text = !e.Ready ? "出口条件未满足 · 先采集" : !e.Inside ? "出口已就绪 · 靠近蓝色传送门按F" : !e.Running ? e.Cancelled ? "已取消撤离 · 按F重新计时" : "F 返回据点" : e.Remaining > 300 ? "撤离倒计时 >300 秒" : _seconds[Mathf.CeilToInt(e.Remaining)];
        }
        private void OnState(RaidStateChangedEvent e)
        {
            if(e.InventoryId!=_session.ReadOnly?.InventoryId)return;
            bool exploring=e.Phase==RaidPhase.Exploring || e.Phase==RaidPhase.Extracting;
            _condition.gameObject.SetActive(exploring);_timer.gameObject.SetActive(exploring);
            if(!exploring){_condition.text=_timer.text="";}
        }
        private void Update()
        { if (_inventory.UiFont != null && _condition.font != _inventory.UiFont) _condition.font = _timer.font = _inventory.UiFont; }
    }
}
