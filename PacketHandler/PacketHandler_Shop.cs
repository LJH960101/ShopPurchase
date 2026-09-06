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

            // 검증 왕복을 시작하기 전에 선점한다 — 왕복이 도는 동안 같은 영수증으로 두 번째 요청이
            // 들어와도 여기서 막히고, 외부 호출도 시작하지 않는다.
            if (!_player.TryConsumeReceipt(_packet.Receipt))
            {
                var response = new P2C_ResultShopBuy(EErrorCode.ReceiptAlreadyInserted, null);
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
                    // 선점은 여기 한 곳에서만 되돌린다 — 어느 단계에서 실패하든 Catch 하나로 모인다.
                    _player.ReleaseReceipt(_packet.Receipt);

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
