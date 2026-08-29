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

    /// <summary>상점 영수증을 DB에 적립한 결과.</summary>
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

    /// <summary>DBManager.InsertShopReceipt가 트랜잭션을 마치고 돌려주는 결과값.</summary>
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
