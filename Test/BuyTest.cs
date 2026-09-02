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
    /// 별도 테스트 프레임워크 없이, 상점 구매 Job을 여러 건 실행하고 Send/Kick 결과를 콘솔로 확인한다.
    /// 정상 구매 3건 + 중복 영수증 + 위조 영수증 + 상품 변조까지 주요 분기를 한 번씩 훑는다.
    /// </summary>
    public static class BuyTest
    {
        private static readonly JHGUIDGenerator s_guidGenerator = new JHGUIDGenerator(_region: 1, _server: 1);

        /// <summary>한 건의 구매 요청 시나리오. 모킹 영수증 형식은 "{플랫폼 토큰}-{상품 ID}"다.</summary>
        private class BuyCase
        {
            public EPlatform Platform { get; }
            public string Receipt { get; }
            public int RequestedProductId { get; }
            public string Expectation { get; }

            public BuyCase(EPlatform _platform, string _receipt, int _requestedProductId, string _expectation)
            {
                Platform = _platform;
                Receipt = _receipt;
                RequestedProductId = _requestedProductId;
                Expectation = _expectation;
            }
        }

        private static readonly BuyCase[] s_cases =
        {
            new BuyCase(EPlatform.GooglePlay, "1111-1000", 1000, "정상 구매"),
            new BuyCase(EPlatform.AppStore,   "2222-1001", 1001, "정상 구매"),
            new BuyCase(EPlatform.Steam,      "3333-1002", 1002, "정상 구매"),
            new BuyCase(EPlatform.GooglePlay, "1111-1000", 1000, "이미 쓴 영수증 -> ReceiptAlreadyInserted"),
            new BuyCase(EPlatform.AppStore,   "0000-1004", 1004, "위조 영수증 -> ReceiptVerifyFailed"),
            // 핵심 케이스: 영수증 자체는 진짜(1002 상품)인데 더 비싼 1004를 달라고 요청한다.
            // 클라이언트가 보낸 ProductId를 그대로 믿으면 그냥 통과해버리는 변조 시나리오다.
            new BuyCase(EPlatform.Steam,      "3333-1002", 1004, "싼 상품 영수증으로 비싼 상품 요청 -> ReceiptProductMismatch + Kick"),
        };

        public static void Run()
        {
            Console.WriteLine($"=== BuyTest: 상점 구매 {s_cases.Length}회 실행 ===");

            for (int i = 0; i < s_cases.Length; i++)
            {
                var buyCase = s_cases[i];
                var player = new Player(s_guidGenerator.Next(), buyCase.Platform);

                var packet = new C2P_RequestShopBuy
                {
                    Receipt = buyCase.Receipt,
                    ProductId = buyCase.RequestedProductId,
                };

                Console.WriteLine($"[{i}] Request: player={player.GetGUID()}, platform={buyCase.Platform}, " +
                    $"receipt={buyCase.Receipt}, productId={buyCase.RequestedProductId} ({buyCase.Expectation})");

                // C2P_RequestShopBuy는 void라 완료 신호를 돌려주지 않는다 — 결과는 Player.Send/Kick이
                // 콘솔에 찍는 로그로만 확인한다.
                PacketHandler_Shop.C2P_RequestShopBuy(player, packet);
            }

            // 완료 신호가 없으니, 전체 비동기 체인(검증 + DB 트랜잭션)이 끝날 시간을 그냥 기다린다.
            Thread.Sleep(TimeSpan.FromSeconds(2));

            Console.WriteLine("=== BuyTest 완료 ===");
        }
    }
}
