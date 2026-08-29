using ShopPurchase.Core;
using ShopPurchase.Core.Thread;

namespace ShopPurchase.HTTP
{
    /// <summary>
    /// 실제 소켓 통신은 하지 않는 HTTP 클라이언트 모킹.
    /// JHTimingWheel로 네트워크 왕복 지연만 흉내내고, 보낸 body를 그대로 응답으로 돌려준다.
    /// 각 IPlatform 구현이 이 응답을 자신의 성공 코드와 비교해서 검증 결과를 판단한다.
    /// 영수증 검증은 플레이어 상태를 건드리지 않는 순수 외부 호출이라 key 기반 직렬화가 필요 없다 —
    /// 그래서 JHTimingWheel.Schedule(keys 버전)이 아니라 순수 시간만 담당하는 ScheduleDelay를 쓴다.
    /// </summary>
    public static class HTTPManager
    {
        public static JHJob<string> Send(string _url, string _body)
        {
            int delay = JHRandomProvider.GetRandom().Next(50, 200);
            var job = new JHJob<string>();
            JHTimingWheel.Instance.ScheduleDelay(delay, () => job.Resolve(_body));
            return job;
        }
    }
}
