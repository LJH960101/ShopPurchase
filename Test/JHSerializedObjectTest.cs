using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ShopPurchase.Core.Thread;

namespace ShopPurchase.Test
{
    /// <summary>
    /// Post/Reserve의 lock-free(Interlocked.Exchange 기반) 직렬화가 극한 경합 상황에서도
    /// 실제로 안전한지 확인한다.
    ///
    /// - 겹치는 실행이 한 건도 없는지 (같은 객체를 동시에 두 곳에서 건드리지 않는지)
    /// - 콜백이 유실되지 않고 요청한 만큼 전부 완료되는지 (락 없이 체인만으로 끝까지 이어지는지)
    /// - 제한 시간 안에 전부 끝나는지 (데드락/라이브락 없는지)
    ///
    /// 적은 수의 객체(m_ObjectCount)에 많은 스레드(m_ProducerThreadCount)를 동시에 몰아붙여서
    /// 객체 하나당 경합이 최대한 심해지도록 만든다. Post/Reserve를 무작위로 섞어서 호출한다.
    /// </summary>
    public static class JHSerializedObjectTest
    {
        private class DummySerialized : JHSerializedObject
        {
            public DummySerialized(GUID _key) : base(_key)
            {
            }

            public void DoReserve(int _delayMs, Action _action) => Reserve(_delayMs, _action);
        }

        private const int m_ObjectCount = 4;
        private const int m_ProducerThreadCount = 50;
        private const int m_CallsPerThread = 250; // 총 4 * 50 * 250 = 50,000회

        public static void Run()
        {
            Console.WriteLine("=== JHSerializedObjectTest: Post/Reserve lock-free 직렬화 극한 검증 ===");

            var targets = new DummySerialized[m_ObjectCount];
            var busy = new int[m_ObjectCount];
            var completedCounts = new int[m_ObjectCount];
            for (int i = 0; i < m_ObjectCount; i++) targets[i] = new DummySerialized((GUID)i);

            int totalCalls = m_ObjectCount * m_ProducerThreadCount * m_CallsPerThread;
            bool violationDetected = false;
            var doneEvent = new CountdownEvent(totalCalls);
            var threads = new List<System.Threading.Thread>();

            for (int objIndex = 0; objIndex < m_ObjectCount; objIndex++)
            {
                int capturedObjIndex = objIndex;

                for (int t = 0; t < m_ProducerThreadCount; t++)
                {
                    var thread = new System.Threading.Thread(() =>
                    {
                        var localRandom = new Random(Guid.NewGuid().GetHashCode());
                        for (int c = 0; c < m_CallsPerThread; c++)
                        {
                            Action work = () =>
                            {
                                if (Interlocked.Increment(ref busy[capturedObjIndex]) != 1) violationDetected = true;
                                Thread.SpinWait(50); // 겹칠 여지를 넓히기 위한 아주 짧은 인위적 작업
                                Interlocked.Decrement(ref busy[capturedObjIndex]);
                                Interlocked.Increment(ref completedCounts[capturedObjIndex]);
                                doneEvent.Signal();
                            };

                            if (localRandom.Next(2) == 0)
                                targets[capturedObjIndex].Post(work);
                            else
                                targets[capturedObjIndex].DoReserve(localRandom.Next(0, 5), work);
                        }
                    })
                    {
                        IsBackground = true,
                    };
                    threads.Add(thread);
                }
            }

            var stopwatch = Stopwatch.StartNew();
            foreach (var thread in threads) thread.Start();
            foreach (var thread in threads) thread.Join();

            bool completedInTime = doneEvent.Wait(TimeSpan.FromSeconds(60));
            stopwatch.Stop();

            int totalCompleted = 0;
            for (int i = 0; i < m_ObjectCount; i++) totalCompleted += completedCounts[i];

            Console.WriteLine($"총 요청: {totalCalls}, 총 완료: {totalCompleted}, 경과: {stopwatch.ElapsedMilliseconds}ms");
            Console.WriteLine(!completedInTime
                ? "FAIL: 제한 시간 안에 모든 작업이 끝나지 않음 (콜백 유실/데드락 의심)"
                : violationDetected
                    ? "FAIL: 같은 객체에 대해 겹치는 실행이 발생함"
                    : totalCompleted != totalCalls
                        ? $"FAIL: 완료 개수 불일치 (기대 {totalCalls}, 실제 {totalCompleted})"
                        : "PASS: 극한 경합 상황에서도 직렬화 유지, 콜백 유실 없음");

            Console.WriteLine("=== JHSerializedObjectTest 완료 ===");
        }
    }
}
