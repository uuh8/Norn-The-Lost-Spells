using System;
using System.Collections;
using System.Collections.Generic;
using Game.Core;
using Game.NetworkProtocol;
using UnityEngine;

namespace Game.Multiplayer
{
    public sealed partial class OnlineAccountClient
    {
        /// <summary>
        /// 账号商店只有一份未知写候选。正文、原账号和双版本在第一次点击时冻结；HTTP超时、关闭Canvas、
        /// 401或重登都不生成新CommandId，也不把历史收据中的旧钱包当成当前Profile安装回去。
        /// 此处只连接Core意图与业务HTTP，价格/库存裁决仍属于业务服，原Run商店不取得本地写权限。
        /// </summary>
        private sealed class AccountShopPending
        {
            internal OnlineAccountShopTradeRequestedEvent Request;
            internal string PlayerId, Json;
            internal long BeforeGold;
            internal AccountShopTradeReply Receipt;
        }
        private AccountShopPending _accountShopPending;
        private OnlineAccountShopPresentationSnapshot _accountShopVerified;
        private bool _accountShopReading, _accountShopHasQueuedOpen;
        private bool _accountShopLobbyRestoring;
        private OnlineAccountShopOpenRequestedEvent _accountShopReadOrigin, _accountShopQueuedOpen;
        private string _accountShopReadPlayer, _accountShopQueuedPlayer;

        private bool TryMapAccountShopReply(AccountShopReply reply, int sceneHandle,
            out OnlineAccountShopPresentationSnapshot snapshot, out string error)
            => TryMapAccountShopValues(reply, sceneHandle, true, out snapshot, out error);

        private bool TryMapAccountShopValues(AccountShopReply reply, int sceneHandle, bool requireScene,
            out OnlineAccountShopPresentationSnapshot snapshot, out string error)
        {
            snapshot = null; error = null;
            var definition = _manifest?.Shop; var state = reply?.Shop; var profile = reply?.Profile;
            if (!Authenticated || _view == null || _manifest == null || _manifest.SchemaVersion != 3 || definition == null
                || state == null || state.SchemaVersion != 1 || profile == null || profile.PlayerId != _view.PlayerId
                || state.PlayerId != profile.PlayerId || state.ShopId != definition.ShopId || state.ConfigHash != definition.Hash
                || profile.Version == 0 || profile.Version < _view.Version || state.InventoryVersion != profile.Version
                || profile.Gold < 0 || profile.AssetsLocked || !string.IsNullOrEmpty(profile.ActiveRunId)
                || state.Version <= 0 || state.RemainingBudget < 0 || state.RemainingBudget > definition.PurchaseBudget
                || state.ProgressUntilRefresh < 0 || state.ProgressUntilRefresh > definition.RefreshEveryProgress)
                return ShopFailure("商店回复不属于当前账号、配置或完整资产版本。", out error);
            try
            {
                if (definition.Hash != ContentShopRules.CalculateHash(definition))
                    return ShopFailure("本机受控商店报价Hash不一致。", out error);
            }
            catch (ArgumentException) { return ShopFailure("本机受控商店配置非法。", out error); }
            if (!OnlineAccountInventoryMapping.TryMap(profile, _manifest.Hash, out _, out error)) return false;
            string inventory = "account." + profile.PlayerId;
            if (requireScene && (!FindAccountScene(sceneHandle).IsValid()
                || !OnlineAccountInventoryHubScope.TryGetSummaryForScene(sceneHandle, out var hub)
                || hub.PlayerId != profile.PlayerId || hub.InventoryId != inventory || hub.Version > profile.Version
                || hub.AssetsLocked || !string.IsNullOrEmpty(hub.ActiveRunId)
                || !OnlineAccountInventoryHubScope.TryCopyForScene(sceneHandle, out var current)
                || current.ContentManifestHash != _manifest.Hash || current.Version == profile.Version && current.Gold != profile.Gold))
                return ShopFailure("原据点账号或场景已结束，未采用商店数据。", out error);
            if (state.Stocks == null || state.Stocks.Length != definition.Offers.Length)
                return ShopFailure("商店供货不是完整报价闭包。", out error);
            var stocks = new Dictionary<string, ulong>(state.Stocks.Length, StringComparer.Ordinal);
            foreach (var row in state.Stocks)
                if (row == null || string.IsNullOrEmpty(row.OfferId) || !stocks.TryAdd(row.OfferId, row.Remaining))
                    return ShopFailure("商店供货包含空值或重复OfferId。", out error);
            var mapped = new OnlineAccountShopStock[definition.Offers.Length];
            for (int i = 0; i < mapped.Length; i++)
            {
                var offer = definition.Offers[i];
                if (!stocks.TryGetValue(offer.OfferId, out var remaining) || remaining > offer.InitialStock)
                    return ShopFailure("商店供货缺少受控报价或超过初始供货。", out error);
                mapped[i] = new OnlineAccountShopStock { OfferId = offer.OfferId, ContentId = offer.ContentId,
                    MaterialKey = offer.MaterialKey, BuyPrice = offer.BuyPrice, SellPrice = offer.SellPrice,
                    InitialCharges = offer.InitialCharges, Unlimited = offer.Unlimited,
                    InitialMaterialQuantity = offer.InitialMaterialQuantity, InitialStock = offer.InitialStock, Remaining = remaining };
            }
            snapshot = new OnlineAccountShopPresentationSnapshot { PlayerId = profile.PlayerId, InventoryId = inventory,
                SceneHandle = sceneHandle, InventoryVersion = profile.Version, ShopId = state.ShopId, ConfigHash = state.ConfigHash,
                ShopVersion = state.Version, Gold = profile.Gold, RemainingBudget = state.RemainingBudget,
                ProgressUntilRefresh = state.ProgressUntilRefresh, Stocks = mapped };
            return true;
        }

