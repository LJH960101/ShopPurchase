using System;
using System.Threading;
using System.Threading.Tasks;
using ShopPurchase.Common;

namespace ShopPurchase.Core.Thread
{
    /// <summary>
    /// 직렬화가 필요한 객체(Player 등)가 상속받는 기반 클래스.
    /// 이 객체가 직렬화에 쓸 key(ulong)는 생성자에서 최초 1회 받아 고정 필드로 들고 있는다 — Post/Reserve를
    /// 부를 때마다 다시 만들어내는 게 아니라, 생성 시점에 이미 정해진 하나의 토큰이다. 아래에서 보듯
    /// 실제 직렬화는 이 key 값 자체가 아니라 이 객체가 직접 들고 있는 m_currentTask로 이루어지므로,
    /// 서로 다른 인스턴스끼리 key가 같은 값일 일이 없다고 가정해도(=key로 뭔가를 조회/색인하지 않으므로)
    /// 문제가 없다 — key는 사실상 로그/식별용 라벨에 가깝다.
    ///
    /// 락(Monitor)이 아니라 이 객체가 직접 들고 있는 Task 참조 하나로 직렬화한다 (lock-free):
    /// - m_currentTask가 null이거나 이미 끝난 Task면 "쉬고 있다"는 뜻 -> 호출한 스레드에서 바로 실행한다.
    /// - m_currentTask가 아직 안 끝난 Task를 가리키고 있으면 "실행 중"이라는 뜻 -> 그 Task 뒤에
    ///   ContinueWith로 이어붙인다. 앞선 작업이 끝나면 자동으로 이어서 실행된다.
    /// - Interlocked.CompareExchange 재시도 루프는 쓰지 않는다 — ContinueWith는 호출하는 순간 이미
    ///   등록이 확정되는 부작용이 있어서, "실패하면 재시도"하는 CAS 루프 안에서 부르면 실패한(버려진)
    ///   시도가 걸어둔 ContinueWith가 사라지지 않고 그대로 살아남아 체인과 무관하게 따로 실행돼버린다.
    ///   대신 Interlocked.Exchange(항상 성공하는 무조건적 스왑, 재시도 자체가 없음)로 m_currentTask를
    ///   갈아끼우고 그 반환값(직전 값)을 기준으로 딱 한 번만 판단한다.
    ///
    /// - Post: 지금 당장 처리해야 하는 작업. 위 규칙 그대로 즉시 실행되거나 체인 뒤에 이어붙는다.
    /// - Reserve: 지연이 필요한 작업. JHTimingWheel의 타이머로 delayMs만큼 기다렸다가, 그 시점에
    ///   Post를 호출한다 — 즉, 시간 대기는 TimingWheel이, 직렬화는 항상 Post(=이 클래스)가 담당한다.
    /// </summary>
    public abstract class JHSerializedObject
    {
        private readonly GUID m_key;

        // null = 쉬고 있음. Interlocked.CompareExchange로만 갈아끼우는 lock-free 체인의 "현재 꼬리".
        private Task m_currentTask;

        protected JHSerializedObject(GUID _key)
        {
            m_key = _key;
        }

        /// <summary>delayMs 뒤에, JHTimingWheel 타이머로 기다렸다가 Post로 넘겨서 직렬화 실행한다.</summary>
        protected void Reserve(int _delayMs, Action _action)
        {
            JHTimingWheel.Instance.ScheduleDelay(_delayMs, () => Post(_action));
        }

        /// <summary>
        /// delayMs 뒤에 work를 실행하고 결과를 JHJob으로 돌려준다. work는 throw하지 않고
        /// (EErrorCode, T)를 반환하면 된다.
        /// </summary>
        protected JHJob<T> Reserve<T>(int _delayMs, Func<(EErrorCode ErrorCode, T Value)> _work)
        {
            var job = new JHJob<T>();
            JHTimingWheel.Instance.ScheduleDelay(_delayMs, () =>
            {
                Post(_work)
                    .Then(job.Resolve)
                    .Catch(job.Reject);
            });
            return job;
        }

