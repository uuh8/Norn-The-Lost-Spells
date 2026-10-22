using System;
using System.Collections.Generic;
using Game.Core;
using UnityEngine;

namespace Game.Run
{
    [Serializable]
    public sealed class ShopOffer
    {
        public string OfferId, ContentId, DisplayName;
        // 材料报价没有物品 ContentId；表现资源随报价冻结，交易规则不依赖 UI。
        public Sprite Icon;
        public byte MaterialKey;
        public long BuyPrice = 10, SellPrice = 4;
        public ulong Stock = 10;
        public int InitialCharges = 12;
        public bool Unlimited;
        public ulong InitialMaterialQuantity = 1;
        public bool IsMaterial => MaterialKey != 0;
        public ShopOffer Copy() => (ShopOffer)MemberwiseClone();
    }
    /// <summary>价格/供货是Authoring配置；冻结副本避免Play Mode改SO导致一笔报价途中改变。</summary>
    [CreateAssetMenu(menuName = "Game/Run/Trading/Shop", fileName = "Shop")]
    public sealed class ShopDefinition : ScriptableObject
    {
        public string ShopId = "hub.shop";
        public long PurchaseBudget = 200;
        [Min(1)] public int RefreshEveryProgress = 1;
        public ShopOffer[] Offers = Array.Empty<ShopOffer>();
        public bool TryFreeze(ItemCatalogIndex catalog, out ShopConfiguration config)
        {
            config = null;
            if (catalog == null || string.IsNullOrWhiteSpace(ShopId) || PurchaseBudget < 0 || RefreshEveryProgress <= 0 || Offers == null || Offers.Length == 0) return false;
            var frozen = new ShopOffer[Offers.Length]; var ids = new HashSet<string>(StringComparer.Ordinal);
            var contents = new HashSet<string>(StringComparer.Ordinal); ushort materials = 0;
            for (int i = 0; i < Offers.Length; i++)
            {
                var o = Offers[i];
                if (o == null || string.IsNullOrWhiteSpace(o.OfferId) || !ids.Add(o.OfferId) || o.BuyPrice <= 0 || o.SellPrice < 0
                    || o.SellPrice > o.BuyPrice || o.Stock == 0 || o.InitialCharges < 0) return false;
                if (o.IsMaterial)
                { if (!MaterialAmount.IsKnown(o.MaterialKey) || (materials & 1 << o.MaterialKey) != 0 || !string.IsNullOrEmpty(o.ContentId)) return false;
                    materials |= (ushort)(1 << o.MaterialKey); }
                else
                { if (!catalog.TryGetRules(o.ContentId, out var rules) || !contents.Add(o.ContentId)) return false;
                    if (rules.Kind == ItemKind.Spell && !o.Unlimited && o.InitialCharges == 0) return false;
                    if (rules.Kind == ItemKind.Material && (o.InitialMaterialQuantity == 0 || o.InitialMaterialQuantity > rules.MaterialItemCapacity)) return false; }
                frozen[i] = o.Copy();
            }
            config = new ShopConfiguration(ShopId, PurchaseBudget, RefreshEveryProgress, frozen); return true;
        }
    }
    public sealed class ShopConfiguration
    {
        public readonly string ShopId;
        public readonly long Budget;
        public readonly int RefreshEveryProgress;
        private readonly ShopOffer[] _offers;
        public int Count => _offers.Length;
        internal ShopConfiguration(string id, long budget, int refresh, ShopOffer[] offers)
        { ShopId = id; Budget = budget; RefreshEveryProgress = refresh; _offers = offers; }
        internal ShopOffer At(int index) => _offers[index];
        internal ShopOffer Find(string id) { for (int i = 0; i < Count; i++) if (_offers[i].OfferId == id) return _offers[i]; return null; }
        internal ShopOffer Item(string content) { for (int i = 0; i < Count; i++) if (!_offers[i].IsMaterial && _offers[i].ContentId == content) return _offers[i]; return null; }
        internal ShopOffer Material(byte key) { for (int i = 0; i < Count; i++) if (_offers[i].MaterialKey == key) return _offers[i]; return null; }
    }
}
