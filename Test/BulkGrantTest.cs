using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ShopPurchase.Core;
using ShopPurchase.Core.Thread;

namespace ShopPurchase.Test
{
    /// <summary>
    /// "유저 3,000명에게 아이템 10개씩 보상 지급" 같은 대량 생성에서, 호출 패턴에 따라
    /// JHGUIDGenerator의 Sequence 대기가 어떻게 달라지는지 실측한다.
    ///
    /// - RunTightLoop: 한 스레드가 tight loop로 Next()를 30,000번 연달아 호출한다. 같은 ms 안에서
    ///   Sequence(1024개)를 소진하면 다음 ms가 될 때까지 기다리게 된다.
    /// - RunViaTimingWheel: PacketHandler_Shop과 같은 패턴으로, 유저별로 JHTimingWheel에 Job을
    ///   나눠 스케줄한다. 처리가 ThreadPool을 통해 여러 스레드와 여러 ms에 걸쳐 퍼진다.
    ///
    /// 어느 쪽이 옳다를 가리는 테스트가 아니다. 중복은 양쪽 다 0건이고(생성기가 lock 기반이라
    /// 충돌은 애초에 나지 않는다), 스케줄러를 거치는 쪽이 오히려 전체 시간은 더 걸린다.
    /// 확인하려는 건 "정확성은 어떤 호출 패턴에서도 지켜지고, 그 대가는 충돌이 아니라 대기 시간으로
    /// 나타난다"는 이 생성기의 성질이다.
    /// </summary>
    public static class BulkGrantTest
    {
        private const int PlayerCount = 3000;
        private const int ItemsPerPlayer = 10;

        public static void Run()
        {
            RunTightLoop();
            RunViaTimingWheel();
        }

        private static void RunTightLoop()
        {
            Console.WriteLine("=== BulkGrantTest: 한 스레드에서 tight loop로 직접 호출 ===");

            var generator = new JHGUIDGenerator(_region: 1, _server: 1);
            var ids = new List<GUID>(PlayerCount * ItemsPerPlayer);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            for (int p = 0; p < PlayerCount; p++)
            {
                for (int i = 0; i < ItemsPerPlayer; i++)
                {
                    ids.Add(generator.Next());
                }
            }
            stopwatch.Stop();

            PrintStats(ids, stopwatch.Elapsed);
            Console.WriteLine("=== tight loop 완료 ===");
        }

        private static void RunViaTimingWheel()
        {
            Console.WriteLine("=== BulkGrantTest: JHTimingWheel로 유저별 Job 분산 ===");

            var generator = new JHGUIDGenerator(_region: 1, _server: 1);
            var playerGuidGenerator = new JHGUIDGenerator(_region: 1, _server: 2);
            var bag = new ConcurrentBag<GUID>();
            var doneEvents = new List<ManualResetEventSlim>();

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            for (int p = 0; p < PlayerCount; p++)
            {
                GUID playerGuid = playerGuidGenerator.Next();
                var doneEvent = new ManualResetEventSlim(false);
                doneEvents.Add(doneEvent);

                // 실제 보상 지급 패킷 핸들러라면 player.GetGUID()로 키를 잡을 자리 — 여기서는 가상의
                // 플레이어 GUID로 대신 흉내낸다. delay=0이라도 TimingWheel/ThreadPool을 거치면서
                // 30,000번의 Next() 호출이 여러 스레드와 실제 ms에 걸쳐 자연스럽게 퍼진다.
                JHTimingWheel.Instance.Schedule(0, new[] { playerGuid }, () =>
                {
                    for (int i = 0; i < ItemsPerPlayer; i++)
                    {
                        bag.Add(generator.Next());
                    }

                    doneEvent.Set();
                });
            }

            bool allCompleted = true;
            foreach (var doneEvent in doneEvents)
            {
                if (!doneEvent.Wait(TimeSpan.FromSeconds(10))) allCompleted = false;
            }
            stopwatch.Stop();

            if (!allCompleted)
                Console.WriteLine("FAIL: 10초 안에 끝나지 않은 작업이 있음 (데드락 의심)");

            PrintStats(bag, stopwatch.Elapsed);
            Console.WriteLine("=== JHTimingWheel 분산 완료 ===");
        }

        private static void PrintStats(IReadOnlyCollection<GUID> _ids, TimeSpan _elapsed)
        {
            int duplicateCount = GuidTestHelpers.PrintDuplicateStats(_ids);
            int distinctTimeValues = _ids.Select(id => JHGUIDGenerator.Decode(id).Time).Distinct().Count();
            Console.WriteLine($"실제 경과 시간: {_elapsed.TotalMilliseconds:F2}ms, 실제로 쓰인 서로 다른 Time 값 개수: {distinctTimeValues}");

            // 두 방식 모두 "느려질지언정 충돌은 없다"가 핵심이라, 판정 기준은 양쪽 다 중복 0건이다.
            Console.WriteLine(duplicateCount == 0
                ? $"PASS: {_ids.Count}개 생성, 충돌 0건"
                : $"FAIL: 중복 {duplicateCount}건 발생");
        }
    }
}
