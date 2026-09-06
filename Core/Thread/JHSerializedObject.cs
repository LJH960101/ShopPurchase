using System;
using System.Collections.Concurrent;
using System.Threading;

namespace ShopPurchase.Core.Thread
{
    /// <summary>
    /// 직렬화가 필요한 객체(Player 등)가 상속받는 기반 클래스.
    /// 생성자로 받는 key는 로그/식별용 라벨이다 — 실제 직렬화는 key가 아니라 이 객체가 직접 들고 있는
    /// 큐와 드레인 권한으로 이루어지므로, 서로 다른 인스턴스끼리 key가 겹쳐도 문제가 없다.
    ///
    /// 락(Monitor)이 아니라 "큐 + 드레인 권한 하나"로 직렬화한다:
    /// - Post는 작업을 큐에 넣고, 드레인 권한을 CAS로 따낸 스레드 하나만 큐를 비운다.
    /// - 이미 다른 스레드가 드레인 중이면 큐에만 넣고 즉시 돌아온다 — 그 스레드가 이어서 처리해준다.
    /// - 따라서 어느 시점에도 이 객체의 작업을 실행하는 스레드는 정확히 하나다.
    ///
    /// 이 자리는 Task 체인으로 만들었다가 두 번 갈아엎은 결과다.
    /// (1) Interlocked.CompareExchange 재시도 루프 안에서 Task.ContinueWith를 투기적으로 불렀는데,
    ///     ContinueWith는 호출하는 순간 등록이 확정되는 부작용이 있어서 실패하고 버려진 CAS 시도가
    ///     걸어둔 continuation이 살아남아 체인과 무관하게 따로 실행됐다. Interlocked.Exchange로 고쳤다.
    /// (2) 고치고 나서도 "각 작업의 완료가 다음 작업을 호출"하는 체인 구조 자체가 남았다. 그러면
    ///     완료 콜백이 호출 스택에 큐 길이만큼 쌓이고(그래서 RunContinuationsAsynchronously가 필요했다),
    ///     Post마다 TaskCompletionSource를 하나씩 할당해야 했다.
    /// 지금의 드레인 루프는 재귀가 아니라 반복이라 스택이 늘지 않고, Post당 할당도 없다.
    /// 덤으로 직렬화된 작업들이 스레드를 옮겨 다니지 않고 한 스레드에서 연속으로 처리된다.
    ///
    /// - Post: 지금 당장 처리해야 하는 작업.
    /// - Reserve: 지연이 필요한 작업. JHTimingWheel로 delayMs만큼 기다렸다가 Post를 호출한다 —
    ///   즉, 시간 대기는 TimingWheel이, 직렬화는 항상 Post(=이 클래스)가 담당한다.
    /// </summary>
    public abstract class JHSerializedObject
    {
        private readonly GUID m_key;

        private readonly ConcurrentQueue<Action> m_queue = new ConcurrentQueue<Action>();

        // 0 = 아무도 처리하지 않는 중, 1 = 어떤 스레드가 큐를 비우는 중.
        private int m_draining;

        protected JHSerializedObject(GUID _key)
        {
            m_key = _key;
        }

        /// <summary>delayMs 뒤에, JHTimingWheel 타이머로 기다렸다가 Post로 넘겨서 직렬화 실행한다.</summary>
        protected void Reserve(int _delayMs, Action _action)
        {
            JHTimingWheel.Instance.ScheduleDelay(_delayMs, () => Post(_action));
        }

        /// <summary>이 객체에 대해 직렬화된 상태로 action을 처리한다 (규칙은 클래스 주석 참고).</summary>
        public void Post(Action _action)
        {
            m_queue.Enqueue(_action);

            // 이미 다른 스레드가 드레인 중이면 그쪽이 방금 넣은 것까지 처리해준다. 작업 안에서 다시
            // Post를 부르는 경우도 여기로 걸러지므로, 재진입이 스택을 쌓지 않는다.
            if (Interlocked.CompareExchange(ref m_draining, 1, 0) != 0) return;

            do
            {
                while (m_queue.TryDequeue(out var work)) RunWork(work);

                // 큐를 비웠다고 선언한다. 이 직후에 들어온 항목은 넣은 쪽이 권한을 못 딴 채 돌아갔을
                // 수 있으므로, 아래에서 큐를 한 번 더 확인하고 비어있지 않으면 권한을 다시 따낸다.
                Interlocked.Exchange(ref m_draining, 0);
            }
            while (!m_queue.IsEmpty && Interlocked.CompareExchange(ref m_draining, 1, 0) == 0);
        }

        /// <summary>
        /// 여기서 예외를 잡는 건 로그를 남기기 위해서만이 아니다 — 드레인 루프 밖으로 예외가 나가면
        /// m_draining이 1로 걸린 채 남아서, 이 객체는 두 번 다시 아무 작업도 처리하지 못하게 된다.
        /// </summary>
        private void RunWork(Action _work)
        {
            try
            {
                _work();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JHSerializedObject:{m_key}] Post에서 처리 안 된 예외: {ex}");
            }
        }
    }
}
