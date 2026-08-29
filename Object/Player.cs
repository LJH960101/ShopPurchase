using System;
using System.Collections.Generic;
using ShopPurchase.Common;
using ShopPurchase.Core.Thread;
using ShopPurchase.Network;

namespace ShopPurchase.Object
{
    public class Player : JHSerializedObject
    {
        private readonly GUID m_guid;
        private readonly EPlatform m_platform;

        // 서버 메모리에 올라온 인벤토리. DB가 진실이고 이건 그 사본이라, 여기 반영은 항상
        // DB 트랜잭션이 확정한 값을 그대로 가져다 쓴다 — 메모리에서 다시 계산/랜덤을 굴리지 않는다.
        private long m_gold;
        private readonly List<ItemData> m_items = new List<ItemData>();

        public Player(GUID _guid, EPlatform _platform)
            : base(_guid) // Player는 이미 GUID를 갖고 있으니 그걸 직렬화 key로 그대로 쓴다.
        {
            m_guid = _guid;
            m_platform = _platform;
        }

        /// <summary>플레이어 고유 GUID. JHSerializedObject의 직렬화 key로도 그대로 쓰인다.</summary>
        public GUID GetGUID() => m_guid;

        /// <summary>이 플레이어가 구매에 사용 중인 플랫폼.</summary>
        public EPlatform GetPlatformType() => m_platform;

        /// <summary>
        /// DB 트랜잭션이 확정한 보상을 메모리에 반영한다. 반드시 이 객체의 Post 콜백 안에서
        /// 호출돼야 한다 — 그래야 호출되는 시점의 "현재" 메모리 상태를 기준으로 더할 수 있고,
        /// DB 작업이 도는 동안 다른 요청이 먼저 메모리를 바꿔놨어도 유실되지 않는다.
        /// </summary>
        public void ApplyDBItemContext(RewardData _reward)
        {
            foreach (var currency in _reward.Currencies)
            {
                if (currency.CurrencyType == ECurrencyType.Gold)
                    m_gold += currency.Count;
            }

            m_items.AddRange(_reward.Items);
        }

        /// <summary>
        /// 정상적인 업무 실패(EErrorCode)가 아니라 더 이상 신뢰할 수 없는 상태(예: DB는 이미 커밋됐는데
        /// 메모리 반영 중 예상 못한 예외가 난 경우)일 때 세션을 끊는다. 재접속하면 메모리가 DB로부터
        /// 다시 로드되니 어긋난 상태로 계속 진행하는 것보다 안전하다.
        /// </summary>
        public void Kick(EErrorCode _reason)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [Kick -> {m_guid}] reason={_reason}");
        }

        /// <summary>
        /// 실제 네트워크 전송(직렬화 + 소켓 송신) 대신 더미로 콘솔에만 출력한다.
        /// </summary>
        public void Send(IPacket _packet)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [Send -> {m_guid}] {_packet}");
        }
    }
}
