using System;
using ShopPurchase.Core.Thread;

namespace ShopPurchase.HTTP
{
    /// <summary>
    /// 실제 소켓 통신은 하지 않는 HTTP 클라이언트 모킹.
    /// JHTimingWheel로 네트워크 왕복 지연만 흉내내고, 보낸 body를 그대로 응답으로 돌려준다 —
    /// "검증 서버가 영수증 내용을 그대로 확인해준다"고 가정한 것이라, 그 응답을 해석해서 성공 여부와
    /// 상품 ID를 뽑아내는 일은 PlatformVerifierBase.ParseResponse가 맡는다.
    /// 영수증 검증은 플레이어 상태를 건드리지 않는 순수 외부 호출이라 key 기반 직렬화가 필요 없다 —
    /// 그래서 JHTimingWheel.Schedule(keys 버전)이 아니라 순수 시간만 담당하는 ScheduleDelay를 쓴다.
    /// </summary>
    public static class HTTPManager
    {
        public static JHJob<string> Send(string _url, string _body)
        {
            int delay = Random.Shared.Next(50, 200);
            var job = new JHJob<string>();
            JHTimingWheel.Instance.ScheduleDelay(delay, () => job.Resolve(_body));
            return job;
        }
    }
}
