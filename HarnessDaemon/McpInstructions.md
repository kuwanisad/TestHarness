# 테스트 하네스 사용 안내

이 MCP 서버는 오토체스 게임 서버들(.NET 8개)을 띄우고, 화면 없는 클라로 조작하고, 일어난 일을 관찰하는 테스트 하네스다.
폴더 배치: GameProject1/{DevTools, Server, Unity, TestHarness}. 실행 기록은 TestHarness/runs/<실행 id>/에 남는다.

## 검증 요청이면: 시나리오

사람이 "검증해 봐", "테스트 돌려 봐", "확인 절차 돌려 봐"처럼 정해진 절차를 요청하면 `scenario_list`에서 맞는 시나리오를 골라
`scenario_start`로 시작한다. 시나리오는 노드들의 그래프이고, **노드는 하네스가 실행하고 판정한다**. 내 역할은 경로를 고르는 것이다:
- `scenario_start`가 맥락(context), 전제, 주의, 규칙, 전체 그래프(graph), 지금 상태, 고를 수 있는 노드(available)를 준다. 맥락을 먼저 읽는다.
- `node_run(node, reason)`으로 다음 노드를 고른다 - 지금 상태에서 시작할 수 있는 노드(available)만. 묶음 노드는 하네스가 자식까지 차례로 돈다.
- 대상(targets): 시나리오의 클라/서버 목록. 대상을 받는 노드는 ID가 `이름@대상`(`Login@client2`, `EnterGame@client1+client2`).
  상태(state)는 칸별이다 - `{ global: Playing, client1: Lobby, client2: Login }`. 노드의 from은 적은 칸이 전부 맞아야 시작할 수 있다.
  오래 걸리면 pending이 온다 - `call_continue`로 기다린다(progress에 실행 중인 노드와 최근 이벤트).
- 결과(visit)를 보고 다음 노드를 정한다. 맥락상 충분하면 `scenario_finish(reason)` - 특정 노드까지 가거나 그래프를 다 돌 필요는 없다.
  진행할 수 없을 때도 scenario_finish(이유에 적기). 정리 노드는 하네스가 돌린다.
- 시나리오가 끝나면 하네스가 그 실행(run)도 닫는다(서버/헤드리스 클라가 남았으면 열어 둠 - 응답 runClosed). 닫힌 뒤 그 실행의 로그는
  `logs_query(runId=<응답의 runId>)`로 본다.
- 응답의 `environment.changes`가 오면 테스트 대상이 바뀐 것(서버 빌드·재기동, Unity 재컴파일, 계정 생성, 하네스 밖 빌드 등) -
  그 전에 돈 노드의 결과는 이전 환경이다. `staleNodes` 중 맥락상 다시 볼 노드를 다시 돌린다.
- `judgedBy=ai` 노드는 실행 뒤 `node_judge(node, result, evidenceSeqs, note)`로 판정을 보고해야 다음으로 간다(Pass에는 visit.seqRange 안의 근거 seq 필수).
- 노드 사이에 관찰 도구로 조사하는 것은 자유다(기록되지만 판정에는 안 쓰인다). 노드 실행 중(pending)에 테스트 대상을 바꾸는 도구는 부르지 않는다.
- 지시 없이 일정 시간(시나리오마다 - 기본 2분)이 지나면 하네스가 정리하고 끝낸다. 보고서는 하네스가 쓴다(끝나면 경로를 사람에게 알릴 것).
- 사람이 "자동으로 돌려 봐"라고 하면 `scenario_start(name, mode=auto)` - 하네스가 정해진 경로로 끝까지 돌고 결과를 준다(pending이면 call_continue로 기다림).
시나리오가 아닌 자유 조사/디버깅은 아래 기본 흐름대로 한다.

## 기본 흐름

1. `harness_status` - 지금 상태 확인. orphans가 있으면 `servers_stop(runId=...)`로 먼저 정리(포트가 겹쳐 새 서버가 못 뜬다).
2. `run_begin(label)` - 조사/재현 하나마다 새 실행을 연다(서버가 꺼져 있을 때만).
3. 서버 코드를 고쳤으면 `server_build` → 오류가 있으면 고치고 다시.
4. `servers_start` - 단계별 결과를 본다. failed면 그 단계의 logTail부터 읽을 것.
5. `client_create(alias)` → `client_login(alias)` → `client_join_match(alias)` 등으로 조작. 에이전트(서버/Unity) 명령은
   `agent_list()`로 영역을 보고 `agent_list(query=<영역>)`으로 그 영역 명령을 찾은 뒤 `agent_command`.
6. 결과는 `wait_for(sinceSeq=<행동 도구가 돌려준 sinceSeq>, ...)`로 기다린다.
7. 원인은 `logs_query`(서버 로그 + Unity 로그 - server에 client1/server/UnityEditor를 주면 그 Unity 프로세스)와 `events_query`(타임라인)로 좁힌다.
8. 끝나면 `servers_stop`(기본은 Docker 컨테이너까지 제거). 서버를 고쳐 다시 볼 때는 `servers_stop(stopInfra=false)` → build → start(인프라는 그대로 둬서 빠름).

