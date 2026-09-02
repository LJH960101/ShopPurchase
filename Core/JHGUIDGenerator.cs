using System;
using System.Threading;

namespace ShopPurchase.Core
{
    /// <summary>
    /// 여러 서버에 걸쳐(범서버) 유일해야 하는 64bit ulong ID 생성기.
    /// Time(40bit) / Sequence(10bit) / Region(5bit) / Server(9bit)로 비트마스킹한다.
    ///
    /// - Region  : 5bit, 0~31 (리전 약 20개를 가정)
    /// - Server  : 9bit, 0~511 (리전당 서버 약 500대를 가정)
    /// - Sequence: 10bit, 0~1023. ms가 바뀌면 0으로 리셋하고, 같은 ms 안에서는 1씩 증가시킨다
    ///             (표준 Snowflake 방식). ms 전환과 무관하게 그냥 누적만 시키면, "0으로 랩어라운드됐다"는
    ///             신호가 "이 ms에서 1024개를 다 썼다"는 것과 일치하지 않는 문제가 있다 — 예를 들어
    ///             Sequence가 여러 ms에 걸쳐 누적되다 1022가 된 채로 새 ms에 진입하면, 그 ms에서 딱
    ///             2번만 불러도 0으로 랩되어 버리고, 반대로 그 ms의 실제 시작값(0이 아닌 값)으로 되돌아오는
    ///             시점은 전혀 감지하지 못해 그 값이 재사용되며 충돌할 수 있다. 그래서 ms가 바뀔 때마다
    ///             확실하게 0부터 다시 세도록 되돌렸다.
    /// - Time    : 나머지 40bit. 실제 벽시계 ms (기준시각부터 ~34.8년). 시계가 뒤로 흘러도(NTP 보정,
    ///             VM 일시정지/마이그레이션 등) 이전에 낸 값보다 작아지지 않도록 마지막 값을 하한으로 둔다 —
    ///             이게 없으면 시계가 뒤로 튈 때 과거 timestamp가 재사용되면서, 그 시점에 Sequence가 다시
    ///             0부터 시작해 예전에 그 timestamp에서 이미 낸 (Time, Sequence) 조합과 겹칠 수 있다.
    ///
    /// GUID 발급은 게임 로직의 hot path가 아니라 "무거워도 되는" 처리로 보고, wait-free 대신 lock
    /// 기반으로 정확성을 우선한다. 같은 ms 안에서 Sequence(1024개)를 다 써버리면, 실제로 다음 ms가
    /// 될 때까지 lock을 잡은 채로 짧게 기다린다 — 그래서 "유저 3,000명에게 아이템 10개씩 한꺼번에
    /// 지급" 같은 벌크 생성도 호출부 수정 없이 정확하게(다만 그만큼 시간이 걸려서) 처리된다.
    ///
    /// 유일성이 근본적으로 깨지는 두 경우(Time 비트 공간 소진, 시계가 허용치 이상 되감김)에는
    /// 예외가 아니라 Environment.FailFast로 프로세스를 즉시 종료시킨다 — 자세한 이유는 각 호출 지점 참고.
    ///
    /// Region/Server가 곧 서버 한 대의 정체성이므로, 서버가 다르면 상위 비트부터 값이 달라져 자동으로 유일하다.
    /// 따라서 여러 서버 x 여러 스레드가 동시에 호출해도 전역적으로 유일한 값이 나온다.
    /// </summary>
    public class JHGUIDGenerator
    {
        private const int ServerBits = 9;    // 0 ~ 511
        private const int RegionBits = 5;    // 0 ~ 31
        private const int SequenceBits = 10; // 0 ~ 1023
        private const int TimeBits = 64 - RegionBits - ServerBits - SequenceBits;

        private const int MaxRegion = (1 << RegionBits) - 1;
        private const int MaxServer = (1 << ServerBits) - 1;
        private const long MaxSequence = (1 << SequenceBits) - 1;
        private const long MaxTime = (1L << TimeBits) - 1;

        private const int RegionShift = ServerBits;
        private const int SequenceShift = ServerBits + RegionBits;
        private const int TimeShift = ServerBits + RegionBits + SequenceBits;

        // 정상적인 "같은 ms에 1024개 몰림"은 보통 1ms 안에 풀린다. 이 값보다 더 오래 기다려야 한다면
        // 부하가 아니라 시스템 시계가 실제로 뒤로 튄 것(NTP 스텝 보정, VM 일시정지/재개 등)으로 본다.
        private const long MaxClockRollbackToleranceMs = 5000;

