# Horizon Service Monitor (Windows 11 트레이 모니터)

전세계 **13개 법인**의 **VMware(Omnissa) Horizon 서비스**(커넥션 서버)가 정상 동작하는지
**Horizon REST API**로 주기 점검하고, 법인별 상세 상태를 보여주는
**Windows 11 시스템 트레이 상주 프로그램**입니다.

- **닫기(X)** 를 누르면 종료되지 않고 **시스템 트레이에서 계속 실행**됩니다.
- 트레이 메뉴의 **종료** 를 눌러야만 프로그램이 실제로 끝납니다.
- 트레이 아이콘 색상이 전체 상태(초록=정상 / 노랑=주의 / 빨강=위험)를 실시간 반영하고,
  법인 상태가 바뀌면(정상↔주의↔위험) **풍선 알림**을 띄웁니다.

## 무엇을 모니터링하나

법인(Horizon Pod)마다 커넥션서버 REST API로 다음을 수집합니다.

| 영역 | 내용 |
|---|---|
| 커넥션서버 | 상태(OK/ERROR/NOT_RESPONDING), 버전/빌드, 연결 수, 터널 연결, LDAP 복제 상태, 인증서 만료, 내부 서비스(PCoIP/Blast/보안 게이트웨이 컴포넌트) UP/DOWN, 프로토콜별 세션 수 |
| 게이트웨이(UAG) | 상태(OK/PROBLEM/NOT_CONTACTED/STALE), 유형/버전, 활성·Blast·PCoIP 연결 수 |
| **세션호스트(RDS)** | 상태(OK/WARNING/ERROR/DISABLED), 활성화 여부, **세션 수/최대**, **부하지수(0~100)**, 부하설정, 에이전트 버전, OS |
| 팜/데스크톱 풀 | 팜 상태·호스트 수·앱 수, 풀 헬스(OK/WARNING/ERROR)·활성/프로비저닝·머신 수·문제 머신 수 |
| 세션 | 총/연결/유휴/끊김/대기, 데스크톱/앱 구분, 동시 사용자 수(지원 버전), 온디맨드 세션 목록(사용자·머신·클라이언트 IP·프로토콜·시작 시각) + CSV |
| 머신(VDI) | 문제 상태 머신(ERROR/AGENT_UNREACHABLE/프로비저닝 오류 등) 자동 추출, 전체 목록 온디맨드 조회 |
| 인프라 | 이벤트 DB 연결 상태, AD 도메인 접근성, vCenter 연결 상태·데이터스토어 이상, SAML 인증기 |
| 인증서 | 커넥션서버 인증서 + 접속 URL TLS 인증서 만료 잔여일(기본 30일 이하 경고) |
| 이력 | 법인별 세션 수/상태 시계열(SQLite, 기본 1년), 상태 전이 목록, CSV 내보내기 |

## 상태 판정

| 상태 | 조건 |
|---|---|
| 정상(초록) | 모든 구성요소 정상 |
| 주의(노랑) | 일부 구성요소 이상 — CS 일부/내부 서비스 DOWN, 게이트웨이 이상, 팜/풀 이상, 세션호스트 이상·고부하(기본 90%↑), 이벤트DB/AD/vCenter/SAML 이상, 인증서 임박, 문제 머신 존재, 일부 수집 실패 |
| 위험(빨강) | 로그인/API 미도달 또는 커넥션서버 전멸 |

- 운영자가 의도적으로 **비활성화**한 세션호스트/팜/풀(DISABLED)은 경고로 치지 않습니다.
- 이벤트 DB **NOT_CONFIGURED**(미구성)는 정보로만 표시합니다.

## 커넥션서버에 직접 접속이 안 되는 환경(UAG만 접속 가능)

Horizon REST API(`/rest/...`)는 커넥션서버가 서비스하지만, **UAG가 `/rest` 경로를
프록시하도록 설정하면 UAG 주소만으로 전체 기능이 동작**합니다.

