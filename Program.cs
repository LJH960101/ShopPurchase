using ShopPurchase.Core.Thread;
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

            // 데모가 다 끝났으니 tick 스레드를 명시적으로 정리한다. tick 스레드는 IsBackground=true라
            // Stop()을 안 불러도 프로세스 종료 자체는 막지 않지만, 실제 서버라면 다른 서비스를 계속
            // 띄워둔 채로 이 컴포넌트만 골라서 내려야 할 수 있으니 그런 정상 종료 경로를 갖춰둔다.
            JHTimingWheel.Instance.Stop();
        }
    }
}
