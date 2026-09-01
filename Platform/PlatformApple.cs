using ShopPurchase.Common;

namespace ShopPurchase.Platform
{
    public class PlatformApple : PlatformVerifierBase
    {
        public PlatformApple()
            : base(EPlatform.AppStore, _successReceipt: "2222", _verifyUrl: "https://mock.appstore.com/verify")
        {
        }
    }
}
