using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ShopPurchase.Common;

namespace ShopPurchase.Core.Thread
{
    /// <summary>
    /// 이 프로젝트의 모든 비동기 지연(HTTP 검증, DB 접근 등)을 처리하는 타이밍 휠.
    /// 이 클래스는 순수하게 "시간"만 담당한다 — 직렬화(락)는 더 이상 여기서 하지 않는다.
    ///
    /// 슬롯(m_delaySlots/m_slots)은 List + m_slotLock으로 보호한다. ConcurrentQueue만으로
    /// lock-free하게 만들어본 적이 있는데, "현재 슬롯이 몇 번인지 읽는 것"과 "그 슬롯을 드레인하고
    /// 다음 슬롯으로 전진하는 것"이 원자적으로 묶여야 한다는 게 문제였다 — 그 둘이 분리되면, 프로듀서가
    /// 막 드레인되고 있는(혹은 이미 드레인된) 슬롯 번호를 그대로 읽어서 거기에 Enqueue해버릴 수 있고,
    /// 그러면 그 작업은 휠이 한 바퀴(약 10초) 다 돌 때까지 방치되거나 최악의 경우 아예 유실될 수 있다.
    /// ConcurrentQueue/Interlocked.Exchange 같은 개별 원자적 연산으로는 "두 가지 다른 상태(현재 슬롯
    /// 번호 + 그 슬롯의 내용물)를 하나의 트랜잭션처럼 묶는" 걸 대체할 수 없어서, 그 정도로 좁고 가벼운
    /// 임계구역은 그냥 lock으로 묶는 게 맞다 — 여기서 lock이 보호하는 작업은 "정수 읽기 + 리스트에
    /// 추가하기"뿐이라 경합 비용이 사실상 없고, 락을 없애서 얻는 이득보다 정합성이 훨씬 중요하다.
    ///
    /// - ScheduleDelay: delayMs 뒤에 action을 그냥 실행한다. 직렬화가 필요하면 호출자가 알아서
    ///   해야 한다 (JHSerializedObject.Reserve가 자기 Post를 호출하는 식으로 쓴다).
    /// - Schedule/ScheduleJob(keys 버전): 여러 key에 걸친 작업(예: 거래처럼 둘 이상의 PlayerKey를
    ///   동시에 건드리는 처리)을 위한 저수준 API로 남겨둔다. key-lock은 key마다 무한정 늘어나는
    ///   딕셔너리가 아니라 (CPU 코어 수 * LocksPerCore)개의 고정 크기 배열로 처음부터 전부
    ///   만들어두고, key는 해시값을 배열 크기로 나눈 나머지(스트라이프)로 슬롯을 찾는다. 서로 다른
    ///   key가 같은 슬롯으로 충돌하면 상관없는 사이인데도 서로 직렬화되는 오탐 대기가 생길 수 있다 —
    ///   lock striping의 정상적인 트레이드오프다.
    ///   데드락 방지: 여러 key를 잠글 때 항상 정렬된 슬롯 인덱스 순서로만 획득하고, 같은 슬롯으로
    ///   충돌하면 한 번만 잠근다 (전역적으로 일관된 획득 순서라 순환 대기가 생길 수 없다 — lock ordering).
    /// </summary>
    public class JHTimingWheel
    {
        public static readonly JHTimingWheel Instance = new JHTimingWheel();

        private const int TickIntervalMs = 10;
        private const int WheelSize = 1024; // 10ms * 1024 = 약 10.24초까지 한 바퀴 안에 스케줄 가능

        // key-lock 슬롯 개수 = CPU 코어 수 * 이 배수. Schedule(keys 버전)에서만 쓰인다.
        private const int LocksPerCore = 4;

        // Stop()의 종료 드레인이 "한 바퀴 돌아도 아무것도 안 나올 때까지" 반복하는 바퀴 수의 상한.
        // 정상적인 호출자는 드레인 도중 재예약을 하지 않으므로 보통 1바퀴로 끝난다 — 이 상한은 버그로
        // 인한 무한 재예약이 Stop()을 영영 못 끝내게 만드는 것만 막는 안전장치다.
        private const int MaxDrainRotations = 8;

        private readonly List<Action>[] m_delaySlots;
        private readonly List<JHTask>[] m_slots;
        private readonly object m_slotLock = new object();
        private int m_currentSlot;
        private volatile bool m_stopRequested;
        private readonly System.Threading.Thread m_tickThread;

        // key마다 새로 만드는 대신, 처음에 고정 개수만큼 전부 만들어두고 key는 해시로 슬롯 인덱스에 매핑한다.
        private readonly KeyLock[] m_keyLocks;

        /// <summary>슬롯 하나에 대응하는 원자적 bool 락. 0 = 비어있음, 1 = 누군가 잡고 있음.</summary>
        private class KeyLock
        {
            public int Locked;
        }

