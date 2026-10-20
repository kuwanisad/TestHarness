# 테스트 하네스 사용 안내

이 MCP 서버는 오토체스 게임 서버들(.NET 8개)을 띄우고, 화면 없는 클라로 조작하고, 일어난 일을 관찰하는 테스트 하네스다.
폴더 배치: GameProject1/{DevTools, Server, Unity, TestHarness}. 실행 기록은 TestHarness/runs/<실행 id>/에 남는다.

## 검증 요청이면: 시나리오

사람이 "검증해 봐", "테스트 돌려 봐", "확인 절차 돌려 봐"처럼 정해진 절차를 요청하면 `scenario_list`에서 맞는 시나리오를 골라
`scenario_start`로 시작한다. 그 뒤로는 하네스가 목표를 하나씩 내준다:
- 응답의 `expect`가 다음에 할 시나리오 행동이다(objective_begin / objective_report / scenario_abort). 그대로 따른다.
- `objective_begin`이 돌려준 sinceSeq 뒤의 이벤트만 그 목표의 근거가 된다. 목표를 수행하는 동안에는 어떤 도구든 써도 된다
  (이상 현상 조사 포함) - 모든 호출은 그 목표에 묶여 기록된다.
- `judgedBy=harness`인 목표는 하네스가 기록으로 판정한다. 그래도 objective_report로 내 판단(result, note)을 보고한다 - 다르면 불일치로 남는다.
  `judgedBy=ai`인 목표는 Pass에 근거 이벤트 번호(evidenceSeqs)가 필수.
- 통과 기준을 바꾸거나 목표를 지어내지 않는다. 판단할 수 없으면 `Unverified` + note.
- 진행할 수 없으면 `scenario_abort(reason, situation)` - 그 뒤 정리 목표가 나온다. 보고서는 하네스가 쓴다(끝나면 경로를 사람에게 알릴 것).
- 하네스 밖에서 한 일(코드 수정, 파일 읽기)은 다음 objective_report의 note에 적는다.
시나리오가 아닌 자유 조사/디버깅은 아래 기본 흐름대로 한다.

## 기본 흐름

1. `harness_status` - 지금 상태 확인. orphans가 있으면 `servers_stop(runId=...)`로 먼저 정리(포트가 겹쳐 새 서버가 못 뜬다).
2. `run_begin(label)` - 조사/재현 하나마다 새 실행을 연다(서버가 꺼져 있을 때만).
3. 서버 코드를 고쳤으면 `server_build` → 오류가 있으면 고치고 다시.
4. `servers_start` - 단계별 결과를 본다. failed면 그 단계의 logTail부터 읽을 것.
5. `client_create(alias)` → `client_login(alias)` → `client_join_match(alias)` 등으로 조작. 에이전트(서버/Unity) 명령은
   `agent_list()`로 영역을 보고 `agent_list(query=<영역>)`으로 그 영역 명령을 찾은 뒤 `agent_command`.
6. 결과는 `wait_for(sinceSeq=<행동 도구가 돌려준 sinceSeq>, ...)`로 기다린다.
7. 원인은 `logs_query`(서버 로그)와 `events_query`(타임라인)로 좁힌다.
8. 끝나면 `servers_stop`. 서버를 고쳐 다시 볼 때는 stop → build → start.

## 원칙

- **판정은 바깥에서 관찰한 사실로**: 클라 이벤트, 에이전트 이벤트, 응답의 열거형 값. 서버 로그는 원인을 찾는 증거다.
- **고정 시간 대기 금지**: sleep 대신 `wait_for`. 시간 초과 응답의 eventsSince/matchedBefore를 먼저 볼 것.
- **sinceSeq를 이어서 쓴다**: 모든 행동 도구는 행동 직전의 이벤트 번호(sinceSeq)를 돌려준다. 그 값을 wait_for에 넣어야
  "행동한 뒤 대기를 걸기 전에 이미 온 이벤트"를 놓치지 않고, 지난 행동의 이벤트를 이번 것으로 착각하지 않는다.
- **조금씩 조회**: 로그/이벤트는 server, contains(playerId, roomId), minLevel, sinceUtc로 좁혀서 본다. 응답의 truncated/hint를 따른다.
- **AI 자신의 도구 호출도 기록된다**: 모든 도구 호출(요청+응답)이 실행 폴더의 mcp-calls.jsonl과 타임라인(source=ai, name=mcp.call)에
  남는다. 출처를 지정하지 않은 wait_for/events_query에는 걸리지 않는다(보려면 source=ai).