        private bool TryInstallAccountShopReply(OnlineAccountShopOpenRequestedEvent origin, AccountShopReply reply, out string error)
        {
            if (!TryPrepareAccountShopReply(origin, reply, 1, 1, out var snapshot, out error)) return false;
            PublishAccountShop(origin, true, "已读取服务器供货与统一金币。", snapshot); return true;
        }

        private bool TryPrepareAccountShopReply(OnlineAccountShopOpenRequestedEvent origin, AccountShopReply reply,
            uint minimumInventory, long minimumShop, out OnlineAccountShopPresentationSnapshot snapshot, out string error)
        {
            snapshot = null; error = null;
            if (origin.CallerId == 0 || !TryMapAccountShopReply(reply, origin.SceneHandle, out var candidate, out error)) return false;
            if (origin.InventoryId != candidate.InventoryId || candidate.InventoryVersion < minimumInventory || candidate.ShopVersion < minimumShop
                || _accountShopVerified != null && _accountShopVerified.PlayerId == candidate.PlayerId
                    && _accountShopVerified.ConfigHash == candidate.ConfigHash && candidate.ShopVersion < _accountShopVerified.ShopVersion)
                return ShopFailure("商店或账号资产水位尚未确认，未采用旧数据。", out error);
            var scene = FindAccountScene(origin.SceneHandle);
            // 真实Profile先经过既有全量映射、账号Scope和每一个Hub消费者，再向原商店发布报价。
            // TryAdopt会同步触发Facade刷新；HTTP忙期间的该读请求由下面的单槽队列保留。
            if (!TryAdoptVerifiedHubProfile(reply.Profile, minimumInventory, out error)
                || !TryPrepareVerifiedHubScene(scene, out error) || !IsVerifiedHubSceneReady(scene, candidate.InventoryVersion))
                return false;
            _accountShopVerified = candidate; snapshot = candidate; return true;
        }

