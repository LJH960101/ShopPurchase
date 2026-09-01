using ShopPurchase.Common;

namespace ShopPurchase.Platform
{
    public class PlatformSteam : PlatformVerifierBase
    {
        public PlatformSteam()
            : base(EPlatform.Steam, _successReceipt: "3333", _verifyUrl: "https://mock.steampowered.com/verify")
        {
        }
    }
}
