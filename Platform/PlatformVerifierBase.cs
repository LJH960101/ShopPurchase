using ShopPurchase.Common;
using ShopPurchase.Core.Thread;
using ShopPurchase.HTTP;

namespace ShopPurchase.Platform
{
    /// <summary>
    /// 플랫폼별 검증 로직(HTTPManager 호출 + 성공 코드 비교)은 전부 동일하고 플랫폼/성공
    /// 코드/URL만 다르므로, 그 세 값만 하위 클래스가 생성자로 넘기게 하고 로직은 여기 한 곳에
    /// 모은다. 하위 클래스는 여전히 public 매개변수 없는 생성자를 가져야 한다 — PlatformManager가
    /// 리플렉션으로 Activator.CreateInstance(type)을 호출하기 때문이다.
    /// </summary>
    public abstract class PlatformVerifierBase : IPlatform
    {
        private readonly EPlatform m_platform;
        private readonly string m_successReceipt;
        private readonly string m_verifyUrl;

        protected PlatformVerifierBase(EPlatform _platform, string _successReceipt, string _verifyUrl)
        {
            m_platform = _platform;
            m_successReceipt = _successReceipt;
            m_verifyUrl = _verifyUrl;
        }

        public EPlatform GetPlatformEnum() => m_platform;

        public JHJob<bool> Verify(string _receipt)
        {
            return HTTPManager.Send(m_verifyUrl, _receipt)
                .Then(_response => _response == m_successReceipt
                    ? JHJob<bool>.Resolved(true)
                    : JHJob<bool>.Rejected(EErrorCode.ReceiptVerifyFailed));
        }
    }
}