- **도구가 isError로 실패하면** error와 hint를 읽고 다음 행동을 바꾼다. 같은 호출을 그대로 반복하지 않는다.
  "하네스 내부 오류"는 하네스 버그일 수 있으니 사람에게 알린다.
- **같은 대상(같은 클라, 같은 서버, 같은 에이전트)에 대한 행동은 앞 결과를 본 뒤에 부른다.** 도구 호출끼리는 순서가
  보장되지 않는다 - 한 번에 여러 도구를 부를 때는 서로 다른 대상이거나 조회(harness_status, events_query, logs_query,
  wait_for)만 묶을 것. 같은 헤드리스 클라에 동시에 온 호출은 하네스가 도착 순서대로 하나씩 실행하지만, 그 순서가
  의도와 같다는 보장은 없다.

## Unity (진짜 인게임 서버와 클라)

사람이 Unity 에디터를 켜 두면 AI가 원격으로 조작한다(빌드 없음). 에이전트는 셋이다:
- `UnityEditor` - 에디터 자체(에디터 브리지). Play 여부와 상관없이 붙어 있다. 영역 `editor`.
- 게임 쪽 - Play하면 붙는다. 이름은 Multiplayer Play Mode 태그에서 온다(보통 메인 에디터 = `client`, 가상 플레이어 =
  `server`). 클라: 영역 `client`(로그인/매칭), `shop`(UI와 같은 경로의 매물대). 서버: `ingame`(방 조회), `cheat`(아래).
  공통: `log`(log.recent = 그 프로세스의 콘솔), `process`(info, status - 클라는 화면 상태 포함).

명령 영역(agent_list의 query 키워드 - 이 이름만 받는다):
editor(에디터) · client(Unity 클라 로그인/매칭) · shop(매물대, UI 경로) · cheat(규칙을 건너뛰는 준비) · ingame(인게임 서버 방) ·
log(콘솔 로그) · process(프로세스 정보/상태). 명령 이름과 인자는 `agent_list(query=<영역>)`으로 확인하고 쓴다.

흐름:
1. Unity 코드를 고쳤으면 `agent_command(UnityEditor, editor.refresh)` → `wait_for(source=UnityEditor,
   names=[editor.ready, editor.compile_failed])`. 실패면 `editor.compile_errors`로 원문을 보고 고친 뒤 다시.
   (Play 중이면 refresh가 거부된다 - `editor.stop` 먼저.)
2. 서버를 띄운다(`servers_start`). Unity 인게임 서버는 GameAgent에 붙어야 하므로 서버가 먼저다.
3. `account_create`로 계정을 만든다(Unity 클라는 계정을 만들 수 없다).
4. `agent_command(UnityEditor, editor.play)` → `wait_for(names=[harness.attached], source=client)`와 `source=server`.
5. `agent_command(client, client.login, {accountId, password})` → `wait_for(source=client, names=[client.login_result])`.
6. `agent_command(client, client.join_match)` → `client.match_found` / `client.match_failed`. 인게임 진행은
   `phase.changed`, `room.*`(server), 게임이 끝나면 `client.lobby_state`(InLobby)로 본다.
7. 끝나면 `editor.stop`. 다시 고칠 때는 stop → 코드 수정 → refresh → play.

화면 확인: 클라의 `process.status`가 지금 화면 상태를 알려준다 - sceneRole(Login/Ingame/Other), stage(Login/Lobby/Ingame),
localPawn(LoginPawn/LobbyPawn), uiManager(LoginUIManager/LobbyUIManager/AutoChessUIManager), autoChessView, pawnCounts.
로그인 후 = stage Lobby/LobbyPawn/LobbyUIManager, 인게임 = sceneRole Ingame/AutoChessView/AutoChessUIManager(로비 Pawn 0개), 게임 후 =
sceneRole Login + LobbyPawn. 씬 전환은 비동기라 이벤트 직후엔 이전 상태일 수 있다 - 잠시 뒤 다시 본다. UIManager가 2개 이상이거나
화면이 기대와 다르면 버그다(log.recent에 [UIManager] 오류가 남는다).

주의: Unity 명령의 ok는 "요청을 보냈다"까지만이다. 결과는 반드시 이벤트로 확인한다. 노멀 모드는 사람 1 + 봇 7이라
클라 하나로 한 판이 돌아간다.

### 테스트 조작 (`cheat.*`, 서버 에이전트에 보낸다)

