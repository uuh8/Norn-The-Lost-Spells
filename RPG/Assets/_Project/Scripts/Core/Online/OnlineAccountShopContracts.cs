using System;

namespace Game.Core
{
    /// <summary>
    /// 账号商店只读报价的中立值。数字来自已验证ContentManifest与业务供货的合并结果；
    /// Run再与本机冻结ShopDefinition逐字段核对。客户端交易只发送OfferId/数量/版本，不回传这些价格。
    /// </summary>
    [Serializable]
    public sealed class OnlineAccountShopStock
    {
        public string OfferId, ContentId;
        public byte MaterialKey;
        public long BuyPrice, SellPrice;
        public int InitialCharges;
        public bool Unlimited;
        public ulong InitialMaterialQuantity, InitialStock, Remaining;
    }

    /// <summary>
    /// 与一次已确认账号库存版本对应的商店完整读模型。SceneHandle/PlayerId限定原Hub寿命，
    /// ConfigHash限定通过账户适配校验的配置来源；此合同没有Profile、数据库或Unity资源写入口。
    /// </summary>
    [Serializable]
    public sealed class OnlineAccountShopPresentationSnapshot
    {
        public string PlayerId, InventoryId, ShopId, ConfigHash;
        public int SceneHandle;
        public uint InventoryVersion;
        public long ShopVersion, Gold, RemainingBudget, ProgressUntilRefresh;
        public OnlineAccountShopStock[] Stocks = Array.Empty<OnlineAccountShopStock>();
    }

    /// <summary>原商店请求当前账号的完整供货；Caller只用于将异步结果交还发起者，不是认证凭据。</summary>
    public struct OnlineAccountShopOpenRequestedEvent : IGameEvent
    {
        public int SceneHandle, CallerId;
        public string InventoryId;
    }

    public struct OnlineAccountShopChangedEvent : IGameEvent
    {
        public int CallerId, SceneHandle;
        public bool Success;
        public string Message;
        public OnlineAccountShopPresentationSnapshot Snapshot;
    }

    /// <summary>
    /// 复用原商店意图但明确改走账号HTTP适配。保留原RequestId与抓取的双版本，
    /// 不进入旧本地ShopTradeService，不因Unknown结果新建一笔不相关交易。
    /// </summary>
    public struct OnlineAccountShopTradeRequestedEvent : IGameEvent
    {
        public int CallerId, SceneHandle;
        public ShopTradeRequestedEvent Request;
    }
}
