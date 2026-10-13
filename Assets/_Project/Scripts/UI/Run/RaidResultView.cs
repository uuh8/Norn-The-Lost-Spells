using Game.Core;
using Game.Run;
using UnityEngine;
using UnityEngine.UI;
namespace Game.UI
{
    /// <summary>只展示可靠提交的终局；UI显示结果不会触发物品奖励或资产保存。</summary>
    public sealed class RaidResultView : MonoBehaviour
    {
        [SerializeField] private SinglePlayerInventorySession _session;
        [SerializeField] private SinglePlayerInventoryController _inventory;
        [SerializeField] private Text _result;
        private void OnEnable() => EventBus<RaidResultEvent>.Subscribe(OnResult);
        private void OnDisable() => EventBus<RaidResultEvent>.Unsubscribe(OnResult);
        private void OnResult(RaidResultEvent e)
        { if (e.InventoryId == _session.ReadOnly?.InventoryId) _result.text = (e.Outcome == RaidOutcome.Success ? "撤离成功" : "冒险失败：" + e.Outcome)
            + " · 带回" + e.Returned + " / 损失" + e.Lost + " · 已保存第" + e.Generation + "代"; }
        private void Update() { if (_inventory.UiFont != null && _result.font != _inventory.UiFont) _result.font = _inventory.UiFont; }
    }
}
