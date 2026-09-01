using ShopPurchase.Common;

namespace ShopPurchase.Platform
{
    public class PlatformGoogle : PlatformVerifierBase
    {
        public PlatformGoogle()
            : base(EPlatform.GooglePlay, _successReceipt: "1111", _verifyUrl: "https://mock.googleplay.com/verify")
        {
        }
    }
}
