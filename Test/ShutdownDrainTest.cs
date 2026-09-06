using System;
using System.Threading;
using ShopPurchase.Core.Thread;

namespace ShopPurchase.Test
{
    /// <summary>
    /// JHTimingWheel.Stop()의 종료 드레인이 예약돼 있던 작업을 하나도 버리지 않는지 확인한다.
    ///
    /// 반드시 맨 마지막에 실행해야 한다 — Stop()은 tick 스레드를 영구히 끝내므로, 이 뒤에 휠을
    /// 쓰는 테스트를 추가하면 그 테스트는 아무 작업도 실행되지 않은 채 조용히 통과하거나 멈춘다.
    ///
    /// 지연을 전부 1초 이상으로 잡는 이유가 이 테스트의 핵심이다. 그래야 Stop()이 걸리기 전에
    /// tick 스레드가 정상 경로로 먼저 집어가는 일이 없어서, 여기서 세는 실행이 전부 "종료 드레인이
    /// 건져낸 것"임이 보장된다 — 짧은 지연으로 잡으면 평소 경로가 처리해버려서 정작 검증하려던
    /// 종료 경로를 타지 않고도 테스트가 통과한다.
    ///
    /// 일부 작업은 드레인 도중에 다시 예약을 건다. 그 재예약은 이번 바퀴에서 이미 지나친 슬롯에
    /// 떨어질 수 있는데, 휠이 "한 바퀴 돌아도 아무것도 안 나올 때까지" 반복하지 않으면 바로 그
    /// 작업이 조용히 유실된다.
    /// </summary>
    public static class ShutdownDrainTest
    {
        private const int DelayTaskCount = 200;
        private const int KeyTaskCount = 50;
        private const int RescheduleCount = 20;

        // 휠 한 바퀴가 약 10.24초라, 그 안에 들어오면서도 tick 스레드가 미리 집어가지 못할 범위.
        private const int MinDelayMs = 1000;
        private const int MaxDelayMs = 9000;
        private const int RescheduleDelayMs = 5000;

        public static void Run()
        {
            Console.WriteLine("=== ShutdownDrainTest: 종료 시 예약 작업 드레인 검증 ===");

            int executed = 0;
            var random = new Random();

            for (int i = 0; i < DelayTaskCount; i++)
            {
                bool reschedules = i < RescheduleCount;
                JHTimingWheel.Instance.ScheduleDelay(random.Next(MinDelayMs, MaxDelayMs), () =>
                {
                    Interlocked.Increment(ref executed);

                    if (reschedules)
                    {
                        JHTimingWheel.Instance.ScheduleDelay(RescheduleDelayMs,
                            () => Interlocked.Increment(ref executed));
                    }
                });
            }

            for (int i = 0; i < KeyTaskCount; i++)
            {
                // 다중 key 예약은 별도의 슬롯 배열에 쌓이므로, 그쪽도 같이 드레인되는지 확인한다.
                JHTimingWheel.Instance.Schedule(random.Next(MinDelayMs, MaxDelayMs), new GUID[] { (GUID)i },
                    () => Interlocked.Increment(ref executed));
            }

            // 기다리지 않고 곧바로 멈춘다 — 위에서 예약한 것은 전부 아직 미래 슬롯에 남아 있다.
            // Stop()은 남은 작업을 tick 스레드가 직접 다 실행한 뒤에야 돌아오므로, 이 줄이
            // 끝난 시점의 카운트가 곧 최종값이다.
            JHTimingWheel.Instance.Stop();

            int expected = DelayTaskCount + KeyTaskCount + RescheduleCount;

            Console.WriteLine($"예약 {expected}건 (지연 {DelayTaskCount} + 다중 key {KeyTaskCount} + " +
                $"드레인 중 재예약 {RescheduleCount}), 실행 {executed}건");
            Console.WriteLine(executed == expected
                ? $"PASS: 종료 드레인이 예약 작업 {expected}건을 하나도 버리지 않음"
                : $"FAIL: {expected - executed}건 유실 (기대 {expected}, 실제 {executed})");

            Console.WriteLine("=== ShutdownDrainTest 완료 ===");
        }
    }
}
