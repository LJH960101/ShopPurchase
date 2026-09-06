using ShopPurchase.Test;

namespace ShopPurchase
{
    public static class Program
    {
        public static void Main(string[] _args)
        {
            BuyTest.Run();
            GuidGeneratorTest.Run();
            BulkGrantTest.Run();
            MultiKeyScheduleTest.Run();
            JHSerializedObjectTest.Run();

            // ShutdownDrainTest는 JHTimingWheel을 정지시키므로 반드시 마지막이어야 한다.
            // 뒤에 휠을 쓰는 테스트를 추가하면 그 테스트는 아무것도 실행되지 않는다.
            ShutdownDrainTest.Run();
        }
    }
}
