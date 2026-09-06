using ShopPurchase.Common;
using ShopPurchase.Core.Thread;
using ShopPurchase.HTTP;

namespace ShopPurchase.Platform
{
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
                .Then(_response => ParseResponse(_receipt, _response));
        }

        private JHJob<VerifiedReceipt> ParseResponse(string _receipt, string _response)
        {
            if (string.IsNullOrEmpty(_response))
                return JHJob<VerifiedReceipt>.Rejected(EErrorCode.ReceiptVerifyFailed);

            string[] parts = _response.Split('-');
            if (parts.Length != 2 || parts[0] != m_successToken)
                return JHJob<VerifiedReceipt>.Rejected(EErrorCode.ReceiptVerifyFailed);

            if (!int.TryParse(parts[1], out int productId))
                return JHJob<VerifiedReceipt>.Rejected(EErrorCode.ReceiptVerifyFailed);

            return JHJob<VerifiedReceipt>.Resolved(new VerifiedReceipt(m_platform, _receipt, productId));
        }
    }
}