규칙을 건너뛰는 "준비"용이다. 실제 UI 경로를 검증할 때는 클라 명령(`shop.buy`, `shop.sell`)을 쓴다. 공통 인자: `roomId`(비우면
진행 중인 방이 하나일 때 그 방), `player`(팀 `"Team3"`/`"3"` 또는 사람의 playerId - 봇은 팀으로만).
- `cheat.players` - 참가자 전원(팀, playerId, 봇 여부, 체력, 탈락, 순위, 골드), 지금 국면, 재정비 설정값.
- `cheat.damage {player, amount}` - 라운드 결과 데미지와 같은 함수. 체력 0이면 즉시 탈락 - 생존자가 한 명 이하면 다음 전투 결과 국면에서 재정비 없이 바로 게임 종료.
- `cheat.buy {player, piece, boardIndex?}` - 골드 자동 지급(끝나면 원래대로) + 리롤 반복으로 매물 찾기 + 진짜 구매 + 보드 칸
  배치(boardIndex = 행*열수+열, 생략하면 대기열). 재정비 중에만. merged=true면 합성돼서 배치를 건너뛴 것.
- `cheat.planning_duration {mode, seconds}` - `CurrentOnly`(이번 재정비만), `Rescale`(설정값 변경 + 이번 재정비는 비율 유지),
  `SettingOnly`(다음 재정비부터). 결과에 설정값(settingBefore/After)과 이번 재정비 길이(currentBefore/After).
- `cheat.skip_planning` - 지금 재정비를 바로 끝낸다.
- `cheat.time_scale {scale}` - 인게임 서버 배속(0.1~10). 전투도 빨라진다. 재접속 유예 같은 다른 시간도 같이 빨라지고, 클라
  타이머 게이지는 어긋나 보인다(정상).

관찰: `player.health_changed {roomId, team, playerId, health, previous, eliminated}`(server).

빨리 한 판 끝내기: 매치 후 `wait_for(names=[phase.changed], source=client)`(첫 Planning) → `cheat.planning_duration(SettingOnly, 5)` →
`cheat.time_scale(5)` → `cheat.players`로 봇 팀 확인 → 봇마다 `cheat.damage(amount=1000)` → `cheat.skip_planning` →
`client.lobby_state`(InLobby)를 기다린다.

재접속/탈주 테스트 중에는 재정비를 재접속 유예(기본 20초)보다 짧게 줄이지 말고 배속은 1로 둘 것 - "유예 안에 돌아옴"과
"한 재정비 안에 탈락 정리" 조건이 깨진다.

## 알아둘 것

- 서버 이름: MasterServer, MatchingServer, AccountServer, LoginServer, DummySubServer, LobbyServer, GameAgent, ChatServer.
  각 서버는 같은 이름의 에이전트로 하네스에 붙는다(agent_list). 서버가 죽으면 그 에이전트의 `harness.detached`가 찍힌다.
- 헤드리스 클라는 로그인/로비/매칭만 한다. 인게임(NGO) 접속은 못 한다 - 인게임은 Unity 클라가 맡는다(위 Unity 절).
- 클라 이벤트 이름: LoginSucceeded, LoginRejected, EnterLobbySucceeded, EnterLobbyRejected, EnterLobbyConflict, StayConnectedOpened,
  LobbyDisconnected, MatchRequested, MatchQueued, MatchStatus, MatchFound, MatchFailed, MatchStreamClosed, MatchCancelled, Error 등.
- 거부/실패 사유는 열거형 이름으로 나온다(예: LoginRejected의 reason=SessionConflict, MatchFailed의 reason=Timeout).
- 인게임 서버(Unity)가 없으면 매칭이 성사되지 않고 매칭 타임아웃(30초)으로 MatchFailed(Timeout)가 오는 것이 정상이다.
- 계정 ID를 안 주면 매번 새 계정을 만든다(앞 실행의 세션이 섞이지 않게). 같은 계정을 두 클라가 쓰는 시나리오는
  두 번째 클라를 accountId/password/accountExists=true로 만든다.
- 서버가 떠 있으면 빌드할 수 없다(실행 중인 DLL은 덮어쓸 수 없다). run_begin/run_end도 서버가 꺼져 있어야 한다.
- 실행 폴더 구조: logs/(서버 로그, *-result.json, server-pids.json), events.jsonl(타임라인 전체), devtools/(스크립트 출력), build/(빌드 출력).
  도구 응답에 담기지 않는 전문은 이 파일들을 직접 읽으면 된다.
