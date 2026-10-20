# TODO (TestHarness)

남은 작업만 간결하게 모아둔다. 세팅/실행 절차는 `README.md`, 서버 쪽 남은 작업은
`Server/TODO.md`, Unity 쪽은 `Unity/TODO.md`에 있다.

---

## 4주 계획 (AI가 하네스를 MCP로 쓴다)

방향: AI(Claude Code)가 하네스를 MCP 도구로 직접 쓴다. 하네스 기능은 `HarnessCore`에 두고, 입구는 둘 -
탐색형 디버깅용 MCP(`HarnessDaemon`)와 회귀 테스트용 C# 시나리오(3주차). 인게임은 가짜 서버 없이 진짜 Unity로
본다(에디터를 켜 두고 하네스가 원격으로 조작).

- **1주차 - 데몬과 서버 (완료, PC 확인 대기)**: HarnessDaemon(:9000 에이전트 + :9100 MCP), MCP 직접 구현(도구만,
  공식 SDK로 바꿀 자리는 `HarnessDaemon/Mcp/McpToolAttribute.cs` 주석), 서버 빌드/기동/제어/종료(DevTools 스크립트 +
  결과 파일), 헤드리스 클라 도구, wait_for/events_query/logs_query, MCP 사용 안내.
- **2주차 - 에디터 브리지와 Unity 조작 (코드 완료, PC 확인 대기)**: 연결 코드 분리(`HarnessConnection`), 에디터 브리지
  (`Editor/HarnessEditorBridge.cs` + `editor.*` 명령), 역할 자동 배정(`Unity/Assets/Test/TestRoleSettings.cs`, Tester), Unity
  클라/서버 명령(`client.*`, `ingame.rooms`, `log.recent`)과 관찰 이벤트, `RoomManager` 조회용 속성, 하네스 `account_create`,
  같은 클라 호출 직렬화, 사용 안내 Unity 절. 실제 네트워크 끊김 흉내(`client.disconnect`)는 4주차 장애 주입으로 미룸
  (Unity가 스스로 끊으면 실제 끊김과 다른 경로를 탈 수 있어서 - DevTools `block-port.ps1` 방식이 맞음).
  - 마일스톤: AI가 클라 1개 + 봇 7로 한 판을 끝까지 돌린다.
  - 추가: 테스트 조작(`cheat.*` - 재정비 시간/즉시 종료/배속, 특정 기물 구매+배치, 데미지, 참가자 상태)과 클라 판매
    (`shop.units`/`shop.sell`), `player.health_changed`. 한 판이 너무 길어서 넣음 - 사용 안내 "빨리 한 판 끝내기".
    Unity 쪽 코드는 `Unity/Assets/Scripts/Test/Cheats/`(PC 컴파일 확인 대기 - `Unity/TODO.md` 맨 위).
- **3주차 - 시나리오와 타임라인**:
  - (앞당겨 구현, PC 확인 대기) 시나리오 틀 `TH_*`(HarnessCore/Scenarios) - 목표 그래프를 하네스가 하나씩 내주고 AI가 수행,
    하네스가 기록으로 판정(judge), 보고서는 하네스가 작성. MCP `scenario_list/start`, `objective_begin/report`, `scenario_abort`.
    AI의 모든 도구 호출 기록(`mcp-calls.jsonl`, 타임라인 source=ai). 명령 영역(`CommandAreas.cs`) + `agent_list(query)`.
    시나리오 `full-match`, `cheat-commands`(HarnessScenarios). 샌드박스에서 데몬 + MCP로 진행/거부/중단/보고서, 가짜 기록으로
    cheat-commands 판정·분기까지 확인.
  - 남은 것: 같은 시나리오 그래프를 사람 없이 도는 C# 수행자(목표마다 실행 함수) + 명령줄 실행, 타임라인 병합(서버 로그 +
    이벤트 + Unity 로그, playerId/roomId로 구간 자르기), 아래 "런타임 검증 대기"를 시나리오로.
- **4주차 - 다중 클라, 장애 주입, 실전 투입**: 가상 플레이어 2~3을 클라로, 재접속/탈주 시나리오, 장애 주입 명령,
  실제 버그 하나를 AI가 하네스로 디버깅해 보고 도구/안내 보완.
- 밀리면 자를 순서: 타임라인 축소 → 다중 클라 시나리오 축소.

## PC 확인 항목

**1주차** - Claude Code 연결, 서버 기동/로그인/매칭/종료는 PC에서 확인됨. Unity → 하네스 평문 HTTP/2 접속도 확인됨.
남은 것: `server_build`(서버 실행 중이면 거부되는지 포함), `devtools/*.txt`의 한글(스크립트가 BOM 없는 UTF-8이라
Windows PowerShell 5.1에서 원래부터 깨질 수 있음 - 판정은 결과 파일로 하므로 동작 영향 없음).