        private bool TryCaptureAccountShopTrade(OnlineAccountShopTradeRequestedEvent request, out string error)
        {
            error = null; var intent = request.Request; var shop = _accountShopVerified;
            if (_busy || _accountInventoryPending != null || _accountShopPending != null)
                return ShopFailure("上一笔账号操作尚未确认，请恢复原请求。", out error);
            if (!Authenticated || _view == null || _manifest?.Shop == null || request.CallerId == 0 || shop == null
                || _view.ContentManifestHash != _manifest.Hash
                || shop.PlayerId != _view.PlayerId || shop.SceneHandle != request.SceneHandle || shop.InventoryId != intent.InventoryId
                || shop.InventoryVersion != intent.InventoryVersion || _view.Version != intent.InventoryVersion
                || shop.ShopVersion != intent.ShopVersion || shop.Gold != _view.Gold || shop.ConfigHash != _manifest.Shop.Hash
                || !OnlineAccountInventoryHubScope.TryGetSummaryForScene(request.SceneHandle, out var hub)
                || hub.PlayerId != shop.PlayerId || hub.InventoryId != shop.InventoryId || hub.Version != intent.InventoryVersion
                || hub.AssetsLocked || !string.IsNullOrEmpty(hub.ActiveRunId) || _view.AssetsLocked || !string.IsNullOrEmpty(_view.ActiveRunId)
                || !OnlineAccountInventoryHubScope.TryCopyForScene(request.SceneHandle, out var current)
                || current.ContentManifestHash != _manifest.Hash || current.Gold != shop.Gold)
                return ShopFailure("商店、账号、场景或双版本已变化，请重新读取供货。", out error);
            if (!Guid.TryParseExact(intent.RequestId, "N", out var command) || command == Guid.Empty || command.ToString("N") != intent.RequestId
                || intent.InventoryVersion == 0 || intent.ShopVersion <= 0 || intent.Quantity == 0 || intent.Kind > ShopTradeKind.SellMaterial)
                return ShopFailure("交易意图或幂等编号非法。", out error);
            intent.OfferId ??= ""; intent.ItemInstanceId ??= ""; intent.ContainerId ??= ""; intent.BackpackId ??= "";
            if (intent.Kind == ShopTradeKind.BuyItem || intent.Kind == ShopTradeKind.BuyMaterial)
            {
                bool found = false;
                foreach (var offer in shop.Stocks)
                    if (offer.OfferId == intent.OfferId && (offer.MaterialKey != 0) == (intent.Kind == ShopTradeKind.BuyMaterial)) { found = true; break; }
                if (!found) return ShopFailure("购买报价不在当前受控供货中。", out error);
            }
            request.Request = intent;
            var body = new AccountShopTradeRequest { CommandId = intent.RequestId, ShopId = shop.ShopId, ConfigHash = shop.ConfigHash,
                ExpectedVersion = intent.InventoryVersion, ExpectedShopVersion = intent.ShopVersion, Kind = (int)intent.Kind,
                OfferId = intent.OfferId, ItemInstanceId = intent.ItemInstanceId, ContainerId = intent.ContainerId,
                BackpackId = intent.BackpackId, Quantity = intent.Quantity, ToBackpack = intent.ToBackpack,
                AllowWarehouseFallback = intent.AllowWarehouseFallback };
            _accountShopPending = new AccountShopPending { PlayerId = shop.PlayerId, Request = request,
                BeforeGold = shop.Gold, Json = JsonUtility.ToJson(body) };
            return true;
        }