        /// <summary>타이밍 휠에 예약되는 최소 작업 단위 (정렬/중복 제거된 락 슬롯 인덱스들 + 실행할 액션).</summary>
        private class JHTask
        {
            public int[] LockIndices;
            public Action Action;
        }

        private JHTimingWheel()
        {
            m_delaySlots = new List<Action>[WheelSize];
            m_slots = new List<JHTask>[WheelSize];
            for (int i = 0; i < WheelSize; i++)
            {
                m_delaySlots[i] = new List<Action>();
                m_slots[i] = new List<JHTask>();
            }

            int lockCount = Math.Max(1, Environment.ProcessorCount * LocksPerCore);
            m_keyLocks = new KeyLock[lockCount];
            for (int i = 0; i < lockCount; i++) m_keyLocks[i] = new KeyLock();

            // ShopPurchase.Core.Thread 네임스페이스와 이름이 겹쳐서 System.Threading.Thread로 완전히 명시한다.
            m_tickThread = new System.Threading.Thread(TickLoop)
            {
                IsBackground = true,
                Name = "JHTimingWheelTick",
            };
            m_tickThread.Start();
        }

        /// <summary>
        /// delayMs 뒤에 action을 그냥 실행한다. 직렬화(비즈니스 로직 관점)는 호출자 책임이지만,
        /// "어느 슬롯에 넣을지"를 정하는 것 자체는 tick 스레드의 슬롯 전진과 원자적이어야 하므로
        /// m_slotLock을 짧게 잡는다.
        /// </summary>
        public void ScheduleDelay(int _delayMs, Action _action)
        {
            int ticksAhead = ComputeTicksAhead(_delayMs);
            lock (m_slotLock)
            {
                int targetSlot = (m_currentSlot + ticksAhead) % WheelSize;
                m_delaySlots[targetSlot].Add(_action);
            }
        }

        /// <summary>delayMs 뒤에, keys가 매핑되는 락 슬롯을 전부 잠근 상태에서 action을 실행하도록 예약한다.</summary>
        public void Schedule(int _delayMs, GUID[] _keys, Action _action)
        {
            if (_keys == null || _keys.Length == 0)
                throw new ArgumentException("최소 하나 이상의 key가 필요합니다.", nameof(_keys));

            // key가 아니라 "실제로 잠글 슬롯 인덱스" 기준으로 중복 제거 + 정렬한다. 서로 다른 key가
            // 같은 슬롯으로 충돌할 수 있는데, 그걸 그대로 두면 같은 락을 두 번 잠그려다 자기 자신을
            // 기다리며 멈추는 자기 데드락이 생긴다.
            int[] lockIndices = _keys.Select(GetLockIndex).Distinct().OrderBy(_i => _i).ToArray();
            int ticksAhead = ComputeTicksAhead(_delayMs);

            lock (m_slotLock)
            {
                int targetSlot = (m_currentSlot + ticksAhead) % WheelSize;
                m_slots[targetSlot].Add(new JHTask { LockIndices = lockIndices, Action = _action });
            }
        }

        /// <summary>
        /// delayMs 뒤에 work를 실행하고 그 결과를 JHJob으로 돌려준다. work가 예상 못한 예외를 던지면
        /// 여기서 잡아서 에러 로그를 남기고 EErrorCode.Exception으로 reject한다.
        /// </summary>
        public JHJob<T> ScheduleJob<T>(int _delayMs, GUID[] _keys, Func<T> _work)
        {
            var job = new JHJob<T>();
            Schedule(_delayMs, _keys, () =>
            {
                try
                {
                    T result = _work();
                    job.Resolve(result);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[JHTimingWheel] ScheduleJob work에서 처리 안 된 예외: {ex}");
                    job.Reject(EErrorCode.Exception);
                }
            });
            return job;
        }

        private int ComputeTicksAhead(int _delayMs)
        {
            int delayMs = _delayMs < 0 ? 0 : _delayMs;
            int ticksAhead = delayMs / TickIntervalMs;
            if (ticksAhead >= WheelSize) ticksAhead = WheelSize - 1; // 포트폴리오 범위이므로 단일 랩으로 클램프
            return ticksAhead;
        }

        private int GetLockIndex(GUID _key) => (int)((uint)_key.GetHashCode() % (uint)m_keyLocks.Length);