        /// <summary>
        /// 이 객체에 대해 직렬화된 상태로 action을 처리한다. 지금 쉬고 있으면 호출한 스레드에서 즉시
        /// 실행하고, 이미 처리 중이면 그 뒤에 이어붙어서 앞선 작업이 끝나는 대로 실행된다.
        /// </summary>
        public void Post(Action _action)
        {
            PostCore(() =>
            {
                try
                {
                    _action();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[JHSerializedObject:{m_key}] Post에서 처리 안 된 예외: {ex}");
                }
            });
        }

        /// <summary>
        /// Post의 결과를 JHJob으로 돌려받는 버전. work는 Reserve&lt;T&gt;와 마찬가지로 throw하지 않고
        /// (EErrorCode, T)를 반환하면 된다.
        /// </summary>
        public JHJob<T> Post<T>(Func<(EErrorCode ErrorCode, T Value)> _work)
        {
            var job = new JHJob<T>();
            PostCore(() =>
            {
                (EErrorCode errorCode, T value) = RunWork(_work);
                if (errorCode == EErrorCode.Success)
                    job.Resolve(value);
                else
                    job.Reject(errorCode);
            });
            return job;
        }

        /// <summary>
        /// wrappedAction을 lock-free로 직렬화해서 실행한다. m_currentTask가 비어있거나(=null)
        /// 이미 끝났으면 그 자리에서 바로 실행하고, 아직 진행 중이면 그 뒤에 이어붙인다.
        /// </summary>
        private void PostCore(Action _wrappedAction)
        {
            // TaskCompletionSource로 "이번 작업을 나타내는 Task"를 미리 만들어둔다 — 아직 시작 여부와
            // 무관하게 m_currentTask에 먼저 꽂아넣을 수 있어야 하기 때문이다(직접 실행할지, 이전 작업
            // 뒤에 이어붙일지는 아래에서 딱 한 번만 결정한다).
            // RunContinuationsAsynchronously가 없으면 SetResult()를 부르는 스레드에서 다음 작업의
            // ContinueWith 콜백이 동기적으로 바로 실행된다 — 같은 객체에 Post/Reserve가 길게 줄서
            // 있으면 그 콜백들이 한 스레드의 호출 스택 위에 재귀적으로 쌓여버릴 수 있다. 이 옵션을
            // 주면 각 콜백이 항상 ThreadPool로 새로 디스패치돼서 스택이 쌓이지 않는다.
            var completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task newTask = completionSource.Task;

            void RunAndComplete()
            {
                try
                {
                    _wrappedAction();
                }
                finally
                {
                    completionSource.SetResult();
                }
            }

            // Exchange는 무조건 성공하는 스왑이라 재시도가 없다 — "실패한 시도가 남기는 부작용" 문제가
            // 아예 생기지 않는다. previous는 이 스왑 직전까지 m_currentTask였던 값을 그대로 돌려준다.
            Task previous = Interlocked.Exchange(ref m_currentTask, newTask);

            if (previous == null || previous.IsCompleted)
            {
                // 쉬고 있었다 -> 호출한 스레드에서 바로 실행한다.
                RunAndComplete();
            }
            else
            {
                // 아직 실행 중이었다 -> 그 뒤에 이어붙인다. previous가 끝나면 자동으로 이어서 실행된다.
                previous.ContinueWith(_ => RunAndComplete());
            }
        }

        /// <summary>
        /// work를 실행해서 (EErrorCode, T)를 그대로 돌려준다 — throw하지 않는다. work가 예상 못한
        /// 예외를 던지면 여기서 잡아서 에러 로그를 남기고 EErrorCode.Exception으로 변환해서 반환한다.
        /// </summary>
        private (EErrorCode ErrorCode, T Value) RunWork<T>(Func<(EErrorCode ErrorCode, T Value)> _work)
        {
            try
            {
                return _work();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JHSerializedObject:{m_key}] work 실행 중 예외 발생: {ex}");
                return (EErrorCode.Exception, default(T));
            }
        }
    }
}