        private bool TryConfirmAccountShopTrade(AccountShopTradeReply receipt, AccountShopReply fresh, out string error)
        {
            error = null; var candidate = _accountShopPending;
            if (!MatchesAccountShopCandidate(candidate) || !MatchesAccountShopReceipt(candidate, receipt)
                || fresh?.Profile == null || fresh.Shop == null || fresh.Profile.Version < receipt.InventoryVersion || fresh.Shop.Version < receipt.ShopVersion)
                return ShopFailure("原交易收据与新鲜商店/完整资产尚未确认。", out error);
            candidate.Receipt = CopyShopReceipt(receipt);
            var origin = ShopOrigin(candidate.Request);
            var scene = FindAccountScene(origin.SceneHandle);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                // 原Canvas已结束仍能恢复同账号结果；不向已卸载Caller伪报UI安装，也不永久锁住下次EnterHub。
                if (!TryMapAccountShopValues(fresh, origin.SceneHandle, false, out _, out error)
                    || !TryAdoptVerifiedHubProfile(fresh.Profile, receipt.InventoryVersion, out error)) return false;
                _accountShopPending = null; _accountShopVerified = null;
                Publish("原商店界面已结束，账号交易结果已确认。", false); return true;
            }
            if (!TryPrepareAccountShopReply(origin, fresh, receipt.InventoryVersion, receipt.ShopVersion, out var snapshot, out error)) return false;
            _accountShopPending = null;
            PublishAccountShop(origin, true, "交易与当前账号资产已确认。", snapshot);
            var materials = new MaterialAmount[receipt.Materials.Length];
            for (int i = 0; i < materials.Length; i++) materials[i] = new MaterialAmount(receipt.Materials[i].MaterialKey, receipt.Materials[i].Quantity);
            EventBus<ShopTradeResultEvent>.Publish(new ShopTradeResultEvent { InventoryId = candidate.Request.Request.InventoryId,
                RequestId = candidate.Request.Request.RequestId, Success = true, Replayed = receipt.Replayed,
                Failure = ShopTradeFailure.None, Message = "交易已保存。", Currency = snapshot.Gold,
                CurrencyDelta = receipt.CurrencyDelta, ShopVersion = snapshot.ShopVersion,
                ActualQuantity = receipt.Quantity, WarehouseQuantity = receipt.WarehouseQuantity, Materials = materials });
            return true;
        }

        private static bool MatchesAccountShopReceipt(AccountShopPending pending, AccountShopTradeReply receipt)
        {
            if (pending == null || receipt == null || !receipt.Success || receipt.CommandId != pending.Request.Request.RequestId
                || receipt.PlayerId != pending.PlayerId || receipt.InventoryVersion < pending.Request.Request.InventoryVersion
                || receipt.ShopVersion < pending.Request.Request.ShopVersion || receipt.Currency < 0 || receipt.Quantity == 0
                || receipt.Quantity > pending.Request.Request.Quantity || receipt.WarehouseQuantity > receipt.Quantity || receipt.Materials == null)
                return false;
            var kind = pending.Request.Request.Kind; bool buy = kind == ShopTradeKind.BuyItem || kind == ShopTradeKind.BuyMaterial;
            if (buy ? receipt.CurrencyDelta >= 0 : receipt.CurrencyDelta <= 0) return false;
            if ((decimal)pending.BeforeGold + receipt.CurrencyDelta != receipt.Currency
                || kind != ShopTradeKind.BuyItem && receipt.WarehouseQuantity != 0) return false;
            bool material = kind == ShopTradeKind.BuyMaterial || kind == ShopTradeKind.SellMaterial;
            if (!material) return receipt.Materials.Length == 0;
            if (receipt.Materials.Length < 1 || receipt.Materials.Length > 8) return false;
            ulong total = 0; ushort seen = 0;
            foreach (var row in receipt.Materials)
            {
                if (row == null || !MaterialAmount.IsKnown(row.MaterialKey) || row.Quantity == 0
                    || (seen & 1 << row.MaterialKey) != 0 || row.Quantity > ulong.MaxValue - total) return false;
                seen |= (ushort)(1 << row.MaterialKey); total += row.Quantity;
            }
            return total == receipt.Quantity;
        }

        private bool MatchesAccountShopCandidate(AccountShopPending candidate)
            => candidate != null && ReferenceEquals(candidate, _accountShopPending) && Authenticated && _view != null && candidate.PlayerId == _view.PlayerId;