        private void TickLoop()
        {
            while (!m_stopRequested)
            {
                System.Threading.Thread.Sleep(TickIntervalMs);

                var (dueDelays, dueTasks) = DrainCurrentSlot();

                // C# 5부터 foreach 변수는 반복마다 새 스코프라, 클로저 캡처용 임시 변수가 따로 필요 없다.
                foreach (var action in dueDelays)
                {
                    ThreadPool.QueueUserWorkItem(_ => RunDelayAction(action));
                }

                foreach (var task in dueTasks)
                {
                    ThreadPool.QueueUserWorkItem(_ => RunWithKeyLocks(task));
                }
            }

            // Stop() 요청됨 — 더 이상 풀에 던지지 않고, 남아있는 슬롯을 tick 스레드가 직접(동기로)
            // 실행해서 확실하게 다 끝낸다. 실제 시간을 더 기다릴 이유가 없으니 Sleep도 하지 않는다.
            //
            // 한 바퀴(WheelSize번)만 돌면 호출 시점에 예약돼 있던 건 슬롯이 몇 번이든 전부 나오는 게
            // 맞지만, 드레인 중에 실행된 액션이 그 자리에서 다시 ScheduleDelay/Schedule을 호출하면
            // 얘기가 달라진다 — 그 새 예약은 "이번 바퀴에서 이미 지나친 슬롯"에 떨어질 수 있고, 그러면
            // 한 바퀴만 도는 루프는 그 슬롯을 다시 안 보기 때문에 조용히 유실된다. 그래서 "한 바퀴를
            // 다 돌았는데 아무것도 안 나왔다"가 될 때까지 바퀴를 반복한다 — 지금 코드베이스의 어떤
            // 호출자도 드레인 도중 재예약을 하지 않으니 보통 딱 한 바퀴로 끝나지만, 병적으로 계속
            // 재예약하는 경우에 대비해 상한(MaxDrainRotations바퀴)을 둔다.
            for (int rotation = 0; rotation < MaxDrainRotations; rotation++)
            {
                bool foundAny = false;

                for (int i = 0; i < WheelSize; i++)
                {
                    var (dueDelays, dueTasks) = DrainCurrentSlot();
                    if (dueDelays.Count > 0 || dueTasks.Count > 0) foundAny = true;

                    foreach (var action in dueDelays) RunDelayAction(action);
                    foreach (var task in dueTasks) RunWithKeyLocks(task);
                }

                if (!foundAny) break;
            }
        }

        /// <summary>현재 슬롯의 내용물을 통째로 비우고 다음 슬롯으로 전진한다.</summary>
        private (List<Action> DueDelays, List<JHTask> DueTasks) DrainCurrentSlot()
        {
            lock (m_slotLock)
            {
                // "비우기 + 전진"이 한 lock 안에서 원자적이어야 하는 이유는 클래스 상단 주석 참고.
                List<Action> dueDelays = m_delaySlots[m_currentSlot];
                if (dueDelays.Count > 0) m_delaySlots[m_currentSlot] = new List<Action>();
                List<JHTask> dueTasks = m_slots[m_currentSlot];
                if (dueTasks.Count > 0) m_slots[m_currentSlot] = new List<JHTask>();
                m_currentSlot = (m_currentSlot + 1) % WheelSize;
                return (dueDelays, dueTasks);
            }
        }

        private static void RunDelayAction(Action _action)
        {
            try
            {
                _action();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JHTimingWheel] ScheduleDelay 액션에서 처리 안 된 예외: {ex}");
            }
        }

        private void RunWithKeyLocks(JHTask _task)
        {
            // LockIndices가 정렬돼 있으므로, 여기서 순서대로 잠그면 시스템 전체가 항상 같은 순서로 잠그는 셈이 된다.
            foreach (var index in _task.LockIndices)
            {
                AcquireIndex(index);
            }

            try
            {
                _task.Action();
            }
            finally
            {
                // 반납 순서는 데드락 여부와 무관하다 — 획득 순서만 전역적으로 일관되면 된다.
                for (int i = _task.LockIndices.Length - 1; i >= 0; i--)
                {
                    ReleaseIndex(_task.LockIndices[i]);
                }
            }
        }

        private void AcquireIndex(int _index)
        {
            var spinWait = new SpinWait();
            while (Interlocked.CompareExchange(ref m_keyLocks[_index].Locked, 1, 0) != 0)
            {
                spinWait.SpinOnce();
            }
        }

        private void ReleaseIndex(int _index)
        {
            Interlocked.Exchange(ref m_keyLocks[_index].Locked, 0);
        }

        /// <summary>
        /// tick 스레드를 멈춘다. 새 예약을 막는 게 아니라, 멈추기 전에 슬롯에 남아 있던 작업을
        /// ThreadPool에 던지지 않고 tick 스레드가 직접(동기로) 전부 실행한다 — 종료 때문에 예약된
        /// 일이 통째로 사라지는 걸 막기 위해서다. 그 실행이 끝날 때까지 Thread.Join으로 블로킹한다.
        /// </summary>
        public void Stop()
        {
            m_stopRequested = true;
            m_tickThread.Join();
        }
    }
}
