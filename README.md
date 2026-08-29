# ShopPurchase

`Task`/`async`-`await`에 기대지 않고 처음부터 직접 만든 작은 비동기/동시성 엔진을 설계하고
검증하기 위한 도구로, 게임 서버 스타일의 **인앱 상점 구매 파이프라인**을 C#(.NET 8)으로 구현한
프로젝트입니다.

흐름: **영수증 검증(플랫폼별 전략) → DB 트랜잭션(영수증 등록 + 아이템 지급) → 메모리 반영 →
응답**, 이 전체를 직접 만든 tick 기반 타이밍 휠이 구동합니다.

## 왜 비동기 스택을 처음부터 만들었나

이 프로젝트의 핵심은 상점 기능 자체가 아니라 그 밑에 깔린 인프라입니다 — Promise 스타일 비동기
타입, 타이밍 휠, lock-free 객체 직렬화 프리미티브를 실제(비록 흉내낸) 기능으로 직접 돌려보고,
눈으로 검토하는 대신 동시성 스트레스 테스트로 검증했습니다.

**`Core/`, `Core/Thread/`가 이 프로젝트의 진짜 핵심(동시성 엔진)이고, 나머지(`Network/`,
`Platform/`, `DB/`, `Data/`, `Object/`, `PacketHandler/`)는 그 엔진을 실제로 돌려보기 위한
최소한의 배선입니다.** 완성된 서버를 보여주려는 게 아니라 코드 스타일과 설계 판단을 보여주려는
목적이라, 시간이 없다면 아래 "핵심만 빠르게 보려면"만 봐도 충분합니다.

## 핵심만 빠르게 보려면

시간이 없다면 이 4곳만 봐도 충분합니다:

