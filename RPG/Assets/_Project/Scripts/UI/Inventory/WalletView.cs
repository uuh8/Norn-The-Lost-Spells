using System.Globalization;
using Game.Run;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>所有钱包栏读同一会话。只在余额变更时更新文字，稳定Update不格式化、不复制金币状态。</summary>
    public sealed class WalletView : MonoBehaviour
    {
        [SerializeField] private SinglePlayerInventorySession _session;
        [SerializeField] private Text _amount;
        private long _last = long.MinValue;
        private static readonly ProfilerMarker Marker = new ProfilerMarker("Norn.Wallet.Refresh");
        private void OnEnable() { _last = long.MinValue; Refresh(); }
        private void Update() => Refresh();
        private void Refresh()
        {
            using var sample = Marker.Auto(); var wallet = _session != null ? _session.WalletReadOnly : null;
            if (wallet == null || _amount == null || wallet.Currency == _last) return;
            _last = wallet.Currency; _amount.text = _last.ToString("N0", CultureInfo.InvariantCulture);
        }
    }
}
