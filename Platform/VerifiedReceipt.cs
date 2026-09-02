using ShopPurchase.Common;

namespace ShopPurchase.Platform
{
    /// <summary>
    /// 플랫폼 검증 서버가 "이 영수증은 실제로 이런 결제였다"고 확인해준 내용.
    ///
    /// 여기서 중요한 건 ProductId다 — 클라이언트가 패킷에 담아 보낸 상품 ID가 아니라, 영수증
    /// 자체가(=플랫폼 서버가) 주장하는 상품 ID다. 실제 Google Play/App Store의 검증 API도 영수증에
    /// 해당하는 productId를 돌려주고, 서버는 그 값을 클라이언트가 요청한 상품과 반드시 대조해야
    /// 한다. 이 대조를 빠뜨리면 싼 상품을 결제한 진짜 영수증으로 비싼 상품을 받아가는 변조가 그대로
    /// 통과한다 — 영수증이 "유효한지"와 "무엇에 대한 영수증인지"는 다른 질문이다.
    /// </summary>
    public class VerifiedReceipt
    {
        public EPlatform Platform { get; }
        public string Receipt { get; }
        public int ProductId { get; }

        public VerifiedReceipt(EPlatform _platform, string _receipt, int _productId)
        {
            Platform = _platform;
            Receipt = _receipt;
            ProductId = _productId;
        }

        public override string ToString() => $"VerifiedReceipt(Platform={Platform}, ProductId={ProductId}, Receipt={Receipt})";
    }
}
