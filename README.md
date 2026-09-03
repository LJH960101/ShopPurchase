# ShopPurchase

`async`/`await` 대신 직접 만든 작은 비동기/동시성 엔진을 설계하고 검증하기 위한 도구로, 게임
서버 스타일의 **인앱 상점 구매 파이프라인**을 C#(.NET 8)으로 구현한 프로젝트입니다.

흐름: **영수증 검증(플랫폼별 전략 + 상품 대조) → DB 트랜잭션(영수증 등록 + 아이템 지급) →
메모리 반영 → 응답**, 이 전체를 직접 만든 tick 기반 타이밍 휠이 구동합니다.

## 왜 직접 만들었나

**실무 코드라면 당연히 `Task`/`async`-`await`를 씁니다.** 직접 만든 쪽이 더 낫다고 주장하려는
프로젝트가 아닙니다. 목적은 하나입니다 — **코드 스타일과 설계 판단을 보여주는 것.**

그런데 `await` 세 줄로 끝나는 코드에는 보여줄 판단이 남지 않습니다. Promise를 직접 만들면
"실패를 예외로 볼 것인가 값으로 볼 것인가", 스케줄러를 직접 만들면 "정합성과 lock-free 중
무엇을 포기할 것인가", ID 생성기를 직접 만들면 "시계가 뒤로 흐르면 어떻게 할 것인가"에 답을
내려야 하고, 그 답이 코드에 그대로 남습니다. 그리고 그 답이 실제로 맞는지는 눈으로 훑는 대신
동시성 스트레스 테스트로 확인했습니다.

정확히는 `Task`를 안 쓴 게 아니라, **`async`/`await` 상태머신을 쓰지 않고 `Task`를 완료 신호를
담는 원시 자료구조로만** 씁니다(`JHSerializedObject`의 `TaskCompletionSource` + `ContinueWith`,
`JHTimingWheel`의 `ThreadPool` 디스패치). 스케줄링과 직렬화 규칙은 직접 만들되, 그 밑의 스레드
풀까지 새로 만들지는 않았습니다.

**`Core/`, `Core/Thread/`가 이 프로젝트의 진짜 핵심(동시성 엔진)이고, 나머지(`Network/`,
`Platform/`, `DB/`, `Data/`, `Object/`, `PacketHandler/`)는 그 엔진을 실제로 돌려보기 위한
최소한의 배선입니다.**

## 핵심만 빠르게 보려면

시간이 없다면 이 4곳만 봐도 충분합니다:

