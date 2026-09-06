using System;
using System.Threading.Tasks;
using ShopPurchase.Common;
using ShopPurchase.Core.Thread;

namespace ShopPurchase.HTTP
{
    /// <summary>
    /// 실제 소켓 통신은 하지 않는 HTTP 클라이언트 모킹. 네트워크 왕복 지연만 흉내내고, 보낸 body를
    /// 그대로 응답으로 돌려준다
    ///
    /// 이 클래스는 이 프로젝트에서 Task/async-await가 쓰이는 유일한 자리다. 실제 HttpClient는 Task만
    /// 돌려주므로 외부 드라이버를 감싸는 계층에서는 Task를 피할 수 없다 — 대신 여기서 끝낸다.
    /// 위쪽 코드(Platform/DB/PacketHandler)는 JHJob만 보고, Task도 예외도 이 경계를 넘지 않는다.
    /// 네트워크 지연을 JHTimingWheel이 아니라 Task.Delay로 흉내내는 것도 같은 이유다 — 진짜
    /// HttpClient는 우리 타이밍 휠을 거치지 않고 자기 IO 완료로 돌아온다.
    /// </summary>
    public static class HTTPManager
    {
        private const int NetworkDelayMinMs = 50;
        private const int NetworkDelayMaxMs = 200;

        public static JHJob<string> Send(string _url, string _body)
        {
            var job = new JHJob<string>();
            _ = SendAsync(job, _url, _body);
            return job;
        }

        /// <summary>Task 기반 호출을 await로 기다렸다가, 그 결과를 JHJob으로 옮겨 담는 어댑터.</summary>
        private static async Task SendAsync(JHJob<string> _job, string _url, string _body)
        {
            string response;
            try
            {
                await Task.Delay(Random.Shared.Next(NetworkDelayMinMs, NetworkDelayMaxMs));
                response = _body;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HTTPManager] {_url} 요청 실패: {ex}");
                _job.Reject(EErrorCode.HttpRequestFailed);
                return;
            }

            _job.Resolve(response);
        }
    }
}
