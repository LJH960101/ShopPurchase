using System;
using ShopPurchase.Common;
using ShopPurchase.Core;
using ShopPurchase.Core.Thread;

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
    /// 영수증 중복 판정은 여기가 아니라 Player가 들고 있다(Player.TryConsumeReceipt).
    ///
    /// 실패해도 throw하지 않는다 — 실패 분기마다 job.Reject(EErrorCode)를 바로 부르고 return한다.
    /// 정말 예상 못한 예외가 나면 감싸고 있는 catch가 잡아서 로그를 남기고 Exception으로 reject한다.
    /// </summary>
    public class DBManager
    {
        public static readonly DBManager Instance = new DBManager();

        private const double ConnectionFailureRate = 0.05;
        private const double InsertFailureRate = 0.05;
        private const double UpdateFailureRate = 0.05;

        // 가짜 DB row/tran ID 발급용. 실제 DB가 아니라 진짜 GUID 채번이 필요한 건 아니지만,
        // System.Guid 대신 우리가 만든 JHGUIDGenerator로 통일해서 쓴다.
        private static readonly JHGUIDGenerator s_idGenerator = new JHGUIDGenerator(_region: 1, _server: 1);

        private DBManager()
        {
        }

        /// <summary>
        /// 영수증 등록 + 아이템 지급을 하나의 트랜잭션으로 처리하고, 그 결과(InsertShopReceiptResult)를
        /// JHJob으로 돌려준다. 이 결과의 AddItemDBData를 그대로 메모리 반영(Player.ApplyDBItemContext)에
        /// 써야 한다 — 메모리 쪽에서 보상을 다시 계산하면 DB와 메모리가 어긋날 수 있다.
        ///
        /// 지급할 보상(_reward)은 이미 환산된 상태로 받는다 — 여기서 상품 테이블을 다시 조회해
        /// 환산하지 않는다. 무엇을 줄지는 상품 정의(ProductRecord.GetReward)가 정하고, 이 계층은
        /// 그걸 트랜잭션 안에서 확정하는 일만 한다.
        /// </summary>
        public JHJob<InsertShopReceiptResult> InsertShopReceipt(GUID _playerGuid, string _receipt, RewardData _reward)
        {
            int delay = Random.Shared.Next(20, 100);
            var job = new JHJob<InsertShopReceiptResult>();

            JHTimingWheel.Instance.ScheduleDelay(delay, () =>
            {
                try
                {
                    if (Random.Shared.NextDouble() < ConnectionFailureRate)
                    {
                        job.Reject(EErrorCode.DBConnectionFailed);
                        return;
                    }

                    var tran = BeginTran();

                    var (receiptErrorCode, receiptResult) = SP_InsertShopReceipt(tran, _playerGuid, _receipt);
                    if (receiptErrorCode != EErrorCode.Success)
                    {
                        RollbackTran(tran);
                        job.Reject(receiptErrorCode);
                        return;
                    }

                    var (itemErrorCode, rewardResult) = SP_InsertItem(tran, _reward);
                    if (itemErrorCode != EErrorCode.Success)
                    {
                        RollbackTran(tran);
                        job.Reject(itemErrorCode);
                        return;
                    }

                    EndTran(tran);
                    job.Resolve(new InsertShopReceiptResult(receiptResult, rewardResult));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DBManager] InsertShopReceipt에서 처리 안 된 예외: {ex}");
                    job.Reject(EErrorCode.Exception);
                }
            });

            return job;
        }

        private DBTransaction BeginTran() => new DBTransaction(s_idGenerator.Next());

        private void EndTran(DBTransaction _tran)
        {
            // 커밋. 실제 DB로 교체되면 여기서 진짜 COMMIT을 호출하게 된다.
        }

        private void RollbackTran(DBTransaction _tran)
        {
            // 롤백. 실제 DB로 교체되면 여기서 진짜 ROLLBACK을 호출하게 된다.
        }

        private (EErrorCode ErrorCode, ShopReceiptData Value) SP_InsertShopReceipt(DBTransaction _tran, GUID _playerGuid, string _receipt)
        {
            if (Random.Shared.NextDouble() < InsertFailureRate)
                return (EErrorCode.InsertReceiptFailed, null);

            return (EErrorCode.Success, new ShopReceiptData(s_idGenerator.Next(), _playerGuid, _receipt));
        }

        private (EErrorCode ErrorCode, RewardData Value) SP_InsertItem(DBTransaction _tran, RewardData _reward)
        {
            if (Random.Shared.NextDouble() < UpdateFailureRate)
                return (EErrorCode.UpdateItemFailed, null);

            // 실제 SP라면 여기서 인벤토리 테이블에 _reward를 반영하고, 반영된 결과를 돌려준다.
            return (EErrorCode.Success, _reward);
        }
    }
}
