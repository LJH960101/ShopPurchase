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
    /// C#의 Task/async-await 대신 사용하는 자체 Promise 타입.
    /// 실제 실행/지연은 JHTimingWheel이 담당하고, JHJob은 결과 전달과 체이닝(Then/Catch)만 담당한다.
    /// 실패는 Exception이 아니라 EErrorCode로 전달된다 — Catch(_errorCode => ...)처럼 바로 EErrorCode를 받는다.
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

        /// <summary>다음 비동기 단계로 체이닝한다. (HTTPManager / DBManager 호출 연결용)</summary>
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

        /// <summary>값은 그대로 흘려보내면서 부가 처리(로그, Send 등)만 수행하는 단계.</summary>
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
                }
                finally
                {
                    next.Reject(_error);
                }
            });
            return next;
        }
    }
}
