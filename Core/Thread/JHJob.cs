using System;
using System.Collections.Generic;
using ShopPurchase.Common;

namespace ShopPurchase.Core.Thread
{
    internal enum JHJobState
    {
        Pending,
        Fulfilled,
        Rejected,
    }

    /// <summary>
    /// C#의 Task/async-await 대신 이 프로젝트가 쓰는 Promise 타입. 실제 실행/지연은 JHTimingWheel이,
    /// 객체 단위 직렬화는 JHSerializedObject가 담당하고, JHJob은 그 사이를 오가는 결과 전달과
    /// 체이닝(Then/Catch)만 맡는다.
    ///
    /// 실패는 Exception이 아니라 EErrorCode로 전달된다 — Catch(_errorCode => ...)처럼 바로 값으로 받는다.
    /// 잘못된 영수증이나 DB 실패처럼 일상적으로 예상되는 결과를 예외로 던지지 않기 위한 선택이다.
    /// 어느 단계에서 reject되든 남은 Then은 건너뛰어지고 Catch로 바로 간다. Catch는 에러를 소비하지
    /// 않고 그대로 흘려보내므로, 중간에서 정리만 하고 실패는 최종 호출자까지 전파할 수 있다.
    /// </summary>
    public class JHJob<T>
    {
        private readonly object m_lock = new object();
        private JHJobState m_state = JHJobState.Pending;
        private T m_value;
        private EErrorCode m_error;
        private List<Action<T>> m_fulfilledCallbacks = new List<Action<T>>();
        private List<Action<EErrorCode>> m_rejectedCallbacks = new List<Action<EErrorCode>>();

        // Then/Catch가 만든 하류 잡인지. 하류 잡이 누락되는 건 상류가 누락된 결과일 뿐이라,
        // 로그는 원인인 뿌리 잡에서만 남긴다 (안 그러면 사고 한 건에 체인 길이만큼 줄이 찍힌다).
        private bool m_isDerived;

        /// <summary>
        /// settle되지 않은 채 수거된 잡을 실패로 마감한다.
        ///
        /// 진행 중인 잡은 그 잡을 끝낼 코드(예약된 콜백 등)가 클로저로 붙잡고 있어서 수거되지
        /// 않는다. 그러니 여기 걸렸다는 건 "아직 안 끝났다"가 아니라 "끝낼 수 있는 코드가 이미
        /// 사라졌다"는 뜻이고, 앞으로도 영원히 settle될 수 없다는 뜻이다 — 오탐이 없다.
        ///
        /// 로그만 남기지 않고 Reject까지 하는 이유는, 그래야 Catch가 불려서 핸들러가 잡아둔
        /// 자원(영수증 선점 등)이 풀리기 때문이다. 이게 없으면 그 자원은 세션이 끝날 때까지
        /// 잠긴 채로 남는다. 다만 GC 시점에 도는 것이라 언제 불릴지는 보장되지 않는다 —
        /// 정확한 복구가 아니라 최선 노력이고, 진짜 해결은 잡을 만든 쪽이 모든 경로에서
        /// settle시키는 것이다.
        ///
        /// 파이널라이저 밖으로 나간 예외는 어떤 catch로도 못 막고 프로세스를 즉사시키므로,
        /// 여기서는 무슨 일이 있어도 예외를 밖으로 내보내지 않는다. 같은 이유로 이 경로에서
        /// 불리는 Catch 핸들러는 블로킹하면 안 된다 — 파이널라이저 스레드는 프로세스에 하나뿐이라
        /// 거기서 멈추면 모든 객체의 정리가 함께 멈춘다.
        /// </summary>
        ~JHJob()
        {
            try
            {
                if (m_state != JHJobState.Pending) return;

                if (!m_isDerived)
                {
                    Console.WriteLine($"[JHJob] 누락: JHJob<{typeof(T).Name}>이 Resolve/Reject 없이 수거됐습니다. " +
                        "이 타입을 만드는 곳이 모든 경로에서 settle시키는지 확인이 필요합니다.");
                }

                // 하류 잡도 reject는 해야 한다. 파이널라이즈 순서는 보장되지 않아서, 하류가 먼저
                // 수거되면 상류의 전파를 기다릴 수 없기 때문이다. 어느 쪽이 먼저 오든 상태 검사에
                // 걸려 Catch 핸들러는 정확히 한 번만 실행된다.
                Reject(EErrorCode.JobDropped);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JHJob] 파이널라이저에서 처리 안 된 예외: {ex}");
            }
        }

