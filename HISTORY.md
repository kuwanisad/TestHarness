# HISTORY (TestHarness)

끝난 일과 PC 확인 기록 - 2026-10-24에 TODO.md에서 그대로 옮겼다(내용은 고치지 않음). 남은 일은 `TODO.md`.
옮긴 시점 기준 PC 확인 상태: 노드 그래프, 지속 조작, fault(gRPC), net.*, multi-client, HarnessBoot/play_start, Unity 로그 기록, reconnect,
레플리케이션 트리, 대기열 슬롯, pending → call_continue, server_build, Docker 컨테이너 제거(stopInfra)는 PC 실행으로 확인됨.
editor.compile_failed와 시나리오 밖 지속 조작의 agent_gone은 확인 안 됨(TODO.md "PC 확인 안 된 것").

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
  - (구현, PC 확인 대기) 타임라인 병합: Unity 로그를 실행 폴더에 남김(proto AgentLog → logs/<에이전트>.log), 시나리오 보고서가
    목표 구간마다 이벤트 + 서버/Unity 로그를 trace/ 파일로 남기고 실패 목표엔 요약을 실음(RunTrace). AI용 구간 조회 도구는 보류.
  - (구현, PC 확인 대기) 노드 그래프 1단계: 시나리오 = 노드 그래프 + 맥락. 노드(행동/묶음 - 후위순회, From/To 상태)는 하네스가 실행·판정하고
    AI는 경로만 고른다(node_run / node_judge / scenario_finish). 환경 변화(테스트 대상 변화 + 하네스 밖 빌드) 시 이전 결과는 이전 환경,
    AI 지시 없음(IdleTimeout 2분) 시 하네스가 마무리, 자동 실행(mode=auto, 명령줄 run). 기존 목표 방식(objective_begin/report/scenario_abort) 제거.
    샌드박스: 가짜 도구 층으로 AI/auto 모드, 거부, 밖의 빌드 감지, AI 판정, IdleTimeout 확인 + MCP/명령줄 입구 확인.
  - 2단계(4주차와 함께): 커버리지 기반 기본 경로, 지난 경로 그대로 재실행, AI가 경로를 고를 때 볼 노드별 커버리지/최근 실패, 다중 클라 노드.
  - 남은 것: 아래 "런타임 검증 대기"를 노드/시나리오로.
