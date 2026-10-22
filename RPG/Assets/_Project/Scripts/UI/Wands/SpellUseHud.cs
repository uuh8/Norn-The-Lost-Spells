using System;
using System.Collections;
using System.Text;
using Game.Core;
using Game.Run;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>测试/开发 HUD 用真实只读库存显示次数和资格；事件触发刷新，没有每帧格式化。</summary>
    public sealed class SpellUseHud : MonoBehaviour
    {
        [SerializeField] private SinglePlayerInventorySession _session;
        [SerializeField] private Text _label, _status;
        [SerializeField] private Button _begin, _end;
        private Font _font;
        public string DisplayText => _label != null ? _label.text : "";
        private void OnEnable()
        {
            EventBus<InventoryChangedEvent>.Subscribe(OnChanged); EventBus<CastResourceResultEvent>.Subscribe(OnCast);
            EventBus<SpellRaidLifecycleResultEvent>.Subscribe(OnRaid);
            if (_begin != null) _begin.onClick.AddListener(Begin); if (_end != null) _end.onClick.AddListener(End);
        }
        private void OnDisable()
        {
            EventBus<InventoryChangedEvent>.Unsubscribe(OnChanged); EventBus<CastResourceResultEvent>.Unsubscribe(OnCast);
            EventBus<SpellRaidLifecycleResultEvent>.Unsubscribe(OnRaid);
            if (_begin != null) _begin.onClick.RemoveListener(Begin); if (_end != null) _end.onClick.RemoveListener(End);
        }
        private IEnumerator Start()
        {
            _font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Arial" }, 18);
            foreach (Text label in GetComponentsInChildren<Text>(true)) label.font = _font;
            while (_session != null && _session.ReadOnly == null) yield return null;
            Refresh();
        }
        private bool Matches(string scope) => _session != null && _session.ReadOnly != null && scope == _session.ReadOnly.InventoryId;
        private void OnChanged(InventoryChangedEvent change) { if (Matches(change.InventoryId)) Refresh(); }
        private void OnRaid(SpellRaidLifecycleResultEvent result)
        { if (Matches(result.InventoryId)) { Refresh(); _status.text = result.Success ? "资格边界已更新" : "资格操作失败：" + result.Reason; } }
        private void OnCast(CastResourceResultEvent result)
        {
            // 清理收据不覆盖玩家上一发结果；只展示实际施法与有意义的失败。
            if (!Matches(result.InventoryId) || result.Operation == CastResourceOperation.Abandon
                || result.Operation == CastResourceOperation.AbandonCaller || result.Operation == CastResourceOperation.Cancel) return;
            _status.text = result.Success ? result.Operation == CastResourceOperation.Complete ? "施法成功" : "准备施法" : FailureText(result.Reason);
        }
        private static string FailureText(OwnedWandFailureReason reason)
        {
            switch (reason)
            {
                case OwnedWandFailureReason.InsufficientUses: return "法术次数不足，本次未施法";
                case OwnedWandFailureReason.MissingEligibility: return "需要取得本局携带资格";
                case OwnedWandFailureReason.InsufficientMana: return "原杖 Mana 不足";
                case OwnedWandFailureReason.Cooldown: return "原杖仍在冷却";
                case OwnedWandFailureReason.BudgetConsumed: return "该触发已经结束";
                case OwnedWandFailureReason.SpawnFailed: return "法术生成失败，本层资源未扣除";
                case OwnedWandFailureReason.InvalidProgram: return "法术配置没有有效产出";
                default: return "施法未提交：" + reason;
            }
        }
        private void Refresh()
        {
            if (_label == null || _session == null || _session.ReadOnly == null) return;
            var read = _session.ReadOnly; var uses = _session.SpellUsesReadOnly;
            var items = new InventoryItemSnapshot[read.Count]; int count = read.CopyItemsTo(items);
            var text = new StringBuilder("法术实例与资格\n");
            text.Append(uses.RaidId == null ? "整备中\n" : "测试冒险中\n");
            for (int i = 0; i < count; i++)
            {
                var item = items[i]; if (!uses.TryGetState(item.InstanceId, out SpellUseState use)) continue;
                text.Append(item.DisplayName).Append("：");
                if (use.IsQualification)
                    text.Append(use.Unlimited ? "无限入局" : "未来入局 " + use.RemainingCharges + " 次").Append(use.Eligible ? " · 本局可用" : " · 未取得资格");
                else text.Append(use.Unlimited ? "无限使用" : "剩余 " + use.RemainingCharges + " 次");
                text.Append(item.Location.Kind == ItemLocationKind.WandSlot ? " [已安装]" : " [备用]").Append('\n');
            }
            _label.text = text.ToString();
        }
        public void Begin()
        { if (_session == null || _session.ReadOnly == null) return;
            EventBus<SpellRaidLifecycleRequestedEvent>.Publish(new SpellRaidLifecycleRequestedEvent {
                InventoryId = _session.ReadOnly.InventoryId, RaidId = "sandbox." + Guid.NewGuid().ToString("N"), Begin = true }); }
        public void End()
        { if (_session == null || _session.ReadOnly == null) return;
            EventBus<SpellRaidLifecycleRequestedEvent>.Publish(new SpellRaidLifecycleRequestedEvent { InventoryId = _session.ReadOnly.InventoryId, Begin = false }); }
        private void OnDestroy() { if (_font != null) Destroy(_font); }
    }
}