        private void OnAccountShopOpen(OnlineAccountShopOpenRequestedEvent request)
        {
            if (!ValidAccountShopOrigin(request, out var player))
            { PublishAccountShop(request, false, "请先确认当前据点账号与商店来源。"); return; }
            if (_busy)
            {
                // 同一个GET产生的同caller读请求由当前响应满足；交易Profile安装产生的刷新则排队一次。
                if (_accountShopReading && SameShopOrigin(request, _accountShopReadOrigin) && player == _accountShopReadPlayer) return;
                QueueAccountShopRead(request, player); return;
            }
            if (_accountShopPending != null)
            {
                if (_accountShopPending.PlayerId != player) { PublishAccountShop(request, false, "原账号交易尚待恢复。"); return; }
                QueueAccountShopRead(request, player);
                StartCoroutine(DeliverAccountShopTrade()); return;
            }
            if (_accountInventoryPending != null) { PublishAccountShop(request, false, "账号库存操作尚未确认，请先刷新恢复。"); return; }
            StartCoroutine(ReadAccountShop(request, player));
        }

        private bool ValidAccountShopOrigin(OnlineAccountShopOpenRequestedEvent request, out string player)
        {
            player = _view?.PlayerId;
            return Authenticated && _manifest?.Shop != null && request.CallerId != 0 && _view != null
                && OnlineAccountInventoryHubScope.TryGetSummaryForScene(request.SceneHandle, out var hub)
                && hub.PlayerId == player && hub.InventoryId == request.InventoryId && !hub.AssetsLocked && string.IsNullOrEmpty(hub.ActiveRunId);
        }
        private void QueueAccountShopRead(OnlineAccountShopOpenRequestedEvent request, string player)
        {
            if (_accountShopHasQueuedOpen && (!SameShopOrigin(request, _accountShopQueuedOpen) || player != _accountShopQueuedPlayer))
            { PublishAccountShop(request, false, "已有原商店读取等待确认，请稍后重试。"); return; }
            _accountShopHasQueuedOpen = true; _accountShopQueuedOpen = request; _accountShopQueuedPlayer = player;
        }
        private void PumpQueuedAccountShopRead()
        {
            if (_busy || !_accountShopHasQueuedOpen || _accountShopPending != null || _accountInventoryPending != null) return;
            var request = _accountShopQueuedOpen; var player = _accountShopQueuedPlayer;
            _accountShopHasQueuedOpen = false; _accountShopQueuedOpen = default; _accountShopQueuedPlayer = null;
            if (!ValidAccountShopOrigin(request, out var current) || current != player)
            { PublishAccountShop(request, false, "原商店账号或场景已结束，未采用迟到读数。"); return; }
            StartCoroutine(ReadAccountShop(request, player));
        }
        private IEnumerator ReadAccountShop(OnlineAccountShopOpenRequestedEvent origin, string player)
        {
            _busy = true; _accountShopReading = true; _accountShopReadOrigin = origin; _accountShopReadPlayer = player;
            try
            {
                yield return Send("GET", "/api/online/shop", null);
                if (!Authenticated || _view?.PlayerId != player) yield break;
                var reply = _lastSuccess ? ParseShop<AccountShopReply>(_lastBody) : null;
                string error = null;
                if (reply == null || !TryInstallAccountShopReply(origin, reply, out error))
                    PublishAccountShop(origin, false, error ?? "服务器商店未就绪，请重新打开或刷新。");
            }
            finally
            {
                _busy = false; _accountShopReading = false; _accountShopReadOrigin = default; _accountShopReadPlayer = null;
                Publish(LastStatus, false); PumpQueuedAccountShopRead();
            }
        }
        private void OnAccountShopTrade(OnlineAccountShopTradeRequestedEvent request)
        {
            if (_accountShopPending != null)
            {
                if (!_busy && SameShopTrade(_accountShopPending.Request, request)) StartCoroutine(DeliverAccountShopTrade());
                else PublishAccountShop(ShopOrigin(request), false, "原交易结果尚未确认，将继续保留同一请求。");
                return;
            }
            if (!TryCaptureAccountShopTrade(request, out var error))
            {
                EventBus<ShopTradeResultEvent>.Publish(new ShopTradeResultEvent { InventoryId = request.Request.InventoryId,
                    RequestId = request.Request.RequestId, Success = false, Failure = _busy || _accountInventoryPending != null
                        ? ShopTradeFailure.ResourceBusy : ShopTradeFailure.StaleVersion, Message = error }); return;
            }
            StartCoroutine(DeliverAccountShopTrade());
        }