- **4주차 - 다중 클라, 장애 주입, 실전 투입**: 가상 플레이어 2~3을 클라로, 재접속/탈주 시나리오, 장애 주입 명령,
  실제 버그 하나를 AI가 하네스로 디버깅해 보고 도구/안내 보완.
  - (구현, PC 확인 대기) 대상 `TH_Target`(HarnessCore) + 게임 쪽 대상 `TH_UnityClient`/`TH_HeadlessClient`/`TH_UnityServer`/`TH_UnityEditor`
    (HarnessScenarios/Targets), 노드 함수(`Login@client2`), 칸별 상태(`TH_StateMap`), 대상별 값(`Values.For`), cheat는 클라의 방(roomId)으로.
    MPPM 태그 Server + Client1, Client2, … (하네스 이름 client1, client2). full-match/cheat-commands를 client1로 옮기고 multi-client 추가
    (Unity 2 + 헤드리스 1 - 헤드리스는 로그인까지). 샌드박스(nodesim): 칸별 거부 사유, 묶음 중간 실패 시 통과한 칸 반영, 클라별 방,
    자동 실행 3개, 선언 안 한 대상/이름 규칙 오류 확인.
    - 같은 방 시험(사람 둘 중 재접속/탈주)은 매칭 인원 문제(게임 모드 설정 논의) 뒤로.
  - (논의 중) 구현 전에 "테스트 코드 분리" 기준(CLAUDE.md)을 먼저 통과시킨다:
    - (구현, PC 확인 대기) 서버 하네스 주입: 서버 csproj/Program.cs에서 하네스 참조 제거, 하네스 모드에서만 DevTools가 시작 훅
      (`DOTNET_STARTUP_HOOKS`) + 호스팅 시작(`ASPNETCORE_HOSTINGSTARTUPASSEMBLIES`)으로 주입. 서버별 명령은 `Shared/Test/<서버>.Harness`
      (GameAgent.Harness), 출력은 `Shared/Test/bin/`. 샌드박스: 서버 8개가 하네스 없이 컴파일, PC 빌드 GameAgent에 주입해 에이전트 접속 +
      `process.status`(서버 DI 사용) 실행 확인, 훅 없이/아무것도 없이는 하네스 없음 확인.
    - `DebugManager`가 게임 코드에서 불리는 문제.
    - 게임 모드 설정 바꾸기(인원 등): 지금 구조로는 게임 코드 변경 필요 - 다른 테스트 도구들의 해결 방식 조사 후 논의.
    - 특정 클라 무시(장애 주입): 별도 도구, 지속 조작의 수명 관리(공통 틀) 논의.
  - (구현, PC 확인 대기) 지속 조작 틀: `OverrideManager`(도구 호출 기록 감시 - 전용 도구 없음) + `OverrideKinds`(time_scale,
    planning_setting). 수명: 노드 끝(주인이 아닌 조작은 노드 직전 값으로) / 시나리오 끝 / 대상 끊김 / 방 닫힘. harness_status overrides,
    타임라인 override.*, 보고서 절. 노드의 finally 되돌리기와 정리 노드의 배속 1 제거. 샌드박스(nodesim): 노드 끝 되돌림, AI 배속 위에
    노드 배속 → 노드 끝 AI 값으로 → 시나리오 끝 1로, 방 닫힘 scope_gone, 시나리오 없이 건 것은 Play 정지에 agent_gone.
  - (합의) 동작 변경 테스트 = 로컬은 하네스가 시점을 유발 + 기존 명령, 경계는 가로채기(CLAUDE.md "테스트 코드 분리"). 게임 코드 훅 없음.
    - 경계 장애 규칙 `fault_add(at, method, dir, target, match, action, times|seconds)` - 동작 rewrite/drop/delay/fail, 기록
      `fault.hit`/`fault.ended`/`fault.unused`, 메서드 찾기 `fault.methods`, 수명은 지속 조작 공통 틀(TH_Override).
    - (구현, PC 확인 대기) gRPC: Harness.Agent/Fault - 인터셉터(PostConfigure로 서버 인터셉터 안쪽, 단일/서버 스트림/클라 스트림/양방향),
      명령 fault.methods/add/remove/list(메서드·경로 걸기 전 확인), 이벤트 fault.hit/ended, 하네스 지속 조작 종류 fault(되돌리기 fault.remove,
      걸린 횟수, override.unused), 영역 fault, 노드용 대상 TH_Servers. 샌드박스: 로비 계약(lobby.proto)을 쓰는 가짜 로비에 진짜 에이전트를
      시작 훅으로 주입해 MCP로 확인 - MatchFound 주소 고치기(p1만, 1회), 요청 버리기(DeadlineExceeded, 3초 만료), 응답 실패(Internal),
      스트림 메시지 하나만 버리기, 오타 경로 거부, times/expired/removed 끝, unused 경고.
    - NGO(2.11.0, UTP 2.6.0): 테스트 코드 전송 래퍼 - 클라 단위 버리기/늦추기 + 머리를 읽어 RPC 단위로 고르기(NGO 내부 전송 형식
      의존 - 버전 오르면 다시 확인). 테스트 코드만 수정.
    - 받는 쪽의 거부 검증(예: 다른 방 접속)은 장애 규칙 없이 헤드리스 클라가 직접 잘못된 입력을 보낸다.
    - 지난번 `block-port.ps1`이 안 막힌 원인 추정: Windows 방화벽은 루프백(127.0.0.1)을 걸러내지 않음 + 서버 포트 기준이라 클라 단위 불가.
- 밀리면 자를 순서: 타임라인 축소 → 다중 클라 시나리오 축소.

## PC 확인 항목

