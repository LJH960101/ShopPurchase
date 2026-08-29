using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using ShopPurchase.Common;
using ShopPurchase.Core;
using ShopPurchase.Core.Thread;
using ShopPurchase.Data;

namespace ShopPurchase.DB
{
    /// <summary>
    /// 실제 DB 접근 없이, JHTimingWheel을 통해 지연 + 확률적 실패만 흉내내는 더미 구현.
    /// 실제 DB처럼 플레이어 한 명 것이 아니라 서버 전체가 공유하는 리소스라, Player가 아니라
    /// 이 클래스 자체가 전역 싱글턴(Instance)이다 — Player별로 따로 들고 있지 않는다.
    ///
    /// BeginTran ~ EndTran은 하나의 delay(=하나의 지연/실패 확률을 가진 원자적 단위) 안에서 전부
    /// 동기로 순서대로 실행한다. 트랜잭션 도중에 다른 작업이 끼어들 수 없어야 하는데, 각 단계를
    /// 별개의 비동기 단계로 쪼개면 그 사이 틈에 다른 작업이 끼어들 수 있기 때문이다.
    /// 이 안에서 공유 상태를 건드리는 곳(영수증 중복 체크)은 그래서 별도 락 없이도 안전하도록
    /// ConcurrentDictionary 같은 원자적 자료구조로만 처리한다.
    ///
    /// 실패해도 throw하지 않는다 — work는 (EErrorCode, T)를 그대로 반환하면 되고, 그걸 JHJob의
    /// EErrorCode reject로 바꾸는 건 여기서 직접 한다. work가 정말 예상 못한 예외를 던지면 잡아서
    /// 로그를 남기고 EErrorCode.Exception으로 변환한다.
    /// </summary>
    public class DBManager
    {
        public static readonly DBManager Instance = new DBManager();

        private const double m_ConnectionFailureRate = 0.05;
        private const double m_InsertFailureRate = 0.05;
        private const double m_UpdateFailureRate = 0.05;

        // 가짜 DB row/tran ID 발급용. 실제 DB가 아니라 진짜 GUID 채번이 필요한 건 아니지만,
        // System.Guid 대신 우리가 만든 JHGUIDGenerator로 통일해서 쓴다.
        private static readonly JHGUIDGenerator m_idGenerator = new JHGUIDGenerator(_region: 1, _server: 1);

        // 영수증 중복 사용 방지. 여러 스레드가 동시에 건드릴 수 있어 lock-free 자료구조를 쓴다.
        private static readonly ConcurrentDictionary<string, byte> m_consumedReceipts = new ConcurrentDictionary<string, byte>();

        private DBManager()
        {
        }

        /// <summary>
        /// 영수증 등록 + 아이템 지급을 하나의 트랜잭션으로 처리하고, 그 결과(InsertShopReceiptResult)를
        /// JHJob으로 돌려준다. 이 결과의 AddItemDBData를 그대로 메모리 반영(Player.ApplyDBItemContext)에
        /// 써야 한다 — 메모리 쪽에서 보상을 다시 계산하면 DB와 메모리가 어긋날 수 있다.
        /// </summary>
        public JHJob<InsertShopReceiptResult> InsertShopReceipt(GUID _playerGuid, string _receipt, ProductRecord _product)
        {
            int delay = JHRandomProvider.GetRandom().Next(20, 100);
            var job = new JHJob<InsertShopReceiptResult>();

            JHTimingWheel.Instance.ScheduleDelay(delay, () =>
            {
                (EErrorCode errorCode, InsertShopReceiptResult value) result;
                try
                {
                    result = RunTran(_receipt, _product);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DBManager] InsertShopReceipt에서 처리 안 된 예외: {ex}");
                    result = (EErrorCode.Exception, null);
                }

                if (result.errorCode == EErrorCode.Success)
                    job.Resolve(result.value);
                else
                    job.Reject(result.errorCode);
            });

            return job;
        }

        private (EErrorCode ErrorCode, InsertShopReceiptResult Value) RunTran(string _receipt, ProductRecord _product)
        {
            if (!m_consumedReceipts.TryAdd(_receipt, 0))
                return (EErrorCode.ReceiptAlreadyInserted, null);

            if (JHRandomProvider.GetRandom().NextDouble() < m_ConnectionFailureRate)
            {
                m_consumedReceipts.TryRemove(_receipt, out _);
                return (EErrorCode.DBConnectionFailed, null);
            }

            var tran = BeginTran();

            var (receiptErrorCode, receiptResult) = SP_InsertShopReceipt(tran, _receipt);
            if (receiptErrorCode != EErrorCode.Success)
            {
                RollbackTran(tran);
                m_consumedReceipts.TryRemove(_receipt, out _);
                return (receiptErrorCode, null);
            }

            var (itemErrorCode, rewardResult) = SP_InsertItem(tran, _product);
            if (itemErrorCode != EErrorCode.Success)
            {
                RollbackTran(tran);
                m_consumedReceipts.TryRemove(_receipt, out _);
                return (itemErrorCode, null);
            }

            EndTran(tran);

            return (EErrorCode.Success, new InsertShopReceiptResult(receiptResult, rewardResult));
        }

        private DBTransaction BeginTran() => new DBTransaction(m_idGenerator.Next());

        private void EndTran(DBTransaction _tran)
        {
            // 커밋. 실제 DB로 교체되면 여기서 진짜 COMMIT을 호출하게 된다.
        }

        private void RollbackTran(DBTransaction _tran)
        {
            // 롤백. 실제 DB로 교체되면 여기서 진짜 ROLLBACK을 호출하게 된다.
        }

        private (EErrorCode ErrorCode, ShopReceiptData Value) SP_InsertShopReceipt(DBTransaction _tran, string _receipt)
        {
            if (JHRandomProvider.GetRandom().NextDouble() < m_InsertFailureRate)
                return (EErrorCode.InsertReceiptFailed, null);

            return (EErrorCode.Success, new ShopReceiptData(m_idGenerator.Next(), _receipt));
        }

        private (EErrorCode ErrorCode, RewardData Value) SP_InsertItem(DBTransaction _tran, ProductRecord _product)
        {
            if (JHRandomProvider.GetRandom().NextDouble() < m_UpdateFailureRate)
                return (EErrorCode.UpdateItemFailed, null);

            var items = new List<ItemData> { new ItemData(_product.ItemId, _product.ItemCount) };
            var currencies = new List<CurrencyData> { new CurrencyData(ECurrencyType.Gold, _product.GoldReward) };

            return (EErrorCode.Success, new RewardData(items, currencies));
        }
    }
}
