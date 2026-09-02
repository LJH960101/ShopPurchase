using System.Collections.Generic;

namespace ShopPurchase.Data
{
    /// <summary>
    /// 실제 데이터 시트 로딩 없이, 상품 테이블만 코드에 박아넣은 더미 구현.
    /// SP_UpdateItem이 하던 "무엇을 얼마나 줄지" 결정을 이제 여기(상품 정의)가 담당한다.
    /// </summary>
    public class DataManager
    {
        public static readonly DataManager Instance = new DataManager();

        private readonly Dictionary<int, ProductRecord> m_products;

        private DataManager()
        {
            m_products = new Dictionary<int, ProductRecord>
            {
                [1000] = new ProductRecord(_productId: 1000, _itemId: 1000, _itemCount: 1, _goldReward: 1000),
                [1001] = new ProductRecord(_productId: 1001, _itemId: 1001, _itemCount: 1, _goldReward: 2000),
                [1002] = new ProductRecord(_productId: 1002, _itemId: 1002, _itemCount: 1, _goldReward: 3000),
                [1003] = new ProductRecord(_productId: 1003, _itemId: 1003, _itemCount: 1, _goldReward: 4000),
                [1004] = new ProductRecord(_productId: 1004, _itemId: 1004, _itemCount: 1, _goldReward: 5000),
            };
        }

        /// <summary>등록 안 된 상품이면 null.</summary>
        public ProductRecord GetProduct(int _productId)
        {
            return m_products.TryGetValue(_productId, out var record) ? record : null;
        }
    }
}