**10-23 2차 실행(scenario-check)** - 인터셉터 수정 뒤 로그인까지 정상. EnterGame에서 매칭 시간 초과 - 가상 플레이어 server가 클라처럼 시작함
(Tester 로그 없음 → 에디터에 열린 씬이 TestScene이 아니었던 것으로 추정). **3차 실행(scenario-check2)**: playModeStartScene은 메인 에디터에만 적용되고
가상 플레이어는 Login으로 시작함(test.role startScene=Login). **4차 실행(094627)**: editor.play(scene)으로 Play 0.5초 전에 TestScene을 열었더니 메인 에디터만 TestScene, 가상 플레이어는 이전 씬 Login →
가상 플레이어는 메인 에디터에 "미리" 열려 있던 씬만 따름. **5차 실행(095808)**: TestScene을 미리 열어 두니 세 플레이어 모두 TestScene, full-match
전 노드 통과. (구현, PC 확인 대기) 빈 출발 씬 HarnessBoot(`Unity/Assets/Test/HarnessBoot.unity` - 이유는 Unity/ARCHITECTURE.md "HarnessBoot 씬")
+ 시나리오 시작 때 대상 준비 단계(TH_Target.PrepareAsync)로 에디터가 editor.open_scene(HarnessBoot) + UnityPlay가 editor.play → 출발 씬 확인
(test.role startScene) → 플레이어마다 scene.load(시나리오가 선언한 시작 씬 Login/ServerBoot) → 역할 시작 확인 + 시나리오 끝에 지속 조작 scene이
editor.restore_scenes(Play 중이면 Play 끝에). ⚠ 하네스 씬 이름(TH_Scenes) = Unity 씬 파일 이름. **6차(144105)**: 사람이 HarnessBoot를 열어 둠 → 세 플레이어 HarnessBoot,
전 노드 통과(HarnessBoot는 Scene List에 없었음). **7차(153154)**: 하네스가 코드로 HarnessBoot를 연 지 33초 뒤 Play → 가상 플레이어는 Login →
코드로 연 씬은 가상 플레이어가 따라오지 않음(사람이 연 씬은 따라옴) + 씬 조작 기록이 Play 진입 리로드의 브리지 끊김(harness.detached)에
agent_gone으로 끝나 되돌리지 못함. (구현, PC 확인 대기) 씬 열기 대신 Play 시작 씬: editor.set_play_start가 공유 파일에 적고 모든 에디터의
HarnessPlayStartSync가 자기 playModeStartScene을 맞춤(열린 씬은 안 바꿈, open_scene/restore_scenes 삭제) + 지속 조작 play_start(EndsWithAgent=false).
PC에서 볼 것: 가상 플레이어(server, client2)가 HarnessBoot에서 시작하는지, 끝난 뒤 editor.status의 playStartScene이 null로 돌아오는지. Docker: servers_stop 기본값을 컨테이너 제거(stopInfra=true)로 바꾸고 결과에 Docker=removed(포트 확인),
정리 판정이 이를 확인 - PC 확인 필요.

**7번(실제 버그) 후보 - 10-23 net.* 1차 실행(171124)에서 드러남** (가상 플레이어 server가 약 54초 멈춤 → GameAgent 하트비트 타임아웃 → server가
깨어나 클라를 내보내고 스스로 종료하는 과정에서):
- 죽어 가는 서버로 재진입 안내: LoginServer가 08:13:21.937에 "재진입 성공 - 127.0.0.1:7777"로 안내했는데 그 서버는 08:13:21.89~.916에 종료 중.
  게임 서버가 죽을 때 Redis 세션(Ingame @ 서버)이 정리되기 전에 로그인이 들어오는 경쟁으로 보임.
- 재접속 포기 뒤 화면에 갇힘: client1이 "인게임 서버 접속 시간 초과(10초) - 포기합니다" 뒤에도 Ingame 씬, networkRole=none으로 남음(로비/로그인으로
  안 돌아감).
