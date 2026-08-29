// C++의 typedef처럼, 프로젝트 전역에서 "GUID"라는 이름으로 ulong을 그대로 쓸 수 있게 하는 별칭이다.
// 실제 타입은 여전히 System.UInt64라서 ulong과 완전히 호환되고(변환/캐스팅 불필요), 이 파일 하나만
// 있으면 다른 모든 파일에서 별도 using 없이 바로 GUID를 쓸 수 있다.
global using GUID = System.UInt64;
