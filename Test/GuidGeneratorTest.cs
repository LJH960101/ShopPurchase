using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ShopPurchase.Core;

namespace ShopPurchase.Test
{
    /// <summary>
    /// 여러 서버(JHGUIDGenerator 인스턴스)에서 여러 스레드가 대기 없이 최대 속도로 Next()를 몰아쳤을 때
    /// 겹치는 값이 있는지 통계로 보여준다. lock 기반으로 정확성을 보장하므로 항상 중복 0%여야 한다.
    /// </summary>
    public static class GuidGeneratorTest
    {
        private const int m_ServerCount = 5;
        private const int m_ThreadsPerServer = 8;
        private const int m_IdsPerThread = 5000;

        public static void Run()
        {
            Console.WriteLine("=== GuidGeneratorTest: JHGUIDGenerator (서버 x 스레드 최대 속도) ===");

            var bag = new ConcurrentBag<GUID>();
            var tasks = new List<Task>();

            for (int serverIndex = 0; serverIndex < m_ServerCount; serverIndex++)
            {
                // 서버 한 대 = region/server 조합 하나 = JHGUIDGenerator 인스턴스 하나.
                var generator = new JHGUIDGenerator(_region: 1, _server: serverIndex);

                for (int t = 0; t < m_ThreadsPerServer; t++)
                {
                    tasks.Add(Task.Run(() =>
                    {
                        for (int i = 0; i < m_IdsPerThread; i++)
                        {
                            bag.Add(generator.Next());
                        }
                    }));
                }
            }

            Task.WaitAll(tasks.ToArray());
            int duplicateCount = GuidTestHelpers.PrintDuplicateStats(bag);

            var sample = bag.First();
            var (region, server, sequence, time) = JHGUIDGenerator.Decode(sample);
            Console.WriteLine($"샘플 디코드: id={sample} -> region={region}, server={server}, sequence={sequence}, time={time}");

            Console.WriteLine(duplicateCount == 0
                ? $"PASS: 서버 {m_ServerCount}개 × 스레드 {m_ThreadsPerServer}개 × {m_IdsPerThread}개 생성, 충돌 0건"
                : $"FAIL: 중복 {duplicateCount}건 발생");

            Console.WriteLine("=== GuidGeneratorTest 완료 ===");
        }
    }
}