- 가상 플레이어 server가 왜 멈췄는지(백그라운드 창 멈춤 추정) - 다시 나면 원인 확인.
- 재접속 뒤 인게임 화면/진행이 안 돌아옴 (**원인 확인, 수정 - PC 확인 대기**) - 10-23 174242/224012/230639 재현: 재진입은 되는데 client1에
  Pawn/View/UI 없음, phase.changed 없음. 원인(NGO 2.11.0 소스): 새 접속의 보임 판정은 승인 직후 동기화 때 한 번이고 서버 OnClientConnected는
  동기화가 끝난 뒤라, 거기서 바꾸던 Pawn 소유권/방 접속자 목록이 판정에 안 들어감 → Pawn과 방 공용 오브젝트(GameState 등)가 재접속 클라에게
  영영 안 감. 승인 시점 PlayerController 소유권 반환도 ChangeOwnership이 관찰자에게만 되는 규칙 때문에 효과 없었음. "failed validation" 경고와는 무관.
  수정(10-24): 방의 레플리케이션 트리(Network/Framework/ReplicationTree.cs - 게임모드가 좌석 비트 수 선택, 스폰마다 대상 지정, 접속 완료 뒤 좌석
  경로 NetworkShow → 소유권 반환). 문서 Server/NETCODE_NOTES.md 1, 3번 정정. 하네스 process.status에 autoChessController 칸.
  반복 재접속 점검(10-24): reconnect 시나리오에 ReconnectPlanning ×3 - 재정비 중 재접속 뒤 클라 spawnedObjects = 서버 트리 visibleTo, 트리 objects =
  방 실제 스폰 수(ingame.rooms replication, IReplicationTree.Stats/CountVisibleTo).
  미룸: 성능 측정(방 여러 개/오브젝트 수천 개/짧은 간격 들락날락) - 방 하나·감지 30초·에디터 수치로는 의미 있는 값이 안 나옴. 실제 서버 빌드와
  부하 수단(더미 오브젝트 대량 스폰 등)이 생기면 다시.
- 클라 재접속 폭주: 서버가 접속을 거부/끊으면(예: NetworkConfig mismatch) 클라가 기다림 없이 바로 재시도 - 10-24 112845에서 30초에 약 135번
  (서버 경고 274건). 재시도 간격/횟수 제한 필요.
- (환경 메모) 10-24 112845의 NetworkConfig mismatch는 MPPM 가상 플레이어를 껐다 켜고 보드 프리팹을 Reimport한 뒤 사라짐(120908 정상) - 프리팹
  직렬화가 바뀌는 변경(컴포넌트 부모 클래스 변경 등) 뒤에는 가상 플레이어 재동기화가 필요할 수 있다. 다시 나면 net.state에 설정 해시/프리팹
  목록 칸을 더해 양쪽을 비교.
- 대기열 슬롯 "Disabled NetworkBehaviours will be excluded" 경고(10-24, 전투 중 재접속 때 보드당 9줄) - 수정: 슬롯을 자리 표시용
  MonoBehaviour로 바꾸고 기물 상태는 보드의 NetworkList로, 꺼진 슬롯의 기물은 GameObject째 끔(늦게 받은 클라는 LateUpdate). 120908에서 경고 없음.
- 재진입 포기 뒤 채팅 토큰 불일치: 재진입 로그인이 새 토큰을 발급했는데 채팅 재연결은 옛 토큰을 쓰는 것으로 보임(채팅 서버 401 "토큰 불일치",
  10-23 3차 실행 175701).
- 실패한 재진입 연결의 퇴장 로그에 playerId 자리에 clientId: "NGO 클라이언트 퇴장: clientId=2 playerId=2", "이미 정리된 방 소속 - PlayerLeft 생략:
  playerId=2"(175701, 195734 두 번 재현). 승인 전에 끊긴 연결의 퇴장 처리에서 playerId를 못 찾아 clientId를 쓰는 것으로 보임 - 로그만인지 처리도
  틀린지 확인.