## 원칙

- **판정은 바깥에서 관찰한 사실로**: 클라 이벤트, 에이전트 이벤트, 응답의 열거형 값. 서버 로그는 원인을 찾는 증거다.
- **고정 시간 대기 금지**: sleep 대신 `wait_for`. 시간 초과 응답의 eventsSince/matchedBefore를 먼저 볼 것.
- **sinceSeq를 이어서 쓴다**: 모든 행동 도구는 행동 직전의 이벤트 번호(sinceSeq)를 돌려준다. 그 값을 wait_for에 넣어야
  "행동한 뒤 대기를 걸기 전에 이미 온 이벤트"를 놓치지 않고, 지난 행동의 이벤트를 이번 것으로 착각하지 않는다.
- **조금씩 조회**: 로그/이벤트는 server, contains(playerId, roomId), minLevel, sinceUtc로 좁혀서 본다. 응답의 truncated/hint를 따른다.
- **AI 자신의 도구 호출도 기록된다**: 모든 도구 호출(요청+응답)이 실행 폴더의 mcp-calls.jsonl과 타임라인(source=ai, name=mcp.call)에
  남는다. 출처를 지정하지 않은 wait_for/events_query에는 걸리지 않는다(보려면 source=ai).
- **응답에 `pending: true`가 오면** 그 도구가 아직 진행 중이라는 뜻이다(응답 한 번은 최대 50초 - 그보다 오래 걸리는 도구는 하네스가
  먼저 이렇게 답하고 작업은 계속한다). `call_continue(callId)`로 이어서 기다린다 - 끝나는 순간 원래 도구의 결과가 그대로 온다.
  아직이면 또 pending이니 같은 callId로 다시 부른다. 그래서 `wait_for(timeoutSeconds=180)`처럼 길게 걸어도 된다.
- **도구가 isError로 실패하면** error와 hint를 읽고 다음 행동을 바꾼다. 같은 호출을 그대로 반복하지 않는다.
  "하네스 내부 오류"는 하네스 버그일 수 있으니 사람에게 알린다.
- **같은 대상(같은 클라, 같은 서버, 같은 에이전트)에 대한 행동은 앞 결과를 본 뒤에 부른다.** 도구 호출끼리는 순서가
  보장되지 않는다 - 한 번에 여러 도구를 부를 때는 서로 다른 대상이거나 조회(harness_status, events_query, logs_query,
  wait_for)만 묶을 것. 같은 헤드리스 클라에 동시에 온 호출은 하네스가 도착 순서대로 하나씩 실행하지만, 그 순서가
  의도와 같다는 보장은 없다.

## Unity (진짜 인게임 서버와 클라)

사람이 Unity 에디터를 켜 두면 AI가 원격으로 조작한다(빌드 없음). 에이전트는 셋이다:
- `UnityEditor` - 에디터 자체(에디터 브리지). Play 여부와 상관없이 붙어 있다. 영역 `editor`.
- 게임 쪽 - Play하면 붙는다. 이름은 Multiplayer Play Mode 태그에서 온다(메인 에디터 = `client1`, 가상 플레이어 =
  `server`, 클라를 더 띄우면 `client2` …). 클라: 영역 `client`(로그인/매칭), `shop`(UI와 같은 경로의 매물대). 서버: `ingame`(방 조회), `cheat`(아래).
  공통: `log`(log.recent = 그 프로세스의 최근 콘솔), `process`(info, status - 클라는 화면 상태 포함).
  Unity 로그는 실행 폴더에도 남는다(logs/<에이전트 이름>.log) - 지난 일은 `logs_query(server="client1")`처럼 본다.
  메인 에디터는 Play 중 같은 로그가 UnityEditor.log와 client.log 둘 다에 있다(정상).

명령 영역(agent_list의 query 키워드 - 이 이름만 받는다):
editor(에디터) · client(Unity 클라 로그인/매칭) · shop(매물대, UI 경로) · cheat(규칙을 건너뛰는 준비) · ingame(인게임 서버 방) ·
log(콘솔 로그) · process(프로세스 정보/상태). 명령 이름과 인자는 `agent_list(query=<영역>)`으로 확인하고 쓴다.

흐름:
1. Unity 코드를 고쳤으면 `agent_command(UnityEditor, editor.refresh)` → `wait_for(source=UnityEditor,
   names=[editor.ready, editor.compile_failed])`. 실패면 `editor.compile_errors`로 원문을 보고 고친 뒤 다시.
   (Play 중이면 refresh가 거부된다 - `editor.stop` 먼저.)