        private IEnumerator DeliverAccountShopTrade()
        {
            var candidate = _accountShopPending;
            if (!MatchesAccountShopCandidate(candidate)) yield break;
            _busy = true;
            try
            {
                for (int attempt = 0; attempt < 3 && MatchesAccountShopCandidate(candidate) && candidate.Receipt == null; attempt++)
                {
                    yield return Send("POST", "/api/online/shop/trade", candidate.Json);
                    if (!MatchesAccountShopCandidate(candidate)) yield break;
                    var receipt = _lastSuccess ? ParseShop<AccountShopTradeReply>(_lastBody) : null;
                    if (MatchesAccountShopReceipt(candidate, receipt)) { candidate.Receipt = CopyShopReceipt(receipt); break; }
                    long postStatus = LastHttpStatus;
                    var postError = ParseShop<OnlineErrorReply>(_lastBody)?.Code;
                    // 成功正文损坏和网络失败都查询原Command收据；不重写原JSON，不用新版本再开另一笔交易。
                    yield return Send("GET", "/api/online/shop/trades/" + candidate.Request.Request.RequestId, null);
                    if (!MatchesAccountShopCandidate(candidate)) yield break;
                    receipt = _lastSuccess ? ParseShop<AccountShopTradeReply>(_lastBody) : null;
                    if (MatchesAccountShopReceipt(candidate, receipt)) { candidate.Receipt = CopyShopReceipt(receipt); break; }
                    if (LastHttpStatus == 404 && postStatus >= 400 && postStatus < 500 && postStatus != 408 && postStatus != 429)
                    {
                        // 只有明确POST拒绝且认证收据查询404才宣布未成交；Unknown绝不发终态Result，
                        // 否则原购买框会解锁并在下一次点击生成新RequestId，掩盖可能已经成交的原命令。
                        _accountShopPending = null;
                        EventBus<ShopTradeResultEvent>.Publish(new ShopTradeResultEvent { InventoryId = candidate.Request.Request.InventoryId,
                            RequestId = candidate.Request.Request.RequestId, Success = false, Failure = AccountShopFailure(postError),
                            Message = "业务服未接受这笔交易，请刷新供货后重试。" });
                        QueueAccountShopRead(ShopOrigin(candidate.Request), candidate.PlayerId); yield break;
                    }
                }
                for (int attempt = 0; attempt < 3 && MatchesAccountShopCandidate(candidate) && candidate.Receipt != null; attempt++)
                {
                    yield return Send("GET", "/api/online/shop", null);
                    if (!MatchesAccountShopCandidate(candidate)) yield break;
                    var fresh = _lastSuccess ? ParseShop<AccountShopReply>(_lastBody) : null;
                    if (fresh != null && TryConfirmAccountShopTrade(candidate.Receipt, fresh, out _)) yield break;
                }
                if (ReferenceEquals(candidate, _accountShopPending))
                    PublishAccountShop(ShopOrigin(candidate.Request), false, "交易结果尚未完整确认；请保存/刷新或重新打开商店恢复原请求。");
            }
            finally { _busy = _accountShopLobbyRestoring; Publish(LastStatus, _busy); if (!_busy) PumpQueuedAccountShopRead(); }
        }