1. [`Core/Thread/JHSerializedObject.cs`의 `PostCore`(60번째 줄)](Core/Thread/JHSerializedObject.cs#L60) —
   CAS 재시도 루프 + `ContinueWith` 조합이 왜 위험한지, `Interlocked.Exchange`로 어떻게
   해결했는지
2. [`Core/Thread/JHTimingWheel.cs`의 클래스 상단 주석](Core/Thread/JHTimingWheel.cs#L33) —
   lock-free로 만들었다가 되돌린 이유 (정합성 vs 성능 트레이드오프 판단)
3. [`Core/JHGUIDGenerator.cs`의 `Next()`(75번째 줄)](Core/JHGUIDGenerator.cs#L75) —
   Sequence를 왜 wraparound가 아니라 ms 전환 기준으로 리셋해야 하는지
4. [`DB/DBManager.cs`의 `InsertShopReceipt`(56번째 줄)](DB/DBManager.cs#L56) —
   왜 트랜잭션 전체가 "하나의 비동기 콜백"이어야 하는지

**보너스: 테스트가 실제로 버그를 잡은 사례**

- [`Test/JHSerializedObjectTest.cs`](Test/JHSerializedObjectTest.cs) — 위 1번의 CAS +
  `ContinueWith` 버그를 실제로 잡아낸 스트레스 테스트(객체 4개 × 스레드 50개 × 스레드당 250회,
  총 5만 회).
- [`Test/MultiKeyScheduleTest.cs`](Test/MultiKeyScheduleTest.cs) — `JHTimingWheel`의 다중 key
  락(lock striping)에서, 진짜 상호 배제를 보장 못 하던 예전의 허술한 "한 번만 실행" 가드를
  잡아낸 테스트. 이 다중 key API는 실제 구매 흐름에서는 쓰이지 않고 테스트에서만 돌려보는
  저수준 프리미티브입니다.

## 아키텍처

```
PacketHandler_Shop.C2P_RequestShopBuy
  │
  ├─ DataManager.GetProduct(clientProductId)         없는 상품 / 지급할 게 없는 상품이면
  │     └─ ProductRecord.GetReward()                  외부 호출 전에 InvalidParam으로 종료
  │
  ├─ PlatformManager.Verify(platform, receipt,       전략 패턴, 리플렉션으로 자동 등록
  │                         clientProductId)          + 영수증이 가리키는 상품과 대조
  │     └─ HTTPManager.Send(...)                     JHTimingWheel로 흉내낸 네트워크 왕복
  │
  ├─ DBManager.InsertShopReceipt(...)                 하나의 원자적 트랜잭션: 중복 체크 →
  │                                                    BeginTran → 영수증 등록 → 아이템
  │                                                    지급 → EndTran
  │
  └─ Player.ApplyDBItemContext(...)                   DB가 확정한 보상을 메모리에 반영,
                                                        항상 최신 상태 기준으로 적용되도록
                                                        다시 Post로 감쌈
```

핵심 엔진 (`Core/`, `Core/Thread/`):

| 타입 | 역할 |
|---|---|
| `JHJob<T>` | 커스텀 Promise. `Then`/`Catch` 체이닝, 실패는 `Exception`이 아니라 `EErrorCode`로 전파. |
| `JHTimingWheel` | 모든 시뮬레이션 지연을 처리하는 tick 기반(10ms × 1024슬롯) 스케줄러. lock striping을 적용한 저수준 다중 key 락 프리미티브(`Schedule`)도 함께 제공. |
| `JHSerializedObject` | "한 번에 하나씩, 순서대로" 처리가 필요한 객체(예: `Player`)의 기반 클래스 — `Monitor` 락이 아니라 `Interlocked.Exchange`로 `Task` 체인을 갈아끼우는 lock-free 방식. |
| `JHGUIDGenerator` | Snowflake 방식의 64bit ID 생성기(Time/Sequence/Region/Server 비트 패킹), 의도적으로 lock 기반. |

## 읽어볼 만한 설계 결정들

- **예외 대신 `EErrorCode`.** `JHJob<T>`의 reject 채널은 `EErrorCode` 값을 직접 실어 나릅니다.
  잘못된 영수증, 중복 영수증, DB 실패 같은 것들은 예외적인 상황이 아니라 일상적으로 예상되는
  실패라서, `.Catch(errorCode => ...)`처럼 예외 타입을 검사하지 않고 값 하나로 분기합니다.

- **영수증 검증은 "유효한가"와 "무엇에 대한 것인가"를 따로 묻습니다.** 인앱 결제에서 가장 흔한
  구멍은 영수증 자체는 진짜인데 **클라이언트가 보낸 상품 ID를 그대로 믿는 것**입니다 — 싼 상품을
  결제한 진짜 영수증으로 비싼 상품을 받아갈 수 있습니다. 그래서 `IPlatform` 구현체는 "이 영수증은
  어떤 상품의 것인가"(`VerifiedReceipt`)까지만 답하고, 그 상품이 요청한 상품과 맞는지 대조하는
  책임은 `PlatformManager.Verify`가 가져갑니다. 대조를 호출자(`PacketHandler`)에 맡기지 않은
  이유는 단순합니다 — 그 검사를 빠뜨려도 흐름은 아무 일 없다는 듯 성공하기 때문에, "기억해서 해야
  하는 검사"로 두면 언젠가 빠집니다. 검증을 부르려면 기대 상품 ID를 반드시 같이 넘기게 만들어서
  빠뜨릴 수 없는 자리로 옮겼고, 불일치는 정상 클라이언트라면 나올 수 없는 요청이므로 실패 응답이
  아니라 `Kick`으로 처리합니다(`BuyTest`의 마지막 케이스).

- **지급할 보상은 외부 호출 이전에 확정합니다.** 핸들러 첫 줄에서
  `GetProduct(productId)?.GetReward()` 한 줄로 "없는 상품"과 "지급할 게 없는 잘못된 상품 정의"를
  같이 걸러내고, 통과하지 못하면 플랫폼 검증 왕복조차 시작하지 않습니다. 검증을 통과했다는 건
  영수증의 상품과 이 상품이 같다는 뜻이므로(다르면 위에서 끊깁니다), `DBManager`는 상품 테이블을
  다시 조회하지 않고 이미 환산된 `RewardData`만 받아 트랜잭션 안에서 확정합니다 — "무엇을 줄지"는
  상품 정의가, "그걸 확정하는 일"은 DB 계층이 맡습니다.

- **`JHSerializedObject`는 lock-free지만, 아무렇게나 만든 게 아닙니다.** 초기 버전은
  `Interlocked.CompareExchange` 재시도 루프 안에서 `Task.ContinueWith`를 투기적으로 호출했습니다.
  `ContinueWith`는 호출하는 순간 바로 등록이 확정되는 부작용이 있어서, 실패하고 버려진 CAS
  시도가 걸어둔 continuation이 그대로 살아남아 "진짜" 체인과 무관하게 따로 실행되는 문제(부하
  상황에서 실제로 겹쳐 실행됨)가 있었습니다. `Interlocked.Exchange`(항상 성공하는 무조건적
  스왑이라 재시도 자체가 없음)로 고쳤고, `JHSerializedObjectTest`(객체 4개 × 스레드 50개 ×
  스레드당 250회, 총 5만 회)로 겹침이 0건임을 검증했습니다.

- **`JHTimingWheel`의 슬롯 저장소는 의도적으로 lock-free가 아니라 `List<T>` + lock입니다.**
  `ConcurrentQueue` 기반 lock-free 버전을 시도했다가 되돌렸습니다 — "지금 슬롯이 몇 번인지 읽는
  것"과 "그 슬롯을 비우고 다음 슬롯으로 전진하는 것"이 하나의 원자적 연산이어야 하는데, 그렇지
  않으면 프로듀서가 방금 바꿔치기된 슬롯에 그대로 추가해버릴 수 있고, 그 항목은 아무도 다시
  들여다보지 않는 고아 큐 객체 안에 남아 조용히 영구 유실될 수 있습니다(그냥 지연되는 게 아니라).
  여기서 lock이 지키는 건 정수 하나 읽고 `List.Add` 하는 몇 나노초짜리 작업이라, 없앤다고 실질적인
  처리량 이득은 없고 정합성만 잃습니다.

- **DB "트랜잭션"은 비동기 단계들의 체인이 아니라 하나의 원자적 예약 콜백입니다.**
  `DBManager.InsertShopReceipt`는 중복 체크 → `BeginTran` → 영수증 등록 → 아이템 지급 →
  `EndTran`을 하나의 `JHTimingWheel` 지연 콜백 안에서 전부 동기로 실행합니다. 이 단계들이 각자
  따로 비동기 홉이었다면, 홉 사이의 틈에 다른 작업이 끼어들어 원래 all-or-nothing이어야 할
  작업 중간을 침범할 수 있습니다.

- **메모리는 DB의 캐시일 뿐, 절대 두 번째 진실의 원천이 아닙니다.** 보상을 계산하는 곳은 DB
  트랜잭션 하나뿐이고, `Player.ApplyDBItemContext`는 그 트랜잭션이 만들어낸 `RewardData`를
  그대로 적용만 합니다. 이 적용은 인라인이 아니라 새로 `_player.Post(...)`로 감싸서 실행되는데,
  `Post`는 자신의 동기 코드 블록이 끝나는 순간까지만 플레이어의 직렬화 락을 쥐고 있고 그 밑의
  비동기 체인이 도는 시간까지는 붙잡고 있지 않기 때문입니다 — DB 결과가 돌아올 때쯤엔 이미 다른
  무언가가 이 플레이어의 메모리를 건드렸을 수 있어서, 보상은 항상 그 실행 시점의 "현재" 상태를
  기준으로 더해져야 합니다.

- **`DBManager`는 각 `Player`가 소유하는 게 아니라 전역 싱글턴입니다.** 실제 DB는 플레이어
  개인이 아니라 서버 전체가 공유하는 자원이고, 애초에 플레이어별 직렬화가 필요한 지점도 없습니다
  — 영수증 중복 체크는 그 자체로 이미 원자적이고(`ConcurrentDictionary`), 트랜잭션 하나하나도
  이미 원자적 작업 단위이기 때문입니다.

## 프로젝트 구조

```
Core/                  JHGUIDGenerator
Core/Thread/           JHJob, JHTimingWheel, JHSerializedObject
Common/                EErrorCode/EPlatform/ECurrencyType, 공용 데이터 타입, GUID 타입 별칭
Network/               패킷 정의 (C2P_RequestShopBuy / P2C_ResultShopBuy)
Platform/              IPlatform 전략 + Google/Apple/Steam + 리플렉션 기반 자동 등록
HTTP/                  흉내낸 HTTP 왕복
Data/                  상품 테이블 (더미)
DB/                    DBManager (더미, 트랜잭션 기반)
Object/                Player
PacketHandler/         PacketHandler_Shop — 전체 흐름을 엮는 지점
Test/                  스트레스 테스트 + 엔드투엔드 스모크 테스트 (아래 참고)
```

## 코드 컨벤션

C# 표준(`_camelCase` 필드, 프로퍼티) 대신 C++/언리얼 계열 게임 서버 컨벤션을 따랐습니다.
다만 접두사가 실제 의미와 어긋나지 않도록 세 가지로 갈라 씁니다.

| 대상 | 표기 | 예 |
|---|---|---|
| 상수(`const`) | 접두사 없이 PascalCase | `WheelSize`, `MaxDrainRotations` |
| `static` 필드 | `s_camelCase` | `s_idGenerator`, `s_consumedReceipts` |
| 인스턴스 필드 | `m_camelCase` | `m_currentSlot`, `m_slotLock` |
| 메서드 파라미터 | `_camelCase` | `_delayMs`, `_keys` |

`m_`은 "멤버 필드"라는 뜻이라 컴파일 타임 상수나 static에 붙이면 접두사가 거짓말을 하게 됩니다.
그래서 상수는 접두사를 떼고, static은 `s_`로 구분했습니다.

## 실행 방법

```bash
dotnet build
dotnet run
```

`Program.cs`가 아래 테스트를 순서대로 전부 실행합니다. 별도 테스트 프레임워크는 쓰지 않고,
각 테스트가 스스로 판정해서 `PASS` 또는 `FAIL` 한 줄을 출력합니다 — 숫자를 읽는 사람이 해석해야
하는 자리는 없습니다. `BuyTest`만은 판정 대상이 아니라 전체 흐름을 눈으로 보는 데모입니다.

## 테스트

| 테스트 | 무엇을 증명하는가 |
|---|---|
| `BuyTest` | 엔드투엔드 스모크 테스트: 3개 플랫폼에 걸쳐 6건의 구매 요청(정상 3건 + 중복 영수증 + 위조 영수증 + 상품 변조)을 실행해서 성공/검증 실패/이미 등록됨/상품 불일치 경로를 확인. 마지막 케이스(싼 상품 영수증으로 비싼 상품 요청)는 매 실행마다 반드시 `Kick`으로 끊깁니다. DB 실패로 인한 `Kick`은 실패율(단계별 5%)이 걸려야 나오는 확률적 경로라 별개입니다. |
| `GuidGeneratorTest` | "서버" 5개 × 스레드 8개 × 대기 없이 최대 속도로 5000개씩 ID 생성, 충돌 0건 기대. |
| `BulkGrantTest` | 호출 패턴에 따라 Sequence 대기가 어떻게 달라지는지 실측 — 한 스레드 tight loop와 `JHTimingWheel` 유저별 Job 분산을 같은 생성기로 나란히 돌립니다. 중복은 양쪽 다 0건이며, 확인하려는 건 "정확성은 어떤 패턴에서도 지켜지고 대가는 충돌이 아니라 대기 시간으로 나타난다"는 성질입니다. |
| `MultiKeyScheduleTest` | 두 단계로 검증: (1) 다중 key 작업 하나가 정확히 한 번만 실행되는지, (2) 무작위 다중 key 작업 300개로 key를 공유하는 작업끼리 절대 겹치지 않는지(예전에 진짜 상호 배제를 보장 못 하던 "한 번만 실행" 가드의 버그를 잡아낸 테스트). |
| `JHSerializedObjectTest` | 객체 4개 × 스레드 50개 × 스레드당 `Post`/`Reserve` 무작위 250회(총 5만 회): 겹치는 실행 0건, 유실된 콜백 0건(위에서 설명한 CAS + `ContinueWith` 버그를 잡아낸 테스트). |

### 실행 예시

`dotnet run`을 실제로 돌렸을 때 나오는 출력 일부(발췌, 타임스탬프/GUID 일부 생략):

```
=== BuyTest: 상점 구매 6회 실행 ===
[5] Request: player=...806852609, platform=Steam, receipt=3333-1002, productId=1004 (싼 상품 영수증으로 비싼 상품 요청 -> ReceiptProductMismatch + Kick)
[Send -> ...806836225] P2C_ResultShopBuy(ErrorCode=ReceiptVerifyFailed, Item=[null])
[Kick -> ...806852609] reason=ReceiptProductMismatch
[Send -> ...806819841] P2C_ResultShopBuy(ErrorCode=Success, Item=[Items=[ItemId=1000, Count=1], Currencies=[Gold=1000]])
[Send -> ...689346561] P2C_ResultShopBuy(ErrorCode=ReceiptAlreadyInserted, Item=[null])
[Kick -> ...806803457] reason=UpdateItemFailed
=== BuyTest 완료 ===

=== GuidGeneratorTest: JHGUIDGenerator (서버 x 스레드 최대 속도) ===
생성 개수: 200000, 유일 개수: 200000, 중복 개수: 0 (0.00%)
PASS: 서버 5개 × 스레드 8개 × 5000개 생성, 충돌 0건

=== BulkGrantTest: 한 스레드에서 tight loop로 직접 호출 ===
생성 개수: 30000, 유일 개수: 30000, 중복 개수: 0 (0.00%)
실제 경과 시간: 406.90ms, 실제로 쓰인 서로 다른 Time 값 개수: 30
PASS: 30000개 생성, 충돌 0건
=== BulkGrantTest: JHTimingWheel로 유저별 Job 분산 ===
생성 개수: 30000, 유일 개수: 30000, 중복 개수: 0 (0.00%)
실제 경과 시간: 543.25ms, 실제로 쓰인 서로 다른 Time 값 개수: 40
PASS: 30000개 생성, 충돌 0건

=== MultiKeyScheduleTest: 다중 key 단일 실행 검증 ===
PASS: 다중 key 작업이 정확히 한 번 실행됨

=== MultiKeyScheduleTest: 겹치는 key 동시 실행 방지 검증 ===
PASS: 300개 작업 모두 겹치는 key끼리 동시 실행되지 않음

=== JHSerializedObjectTest: Post/Reserve lock-free 직렬화 극한 검증 ===
총 요청: 50000, 총 완료: 50000, 경과: 61ms
PASS: 극한 경합 상황에서도 직렬화 유지, 콜백 유실 없음
```

`BulkGrantTest`의 두 숫자(407ms vs 543ms)는 어느 쪽이 낫다는 뜻이 아닙니다. 스케줄러를 거치는
쪽이 tick 지연과 `ThreadPool` 디스패치 비용 때문에 오히려 더 걸립니다. 여기서 볼 것은 **중복이
양쪽 다 0건**이라는 점입니다 — 30,000개를 한 스레드에서 30개 ms에 몰아넣든, 여러 스레드에 걸쳐
40개 ms로 퍼뜨리든 생성기는 유일성을 지킵니다. lock 기반으로 만든 대가는 충돌이 아니라 대기
시간으로 나타나고, 그래서 "GUID 발급은 느려도 된다"는 판단이 성립합니다.

## 알려진 한계

포트폴리오/데모 목적의 프로젝트이며 프로덕션 코드가 아닙니다:

- `DBManager`와 `HTTPManager`는 전부 더미(랜덤 지연 + 실패율)이며, 실제 DB나 네트워크 호출은
  전혀 없습니다.
- 영수증 검증도 진짜 서명 검증이 아니라 `"{플랫폼 토큰}-{상품 ID}"` 형식의 문자열 비교입니다.
  실제 구현이라면 이 자리에 Google Play Developer API / App Store Server API 호출과 서명 검증이
  들어가지만, "영수증에서 상품 ID를 읽어 요청 상품과 대조한다"는 흐름은 동일합니다.
- `DBManager`의 영수증 중복 체크용 집합(`ConcurrentDictionary<string, byte>`)은 무한정
  커집니다 — 실제 구현이라면 DB의 유니크 제약으로 대체될 부분입니다.
- 실제 패킷 직렬화(`IPacket`은 빈 마커 인터페이스)나 실제 소켓 계층은 없습니다.
