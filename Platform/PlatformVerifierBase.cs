using ShopPurchase.Common;
using ShopPurchase.Core.Thread;
using ShopPurchase.HTTP;

namespace ShopPurchase.Platform
{
    /// <summary>
    /// 플랫폼별 검증 로직(HTTPManager 호출 + 응답 파싱)은 전부 동일하고 플랫폼/성공 토큰/URL만
    /// 다르므로, 그 세 값만 하위 클래스가 생성자로 넘기게 하고 로직은 여기 한 곳에 모은다.
    /// 하위 클래스는 여전히 public 매개변수 없는 생성자를 가져야 한다 — PlatformManager가
    /// 리플렉션으로 Activator.CreateInstance(type)을 호출하기 때문이다.
    ///
    /// 모킹된 영수증 형식은 "{플랫폼 토큰}-{상품 ID}"다(예: "1111-1000"). 실제 플랫폼 검증 API가
    /// 돌려주는 JSON에서 "이 영수증이 어떤 상품의 것인지"만 뽑아낸 것에 해당한다 — 진짜 서명 검증
    /// 대신 토큰 문자열 비교로 흉내내지만, "영수증에서 상품 ID를 읽어온다"는 핵심 단계는 동일하다.
    /// </summary>
    public abstract class PlatformVerifierBase : IPlatform
    {
        private readonly EPlatform m_platform;
        private readonly string m_successToken;
        private readonly string m_verifyUrl;

        protected PlatformVerifierBase(EPlatform _platform, string _successToken, string _verifyUrl)
        {
            m_platform = _platform;
            m_successToken = _successToken;
            m_verifyUrl = _verifyUrl;
        }

        public EPlatform GetPlatformType() => m_platform;

        public JHJob<VerifiedReceipt> Verify(string _receipt)
        {
            return HTTPManager.Send(m_verifyUrl, _receipt)
                .Then(_response => ParseResponse(_response));
        }

        /// <summary>
        /// 검증 서버 응답에서 성공 여부와 상품 ID를 뽑는다. 토큰이 다르거나 형식이 깨져 있으면
        /// ReceiptVerifyFailed로 reject한다 — 상품 대조는 여기서 하지 않는다(상위 계층의 몫).
        /// </summary>
        private JHJob<VerifiedReceipt> ParseResponse(string _response)
        {
            string[] parts = _response.Split('-');
            if (parts.Length != 2 || parts[0] != m_successToken)
                return JHJob<VerifiedReceipt>.Rejected(EErrorCode.ReceiptVerifyFailed);

            if (!int.TryParse(parts[1], out int productId))
                return JHJob<VerifiedReceipt>.Rejected(EErrorCode.ReceiptVerifyFailed);

            return JHJob<VerifiedReceipt>.Resolved(new VerifiedReceipt(m_platform, _response, productId));
        }
    }
}
