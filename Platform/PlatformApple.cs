using ShopPurchase.Common;
using ShopPurchase.Core.Thread;
using ShopPurchase.HTTP;

namespace ShopPurchase.Platform
{
    public class PlatformApple : IPlatform
    {
        private const string m_SuccessReceipt = "2222";
        private const string m_VerifyUrl = "https://mock.appstore.com/verify";

        public EPlatform GetPlatformEnum() => EPlatform.AppStore;

        public JHJob<bool> Verify(string _receipt)
        {
            return HTTPManager.Send(m_VerifyUrl, _receipt)
                .Then(_response => _response == m_SuccessReceipt
                    ? JHJob<bool>.Resolved(true)
                    : JHJob<bool>.Rejected(EErrorCode.ReceiptVerifyFailed));
        }
    }
}
