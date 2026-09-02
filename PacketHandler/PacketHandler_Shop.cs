using ShopPurchase.Common;
using ShopPurchase.Core.Thread;
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
            var productRecord = DataManager.Instance.GetProductTable(_packet.ProductId);
            if (productRecord == null)
            {
                var response = new P2C_ResultShopBuy(EErrorCode.InvalidParam, null);
                _player.Send(response);
                return;
            }

            // 체인 중 어디서 실패하든(EErrorCode로 reject) 남은 Then들은 자동으로 건너뛰어지고
            // Catch로 바로 전파된다 — JHJob의 기본 동작이라 여기서 성공/실패를 따로 검사할 필요가 없다.
            //
            // 영수증 등록 + 아이템 지급(DB)은 InsertShopReceipt 안에서 BeginTran ~ EndTran으로 원자적으로
            // 처리되고, 그 결과(_result.AddItemDBData)를 메모리에 반영하는 것도 이 체인 안에서
            // _player.Post로 다시 감싸서 처리한다 — 그래야 그 시점의 "현재" 메모리 상태 기준으로 더해진다.
            PlatformManager.Instance.Verify(_player.GetPlatformType(), _packet.Receipt)
                .Then(_ => DBManager.Instance.InsertShopReceipt(_player.GetGUID(), _packet.Receipt, productRecord))
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
