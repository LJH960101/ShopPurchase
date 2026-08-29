using System;

namespace ShopPurchase.Core
{
    /// <summary>System.Random은 스레드 세이프하지 않아서, 스레드마다 독립된 인스턴스를 준다.</summary>
    internal static class JHRandomProvider
    {
        private static readonly Random m_SeedSource = new Random();
        private static readonly object m_SeedLock = new object();

        [ThreadStatic]
        private static Random m_local;

        public static Random GetRandom()
        {
            if (m_local == null)
            {
                int seed;
                lock (m_SeedLock) seed = m_SeedSource.Next();
                m_local = new Random(seed);
            }

            return m_local;
        }
    }
}