        /// <summary>Login/Refresh外层仍持有一次请求寿命；内部恢复不能提前启动排队GET并释放全局busy。</summary>
        private IEnumerator RestorePendingAccountCommand()
        {
            _accountShopLobbyRestoring = true;
            try
            {
                if (_accountInventoryPending != null) yield return DeliverAccountInventoryCommand();
                else if (_accountShopPending != null) yield return DeliverAccountShopTrade();
            }
            finally { _accountShopLobbyRestoring = false; }
        }

        private void ClearAccountShopPresentation()
        {
            // 读缓存随Session结束；未知写候选仍属于原Player，留给同账号重新登录恢复。
            _accountShopVerified = null; _accountShopReading = false; _accountShopReadOrigin = default; _accountShopReadPlayer = null;
            _accountShopHasQueuedOpen = false; _accountShopQueuedOpen = default; _accountShopQueuedPlayer = null;
        }
        private void PublishAccountShop(OnlineAccountShopOpenRequestedEvent origin, bool success, string message,
            OnlineAccountShopPresentationSnapshot snapshot = null)
        {
            LastStatus = message;
            EventBus<OnlineAccountShopChangedEvent>.Publish(new OnlineAccountShopChangedEvent { CallerId = origin.CallerId,
                SceneHandle = origin.SceneHandle, Success = success, Message = message, Snapshot = CopyShopPresentation(snapshot) });
        }
        private static OnlineAccountShopPresentationSnapshot CopyShopPresentation(OnlineAccountShopPresentationSnapshot value)
            => value == null ? null : JsonUtility.FromJson<OnlineAccountShopPresentationSnapshot>(JsonUtility.ToJson(value));
        private static AccountShopTradeReply CopyShopReceipt(AccountShopTradeReply value)
            => value == null ? null : JsonUtility.FromJson<AccountShopTradeReply>(JsonUtility.ToJson(value));
        private static T ParseShop<T>(string json) where T : class
        { if (string.IsNullOrEmpty(json)) return null; try { return JsonUtility.FromJson<T>(json); } catch (ArgumentException) { return null; } }
        private static bool ShopFailure(string message, out string error) { error = message; return false; }
        private static OnlineAccountShopOpenRequestedEvent ShopOrigin(OnlineAccountShopTradeRequestedEvent value)
            => new OnlineAccountShopOpenRequestedEvent { CallerId = value.CallerId, SceneHandle = value.SceneHandle, InventoryId = value.Request.InventoryId };
        private static bool SameShopOrigin(OnlineAccountShopOpenRequestedEvent a, OnlineAccountShopOpenRequestedEvent b)
            => a.CallerId == b.CallerId && a.SceneHandle == b.SceneHandle && a.InventoryId == b.InventoryId;
        private static bool SameShopTrade(OnlineAccountShopTradeRequestedEvent a, OnlineAccountShopTradeRequestedEvent b)
        {
            var x = a.Request; var y = b.Request;
            return a.CallerId == b.CallerId && a.SceneHandle == b.SceneHandle && x.RequestId == y.RequestId && x.InventoryId == y.InventoryId
                && x.InventoryVersion == y.InventoryVersion && x.ShopVersion == y.ShopVersion && x.Kind == y.Kind && x.Quantity == y.Quantity
                && x.ToBackpack == y.ToBackpack && x.AllowWarehouseFallback == y.AllowWarehouseFallback
                && (x.OfferId ?? "") == (y.OfferId ?? "") && (x.ItemInstanceId ?? "") == (y.ItemInstanceId ?? "")
                && (x.ContainerId ?? "") == (y.ContainerId ?? "") && (x.BackpackId ?? "") == (y.BackpackId ?? "");
        }
        private static ShopTradeFailure AccountShopFailure(string code)
        {
            if (!string.IsNullOrEmpty(code))
                foreach (ShopTradeFailure failure in Enum.GetValues(typeof(ShopTradeFailure)))
                    if (failure != ShopTradeFailure.None && (code == failure.ToString() || code == "Shop" + failure)) return failure;
            return ShopTradeFailure.InvalidRequest;
        }
    }
}
