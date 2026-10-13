using UnityEngine;

namespace Game.Run
{
    public readonly struct ShopOfferSnapshot
    {
        public readonly string OfferId, ContentId, DisplayName;
        public readonly Sprite Icon;
        public readonly ItemKind Kind;
        public readonly ItemUseKind UseKind;
        public readonly byte MaterialKey;
        public readonly long BuyPrice, SellPrice;
        public readonly int InitialCharges;
        public readonly bool Unlimited, IsQualification, IsSpell;
        public readonly ulong Stock;
        public bool IsMaterial => MaterialKey != 0;
        internal ShopOfferSnapshot(ShopOffer offer, ulong stock, ItemCatalogIndex catalog)
        {
            OfferId=offer.OfferId;ContentId=offer.ContentId;MaterialKey=offer.MaterialKey;Stock=stock;BuyPrice=offer.BuyPrice;SellPrice=offer.SellPrice;InitialCharges=offer.InitialCharges;Unlimited=offer.Unlimited;
            IsQualification=false;IsSpell=false;Kind=ItemKind.Material;UseKind=ItemUseKind.None;
            if(catalog.TryGet(offer.ContentId,out var definition))
            {
                DisplayName=definition.DisplayName;
                // 报价与库存复用相同图标优先级；未覆盖Item图标的法术仍有自己的指令图标。
                Icon=definition.Icon!=null?definition.Icon:definition.SpellDefinition!=null?definition.SpellDefinition.Icon:null;
                Kind=definition.Kind;UseKind=definition.UseKind;
            }
            else {DisplayName=string.IsNullOrEmpty(offer.DisplayName)?offer.OfferId:offer.DisplayName;Icon=offer.Icon;}
            // 定点产出同样消耗施法次数；资格语义只属于Modify/Multicast，复用Run权威分类。
            if(definition!=null && definition.SpellDefinition!=null){IsSpell=true;IsQualification=RaidSpellEligibility.IsQualification(definition.SpellDefinition);}
        }
    }
    public interface IShopReadOnly
    {
        string InventoryId { get; }
        long Version { get; }
        long Currency { get; }
        long RemainingBudget { get; }
        int Count { get; }
        long ProgressUntilRefresh { get; }
        ShopOfferSnapshot GetOffer(int index);
    }
    /// <summary>只返回值快照，UI拿不到库存数组、余额setter或容器写方法。</summary>
    internal sealed class ShopReadModel : IShopReadOnly
    {
        private readonly SinglePlayerProfile _profile;
        private readonly ShopConfiguration _configuration;
        public string InventoryId { get; }
        public long Version=>_profile.Shop?.Version??0;
        public long Currency=>_profile.Currency;
        public long RemainingBudget=>_profile.Shop?.RemainingBudget??0;
        public int Count=>_configuration.Count;
        public long ProgressUntilRefresh=>System.Math.Max(0,_configuration.RefreshEveryProgress-(_profile.AdventureProgress-(_profile.Shop?.AppliedProgress??0)));
        public ShopReadModel(SinglePlayerProfile profile,ShopConfiguration configuration,string inventoryId)
        {_profile=profile;_configuration=configuration;InventoryId=inventoryId;}
        public ShopOfferSnapshot GetOffer(int index)
        {var offer=_configuration.At(index);return new ShopOfferSnapshot(offer,_profile.Shop?.Find(offer.OfferId)?.Remaining??0,_profile.Items.Catalog);}
    }
}