**6번 재접속/탈주 시나리오 reconnect (2차 구현 - PC 확인 대기)** - 1차 PC 실행(201909): BriefDrop 통과. Reconnect는 끊김 재적용(reapplied) 7ms 뒤 다시
이었더니 재진입 연결이 서버에 닿았다 0.5초 만에 끊김("NGO 클라이언트 퇴장: clientId=2 playerId=2") → 10초 뒤 포기. Abandon은 그 여파로 클라에 NGO
전송이 없어 net.link 거부. → 2차: net.link에 hold 인자(false = 지금 연결에만), Reconnect = hold=false로 끊고 스스로 재진입, 끊긴 상태를 Disconnected로
드러내 Cut(Ingame→Disconnected, 끊김 유지) / Restore(→Ingame, Needs net_link) / GraceExpire(→Abandoned, Needs net_link)로 나눔 - 끊긴 동안의 게임 일
(예: 전원 기물 지급)을 보는 노드를 Disconnected에서 고를 수 있게. 공통 "접속 중이어야 함" 시작 조건은 넣지 않음(그런 테스트를 막으므로).
판단 보류: 끊김 재적용 직후 다시 이으면 재진입 실패 - 하네스 조작 영향일 수 있음. 재진입 시도가 한 번(10초)뿐이고 그 안에 재시도하지 않는 것도
실제 네트워크에서 약점일 수 있어 7번에서 같이 볼 것(Restore 결과로 다시 확인). → 3차(224012): Restore 뒤 재진입 0.1초로 재현 안 됨(재진입 뒤 화면 문제는 위 7번 항목).
3차 하네스 수정: (1) hold=false로 끊은 연결이 재진입으로 이어지면 Unity가 net.link_changed(cause=transport_recreated)를 알리고, 지속 조작 종류에
값 이벤트(OverrideKind.ValueEvent)를 둬 그 시각에 net_link 기록을 끝냄(restored(...)) - 전에는 노드 끝까지 켜 둔 것으로 기록. (2) 진행 확인 판정
(ProgressReceived)이 화면부터 봄 - 인게임 화면이 아니면 그것을 실패 이유로(실제 화면 포함). 전에는 "phase.changed 못 받음"이 원인처럼 보임.

**10-23 코드 검토 수정 (PC 확인 끝 - 195734: 끊김 이유 ProtocolTimeout, 다시 잇기 current=true → net_link manual, 노드 밖 경고 3건(끊김 2, 탈락 1), 구간 기록 지역 시각)** - (1) 되돌리는 명령이 ok여도 값이 처음 값이 아니면 되돌림으로 기록하지 않고 확인 대기
(OverrideManager.RevertWhere), (2) net.link/net.state의 연결 상태를 한 곳(LinkConnected)에서 - 전송이 없으면 시뮬레이터 플래그 대신 "끊어 둔 채
유지 중이 아님", (3) client.ingame_disconnected 이유를 끊기는 순간(OnClientDisconnectCallback)에 잡음 - 정리 뒤 이유(TransportShutdown) 대신 원인,
(4) 노드 밖 이상 범위를 scenario.node_end까지, (5) 순간 끊김 중 net.link 끊기 거부, (6) 에디터를 켤 때 남은 Play 시작 씬 파일 경고.
PC에서 볼 것: 탈주 흐름 재실행에서 client.ingame_disconnected 이유가 ProtocolTimeout인지, 포기 뒤 다시 잇기 응답이 current=true이고 net_link가
manual로 끝나는지. + 보고서/구간 기록 시각을 지역 시각으로 통일(RunTrace - 원본은 UTC), 노드 밖 경고에 사람 플레이어 탈락(player.health_changed eliminated, playerId 있음).

**net.link 끊김 유지 + 노드 밖 이상 보고 (구현 - PC 확인 대기)** - 2차 실행에서 재진입으로 NGO 전송이 새로 생기자 끊김이 풀려 바로 재진입됨(하네스는
모르고 o2를 켜진 채 둠) → NetHarnessCommands의 LinkKeeper가 다시 끊음(cause=reapplied). 노드 사이에 일어난 끊김/에이전트 끊김은 TH_OutsideEvents로
보고서 머리 경고. PC에서 볼 것: 끊은 채 두면 재진입 시도가 계속 실패하고 다시 이으면 붙는지, 보고서의 "노드 밖에서 일어난 일" 절.