        // 이 생성기만의 기준 시각. 여기서부터 흐른 ms를 Time 필드로 쓴다.
        private static readonly DateTime s_epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly ulong m_regionServerPart;
        private readonly object m_lock = new object();

        private long m_lastTimestamp;
        private long m_sequence;

        public JHGUIDGenerator(int _region, int _server)
        {
            if (_region < 0 || _region > MaxRegion)
                throw new ArgumentOutOfRangeException(nameof(_region), $"region은 0~{MaxRegion} 범위여야 합니다.");
            if (_server < 0 || _server > MaxServer)
                throw new ArgumentOutOfRangeException(nameof(_server), $"server는 0~{MaxServer} 범위여야 합니다.");

            m_regionServerPart = ((ulong)(uint)_region << RegionShift) | (uint)_server;
        }

        /// <summary>스레드 세이프하게(lock), 범서버로 유일한 GUID를 하나 뽑는다.</summary>
        public GUID Next()
        {
            lock (m_lock)
            {
                long now = CurrentTimeMs();

                long newTimestamp;
                long newSequence;

                if (now > m_lastTimestamp)
                {
                    newTimestamp = now;
                    newSequence = 0;
                }
                else
                {
                    // 같은 ms(혹은 시계가 뒤로 흐른 경우)라면 이전 timestamp를 유지하고 Sequence만 늘린다.
                    newTimestamp = m_lastTimestamp;
                    newSequence = m_sequence + 1;

                    if (newSequence > MaxSequence)
                    {
                        // 이번 ms의 Sequence(0~1023)를 다 썼으면, 실제로 다음 ms가 될 때까지 기다린 뒤 0부터 다시 센다.
                        newTimestamp = WaitNextMillis(m_lastTimestamp);
                        newSequence = 0;
                    }
                }

                if (newTimestamp > MaxTime)
                {
                    // 유일성 보장이 근본적으로 깨진 상태라, 계속 돌게 두면 중복 ID가 DB까지 흘러간다.
                    // FailFast는 어떤 catch(Exception)로도 가로챌 수 없다 — DBManager/JHTimingWheel의
                    // catch가 이 장애를 EErrorCode.Exception 하나로 뭉개지 못하게 하려는 선택이다.
                    string message = "[FATAL] JHGUIDGenerator의 Time 비트 공간을 초과했습니다. GUID 유일성을 더 이상 보장할 수 없어 서버를 즉시 종료합니다.";
                    Environment.FailFast(message);
                    throw new InvalidOperationException(message); // FailFast가 이미 죽여서 도달하지 않는다.
                }

                m_lastTimestamp = newTimestamp;
                m_sequence = newSequence;

                return ((ulong)newTimestamp << TimeShift) | ((ulong)newSequence << SequenceShift) | m_regionServerPart;
            }
        }

        private static long CurrentTimeMs() => (long)(DateTime.UtcNow - s_epoch).TotalMilliseconds;

        private static long WaitNextMillis(long _lastTimestamp)
        {
            long now = CurrentTimeMs();
            long rollbackMs = _lastTimestamp - now;
            if (rollbackMs > MaxClockRollbackToleranceMs)
            {
                // 시계가 이만큼 되감겼다는 건 이미 발급한 (Time, Sequence) 조합을 다시 낼 수 있다는
                // 뜻이다 — 위 비트 공간 소진과 같은 급의 장애라 똑같이 프로세스를 죽인다.
                string message = $"[FATAL] JHGUIDGenerator: 시스템 시계가 {rollbackMs}ms만큼 뒤로 흘렀습니다 " +
                    $"(허용 오차 {MaxClockRollbackToleranceMs}ms). GUID 유일성을 더 이상 보장할 수 없어 서버를 즉시 종료합니다.";
                Environment.FailFast(message);
                throw new InvalidOperationException(message); // FailFast가 이미 죽여서 도달하지 않는다.
            }

            var spinWait = new SpinWait();
            while (now <= _lastTimestamp)
            {
                spinWait.SpinOnce();
                now = CurrentTimeMs();
            }

            return now;
        }

        /// <summary>디버깅/테스트용: GUID를 다시 Region/Server/Sequence/Time으로 분해한다.</summary>
        public static (int Region, int Server, int Sequence, ulong Time) Decode(GUID _id)
        {
            int region = (int)((_id >> RegionShift) & (ulong)MaxRegion);
            int server = (int)(_id & (ulong)MaxServer);
            int sequence = (int)((_id >> SequenceShift) & (ulong)MaxSequence);
            ulong time = _id >> TimeShift;
            return (region, server, sequence, time);
        }
    }
}
