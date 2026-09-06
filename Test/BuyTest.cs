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
    /// 정상 구매 + 영수증 재사용 + 위조 영수증 + 상품 변조까지 주요 분기를 한 번씩 훑는다.
    ///
    /// 모든 요청을 플레이어 한 명이 보낸다. 영수증 중복 판정이 (플레이어, 영수증) 단위라, 케이스마다
    /// 플레이어를 새로 만들면 같은 영수증을 다시 써도 중복으로 잡히지 않기 때문이다. 플레이어의
    /// 플랫폼은 생성자에서 고정되므로 이 테스트가 실제로 태우는 검증 경로는 GooglePlay 하나다
    /// (Apple/Steam 구현체는 PlatformManager가 리플렉션으로 등록만 하고 여기서 호출되지는 않는다).
    /// </summary>
    public static class BuyTest
    {
        private const EPlatform TestPlatform = EPlatform.GooglePlay;
        private const string PlatformToken = "1111"; // TestPlatform(GooglePlay)의 성공 토큰

        private static readonly JHGUIDGenerator s_guidGenerator = new JHGUIDGenerator(_region: 1, _server: 1);

        /// <summary>한 건의 구매 요청 시나리오. 모킹 영수증 형식은 "{플랫폼 토큰}-{상품 ID}"다.</summary>
        private class BuyCase
        {
            public string Receipt { get; }
            public int RequestedProductId { get; }
            public string Expectation { get; }

            public BuyCase(string _receipt, int _requestedProductId, string _expectation)
            {
                Receipt = _receipt;
                RequestedProductId = _requestedProductId;
                Expectation = _expectation;
            }
        }

        private static readonly BuyCase[] s_cases =
        {
            new BuyCase($"{PlatformToken}-1000", 1000, "정상 구매"),
            new BuyCase($"{PlatformToken}-1001", 1001, "정상 구매"),
            // 위 1000번과 같은 영수증. 선점(Player.TryConsumeReceipt)이 요청 루프에서 동기로
            // 끝나므로 순서가 보장된다 — 이 건은 매 실행마다 검증 왕복 없이 즉시 끊긴다.
            new BuyCase($"{PlatformToken}-1000", 1000, "같은 영수증 재사용 -> ReceiptAlreadyInserted"),
            new BuyCase($"0000-1004", 1004, "위조 영수증 -> ReceiptVerifyFailed"),
            // 핵심 케이스: 영수증 자체는 진짜(1002 상품)인데 더 비싼 1004를 달라고 요청한다.
            // 클라이언트가 보낸 ProductId를 그대로 믿으면 그냥 통과해버리는 변조 시나리오다.
            new BuyCase($"{PlatformToken}-1002", 1004, "싼 상품 영수증으로 비싼 상품 요청 -> ReceiptProductMismatch + Kick"),
        };

        public static void Run()
        {
            Console.WriteLine($"=== BuyTest: 상점 구매 {s_cases.Length}회 실행 ===");

            var player = new Player(s_guidGenerator.Next(), TestPlatform);
            Console.WriteLine($"플레이어: {player.GetGUID()} (platform={TestPlatform})");

            for (int i = 0; i < s_cases.Length; i++)
            {
                var buyCase = s_cases[i];

                var packet = new C2P_RequestShopBuy
                {
                    Receipt = buyCase.Receipt,
                    ProductId = buyCase.RequestedProductId,
                };

                Console.WriteLine($"[{i}] Request: receipt={buyCase.Receipt}, " +
                    $"productId={buyCase.RequestedProductId} ({buyCase.Expectation})");

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