        /// <summary>JHTimingWheel.ScheduleJob이 "본문이 잡을 settle시켰는지"를 확인하는 데 쓴다.</summary>
        internal JHJobState State
        {
            get { lock (m_lock) return m_state; }
        }

        public static JHJob<T> Resolved(T _value)
        {
            var job = new JHJob<T>();
            job.Resolve(_value);
            return job;
        }

        public static JHJob<T> Rejected(EErrorCode _error)
        {
            var job = new JHJob<T>();
            job.Reject(_error);
            return job;
        }

        public void Resolve(T _value)
        {
            List<Action<T>> callbacks;
            lock (m_lock)
            {
                if (m_state != JHJobState.Pending) return;
                m_state = JHJobState.Fulfilled;
                m_value = _value;
                callbacks = m_fulfilledCallbacks;
                m_fulfilledCallbacks = null;
                m_rejectedCallbacks = null;
            }

            // 정상적으로 끝난 잡은 파이널라이저가 할 일이 없다. 여기서 등록을 해제해야
            // finalization 큐를 거치지 않아서, 누락된 잡만 그 비용을 낸다.
            GC.SuppressFinalize(this);

            foreach (var callback in callbacks) callback(_value);
        }

        public void Reject(EErrorCode _error)
        {
            List<Action<EErrorCode>> callbacks;
            lock (m_lock)
            {
                if (m_state != JHJobState.Pending) return;
                m_state = JHJobState.Rejected;
                m_error = _error;
                callbacks = m_rejectedCallbacks;
                m_fulfilledCallbacks = null;
                m_rejectedCallbacks = null;
            }

            GC.SuppressFinalize(this);

            foreach (var callback in callbacks) callback(_error);
        }

        private void OnFulfilled(Action<T> _callback)
        {
            bool invokeNow;
            T value;
            lock (m_lock)
            {
                if (m_state == JHJobState.Pending)
                {
                    m_fulfilledCallbacks.Add(_callback);
                    return;
                }

                invokeNow = m_state == JHJobState.Fulfilled;
                value = m_value;
            }

            if (invokeNow) _callback(value);
        }

        private void OnRejected(Action<EErrorCode> _callback)
        {
            bool invokeNow;
            EErrorCode error;
            lock (m_lock)
            {
                if (m_state == JHJobState.Pending)
                {
                    m_rejectedCallbacks.Add(_callback);
                    return;
                }

                invokeNow = m_state == JHJobState.Rejected;
                error = m_error;
            }

            if (invokeNow) _callback(error);
        }

        public JHJob<TNext> Then<TNext>(Func<T, JHJob<TNext>> _onFulfilled)
        {
            var next = new JHJob<TNext> { m_isDerived = true };
            OnFulfilled(_value =>
            {
                try
                {
                    var innerJob = _onFulfilled(_value);
                    innerJob.OnFulfilled(next.Resolve);
                    innerJob.OnRejected(next.Reject);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[JHJob] Then에서 처리 안 된 예외: {ex}");
                    next.Reject(EErrorCode.Exception);
                }
            });
            OnRejected(next.Reject);
            return next;
        }

        public JHJob<T> Then(Action<T> _onFulfilled)
        {
            var next = new JHJob<T> { m_isDerived = true };
            OnFulfilled(_value =>
            {
                try
                {
                    _onFulfilled(_value);
                    next.Resolve(_value);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[JHJob] Then에서 처리 안 된 예외: {ex}");
                    next.Reject(EErrorCode.Exception);
                }
            });
            OnRejected(next.Reject);
            return next;
        }

        /// <summary>체인 어디에서 실패하든 여기서 EErrorCode 하나로 한 번에 받는다.</summary>
        public JHJob<T> Catch(Action<EErrorCode> _onRejected)
        {
            var next = new JHJob<T> { m_isDerived = true };
            OnFulfilled(next.Resolve);
            OnRejected(_error =>
            {
                try
                {
                    _onRejected(_error);
                    next.Reject(_error);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[JHJob] Catch에서 처리 안 된 예외: {ex}");
                    next.Reject(EErrorCode.Exception);
                }
            });
            return next;
        }
    }
}
