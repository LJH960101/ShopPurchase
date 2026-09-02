using ShopPurchase.Common;
using ShopPurchase.Core.Thread;

namespace ShopPurchase.Platform
{
    /// <summary>플랫폼별 영수증 검증 전략.</summary>
    public interface IPlatform
    {
        /// <summary>이 구현체가 담당하는 플랫폼. PlatformManager가 리플렉션으로 등록할 때 키로 쓴다.</summary>
        EPlatform GetPlatformType();

        /// <summary>
        /// 영수증이 진짜인지 확인하고, 그 영수증이 "무엇에 대한 결제인지"(VerifiedReceipt)를 돌려준다.
        /// 구현체는 여기까지만 책임진다 — 그 상품이 클라이언트가 요청한 상품과 맞는지 대조하는 건
        /// 상품 정책을 아는 상위 계층(PlatformManager.Verify)의 몫이다.
        /// 실패하면 EErrorCode를 담아 reject된다.
        /// </summary>
        JHJob<VerifiedReceipt> Verify(string _receipt);
    }
}
