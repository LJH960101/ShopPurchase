using System;
using System.Threading;
using ShopPurchase.Common;
using ShopPurchase.Core;
using ShopPurchase.Network;
using ShopPurchase.Object;
using ShopPurchase.PacketHandler;

namespace ShopPurchase.Test
{
    /// <summary>
    /// 별도 테스트 프레임워크 없이, 상점 구매 Job을 5회 실행하고 Send 결과만 콘솔로 확인한다.
    /// </summary>
    public static class BuyTest
    {
        private static readonly JHGUIDGenerator m_guidGenerator = new JHGUIDGenerator(_region: 1, _server: 1);

        public static void Run()
        {
            Console.WriteLine("=== BuyTest: 상점 구매 5회 실행 ===");

            for (int i = 0; i < 5; i++)
            {
                var platform = (EPlatform)(i % 3);
                var player = new Player(m_guidGenerator.Next(), platform);
                string receipt = GetReceiptForCase(platform, i);

                var packet = new C2P_RequestShopBuy
                {
                    Receipt = receipt,
                    ProductId = 1000 + i,
                };

                Console.WriteLine($"[{i}] Request: player={player.GetGUID()}, platform={platform}, receipt={receipt}");

                // C2P_RequestShopBuy는 void라 완료 신호를 돌려주지 않는다 — 결과는 Player.Send가
                // 콘솔에 찍는 P2C_ResultShopBuy 로그로만 확인한다.
                PacketHandler_Shop.C2P_RequestShopBuy(player, packet);
            }

            // 완료 신호가 없으니, 5건 전부의 비동기 체인(검증 + DB 트랜잭션)이 끝날 시간을 그냥 기다린다.
            Thread.Sleep(TimeSpan.FromSeconds(2));

            Console.WriteLine("=== BuyTest 완료 ===");
        }

        private static string GetReceiptForCase(EPlatform _platform, int _index)
        {
            // 마지막 케이스는 영수증 검증 실패(Catch) 경로를 확인하기 위해 일부러 틀린 값을 준다.
            if (_index == 4) return "0000";

            return _platform switch
            {
                EPlatform.GooglePlay => "1111",
                EPlatform.AppStore => "2222",
                EPlatform.Steam => "3333",
                _ => "0000",
            };
        }
    }
}
