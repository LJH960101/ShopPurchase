# ShopPurchase

게임 서버 스타일의 **인앱 상점 구매 파이프라인**을 C#(.NET 8)으로 구현한 프로젝트입니다.
흐름: **영수증 검증(플랫폼별 전략 + 상품 대조) → DB 트랜잭션(영수증 등록 + 아이템 지급) →
메모리 반영 → 응답.**

## 이 프로젝트가 답하려는 질문

**"내가 서버 구조를 짠다면, 핸들러의 비즈니스 로직을 어떻게 구성할 것인가."**

게임 서버에서 요청 하나는 검증 → DB → 메모리 반영처럼 여러 번의 비동기 왕복을 거칩니다. 그
왕복이 도는 동안 같은 플레이어에게 다른 요청이 또 들어옵니다. 그래서 **병렬성과 직렬화를 동시에**
만족시켜야 합니다 — 서버 전체는 코어 수만큼 병렬로 돌되, 한 플레이어의 상태는 한 번에 한
곳에서만 바뀌어야 합니다.

선택지는 크게 둘이었습니다.

**Tick 기반.** 프레임마다 쌓인 큐를 한 번에 처리합니다. 그 안에서는 사실상 단일 스레드라
**코드 난이도가 확 낮아집니다** — 락을 고민할 일이 거의 없고 실행 순서도 자명합니다. 대신 처리
시간이 tick 예산을 넘는 순간 전체가 밀리고, 무거운 요청 하나가 아무 상관 없는 플레이어까지 같이
세웁니다. **부하에 취약합니다.**

**Actor 기반.** 잠금 단위를 플레이어(액터) 하나로 좁히고 실행은 스레드 풀에 맡깁니다. 한 액터가
느려도 다른 액터는 영향을 받지 않아 부하에 강합니다. 대신 요청 하나가 여러 스레드에 걸쳐 조각나서
**프로파일링이 어려워집니다** — tick처럼 "이번 프레임에 몇 ms" 하고 잘라 볼 수가 없습니다.

**Actor 쪽을 골랐습니다.** 프로파일링 난이도는 추적 ID와 도구로 보완할 수 있지만, tick 예산
초과는 구조를 바꾸지 않는 한 못 피한다고 봤기 때문입니다. 그래서 이 프로젝트는

- **병렬성**을 작업 단위(`JHJob`)를 `ThreadPool`이 실행하는 것으로 얻고,
- **직렬화**를 액터 단위(`JHSerializedObject`)로 잠가서 얻습니다.

액터는 `Player` 하나만을 뜻하지 않습니다. `Zone`, `Monster`, 길드처럼 **"한 번에 하나씩 바뀌어야
하는 것"이면 전부 액터**이고, 각자 자기 큐를 들고 서로 독립적으로 돌아갑니다. 잠금 단위가 곧
경합 단위이므로, 액터를 잘게 쪼갤수록 병렬성이 올라갑니다.