**NGO 통신 조건 net.* (4주차 5번, 구현 - PC 확인 대기)** - NGO 2.x의 UnityTransport 디버그 시뮬레이터가 효과 없음으로 바뀌어 Multiplayer Tools
2.2.12의 Network Simulator로(패키지는 사람이 설치). `Unity/Assets/Scripts/Test/Harness/NetHarnessCommands.cs`(net.condition/link/lag_spike/state) +
지속 조작 net_condition/net_link + 영역 net. PC에서 볼 것: 인게임 중 client1에 net.link false → 서버가 제한 시간 뒤 끊김을 알아채는지(서버 이벤트/로그),
net.condition 지연 200ms 동안 게임이 계속 도는지, 노드 끝/시나리오 끝에 다시 이어지는지, 가상 플레이어(client2)에 걸면 그 플레이어만 영향받는지.

**지속 조작 되돌리기 실패 / 노드 선언 (10-23 6차 실행 144105 뒤, 구현 - PC 확인 대기)** - FinishGameFast 노드 끝에 planning_setting 되돌리기가
"방 없음"으로 실패(room.closed가 0.1초 뒤 옴)해 보고서에 "되돌리기 실패"로 남던 것. 되돌리기 실패 → 확인 대기(override.unconfirmed) → 이벤트로
scope_gone. 노드 선언 `.Forbids/.Needs`(WaitGameEnd에 Forbids time_scale, planning_setting) + 시나리오 `s.OverrideMismatch`(기본 Reject) + 종류별
조회(Probe - 기존 조회 명령, 확인 대기인 것만 선언한 노드 직전에). Unity: ingame.rooms에 planningDuration, process.status에 timeScale.
PC에서 볼 것: FinishGameFast 뒤 보고서 o줄이 "scope_gone (그 전에 되돌리기 실패: ...)"인지, AI가 배속을 남긴 채 WaitGameEnd를 고르면 거부되는지.

**경계 장애 규칙(fault.*)** - 1차 PC 실행(10-23)에서 버그: 응답 스트림 Writer가 `WriteAsync(T, CancellationToken)` 겹을 빠뜨려 규칙이 없어도
마스터/로그인 스트림이 깨짐(NotSupportedException → account_create 실패). 고침(FaultInterceptor.cs) - 다시 확인. PC에서: 솔루션 빌드(Harness.Agent에 Grpc.AspNetCore.Server 2.63.0 패키지 추가 - 복원 필요) → 하네스로 서버 기동 →
`agent_command(LobbyServer, fault.methods, {query: "Match"})`에 `/LobbyControl/RequestMatch` → Unity 클라 로그인 후 그 playerId로
`fault.add {method: "/LobbyControl/RequestMatch", dir: "response", playerId, match: {"matchFound": {"$exists": true}}, action: "rewrite",
rewrite: {"matchFound.gameServerAddress": "127.0.0.1:1"}, times: 1}` → 매칭 → 타임라인에 fault.hit, 클라가 엉뚱한 주소로 접속 실패하는지.
확인할 위험: 로비의 AuthInterceptor 안쪽에 끼워져 playerId가 보이는지(안 보이면 playerId 조건이 안 걸림 - fault.hit 없음, unused 경고).

**지속 조작** - PC에서: `cheat-commands`를 mode=auto로 → 보고서 "지속 조작" 절에 planning_setting/time_scale이 켜졌다가 node_end(또는
FinishGameFast의 설정값은 scope_gone)로 끝났는지, 끝난 뒤 `harness_status`에 overrides가 없는지. AI로 시나리오 없이
`agent_command(server, cheat.time_scale, {scale: 3})` → `harness_status` overrides에 보이는지 → Stop 하면 agent_gone으로 사라지는지.

