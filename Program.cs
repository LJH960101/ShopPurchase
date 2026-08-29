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
        }
    }
}
