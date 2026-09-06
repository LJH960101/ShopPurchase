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
                    // Then 콜백은 잡을 완료시킨 스레드에서 돌기 때문에, 플레이어 메모리를 건드리는
                    // ApplyDBItemContext는 여기서 바로 부르면 안 되고 Post로 다시 들어와야 한다.
                    // Send는 직렬화가 필요 없지만 같은 블록에 두면 "메모리 반영이 끝난 뒤에 응답이
                    // 나간다"는 순서까지 공짜로 보장된다.
                    _player.Post(() =>
                    {
                        _player.ApplyDBItemContext(_result.AddItemDBData);

                        var response = new P2C_ResultShopBuy(EErrorCode.Success, _result.AddItemDBData,
                            _result.Receipt.ReceiptRowId);
                        _player.Send(response);
                    });
                })
                .Catch(_errorCode =>
                {
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