문제는 **둘 이상을 동시에 건드려야 하는 처리**입니다 — 플레이어 간 거래, 존 이동(떠나는 존 +
들어가는 존), 파티 전체에 대한 보상 지급. 하나씩 순서대로 잠그면 A→B와 B→A가 맞물려 데드락이
납니다. 그래서 [`JHTimingWheel.Schedule(keys)`](Core/Thread/JHTimingWheel.cs#L116)로 **여러 키를
한 번에 잠그는 경로**를 따로 뒀습니다. 락은 키마다 무한정 늘어나는 딕셔너리가 아니라 고정 크기
배열(lock striping)이고, 잠글 때는 항상 정렬된 슬롯 인덱스 순서로만 획득합니다 — 획득 순서가
전역적으로 일관되면 순환 대기 자체가 성립하지 않습니다.

이번 구매 흐름은 건드리는 액터가 `Player` 하나뿐이라 이 경로를 쓰지 않습니다. 다중 액터 잠금은
`MultiKeyScheduleTest`에서만 돌려보는 저수준 프리미티브로 남겨뒀습니다.

[`PacketHandler/PacketHandler_Shop.cs`](PacketHandler/PacketHandler_Shop.cs)가 이 구성으로 짠
비즈니스 로직이 실제로 어떻게 읽히는지를 보여주는 예제입니다.

`JHJob`/`JHSerializedObject`/`JHTimingWheel`은 `Task`를 안 쓰려고가 아니라 **액터와 맞물리게
하려고** 직접 만들었습니다. 갈림길은 "액터를 언제 놓느냐"였습니다 — 요청이 끝날 때까지 잡고
있으면 추론은 쉽지만 DB 왕복 내내 그 액터는 아무 요청도 못 받습니다. **동기 블록이 끝나는 순간
놓는 쪽**을 골랐고, 대가로 왕복 사이에 상태가 바뀝니다. `Then`은 액터 밖에서 돌고 상태를
만지려면 `Post`로 다시 들어와야 한다는 규칙이 그 대가를 코드에 드러냅니다 — `await`로 감싸면
바로 그 지점이 사라집니다. 긴 흐름이 `await`보다 읽기 나쁜 건 감수했습니다.

`Task`와 `async`/`await`는 외부 드라이버 경계 한 곳([`HTTP/HTTPManager.cs`](HTTP/HTTPManager.cs))에만
두고, 그 위쪽은 `JHJob`과 `EErrorCode`만 봅니다.

## 핵심만 빠르게 보려면

1. [`JHSerializedObject.Post`](Core/Thread/JHSerializedObject.cs#L52) — 락 없이 "큐 + 드레인 권한
   하나"로 액터를 직렬화하는 방법. 두 번 갈아엎은 과정이 클래스 주석에 남아 있습니다
2. [`JHTimingWheel` 클래스 주석](Core/Thread/JHTimingWheel.cs#L33) — lock-free로 만들었다가
   되돌린 이유
3. [`JHGUIDGenerator.Next()`](Core/JHGUIDGenerator.cs#L75) — Sequence를 왜 wraparound가 아니라
   ms 전환 기준으로 리셋해야 하는지
4. [`DBManager.InsertShopReceipt`](DB/DBManager.cs#L49) — DB 호출 네 번을 `Then`으로 잇고, 실패는
   어디서 나든 `Catch` 한 곳에서 롤백하는 흐름

## 아키텍처

```
PacketHandler_Shop.C2P_RequestShopBuy
  │
  ├─ DataManager.GetProduct(clientProductId)         없는 상품 / 지급할 게 없는 상품이면
  │     └─ ProductRecord.GetReward()                  외부 호출 전에 InvalidParam으로 종료
  │
  ├─ Player.TryConsumeReceipt(receipt)               같은 영수증 재사용이면 여기서 끊는다
  │                                                    (검증 왕복을 시작조차 하지 않음)
  │
  ├─ PlatformManager.Verify(platform, receipt,       전략 패턴, 리플렉션으로 자동 등록
  │                         clientProductId)          + 영수증이 가리키는 상품과 대조
  │     └─ HTTPManager.Send(...)                     흉내낸 네트워크 왕복 (Task → JHJob 어댑터)
  │
  ├─ DBManager.InsertShopReceipt(...)                 비동기 홉 네 번을 Then으로 연결:
  │                                                    BeginTran → 영수증 등록 → 아이템 지급
  │                                                    → EndTran (실패 시 Catch에서 롤백)
  │
  └─ Player.ApplyDBItemContext(...)                   DB가 확정한 보상을 메모리에 반영,
                                                        항상 최신 상태 기준으로 적용되도록
                                                        다시 Post로 감쌈

  (어느 단계에서 실패하든) Catch 하나로 모임 → Player.ReleaseReceipt로 선점을 되돌리고,
  클라이언트 잘못이면 실패 응답, 그 밖이면 Kick
```

| 타입 | 역할 |
|---|---|
| `JHJob<T>` | 커스텀 Promise. `Then`/`Catch` 체이닝, 실패는 `Exception`이 아니라 `EErrorCode`로 전파. |
| `JHSerializedObject` | 액터 기반 클래스(예: `Player`). `Monitor` 락이 아니라 큐 + CAS 드레인 권한으로 직렬화하고, 권한을 딴 스레드 하나가 큐를 끝까지 비운다. |
| `JHTimingWheel` | tick 기반(10ms × 1024슬롯) 지연 스케줄러. 여러 액터를 동시에 잠가야 하는 처리(거래 등)를 위한 다중 key 락 프리미티브(lock striping + 정렬 순서 획득)도 함께 제공. |
| `JHGUIDGenerator` | Snowflake 방식의 64bit ID 생성기(Time/Sequence/Region/Server 비트 패킹), 의도적으로 lock 기반. |

## 읽어볼 만한 설계 결정들

- **액터 직렬화는 두 번 갈아엎고 나서야 지금 모양이 됐습니다.** 처음엔
  `Interlocked.CompareExchange` 재시도 루프 안에서 `Task.ContinueWith`를 투기적으로 호출했는데,
  `ContinueWith`는 호출하는 순간 등록이 확정되는 부작용이 있어 실패하고 버려진 CAS 시도의
  continuation이 살아남아 따로 실행됐습니다(부하 상황에서 실제로 겹쳐 실행됨). 고치고 나서도
  "각 작업의 완료가 다음 작업을 호출"하는 체인 구조가 남아, 완료 콜백이 큐 길이만큼 스택에 쌓이고
  `Post`마다 `TaskCompletionSource`를 할당해야 했습니다. 결국 체인을 버리고 **큐 + 드레인 권한
  하나**로 바꿨습니다 — 재귀가 아니라 반복이라 스택이 늘지 않고, `Post`당 할당이 없으며, 직렬화된
  작업이 스레드를 옮겨 다니지 않고 한 스레드에서 연속 처리됩니다. 겹침·유실 0건은 객체 4개에
  스레드 50개를 몰아 5만 회를 두들기는 테스트로 확인했고, 이 테스트가 위의 겹쳐 실행 버그를
  실제로 잡아냈습니다.

- **`JHTimingWheel`의 슬롯 저장소는 의도적으로 lock-free가 아니라 `List<T>` + lock입니다.**
  `ConcurrentQueue` 기반으로 만들었다가 되돌렸습니다 — "지금 슬롯이 몇 번인지 읽는 것"과 "그
  슬롯을 비우고 다음으로 전진하는 것"이 하나의 원자적 연산이어야 하는데, 그렇지 않으면 프로듀서가
  방금 바꿔치기된 슬롯에 추가해버리고 그 작업은 아무도 다시 보지 않는 고아 큐에서 영구 유실됩니다.
  여기서 lock이 지키는 건 정수 하나 읽고 `List.Add` 하는 작업이라, 없앤다고 얻는 처리량은 없고
  정합성만 잃습니다.

- **끝나지 않은 잡은 수거될 때 실패로 마감합니다.** 잡을 만든 코드가 settle을 빠뜨리면 — 예외로
  죽든 분기 하나를 놓치든 — `Then`도 `Catch`도 불리지 않고 체인이 조용히 멈춥니다. 실패가 실패로
  보고조차 되지 않는, 가장 찾기 힘든 형태입니다. 그런데 **진행 중인 잡은 그걸 끝낼 코드가 붙잡고
  있어서 수거되지 않으므로, "Pending인 채로 수거됐다"는 곧 "영원히 끝날 수 없다"와 같은 말입니다**
  — 오탐이 원리적으로 없습니다. 그래서 파이널라이저에서 `JobDropped`로 reject합니다. 다만 GC
  시점이라 타이밍이 보장되지 않으니 이건 최후의 그물이고, 정면 대응은 **잡을 pending 상태로
  만드는 경로를 `JHTimingWheel.ScheduleJob` 하나로 모은 것**입니다. 호출부는 `new JHJob`도
  `try`/`catch`도 쓰지 않고 "성공이면 `Resolve`, 실패면 `Reject`"만 적으며, 빠뜨린 분기는 본문이
  반환된 직후 동기적으로 잡힙니다.

- **영수증 검증은 "유효한가"와 "무엇에 대한 것인가"를 따로 묻습니다.** 인앱 결제에서 가장 흔한
  구멍은 영수증 자체는 진짜인데 **클라이언트가 보낸 상품 ID를 그대로 믿는 것**입니다 — 싼 상품을
  결제한 진짜 영수증으로 비싼 상품을 받아갈 수 있습니다. 그래서 `IPlatform`은 "이 영수증이 어떤
  상품의 것인가"까지만 답하고, 요청 상품과의 대조는 `PlatformManager.Verify`가 가져갑니다.
  호출자에게 맡기지 않은 이유는, **그 검사를 빠뜨려도 흐름이 멀쩡히 성공하기 때문**입니다 —
  기억해서 해야 하는 검사는 언젠가 빠집니다. 검증을 부르려면 기대 상품 ID를 반드시 같이 넘기게
  만들어서 빠뜨릴 수 없는 자리로 옮겼습니다.

- **보상을 계산하는 곳은 DB 트랜잭션 하나뿐입니다.** `Player.ApplyDBItemContext`는 DB가 확정한
  `RewardData`를 적용만 하고 메모리에서 다시 계산하지 않습니다 — 두 곳에서 계산하면 값이
  어긋나는 순간 어느 쪽이 맞는지 판정할 방법이 없습니다.

## 프로젝트 구조

```
Core/                  JHGUIDGenerator
Core/Thread/           JHJob, JHSerializedObject, JHTimingWheel     ← 이 프로젝트의 핵심
Common/                EErrorCode/EPlatform, 공용 데이터 타입, GUID 타입 별칭
Network/               패킷 정의 (C2P_RequestShopBuy / P2C_ResultShopBuy)
Platform/              IPlatform 전략 + Google/Apple/Steam + 리플렉션 기반 자동 등록
HTTP/                  흉내낸 HTTP 왕복 — Task/async-await가 존재하는 유일한 경계
Data/                  상품 테이블 (더미)
DB/                    DBManager (더미, 트랜잭션 기반)
Object/                Player — 액터
PacketHandler/         PacketHandler_Shop — 전체 흐름을 엮는 지점
Test/                  동시성 스트레스 테스트 + 엔드투엔드 스모크 테스트
```

## 알려진 한계

포트폴리오/데모 목적의 프로젝트이며 프로덕션 코드가 아닙니다:

- `DBManager`와 `HTTPManager`는 더미(랜덤 지연 + 실패율)이고, 실제 DB나 네트워크 호출은 없습니다.
  영수증 검증도 서명 검증이 아니라 `"{플랫폼 토큰}-{상품 ID}"` 형식의 문자열 비교입니다.
- 실제 패킷 직렬화(`IPacket`은 빈 마커 인터페이스)나 소켓 계층은 없습니다.