2. 서버를 띄운다(`servers_start`). Unity 인게임 서버는 GameAgent에 붙어야 하므로 서버가 먼저다.
3. `account_create`로 계정을 만든다(Unity 클라는 계정을 만들 수 없다).
4. `agent_command(UnityEditor, editor.set_play_start, {scene: "HarnessBoot"})`(빈 출발 씬 - 모든 에디터의 Play 시작 씬, 가상 플레이어는
   1초 안에 맞춤. 열린 씬은 그대로) → `agent_command(UnityEditor, editor.play)` →
   `wait_for(names=[harness.attached], source=client1)`와 `source=server`(각 플레이어의 `test.role` startScene이 HarnessBoot여야 함) →
   플레이어마다 `agent_command(<이름>, scene.load, {scene: "Login"})`(클라) / `{scene: "ServerBoot"}`(server) → `scene.loaded` →
   서버는 `network.role`(server)까지 와야 진짜 서버로 시작한 것. 끝나면 `editor.stop`, `editor.clear_play_start`. 시나리오로 돌리면 이 전부를
   하네스가 한다(준비@UnityEditor, UnityPlay). 씬 이름은 Unity 씬 파일 이름과 같아야 함(목록 `editor.scenes`).
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

빨리 한 판 끝내기: 매치 후 `wait_for(names=[phase.changed], source=client1)`(첫 Planning) → `cheat.planning_duration(SettingOnly, 5)` →
`cheat.time_scale(5)` → `cheat.players`로 봇 팀 확인 → 봇마다 `cheat.damage(amount=1000)` → `cheat.skip_planning` →
`client.lobby_state`(InLobby)를 기다린다.

재접속/탈주 테스트 중에는 재정비를 재접속 유예(기본 20초)보다 짧게 줄이지 말고 배속은 1로 둘 것 - "유예 안에 돌아옴"과
"한 재정비 안에 탈락 정리" 조건이 깨진다.

지속 조작: 배속(`cheat.time_scale`)과 재정비 설정값(`cheat.planning_duration` SettingOnly/Rescale)은 켜 두면 남는 조작이라 하네스가
추적한다(`harness_status`의 overrides, 타임라인 `override.*`). 시나리오 노드가 건 것은 그 노드가 끝날 때, 시나리오 중에 내가 건 것은
시나리오가 끝날 때 하네스가 처음 값으로 되돌린다. 시나리오 없이 건 것은 내가 되돌리거나(같은 명령으로 처음 값) Play/서버가 꺼질 때 사라진다.
되돌리기가 실패하면 overrides에 unconfirmed(확인 대기)로 남는다. 그래프 노드의 forbidsOverrides/needsOverrides는 그 노드가 시작할 때 남아 있으면
안 되는/켜져 있어야 하는 종류 - 어긋나면 기본은 node_run 거부(이유에 남은 조작이 나옴). 남은 조작을 끄고(같은 명령으로 처음 값, fault.remove) 다시 고른다.

경계 장애(영역 fault - .NET 서버): 서버가 받는 gRPC 메시지를 고치기/버리기/늦추기/실패시킨다. `agent_command(<서버>, fault.methods)`로
메서드와 필드 경로를 보고 `fault.add {method, dir(request|response), action(rewrite|drop|delay|fail), playerId?, match?, rewrite?, times?, seconds?}`.
걸릴 때마다 `fault.hit`(그 서버 출처) - 장애가 실제로 걸렸는지부터 확인한다. 끄기는 `fault.remove {ruleId}`(지속 조작이라 시나리오 끝엔 하네스가 끈다).
한 번도 안 걸린 채 끝나면 `override.unused` - 조건을 다시 본다.

NGO 통신 조건(영역 net - Unity 에이전트, 그 플레이어 프로세스 단위): `net.condition {delayMs, jitterMs, lossPercent, lossInterval}`(다 0 = 정상),
`net.link {connected: false}`(끊기 - 손실 100%, 약 30초 넘게 두면 진짜 끊김, 다시 잇기 전까지 재진입도 막힘 - `hold: false`면 지금 연결에만 걸어
감지 뒤 클라가 스스로 재진입) / `{connected: true}`(다시 잇기),
`net.lag_spike {seconds}`(잠깐 끊었다 잇기), 상태는 `net.state`. 이벤트 `net.link_changed`. condition/link는 지속 조작이라 하네스가 되돌린다.
끊기와 lag_spike는 인게임 접속 뒤에만(net.state의 available).

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
- 실행 폴더 구조: logs/(서버 로그, Unity 로그 client1/server/UnityEditor.log, *-result.json, server-pids.json), events.jsonl(타임라인 전체),
  devtools/(스크립트 출력), build/(빌드 출력), trace/(시나리오 방문별 이벤트 + 로그 - 보고서가 가리킨다).
  도구 응답에 담기지 않는 전문은 이 파일들을 직접 읽으면 된다.
