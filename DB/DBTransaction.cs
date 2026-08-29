namespace ShopPurchase.DB
{
    /// <summary>실제 DB 트랜잭션이 아닌 더미. 다른 대화에서 실제 구현으로 교체 예정.</summary>
    public class DBTransaction
    {
        public GUID TranId { get; }

        public DBTransaction(GUID _tranId)
        {
            TranId = _tranId;
        }
    }
}