1. UAG 관리 UI 접속: `https://<UAG>:9443/admin`
2. **Edge Service Settings → Horizon Settings → 고급 → Proxy Pattern** 에 `|/rest(.*)` 추가.
   - 예: `(/|/view-client(.*)|/portal(.*)|/appblast(.*))` → `(/|/view-client(.*)|/portal(.*)|/appblast(.*)|/rest(.*))`
3. 본 프로그램 설정에서 법인의 **커넥션서버 URL 자리에 UAG 주소**(예: `https://uag-oc1.corp.com`)를
   입력하고 연결 테스트.

> ⚠️ 보안: 인터넷 노출 UAG에 `/rest`를 열면 관리 API 로그인 표면이 외부에 노출됩니다.
> 가능하면 내부용 UAG에만 적용하거나 방화벽으로 모니터링 PC IP만 허용하고,
> 모니터링 계정은 읽기 전용 관리자 역할로 제한하세요.
> `/rest` 미노출 상태로 접속하면 앱이 "로그인 실패(HTTP 404): /rest 미노출 — Proxy Pattern에
> |/rest(.*) 추가 필요"로 안내합니다.

## 요구 사항

- **Horizon 8 (2006 이상)** — REST API 기준. 신형 엔드포인트(풀 헬스, 세션 메트릭)는
  지원 버전에서 자동 활성화되고, 구버전이면 조용히 생략됩니다(버전 자동 폴백 v3→v2→v1).
- 모니터링 계정: Horizon 관리자 콘솔의 **읽기 전용 관리자** 역할(Root 액세스 그룹) 권장.
  로그인은 `도메인 + 사용자명`(UPN 미지원)이며 비밀번호는 **DPAPI(사용자 단위)로 암호화 저장**됩니다.
- 커넥션서버(또는 LB) 443 접근. 사설/자체 서명 인증서 허용(만료일만 기록).

## 성능 설계(고지연·다수 법인)

- 법인별 **재진입 가드**(이전 수집이 끝나지 않으면 이번 주기 건너뜀) + **동시 수집 제한**(기본 4).
- 모니터 엔드포인트는 병렬 수집 + 법인별 타임아웃(기본 15초, RTT 800ms+ 사이트 고려).
- 무거운 인벤토리(머신/세션)는 **N주기마다 1회**(기본 5) + 페이지 상한. 세션 요약은
  지원 시 경량 메트릭 API(`sessions/metrics`) 사용.
- SQLite WAL, 시계열은 숫자 요약만 매 주기 저장(상세 JSON은 상태 전이/하트비트 시점만),
  보존기간 정리는 스로틀(10분) + `ts` 인덱스 범위 삭제.

## 다운로드(빌드 산출물)

GitHub Actions(`release.yml`)가 windows-latest에서 빌드해 롤링 릴리스 `downloads`에 게시합니다:

- `https://github.com/noainred/horizon_mon/releases/download/downloads/HorizonServiceMonitor.exe`

## 빌드 (Windows, .NET 8 SDK 필요)

단일 실행 파일(.exe, 설치형 런타임 불필요, Win11 x64):

```powershell
# 저장소 루트에서
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
# 산출물: publish\HorizonServiceMonitor.exe
```

또는 `powershell -ExecutionPolicy Bypass -File build.ps1`.

## 최초 사용

1. 실행하면 기본 13개 법인이 **비활성·자리표시자 주소**로 들어 있습니다.
2. **설정(법인 관리)** 에서 각 법인의 **커넥션서버 URL/도메인/계정/비밀번호**를 입력하고
   **연결 테스트** 후 **활성** 체크.
3. 주기(기본 60초)마다 자동 수집되며, 창을 닫아도 트레이에서 계속 동작합니다.
4. 카드/표에서 법인을 **더블클릭**하면 상세(커넥션서버·서비스·게이트웨이·세션호스트·
   풀·팜·세션·머신·인프라·이력)가 열립니다.

- 데이터 위치: `%LOCALAPPDATA%\HorizonServiceMonitor\monitor.db` (SQLite, WAL)
- 시작 옵션: `--hidden`(트레이로 시작), `--db <경로>`(DB 위치 지정)
