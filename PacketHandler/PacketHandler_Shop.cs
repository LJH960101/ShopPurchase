using ShopPurchase.Common;
using ShopPurchase.Data;
using ShopPurchase.DB;
using ShopPurchase.Network;
using ShopPurchase.Object;
using ShopPurchase.Platform;

namespace ShopPurchase.PacketHandler
{
    public static class PacketHandler_Shop
    {
        public static void C2P_RequestShopBuy(Player _player, C2P_RequestShopBuy _packet)
        {
            var reward = DataManager.Instance.GetProduct(_packet.ProductId)?.GetReward();
            if (reward == null)
            {
                var response = new P2C_ResultShopBuy(EErrorCode.InvalidParam, null);
                _player.Send(response);
                return;
            }

            PlatformManager.Instance.Verify(_player.GetPlatformType(), _packet.Receipt, _packet.ProductId)
                .Then(_ => DBManager.Instance.InsertShopReceipt(_player.GetGUID(), _packet.Receipt, reward))
                .Then(_result =>
                {
                    _player.Post(() =>
                    {
                        _player.ApplyDBItemContext(_result.AddItemDBData);
                    });

                    var response = new P2C_ResultShopBuy(EErrorCode.Success, _result.AddItemDBData);
                    _player.Send(response);
                })
                .Catch(_errorCode =>
                {
                    if (_errorCode.IsOneOf(EErrorCode.ReceiptAlreadyInserted, EErrorCode.ReceiptVerifyFailed))
                    {
                        var response = new P2C_ResultShopBuy(_errorCode, null);
                        _player.Send(response);
                    }
                    else
                    {
                        _player.Kick(_errorCode);
                    }
                });
        }
    }
}
