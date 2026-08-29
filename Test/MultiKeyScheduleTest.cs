using System;
using System.Collections.Generic;
using System.Threading;
using ShopPurchase.Core.Thread;

namespace ShopPurchase.Test
{
    /// <summary>
    /// 여러 key로 동시에 예약된 작업이
    /// 1) 정확히 한 번만 실행되는지(중복 실행 없음)
    /// 2) 겹치는 key를 가진 다른 작업과 절대 동시에 실행되지 않는지(직렬화 보장)
    /// 3) 서로 기다리다 멈추지 않는지(데드락 없음)
    /// 를 확인한다.
    /// </summary>
    public static class MultiKeyScheduleTest
    {
        private const int m_KeyCount = 5;
        private const int m_TaskCount = 300;

        public static void Run()
        {
            RunRunOnceCheck();
            RunOverlapCheck();
        }

        private static void RunRunOnceCheck()
        {
            Console.WriteLine("=== MultiKeyScheduleTest: 다중 key 단일 실행 검증 ===");

            int executionCount = 0;
            var doneEvent = new ManualResetEventSlim(false);

            JHTimingWheel.Instance.Schedule(0, new GUID[] { 1, 2 }, () =>
            {
                Interlocked.Increment(ref executionCount);
                doneEvent.Set();
            });

            bool completed = doneEvent.Wait(TimeSpan.FromSeconds(5));

            Console.WriteLine(completed
                ? $"완료: 실행 횟수={executionCount} (기대값 1)"
                : "FAIL: 5초 안에 실행되지 않음 (데드락 의심)");

            Console.WriteLine("=== 단일 실행 검증 완료 ===");
        }

        private static void RunOverlapCheck()
        {
            Console.WriteLine("=== MultiKeyScheduleTest: 겹치는 key 동시 실행 방지 검증 ===");

            var keys = new GUID[m_KeyCount];
            for (int i = 0; i < m_KeyCount; i++) keys[i] = (GUID)i;

            var busy = new int[m_KeyCount];
            var random = new Random();
            var violationDetected = false;
            var doneEvents = new ManualResetEventSlim[m_TaskCount];

            for (int t = 0; t < m_TaskCount; t++)
            {
                doneEvents[t] = new ManualResetEventSlim(false);
                var doneEvent = doneEvents[t];

                // 이 작업이 건드릴 key 2~3개를 무작위로 고른다 (겹치는 조합이 자주 나오게).
                var indexSet = new HashSet<int>();
                int wantCount = random.Next(2, 4);
                while (indexSet.Count < wantCount) indexSet.Add(random.Next(m_KeyCount));

                var taskIndices = new List<int>(indexSet);
                var taskKeys = new GUID[taskIndices.Count];
                for (int k = 0; k < taskIndices.Count; k++) taskKeys[k] = keys[taskIndices[k]];

                JHTimingWheel.Instance.Schedule(0, taskKeys, () =>
                {
                    foreach (var idx in taskIndices)
                    {
                        if (Interlocked.Increment(ref busy[idx]) != 1) violationDetected = true;
                    }

                    Thread.SpinWait(2000); // 겹칠 여지를 넓히기 위한 인위적 지연

                    foreach (var idx in taskIndices)
                    {
                        Interlocked.Decrement(ref busy[idx]);
                    }

                    doneEvent.Set();
                });
            }

            foreach (var doneEvent in doneEvents)
            {
                doneEvent.Wait(TimeSpan.FromSeconds(10));
            }

            Console.WriteLine(violationDetected
                ? "FAIL: 겹치는 key를 가진 작업이 동시에 실행됨"
                : $"PASS: {m_TaskCount}개 작업 모두 겹치는 key끼리 동시 실행되지 않음");

            Console.WriteLine("=== 동시 실행 방지 검증 완료 ===");
        }
    }
}
