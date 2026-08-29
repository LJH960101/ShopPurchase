using System.Linq;
using ShopPurchase.Common;

namespace ShopPurchase.Network
{
    public class C2P_RequestShopBuy : IPacket
    {
        public string Receipt { get; set; }
        public int ProductId { get; set; }
    }

    public class P2C_ResultShopBuy : IPacket
    {
        public EErrorCode ErrorCode { get; }
        public RewardData RewardData { get; }

        public P2C_ResultShopBuy(EErrorCode _errorCode, RewardData _rewardData)
        {
            ErrorCode = _errorCode;
            RewardData = _rewardData;
        }

        public override string ToString()
        {
            string rewardInfo = RewardData == null ? "null" : FormatRewardData(RewardData);
            return $"P2C_ResultShopBuy(ErrorCode={ErrorCode}, Item=[{rewardInfo}])";
        }

        private static string FormatRewardData(RewardData _rewardData)
        {
            string items = string.Join(", ", _rewardData.Items.Select(_i => $"ItemId={_i.ItemId}, Count={_i.Count}"));
            string currencies = string.Join(", ", _rewardData.Currencies.Select(_c => $"{_c.CurrencyType}={_c.Count}"));
            return $"Items=[{items}], Currencies=[{currencies}]";
        }
    }
}
