using System.Globalization;
using Game.Core;
using Game.Run;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// 原钱包栏的纯显示边界。本地玩法读原Session，账号Hub只显示唯一Scope确认的余额，
    /// 不把刻意不存在的本地Profile解释成账号金币0，也不创建任何资产或交易写入口。
    /// </summary>
    public sealed class WalletView : MonoBehaviour, IAccountInventoryHubConsumer
    {
        [SerializeField] private SinglePlayerInventorySession _session;
        [SerializeField] private Text _amount;
        private long _last = long.MinValue;
        private string _accountPlayer;
        private uint _accountVersion;
        private long _accountGold;
        private bool _accountPrepared, _showingUnknown;
        private static readonly ProfilerMarker Marker = new ProfilerMarker("Norn.Wallet.Refresh");
        private bool UsesAccountSource => _session != null && _session.AccountInventoryReadOnly
            || OnlineAccountInventoryHubScope.IsRequiredForScene(gameObject.scene);
        private void OnEnable()
        {
            _last = long.MinValue;
            EventBus<OnlineAccountInventoryHubChangedEvent>.Subscribe(OnAccountChanged);
            if (UsesAccountSource) SynchronizeAccountGold();
            Refresh();
        }
        private void OnDisable()
        {
            EventBus<OnlineAccountInventoryHubChangedEvent>.Unsubscribe(OnAccountChanged);
            // 关商店/仓库只结束可见寿命。inactive窗口仍可由账号组合层预安装；
            // 不清准备状态，否则全Scene就绪检查会被一个正常关闭的钱包永久挡住。
        }
        private void Update() => Refresh();

        /// <summary>
        /// 与原库存UI共用账号消费者合同，支持Hub激活前和关闭的窗口。这里只缓存三个基础值；
        /// 完整Scope副本仅在Profile/启用的冷边界核验，绝不保留Items或在Update复制全库存。
        /// </summary>
        public bool TryPrepareAccountInventory(OnlineAccountInventorySnapshot snapshot, out string error)
        {
            error = null;
            if (!TryGetAvailableScope(out var summary) || !Matches(snapshot, summary)
                || !OnlineAccountInventoryHubScope.TryCopyForScene(gameObject.scene.handle, out var confirmed)
                || confirmed.Gold != snapshot.Gold || confirmed.ContentManifestHash != snapshot.ContentManifestHash)
            {
                ClearAccountGold(); ShowUnknown();
                error = "钱包尚未取得当前据点账号的确认余额。"; return false;
            }
            AdoptAccountGold(snapshot); Refresh(); return true;
        }

        public bool HasPreparedAccountInventory(string playerId, uint minimumVersion)
            => minimumVersion > 0 && _accountPrepared && _accountPlayer == playerId && _accountVersion >= minimumVersion
                && TryGetAvailableScope(out var summary) && summary.PlayerId == _accountPlayer && summary.Version == _accountVersion;

        private bool TryGetAvailableScope(out OnlineAccountHubSummary summary)
        {
            summary = default;
            return _session != null && _session.AccountInventoryReadOnly && _session.gameObject.scene == gameObject.scene
                && OnlineAccountInventoryHubScope.TryGetSummaryForScene(gameObject.scene.handle, out summary)
                && summary.Version != 0 && !summary.AssetsLocked && string.IsNullOrEmpty(summary.ActiveRunId);
        }
        private static bool Matches(OnlineAccountInventorySnapshot snapshot, in OnlineAccountHubSummary summary)
            => snapshot != null && snapshot.SchemaVersion == OnlineAccountInventorySnapshot.CurrentSchemaVersion
                && snapshot.PlayerId == summary.PlayerId && snapshot.Version == summary.Version && snapshot.Gold >= 0
                && !snapshot.AssetsLocked && string.IsNullOrEmpty(snapshot.ActiveRunId);
        private void AdoptAccountGold(OnlineAccountInventorySnapshot snapshot)
        {
            _accountPlayer = snapshot.PlayerId; _accountVersion = snapshot.Version; _accountGold = snapshot.Gold;
            _accountPrepared = true;
        }
        private void ClearAccountGold()
        { _accountPlayer = null; _accountVersion = 0; _accountGold = 0; _accountPrepared = false; }
        private void OnAccountChanged(OnlineAccountInventoryHubChangedEvent change)
        {
            if (change.SceneHandle != gameObject.scene.handle || !UsesAccountSource) return;
            SynchronizeAccountGold(); Refresh();
        }
        private void SynchronizeAccountGold()
        {
            if (TryGetAvailableScope(out var summary))
            {
                // Scope禁止同版本更改金币；同版重复通知不再复制完整资产或格式化字符串。
                if (_accountPrepared && _accountPlayer == summary.PlayerId && _accountVersion == summary.Version) return;
                if (OnlineAccountInventoryHubScope.TryCopyForScene(gameObject.scene.handle, out var snapshot) && Matches(snapshot, summary))
                { AdoptAccountGold(snapshot); return; }
            }
            ClearAccountGold();
        }
        private void Refresh()
        {
            using var sample = Marker.Auto();
            if (UsesAccountSource)
            {
                // 即使inactive期间错过通知，也先核当前身份/版本/租约。未知是未知，不能回退旧余额或0。
                if (!HasPreparedAccountInventory(_accountPlayer, _accountVersion)) { ShowUnknown(); return; }
                ShowAmount(_accountGold); return;
            }
            var wallet = _session != null ? _session.WalletReadOnly : null;
            if (wallet != null) ShowAmount(wallet.Currency);
        }
        private void ShowAmount(long value)
        {
            if (_amount == null || !_showingUnknown && value == _last) return;
            _last = value; _showingUnknown = false; _amount.text = value.ToString("N0", CultureInfo.InvariantCulture);
        }
        private void ShowUnknown()
        {
            if (_amount == null || _showingUnknown) return;
            _last = long.MinValue; _showingUnknown = true; _amount.text = "—";
        }
    }
}
