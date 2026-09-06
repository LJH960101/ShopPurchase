using System.Collections.Generic;

namespace ShopPurchase.Common
{
    public class ItemData
    {
        public int ItemId { get; }
        public int Count { get; }

        public ItemData(int _itemId, int _count)
        {
            ItemId = _itemId;
            Count = _count;
        }
    }

    public class CurrencyData
    {
        public ECurrencyType CurrencyType { get; }
        public long Count { get; }

        public CurrencyData(ECurrencyType _currencyType, long _count)
        {
            CurrencyType = _currencyType;
            Count = _count;
        }
    }

    public class RewardData
    {
        public List<ItemData> Items { get; }
        public List<CurrencyData> Currencies { get; }

        public RewardData(List<ItemData> _items, List<CurrencyData> _currencies)
        {
            Items = _items;
            Currencies = _currencies;
        }
    }

    public class ShopReceiptData
    {
        public GUID ReceiptRowId { get; }
        public string Receipt { get; }

        public ShopReceiptData(GUID _receiptRowId, string _receipt)
        {
            ReceiptRowId = _receiptRowId;
            Receipt = _receipt;
        }
    }

    public class InsertShopReceiptResult
    {
        public ShopReceiptData Receipt { get; }
        public RewardData AddItemDBData { get; }

        public InsertShopReceiptResult(ShopReceiptData _receipt, RewardData _addItemDBData)
        {
            Receipt = _receipt;
            AddItemDBData = _addItemDBData;
        }
    }
}
