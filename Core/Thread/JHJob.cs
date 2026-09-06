using System;
using System.Collections.Generic;
using ShopPurchase.Common;

namespace ShopPurchase.Core.Thread
{
    public enum JHJobState
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

        public JHJobState State
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
            var next = new JHJob<TNext>();
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
            var next = new JHJob<T>();
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
            var next = new JHJob<T>();
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
