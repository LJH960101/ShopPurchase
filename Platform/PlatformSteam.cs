using ShopPurchase.Common;
using ShopPurchase.Core.Thread;
using ShopPurchase.HTTP;

namespace ShopPurchase.Platform
{
    public class PlatformSteam : IPlatform
    {
        private const string m_SuccessReceipt = "3333";
        private const string m_VerifyUrl = "https://mock.steampowered.com/verify";

        public EPlatform GetPlatformEnum() => EPlatform.Steam;

        public JHJob<bool> Verify(string _receipt)
        {
            return HTTPManager.Send(m_VerifyUrl, _receipt)
                .Then(_response => _response == m_SuccessReceipt
                    ? JHJob<bool>.Resolved(true)
                    : JHJob<bool>.Rejected(EErrorCode.ReceiptVerifyFailed));
        }
    }
}
