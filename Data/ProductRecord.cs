namespace ShopPurchase.Data
{
    /// <summary>상품 하나가 무엇을 지급하는지 정의하는 정적 테이블 데이터.</summary>
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
    }
}
