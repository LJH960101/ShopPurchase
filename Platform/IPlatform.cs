using ShopPurchase.Common;
using ShopPurchase.Core.Thread;

namespace ShopPurchase.Platform
{
    /// <summary>플랫폼별 영수증 검증 전략.</summary>
    public interface IPlatform
    {
        /// <summary>이 구현체가 담당하는 플랫폼. PlatformManager가 리플렉션으로 등록할 때 키로 쓴다.</summary>
        EPlatform GetPlatformType();

        /// <summary>성공하면 true로 resolve, 실패하면 EErrorCode를 담아 reject된다.</summary>
        JHJob<bool> Verify(string _receipt);
    }
}