1. [`Core/Thread/JHSerializedObject.cs`의 `PostCore`(105번째 줄)](Core/Thread/JHSerializedObject.cs#L105) —
   CAS 재시도 루프 + `ContinueWith` 조합이 왜 위험한지, `Interlocked.Exchange`로 어떻게
   해결했는지
2. [`Core/Thread/JHTimingWheel.cs`의 클래스 상단 주석](Core/Thread/JHTimingWheel.cs#L34) —
   lock-free로 만들었다가 되돌린 이유 (정합성 vs 성능 트레이드오프 판단)
3. [`Core/JHGUIDGenerator.cs`의 `Next()`(70번째 줄)](Core/JHGUIDGenerator.cs#L70) —
   Sequence를 왜 wraparound가 아니라 ms 전환 기준으로 리셋해야 하는지
4. [`DB/DBManager.cs`의 `InsertShopReceipt`(50번째 줄)](DB/DBManager.cs#L50) —
   왜 트랜잭션 전체가 "하나의 비동기 콜백"이어야 하는지

**보너스: 테스트가 실제로 버그를 잡은 사례**

- [`Test/JHSerializedObjectTest.cs`](Test/JHSerializedObjectTest.cs) — 위 1번의 CAS +
  `ContinueWith` 버그를 실제로 잡아낸 스트레스 테스트(객체 4개 × 스레드 50개 × 무작위 호출
  5만 회).
- [`Test/MultiKeyScheduleTest.cs`](Test/MultiKeyScheduleTest.cs) — `JHTimingWheel`의 다중 key
  락(lock striping)에서, 진짜 상호 배제를 보장 못 하던 예전의 허술한 "한 번만 실행" 가드를
  잡아낸 테스트. 이 다중 key API는 실제 구매 흐름에서는 안 쓰이고 `BulkGrantTest`에서만
  예시로 쓰이는 저수준 프리미티브다.

## 아키텍처

```
PacketHandler_Shop.C2P_RequestShopBuy
  │
  ├─ PlatformManager.Verify(platform, receipt)      전략 패턴, 리플렉션으로 자동 등록
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
| `JHTimingWheel` | 모든 시뮬레이션 지연을 처리하는 tick 기반(10ms × 1024슬롯) 스케줄러. lock striping을 적용한 저수준 다중 key 락 프리미티브(`Schedule`/`ScheduleJob`)도 함께 제공. |
| `JHSerializedObject` | "한 번에 하나씩, 순서대로" 처리가 필요한 객체(예: `Player`)의 기반 클래스 — `Monitor` 락이 아니라 `Interlocked.Exchange`로 `Task` 체인을 갈아끼우는 lock-free 방식. |
| `JHGUIDGenerator` | Snowflake 방식의 64bit ID 생성기(Time/Sequence/Region/Server 비트 패킹), 의도적으로 lock 기반. |

## 읽어볼 만한 설계 결정들

- **예외 대신 `EErrorCode`.** `JHJob<T>`의 reject 채널은 `EErrorCode` 값을 직접 실어 나릅니다.
  잘못된 영수증, 중복 영수증, DB 실패 같은 것들은 예외적인 상황이 아니라 일상적으로 예상되는
  실패라서, `.Catch(errorCode => ...)`처럼 예외 타입을 검사하지 않고 값 하나로 분기합니다.

- **`JHSerializedObject`는 lock-free지만, 아무렇게나 만든 게 아닙니다.** 초기 버전은
  `Interlocked.CompareExchange` 재시도 루프 안에서 `Task.ContinueWith`를 투기적으로 호출했습니다.
  `ContinueWith`는 호출하는 순간 바로 등록이 확정되는 부작용이 있어서, 실패하고 버려진 CAS
  시도가 걸어둔 continuation이 그대로 살아남아 "진짜" 체인과 무관하게 따로 실행되는 문제(부하
  상황에서 실제로 겹쳐 실행됨)가 있었습니다. `Interlocked.Exchange`(항상 성공하는 무조건적
  스왑이라 재시도 자체가 없음)로 고쳤고, `JHSerializedObjectTest`(객체 4개 × 스레드 50개 ×
  무작위 호출 250회)로 겹침이 0건임을 검증했습니다.

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
Core/                  JHGUIDGenerator, JHRandomProvider
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

## 실행 방법

```bash
dotnet build
dotnet run
```

`Program.cs`가 아래 테스트를 순서대로 전부 실행하고 결과를 콘솔에 출력합니다 — 별도 테스트
프레임워크는 없어서 성공/실패는 콘솔 출력으로 확인합니다.

## 테스트

| 테스트 | 무엇을 증명하는가 |
|---|---|
| `BuyTest` | 엔드투엔드 스모크 테스트: 3개 플랫폼에 걸쳐 5건의 구매 요청(잘못된 영수증, 중복 영수증 포함)을 실행해서 모든 응답 경로(성공 / 검증 실패 / 이미 등록됨 / 강제 종료)를 확인. |
| `GuidGeneratorTest` | "서버" 5개 × 스레드 8개 × 대기 없이 최대 속도로 5000개씩 ID 생성, 충돌 0건 기대. |
| `BulkGrantTest` | 같은 ID 생성기를 두 가지 방식으로 나란히 호출 — tight loop(1024/ms Sequence 예산을 금방 소진)와 `JHTimingWheel` 플레이어별 Job으로 분산하는 방식을 비교해서, "GUID 발급은 느려도 괜찮다"는 설계 판단을 실제 숫자로 보여줌. |
| `MultiKeyScheduleTest` | 무작위 다중 key `Schedule()` 작업 300개로 정확히 한 번만 실행되는지, key를 공유하는 작업끼리 절대 겹치지 않는지 검증(예전에 진짜 상호 배제를 보장 못 하던 "한 번만 실행" 가드의 버그를 잡아낸 테스트). |
| `JHSerializedObjectTest` | 객체 4개 × 스레드 50개 × 무작위 `Post`/`Reserve` 호출 250회(총 5만 회): 겹치는 실행 0건, 유실된 콜백 0건(위에서 설명한 CAS + `ContinueWith` 버그를 잡아낸 테스트). |

## 알려진 한계

포트폴리오/데모 목적의 프로젝트이며 프로덕션 코드가 아닙니다:

- `DBManager`와 `HTTPManager`는 전부 더미(랜덤 지연 + 실패율)이며, 실제 DB나 네트워크 호출은
  전혀 없습니다.
- `DBManager`의 영수증 중복 체크용 집합(`ConcurrentDictionary<string, byte>`)은 무한정
  커집니다 — 실제 구현이라면 DB의 유니크 제약으로 대체될 부분입니다.
- 실제 패킷 직렬화(`IPacket`은 빈 마커 인터페이스)나 실제 소켓 계층은 없습니다.
