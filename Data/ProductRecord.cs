using System.Collections.Generic;
using ShopPurchase.Common;

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

        /// <summary>
        /// 이 상품이 지급할 보상으로 환산한다. 아이템도 재화도 없는 상품은 정상적인 상품 정의가
        /// 아니므로(데이터 시트 실수) null을 돌려준다 — 호출부가 "없는 상품"과 "잘못 정의된 상품"을
        /// null 하나로 같이 걸러낼 수 있게 하기 위한 것이다.
        /// </summary>
        public RewardData GetReward()
        {
            if (ItemCount <= 0 && GoldReward <= 0) return null;

            var items = new List<ItemData> { new ItemData(ItemId, ItemCount) };
            var currencies = new List<CurrencyData> { new CurrencyData(ECurrencyType.Gold, GoldReward) };

            return new RewardData(items, currencies);
        }
    }
}
