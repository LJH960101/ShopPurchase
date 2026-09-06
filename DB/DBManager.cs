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
    /// DB 호출은 하나하나가 비동기다. BeginTran/SP_*/EndTran이 각자 자기 왕복 지연과 실패 확률을
    /// 가진 JHJob을 돌려주고, InsertShopReceipt는 그걸 Then으로 이어 붙인다 — 실제 DB 드라이버도
    /// 호출마다 왕복이 생기므로 이쪽이 진짜 모습에 가깝다. 홉 사이에 다른 작업이 끼어들어도
    /// all-or-nothing이 깨지지 않는 건 그걸 DB의 트랜잭션 격리가 보장하기 때문이지, 클라이언트가
    /// 중간에 양보하지 않아서가 아니다.
    ///
    /// 실패는 전부 EErrorCode로 돌아온다. 각 단계는 예외를 던지지 않고 job.Reject로 실패를 알리고,
    /// 어느 단계에서 실패하든 남은 Then은 건너뛰어져 Catch 하나로 모인다 — 롤백은 거기서 한 번만 한다.
    /// 영수증 중복 판정은 여기가 아니라 Player가 들고 있다(Player.TryConsumeReceipt).
    /// </summary>
    public class DBManager
    {
        public static readonly DBManager Instance = new DBManager();

        private const double ConnectionFailureRate = 0.05;
        private const double InsertFailureRate = 0.05;
        private const double UpdateFailureRate = 0.05;

        // DB 왕복 한 번의 지연 범위. 단계마다 따로 걸리므로 홉이 늘면 총 지연도 그만큼 늘어난다.
        private const int CallDelayMinMs = 5;
        private const int CallDelayMaxMs = 30;

        private static readonly JHGUIDGenerator s_idGenerator = new JHGUIDGenerator(_region: 1, _server: 1);

        private DBManager()
        {
        }

        /// <summary>
        /// 영수증 등록 + 아이템 지급을 하나의 트랜잭션으로 처리하고, 그 결과(InsertShopReceiptResult)를
        /// JHJob으로 돌려준다. 이 결과의 AddItemDBData를 그대로 메모리 반영(Player.ApplyDBItemContext)에
        /// 써야 한다 — 메모리 쪽에서 보상을 다시 계산하면 DB와 메모리가 어긋날 수 있다.
        ///
        /// 지급할 보상(_reward)은 이미 환산된 상태로 받는다. 무엇을 줄지는 상품 정의
        /// (ProductRecord.GetReward)가 정하고, 이 계층은 그걸 트랜잭션 안에서 확정하는 일만 한다.
        /// </summary>
        public JHJob<InsertShopReceiptResult> InsertShopReceipt(GUID _playerGuid, string _receipt, RewardData _reward)
        {
            // JHJob은 값 하나만 실어 나르는데 마지막 결과는 여러 단계의 산출물을 합쳐야 한다.
            // 중간 산출물은 이렇게 캡처해두고 마지막 Then에서 조립한다.
            DBTransaction tran = null;
            ShopReceiptData receiptRow = null;
            RewardData grantedReward = null;

            return BeginTran()
                .Then(_tran =>
                {
                    tran = _tran;
                    return SP_InsertShopReceipt(tran, _playerGuid, _receipt);
                })
                .Then(_receiptRow =>
                {
                    receiptRow = _receiptRow;
                    return SP_InsertItem(tran, _reward);
                })
                .Then(_granted =>
                {
                    grantedReward = _granted;
                    return EndTran(tran);
                })
                .Then(_ => JHJob<InsertShopReceiptResult>.Resolved(
                    new InsertShopReceiptResult(receiptRow, grantedReward)))
                .Catch(_errorCode =>
                {
                    // 어느 단계에서 실패했든 여기 한 번만 온다. BeginTran 자체가 실패했다면 되돌릴
                    // 트랜잭션이 아직 없다. Catch는 에러를 소비하지 않고 그대로 흘려보내므로,
                    // 여기서는 롤백만 하고 실패 자체는 호출자까지 그대로 전파된다.
                    if (tran != null) RollbackTran(tran);
                });
        }

        /// <summary>DB 왕복 한 번을 흉내낸다 — 지연 뒤에 action을 실행한다.</summary>
        private static void ScheduleCall(Action _action)
        {
            JHTimingWheel.Instance.ScheduleDelay(Random.Shared.Next(CallDelayMinMs, CallDelayMaxMs), _action);
        }

        private JHJob<DBTransaction> BeginTran()
        {
            var job = new JHJob<DBTransaction>();
            ScheduleCall(() =>
            {
                // 커넥션을 얻지 못하는 경우. 트랜잭션이 아직 없으므로 롤백할 대상도 없다.
                if (Random.Shared.NextDouble() < ConnectionFailureRate)
                {
                    job.Reject(EErrorCode.DBConnectionFailed);
                    return;
                }

                job.Resolve(new DBTransaction(s_idGenerator.Next()));
            });
            return job;
        }

        private JHJob<DBTransaction> EndTran(DBTransaction _tran)
        {
            var job = new JHJob<DBTransaction>();
            ScheduleCall(() =>
            {
                // 커밋. 실제 DB로 교체되면 여기서 진짜 COMMIT을 호출하게 된다.
                job.Resolve(_tran);
            });
            return job;
        }

        /// <summary>
        /// 롤백은 실패 경로에서만 불리고 결과를 기다릴 이유가 없어 동기로 둔다 — 던져만 놓고
        /// 호출자는 원래의 실패 코드를 그대로 위로 올린다.
        /// </summary>
        private void RollbackTran(DBTransaction _tran)
        {
            // 실제 DB로 교체되면 여기서 진짜 ROLLBACK을 호출하게 된다.
        }

        private JHJob<ShopReceiptData> SP_InsertShopReceipt(DBTransaction _tran, GUID _playerGuid, string _receipt)
        {
            var job = new JHJob<ShopReceiptData>();
            ScheduleCall(() =>
            {
                if (Random.Shared.NextDouble() < InsertFailureRate)
                {
                    job.Reject(EErrorCode.InsertReceiptFailed);
                    return;
                }

                job.Resolve(new ShopReceiptData(s_idGenerator.Next(), _playerGuid, _receipt));
            });
            return job;
        }

        private JHJob<RewardData> SP_InsertItem(DBTransaction _tran, RewardData _reward)
        {
            var job = new JHJob<RewardData>();
            ScheduleCall(() =>
            {
                if (Random.Shared.NextDouble() < UpdateFailureRate)
                {
                    job.Reject(EErrorCode.UpdateItemFailed);
                    return;
                }

                // 실제 SP라면 여기서 인벤토리 테이블에 _reward를 반영하고, 반영된 결과를 돌려준다.
                job.Resolve(_reward);
            });
            return job;
        }
    }
}