**시나리오/영역** - PC에서: 하네스 재빌드·재시작 → Claude Code `/mcp` 다시 연결 → "cheat-commands 시나리오 돌려 봐" →
runs/<실행>/scenario-cheat-commands-*-report.md 확인(판정 불일치/순서 위반이 의도대로 찍히는지). 서버도 다시 빌드할 것
(`agent.status` → `process.status`, 안 하면 agent_list에 unknownAreas로 나온다).

**2주차** - Unity 코드는 여기서 대역(stub)으로 문법만 확인했다. PC에서:
- 컴파일: 특히 `Unity.Multiplayer.PlayMode.CurrentPlayer.Tags` / `IsMainEditor`(6000.3 문서 기준 이름), 에디터 폴더가
  게임 쪽 public 타입(`HarnessConnection`, `HarnessCommandRegistry`, `HarnessLogBuffer`, `HarnessSettings`)을 보는지.
- 에디터 브리지: 메뉴 체크 → `agent_list`에 `UnityEditor`. 코드 한 줄 바꾸고 `editor.refresh` → `editor.ready(reload)`.
  일부러 컴파일 오류 → `editor.compile_failed`와 `editor.compile_errors`. 가상 플레이어에는 안 붙는지.
- `editor.play`/`editor.stop`: 결과를 받은 뒤 Play가 바뀌는지(도메인 리로드로 응답이 끊기지 않는지), 가상 플레이어도 같이 들어가는지.
- 태그: 메인 `Client`, 가상 `Server`로 달고 Play → 버튼 없이 역할 시작, 하네스 이름 `client`/`server`.
- 한 판: `account_create` → `client.login` → `client.join_match` → `client.match_found` → `phase.changed` →
  게임 종료 후 `client.lobby_state(InLobby)`. 서버 쪽 `room.*`, `server.load`, `ingame.rooms`.

## HeadlessClient

- **채팅(WebSocket) 미지원.**
- **전입 티켓 재사용이 Unity와 다름** - Unity 클라는 MatchFound로 받은 티켓을 기억했다가 로그인 서버 재진입이 같은 주소로
  안내하면 다시 낸다(`ClientInstance._ingameTransfer`). HeadlessClient의 `ReconnectViaLoginServerAsync`는 인게임 안내를 받으면
  티켓 없이 `PendingIngameTransfer`를 만든다. 인게임은 이제 Unity 클라가 맡으므로 우선순위 낮음.
- (완료) 코드 리뷰 수정 3건 - 이전 매치 정보 재사용(`LastMatchFound` + 유효 플래그), 서버 쪽 끊김 시 단계 미갱신
  (`ConnectionStage`로 이름 변경 + 서버가 닫으면 갱신), 로비 연결 교체 경합(`DetachLobby`의 원자적 교체).

## 런타임 검증 대기 (코드로만 확인한 수정)

서버/Unity에서 코드로 원인을 확정하고 고쳤지만 실제 실행으로는 아직 확인 못 한 것들
(`Server/TODO.md` "코드 리뷰로 발견해 수정한 항목"). 2주차(진짜 Unity 조작) 이후 3주차에 시나리오로 만들 것.

- 정상 종료를 여러 판 반복해도 게임 에이전트 부하가 쌓이지 않는지(`GameServerStatus` 값 추적),
  수락~응접실 생성 사이에 정원을 넘겨 수락하지 않는지.
- PrepareMatch가 계속 거부될 때 매칭 타임아웃이 처음 대기열에 들어온 시각 기준으로 걸리는지.
- 재접속 가능 방: 유예 안 재접속 → 복귀, 유예 밖 → `PlayerAbandoned` 후 세션 삭제, 이후 재접속은
  `PLAYER_ABANDONED`로 거부. 재접속 불가 방: 끊기면 즉시 소유권 해제.
- 게임 에이전트 승인 조건: `Moving`인데 티켓 없음/틀림 → `INVALID_TRANSFER_TICKET`, 틀린 티켓
  뒤에도 진짜 티켓으로 입장 가능, `Reconnecting`은 티켓 없이 승인, 소유자가 다른 서버면 거부.
- 이동 중 첫 접속 실패 → 로그인 서버가 `NextServer`로 안내 → 받아둔 티켓으로 입장. TTL이 지나면
  세션 삭제 → 로그인 서버가 `NO_ACTIVE_SESSION`.
- 응접실 승격 중(닉네임 조회 대기 중) 접속 완료/끊김/방 종료가 반영되는지, 닉네임 조회가 5초 안에
  안 오거나 계정이 없으면 방이 깨지는지.

## 서버 쪽 보완 (하네스 판정에 필요)

- **`INVALID_CREDENTIALS` 분기 일부에 로그가 없음** - 로그인 서버가 거부 사유를 클라에는 보내지만
  서버 로그에 구체적인 값(어느 계정, 어느 단계)을 남기지 않는 분기가 있다. 하네스는 로그로 원인을 좁히므로
  "무엇이 일어났는지"를 값으로 남기도록 보완할 것.