**대상(TH_Target)과 다중 클라** - PC에서: MPPM 태그를 바꾼다(메인 에디터 `Client1`, 가상 플레이어 `Server`, 다른 가상 플레이어 `Client2`) →
`full-match`(또는 `cheat-commands`)를 mode=auto로 → 전부 통과인지(노드 ID가 `@client1`) → `multi-client`를 mode=ai로 돌려 상태가 칸별로
나오는지, `EnterGame@client1+client2` 뒤 메모에 방이 둘(room 서로 다름)인지, FinishGameFast를 클라마다 따로 해서 둘 다 로비로 오는지,
헤드리스 hc1이 로그인+로비까지 되는지. 확인할 위험: 가상 플레이어 Client2의 harness.attached가 늦게 오는지(UnityPlay 120초 안),
`client.state`의 playerId가 로그인 직후 비어 있지 않은지.

**서버 하네스 주입(시작 훅 + 호스팅 시작)** - PC 확인 완료(10-23): 서버 8개 접속, GameAgent process.status(GameAgent.Harness), fault 명령,
서버 bin에 하네스 DLL 없음, 주입 로그 확인. 아래는 참고용 원래 항목: VS로 `AutoBattlerServer.slnx` 전체 빌드(또는 하네스 `server_build`) →
`Server/GameServer/Shared/Test/bin/Debug/net8.0/`에 `Harness.Agent.dll`, `GameAgent.Harness.dll`이 생겼는지 → 각 서버
`bin/Debug/net8.0/`에 `Harness.*.dll`이 **없는지** → 하네스로 `servers_start` → `agent_list`에 서버 8개가 붙고 GameAgent에
`process.status`가 있는지 → 수동 모드(`start-servers.ps1`만)로 띄운 서버 로그에 하네스 줄이 없는지. 확인할 위험: 서버와 하네스의
패키지 버전 차이(Grpc.Net.Client 2.63.0 등 - 서버 쪽 버전이 쓰임), Windows에서 시작 훅 경로(공백 포함 경로).

**1주차** - Claude Code 연결, 서버 기동/로그인/매칭/종료는 PC에서 확인됨. Unity → 하네스 평문 HTTP/2 접속도 확인됨.
남은 것: `server_build`(서버 실행 중이면 거부되는지 포함), `devtools/*.txt`의 한글(스크립트가 BOM 없는 UTF-8이라
Windows PowerShell 5.1에서 원래부터 깨질 수 있음 - 판정은 결과 파일로 하므로 동작 영향 없음).

**시나리오/영역** - PC에서: 하네스 재빌드·재시작 → Claude Code `/mcp` 다시 연결 → "cheat-commands 시나리오 돌려 봐" →
runs/<실행>/scenario-cheat-commands-*-report.md 확인(판정 불일치/순서 위반이 의도대로 찍히는지). 서버도 다시 빌드할 것
(`agent.status` → `process.status`, 안 하면 agent_list에 unknownAreas로 나온다).

**노드 그래프 1단계** - PC에서: 하네스 재빌드 → `/mcp` 다시 연결 → "cheat-commands 돌려 봐"(AI가 노드를 고름) → 보고서의 방문 순서·노드별 결과·
trace. "full-match 자동으로 돌려 봐"(mode=auto)와 명령줄 `dotnet run --project HarnessDaemon -- run full-match`(하네스 데몬은 끈 상태).
노드 사이에 VS로 서버를 직접 빌드해 보고 다음 node_run 응답에 environment.changes가 오는지. 아무 지시 없이 2분 두면 하네스가 정리하고 끝내는지.

**응답 시간 한도(pending → call_continue)** - 샌드박스에서 한도 10초로 확인: 25초 대기 → pending → 이어서 → 결과, 대기 중 이벤트가 오면
그 순간 응답, 원래 도구 이름으로 완료 기록. PC에서: `wait_for(timeoutSeconds=180)`을 걸어 50초마다 pending이 오고 call_continue로 이어지는지,
`server_build`처럼 오래 걸리는 도구도 같은 흐름인지.

