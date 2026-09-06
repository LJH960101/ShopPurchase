using System.Collections.Generic;
using ShopPurchase.Common;

namespace ShopPurchase.Data
{
    public class ProductRecord
    {
        public int ProductId { get; }
        public int ItemId { get; }
        public int ItemCount { get; }
        public long GoldReward { get; }

        public ProductRecord(int _productId, int _itemId, int _itemCount, long _goldReward)
        {
            ProductId = _productId;
            ItemId = _itemId;
            ItemCount = _itemCount;
            GoldReward = _goldReward;
        }

        public RewardData GetReward()
        {
            var items = new List<ItemData>();
            if (ItemCount > 0) items.Add(new ItemData(ItemId, ItemCount));

            var currencies = new List<CurrencyData>();
            if (GoldReward > 0) currencies.Add(new CurrencyData(ECurrencyType.Gold, GoldReward));

            if (items.Count == 0 && currencies.Count == 0) return null;

            return new RewardData(items, currencies);
        }
    }
}
