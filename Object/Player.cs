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

        private long m_gold;
        private readonly List<ItemData> m_items = new List<ItemData>();

        public Player(GUID _guid, EPlatform _platform)
            : base(_guid) // Player는 이미 GUID를 갖고 있으니 그걸 직렬화 key로 그대로 쓴다.
        {
            m_guid = _guid;
            m_platform = _platform;
        }

        /// <summary>JHSerializedObject의 직렬화 key로도 그대로 쓰인다.</summary>
        public GUID GetGUID() => m_guid;

        public EPlatform GetPlatformType() => m_platform;

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
        /// 다음 두 경우에 세션을 끊는다.
        /// 1) 서버 쪽 문제(DB 연결/삽입/지급 실패, 미등록 플랫폼, 예상 못한 예외 등) — 재시도해도
        ///    서버 상태가 고쳐지는 게 아니라서, 응답만 돌려주고 계속 진행하기보다 재접속시켜 다시
        ///    시도하게 하는 편이 안전하다.
        /// 2) 정상 클라이언트라면 애초에 나올 수 없는 요청(ReceiptProductMismatch) — 영수증이 가리키는
        ///    상품과 다른 상품을 요청했다는 건 변조 시도로 봐야 하므로, 친절한 실패 응답 대신 끊는다.
        /// (PacketHandler_Shop.Catch에서 ReceiptAlreadyInserted/ReceiptVerifyFailed처럼 정상 플레이도
        /// 자연히 마주칠 수 있는 실패만 화이트리스트로 걸러내고, 나머지는 전부 여기로 온다.)
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
