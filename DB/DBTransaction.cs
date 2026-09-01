namespace ShopPurchase.DB
{
    /// <summary>
    /// 실제 DB 트랜잭션이 아닌 더미 핸들. 진짜 DB 드라이버로 교체되면 이 자리에 실제 커넥션/트랜잭션
    /// 객체가 들어가고, DBManager.EndTran/RollbackTran이 이 핸들로 커밋/롤백을 호출하게 된다.
    /// </summary>
    public class DBTransaction
    {
        public GUID TranId { get; }

        public DBTransaction(GUID _tranId)
        {
            TranId = _tranId;
        }
    }
}