**10/21 수정분** - skip_planning 문구, 끊긴 도구 호출 기록(mcp-calls.jsonl에 "응답 전에 호출이 끊김"), contains의 JSON 공백 무시,
시나리오 끝 알림음(`FinishSound` - 완료면 tada.wav, 아니면 Windows Exclamation.wav, `s.PlayFinishSound`), DebugManager(Unity 화면 전용 로그).
PC에서: 시나리오 끝에 소리가 나는지(성공/실패 구분), 60초 넘는 wait_for를 일부러 걸면 mcp-calls.jsonl에 끊김 기록이 남는지.

**Unity 로그 기록 + 보고서 구간 기록** - harness.proto 버전 2(AgentLog). TestHarness → Server → Unity 순서로 다시 빌드. PC에서:
Play → `runs/<실행>/logs/client1.log`, `server.log`, `UnityEditor.log`가 생기고 Unity 콘솔과 같은 내용인지(한글은 \uXXXX), `logs_query(server="client1")`.
시나리오 하나 돌린 뒤 보고서의 "구간 기록" 줄과 `trace/*.txt`, 실패 목표의 요약, 메인 에디터 중복 로그가 `UnityEditor+client`로 합쳐지는지.
로그가 아주 많을 때(전투 중) 에디터가 느려지지 않는지 - 느리면 수준 필터(경고 이상만 보내기 등)를 검토.
(타임라인 병합 남은 것: AI가 직접 구간을 잘라 보는 도구(trace)는 보류 - 필요해지면 RunTrace를 그대로 도구로 노출.)

**결과 전송 후 작업(AfterReply)** - 연결을 끊는 동작을 공통 경로로 옮김(Unity `HarnessConnection` + `HarnessCommandContext`,
서버 `HarnessAgentService` + `HarnessCommandContext`, 에디터 브리지의 Play/새로고침 전용 코드 제거). 대역 컴파일/오프라인 빌드만 확인. PC에서:
`editor.play`/`editor.stop`/`editor.refresh` 응답이 끊김 없이 오고(requested=true), 그 뒤 `editor.play_mode` / `editor.ready`(reload 또는
no-changes, 바뀐 것 없을 때 약 2초 뒤) / `editor.compile_failed`가 오는지. 서버는 AfterReply를 쓰는 명령이 아직 없어 기존 명령이 그대로 되는지만.

**2주차** - Unity 코드는 여기서 대역(stub)으로 문법만 확인했다. PC에서:
- 컴파일: 특히 `Unity.Multiplayer.PlayMode.CurrentPlayer.Tags` / `IsMainEditor`(6000.3 문서 기준 이름), 에디터 폴더가
  게임 쪽 public 타입(`HarnessConnection`, `HarnessCommandRegistry`, `HarnessLogBuffer`, `HarnessSettings`)을 보는지.
- 에디터 브리지: 메뉴 체크 → `agent_list`에 `UnityEditor`. 코드 한 줄 바꾸고 `editor.refresh` → `editor.ready(reload_compile)`. Play 하면 `editor.ready(reload_play)`.
  일부러 컴파일 오류 → `editor.compile_failed`와 `editor.compile_errors`. 가상 플레이어에는 안 붙는지.
- `editor.play`/`editor.stop`: 결과를 받은 뒤 Play가 바뀌는지(도메인 리로드로 응답이 끊기지 않는지), 가상 플레이어도 같이 들어가는지.
- 태그: 메인 `Client1`, 가상 `Server`로 달고 Play → 버튼 없이 역할 시작, 하네스 이름 `client1`/`server`.
- 한 판: `account_create` → `client.login` → `client.join_match` → `client.match_found` → `phase.changed` →
  게임 종료 후 `client.lobby_state(InLobby)`. 서버 쪽 `room.*`, `server.load`, `ingame.rooms`.

## HeadlessClient (완료분)

- 코드 리뷰 수정 3건 - 이전 매치 정보 재사용(`LastMatchFound` + 유효 플래그), 서버 쪽 끊김 시 단계 미갱신
  (`ConnectionStage`로 이름 변경 + 서버가 닫으면 갱신), 로비 연결 교체 경합(`DetachLobby`의 원자적 교체).
