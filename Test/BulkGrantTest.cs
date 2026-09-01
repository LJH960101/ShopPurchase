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
    /// "유저 3,000명에게 아이템 10개씩 보상 지급" 같은 대량 생성이 JHGUIDGenerator와
    /// 어떻게 만나야 안전한지 두 방식을 실측으로 비교한다.
    ///
    /// - 나쁜 예(RunTightLoop): 한 스레드가 tight loop로 Next()를 30,000번 연달아 호출한다.
    ///   같은 ms 안에 Sequence(1024개)를 훌쩍 넘겨서 충돌이 크게 난다.
    /// - 좋은 예(RunViaTimingWheel): PacketHandler_Shop과 같은 패턴으로, 유저별로 JHTimingWheel에
    ///   Job을 나눠 스케줄한다. 실제 처리가 ThreadPool을 통해 여러 스레드/여러 ms에 걸쳐 자연스럽게
    ///   퍼지기 때문에 충돌이 거의 발생하지 않는다.
    /// </summary>
    public static class BulkGrantTest
    {
        private const int m_PlayerCount = 3000;
        private const int m_ItemsPerPlayer = 10;

        public static void Run()
        {
            RunTightLoop();
            RunViaTimingWheel();
        }

        private static void RunTightLoop()
        {
            Console.WriteLine("=== BulkGrantTest: tight loop로 직접 호출 (나쁜 예) ===");

            var generator = new JHGUIDGenerator(_region: 1, _server: 1);
            var ids = new List<GUID>(m_PlayerCount * m_ItemsPerPlayer);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            for (int p = 0; p < m_PlayerCount; p++)
            {
                for (int i = 0; i < m_ItemsPerPlayer; i++)
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
            Console.WriteLine("=== BulkGrantTest: JHTimingWheel로 유저별 Job 분산 (좋은 예) ===");

            var generator = new JHGUIDGenerator(_region: 1, _server: 1);
            var playerGuidGenerator = new JHGUIDGenerator(_region: 1, _server: 2);
            var bag = new ConcurrentBag<GUID>();
            var doneEvents = new List<ManualResetEventSlim>();

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            for (int p = 0; p < m_PlayerCount; p++)
            {
                GUID playerGuid = playerGuidGenerator.Next();
                var doneEvent = new ManualResetEventSlim(false);
                doneEvents.Add(doneEvent);

                // 실제 보상 지급 패킷 핸들러라면 player.GetGUID()로 키를 잡을 자리 — 여기서는 가상의
                // 플레이어 GUID로 대신 흉내낸다. delay=0이라도 TimingWheel/ThreadPool을 거치면서
                // 30,000번의 Next() 호출이 여러 스레드와 실제 ms에 걸쳐 자연스럽게 퍼진다.
                JHTimingWheel.Instance.Schedule(0, new[] { playerGuid }, () =>
                {
                    for (int i = 0; i < m_ItemsPerPlayer; i++)
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
            GuidTestHelpers.PrintDuplicateStats(_ids);
            int distinctTimeValues = _ids.Select(id => JHGUIDGenerator.Decode(id).Time).Distinct().Count();
            Console.WriteLine($"실제 경과 시간: {_elapsed.TotalMilliseconds:F2}ms, 실제로 쓰인 서로 다른 Time 값 개수: {distinctTimeValues}");
        }
    }
}
