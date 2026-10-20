# 테스트 하네스 (TestHarness)

**하네스를 이용한 E2E 시나리오 테스트** 도구. 서버들과 Unity를 사람 대신 띄우고, 조작하고, 결과를 판정한다.
수동 테스트(창 9개 띄우고 Unity Play 누르고 눈으로 로그 확인)를 코드로 옮기는 것이 목표다.

"테스트 하네스"는 기법 이름이 아니라 테스트를 돌리는 장치(드라이버, 스텁, 실행 환경, 계측)를 말한다.
이 장치 위에서 쓰는 기법은 시스템/E2E 테스트, 블랙박스 테스트(클라가 쓰는 프로토콜 그대로),
테스트 더블(가짜 인게임 서버), 테스트 훅(디버그 명령), 장애 주입(서버 강제 종료, 포트 차단),
시나리오 기반 테스트다.

---

## 구성

```
GameProject1/
├─ TestHarness/                 테스트를 "하는" 쪽 (이 폴더)
│   ├─ Protos/harness.proto     하네스 제어 프로토콜 원본 - 여기서만 고친다
│   ├─ HarnessHost/             하네스 제어 서버. 서버/Unity가 여기로 접속해 명령을 받는다
│   ├─ HarnessHost.Smoke/       HarnessHost 동작 확인 (서버 실행 → 접속 → 명령 → 종료)
│   ├─ HeadlessClient/          화면 없는 게임 클라. 로그인/로비/매칭을 프로토콜로 직접 수행
│   ├─ HeadlessClient.Smoke/    HeadlessClient 동작 확인
│   ├─ HarnessCore/             하네스 기능 모음(실행 단위, 타임라인, 서버 제어, 빌드, 클라 풀, 로그 조회)
│   ├─ HarnessScenarios/        시나리오(검증 절차) 정의 - [TH_Scenario] 클래스 하나 = 목표 그래프 하나
│   ├─ HarnessDaemon/           AI용 입구. HarnessCore를 MCP 도구로 노출하는 상주 프로세스
│   └─ runs/                    실행 기록(로그, 이벤트). git 제외
├─ DevTools/                    개발용 도구 - 하네스는 이 스크립트로 서버를 켜고 끈다
│   ├─ start-servers.ps1 / stop-servers.ps1 / control-server.ps1   (-LogDir를 주면 하네스 모드)
│   └─ server-common.ps1        세 스크립트가 같이 쓰는 함수
├─ Server/GameServer/
│   ├─ Shared/Test/Harness.Contracts/   harness.proto 사본 + 생성 코드(→ Unity로 복사)
│   ├─ Shared/Test/Harness.Agent/       서버 공통 에이전트 (접속, 명령 실행)
│   └─ <각 서버>/Test/Harness/           서버별 디버그 명령
└─ Unity/Assets/Scripts/Test/Harness/   Unity 에이전트 + 클라/서버 디버그 명령 (+ Generated/, Editor/ = 에디터 브리지)
```

**동작 흐름**

```
HarnessHost (localhost:9000)
   ▲ 접속(HARNESS_ADDRESS)       ▲ 접속            ▲ 접속
.NET 서버 8개                    Unity 인게임 서버   Unity 클라
   │                                │                 │
   └─ 명령 실행 결과 / 이벤트 ──────┴─────────────────┘ → 하네스가 판정

DevTools 스크립트(하네스 모드): 서버를 창 없이 띄우고 콘솔 출력을 runs/<시각>/logs/에 JSON으로 받는다
HeadlessClient: 로그인/로비/매칭 서버와 진짜 클라처럼 통신한다

HarnessDaemon = HarnessHost(:9000, 에이전트용) + MCP(:9100, AI용)
   AI(Claude Code) ──MCP 도구──▶ HarnessDaemon ──▶ DevTools 스크립트 / HeadlessClient / 에이전트 명령
                                    ├─ 모든 이벤트를 타임라인 하나로 모아 runs/<실행>/events.jsonl에 남긴다
                                    └─ AI가 부른 모든 도구(요청+응답)도 runs/<실행>/mcp-calls.jsonl과 타임라인(source=ai)에 남긴다
```

**원칙**

- 판정은 하네스가 한다. 서버는 "테스트 성공"을 보고하지 않는다. 판정 근거는 클라 이벤트, 에이전트 이벤트, Redis 상태 같은 바깥에서 관찰한 사실이고, 서버 로그는 실패 원인을 찾는 증거로만 쓴다.
- 고정 시간 대기(`Sleep 30초`) 대신 조건 대기("N초 안에 이 이벤트가 오면 통과")를 쓴다.
- 테스트 코드는 실제 코드와 분리한다(`Test` 폴더). 실제 코드에는 `AddHarnessAgent()` 한 줄만 들어가고, 하네스 모드가 아니면 아무것도 하지 않는다.

---

## 전제 조건

- 폴더 배치: `GameProject1/{DevTools, Server, Unity, TestHarness}`. 프로젝트들이 서로를 상대 경로로 찾는다.
- .NET 8 SDK, `.slnx`를 여는 VS (Server와 같은 환경)
- Docker Desktop (Redis / RabbitMQ / Kafka)
- Windows PowerShell 5.1 (DevTools 스크립트)
- Unity 6000.3 (Unity 쪽을 테스트할 때만)

---

## 최초 세팅

**빌드 순서가 중요하다.** proto 파일이 TestHarness → Server → Unity 방향으로 복사되기 때문이다.

1. **TestHarness 빌드**
   ```powershell
   cd GameProject1\TestHarness
   dotnet build TestHarness.slnx
   ```
   `HarnessHost`를 빌드할 때 `Protos/harness.proto`가 `Server/GameServer/Shared/Test/Harness.Contracts/Protos/`로 복사된다.

2. **Server 빌드** (VS에서 `AutoBattlerServer.slnx` 빌드, 또는 `dotnet build`)
   `Harness.Contracts`를 빌드할 때 생성 코드(`Harness.cs`, `HarnessGrpc.cs`)가 `Unity/Assets/Scripts/Test/Harness/Generated/`로 복사된다.

3. **BrokerInitializer 빌드**
   ```powershell
   cd GameProject1\DevTools\BrokerInitializer
   dotnet build
   ```
   스크립트는 빌드된 DLL을 실행하므로, 코드가 바뀌었으면 다시 빌드해야 반영된다.

4. **Unity 열기** (Unity 쪽을 쓸 때만)
   2번을 먼저 해야 한다. 안 그러면 `Harness.Protocol`을 못 찾는다는 컴파일 오류가 난다.

5. **동작 확인**
   ```powershell
   cd GameProject1\TestHarness
   dotnet run --project HarnessHost.Smoke
   ```
   Docker → 서버 8개 실행 → 전부 하네스 접속 → 명령 확인 → 정리까지 자동으로 돈다. 마지막 줄이 `결과: 전부 PASS`면 세팅 완료다.

---

## AI에게 하네스 쥐여주기 (HarnessDaemon)

AI(Claude Code)가 서버 빌드/기동/종료, 헤드리스 클라 조작, 이벤트 대기, 로그 조회를 MCP 도구로 직접 한다.
하네스는 상주 프로세스라서 Claude Code 대화를 새로 시작해도 띄워 둔 서버와 클라가 그대로 남는다.

1. **하네스 띄우기** (터미널 하나를 이 용도로 둔다)
   ```powershell
   cd GameProject1\TestHarness
   dotnet run --project HarnessDaemon
   ```
   `=== 테스트 하네스 준비 완료 ===`가 나오면 된다. 이 창에 AI가 부른 도구가 한 줄씩 찍힌다.
2. **Claude Code에 등록** (한 번만 - 사용자 범위라 어느 폴더에서 열어도 쓸 수 있다)
   ```powershell
   claude mcp add --transport http --scope user harness http://localhost:9100/mcp
   ```
3. **확인**: `GameProject1`에서 Claude Code를 열고 `/mcp`에 `harness`가 connected로 보이면 끝.
   "harness_status 불러 봐" → "서버 띄우고 p1으로 로그인해서 매칭 요청한 다음 결과 기다려 봐" 같은 식으로 쓴다.

하네스 사용법(작업 순서, 판정 원칙, 함정)은 MCP 접속 때 하네스가 AI에게 직접 보낸다
(`HarnessDaemon/McpInstructions.md`). 그래서 Claude Code를 어느 폴더에서 열든 똑같이 안다. 하네스는 이 파일을 디스크에서
읽으므로 고쳐도 다시 빌드할 필요가 없다 - 하네스를 재시작하고 Claude Code에서 `/mcp`로 다시 연결하면 반영된다.

| 묶음 | 도구 |
|---|---|
| 상태/실행 | `harness_status`, `run_begin(label)`, `run_end` |
| 서버 | `infra_status`, `server_build(names)`, `servers_start(only, infra, readyTimeoutSeconds)`, `server_control(name, kill/start/restart)`, `servers_stop(stopInfra, runId)` |
| 에이전트 | `agent_list(query, agent)` - query 없이 부르면 영역 요약, `query=<영역>`이면 그 영역 명령, `agent_command(agent, command, args)` |
| 헤드리스 클라 | `account_create`, `client_create`, `client_login`, `client_enter_lobby`, `client_join_match`, `client_cancel_match`, `client_logout`, `client_drop`, `client_reconnect`, `client_state`, `client_close` |
| 관찰 | `wait_for(source, names, sinceSeq, timeoutSeconds)`, `events_query`, `logs_query(server, minLevel, contains, sinceUtc)` |
| 시나리오 | `scenario_list`, `scenario_start(name)`, `objective_begin(objective, reason)`, `objective_report(objective, result, evidenceSeqs, note)`, `scenario_abort(reason, situation)` |

- 행동 도구는 전부 `sinceSeq`(행동 직전 이벤트 번호)를 돌려준다. 결과는 그 값으로 `wait_for`를 걸어 기다린다.
- 로그/이벤트 조회는 항상 개수 제한이 있고(기본 50), 넘치면 전체 개수와 좁히는 방법을 같이 돌려준다.
- 서버 기동/종료는 DevTools 스크립트를 그대로 부른다 - 사람이 띄우는 방식과 같다. 성공/실패 판단은 스크립트가
  남기는 결과 파일(`logs/start-result.json` 등)과 하네스 에이전트 접속 여부로 한다.
- 하네스를 꺼도(Ctrl+C) 띄운 서버는 끄지 않는다. 다음에 켜면 `harness_status`의 orphans에 보이고 `servers_stop(runId)`로 정리한다.
- HarnessHost.Smoke와 같은 포트(9000)를 쓰므로 둘을 동시에 띄울 수 없다.

| 옵션 (`dotnet run --project HarnessDaemon -- <옵션>`) | 설명 |
|---|---|
| `--root <폴더>` | GameProject1 위치 (기본: 실행 위치에서 위로 찾음) |
| `--runs <폴더>` | 실행 기록 위치 (기본: `TestHarness\runs`) |
| `--agent-port <n>` / `--mcp-port <n>` | 에이전트용(기본 9000) / MCP용(기본 9100) 포트 |
| `--login <주소>` | 헤드리스 클라가 붙을 로그인 서버 (기본 `https://localhost:5400`) |
| `--powershell <경로>` | DevTools 스크립트를 실행할 PowerShell (기본: Windows는 `powershell.exe`) |

---

## 실행 방법

### HarnessHost.Smoke (서버 + 하네스 제어 채널 확인)

```powershell
dotnet run --project HarnessHost.Smoke [-- 옵션]
```

| 옵션 | 설명 |
|---|---|
| `--only A,B,...` | 띄울 서버만 지정 (기본: 전부). `process.status`(GameAgent)까지 확인하려면 `GameAgent` 포함 |
| `--skip-docker` | Docker/BrokerInitializer 생략 (이미 떠 있을 때) |
| `--port <n>` | 하네스 포트 (기본 9000) |
| `--devtools <폴더>` | DevTools 위치 (기본: `..\DevTools`) |

### HeadlessClient.Smoke (헤드리스 클라 확인)

서버를 먼저 띄워둔 상태에서 실행한다(수동 모드로 띄워도 된다).

```powershell
dotnet run --project HeadlessClient.Smoke [-- --login https://localhost:5400]
```

계정 생성 → 로비 입장 → 틀린 비밀번호 → 중복 로그인 → 매칭 타임아웃 → 재로그인 순서로 확인한다. 매칭 타임아웃 확인 때문에 30초 정도 걸린다.

### DevTools 스크립트의 하네스 모드

옵션 없이 실행하면 예전과 똑같이 동작한다(서버마다 창을 띄우는 수동 모드). `-LogDir`를 주면 하네스 모드가 된다.

| 스크립트 | 하네스 모드 옵션 |
|---|---|
| `start-servers.ps1` | `-LogDir <폴더>` 창 없이 실행, 출력은 `<폴더>\<서버>.log`(JSON)와 `.err.log`로<br>`-HarnessAddress <주소>` 서버가 하네스에 접속 (`-LogDir`와 같이 써야 함)<br>`-SkipDocker` Docker/BrokerInitializer 생략<br>`-Only A,B` 일부 서버만 (수동 모드에서도 가능)<br>`-ReadyTimeoutSeconds <n>` 서버 하나당 준비 대기 한도 (기본 60) |
| `stop-servers.ps1` | `-LogDir <같은 폴더>` 그 폴더의 PID 파일에 있는 서버만 끔<br>`-SkipDocker` Docker는 그대로 둠 |
| `control-server.ps1` | `-LogDir`, `-HarnessAddress` 하네스 모드로 서버 하나 Kill/Restart/Start.<br>재시작해도 로그는 `<서버>.2.log`처럼 이어서 남음 |

하네스 모드에서는 고정 대기 대신 서버가 `Application started`를 찍을 때까지 기다린다. 준비 전에 서버가 죽거나 시간을 넘기면 로그 앞/뒷부분을 보여주고 exit 1로 끝난다.

하네스 모드의 `start-servers.ps1` / `control-server.ps1`은 끝날 때 결과 파일(`<폴더>\start-result.json` / `control-result.json`)을 남긴다.
단계(Docker, BrokerInitializer, 서버)마다 `status`(ready / failed / skipped / killed / not-running)와 `detail`(exited, timeout, missing-dll,
already-running, aborted 등 영문 코드), pid, 로그 경로가 한 줄씩 들어간다. 하네스는 콘솔 문구가 아니라 이 파일로 판단한다.

### Unity를 하네스에 붙이기

접속 설정은 아래 순서로 먼저 찾은 것을 쓴다. 셋 다 없으면 에이전트는 아무것도 하지 않는다.

1. 실행 인자: `-harness http://localhost:9000 -harnessName client-1`
2. 환경 변수: `HARNESS_ADDRESS`, `HARNESS_NAME`
3. 에디터 메뉴 **Test → Harness → 에디터 Play 시 하네스 접속** (체크하면 `http://localhost:9000`. 이 PC의 EditorPrefs에 저장)

이름을 지정하지 않으면 Multiplayer Play Mode 플레이어 태그로 정한다(아래 "AI가 에디터를 조작하게 하기"). 태그도 없으면
`UnityEditor-<pid>` / `UnityPlayer-<pid>`가 된다 - 에디터를 여러 개 띄워도 이름이 겹쳐 거부되지 않게 pid를 붙인다.

에이전트 코드는 `#if UNITY_EDITOR || DEVELOPMENT_BUILD`로 감싸져 있어서 정식 빌드에는 들어가지 않는다.

### AI가 에디터를 조작하게 하기 (에디터 브리지)

사람이 에디터를 켜 두면 AI가 빌드 없이 "Unity 코드 수정 → 재컴파일 → 컴파일 오류 확인 → Play → 조작 → Stop"을 돌린다.
한 번만 해 두면 되는 설정:

1. **메뉴 두 개 체크**: **Test → Harness → 에디터 브리지 켜기**(에디터 자체가 `UnityEditor`로 붙음 - Play와 무관)와
   **Test → Harness → 에디터 Play 시 하네스 접속**(Play하면 게임 쪽이 붙음).
2. **역할 태그**: Project Settings → Multiplayer → Playmode에서 태그 `Client`, `Server`를 만들고, Multiplayer Play Mode
   창에서 메인 에디터(Player 1)에 `Client`, 가상 플레이어 하나에 `Server`를 단다. 그러면
   - `Tester`가 버튼 없이 그 역할로 바로 시작한다(`Unity/Assets/Test/TestRoleSettings.cs`). 클라는 `Login` 씬, 서버는
     `ServerBoot` 씬을 연다 - 실제 빌드와 같은 시작 경로(서버는 `IngameServerBootstrap`이 `Ingame` 씬을 열고 서버
     오브젝트를 만든다). Play할 때 에디터에 열려 있는 씬은 `TestScene`이어야 한다.
   - 하네스 이름이 태그를 소문자로 한 것(`client`, `server`)이 된다. 클라를 더 늘리면 `Client2`, `Client3`처럼 태그를 다르게.
   태그가 없으면 지금처럼 역할 버튼이 뜬다(수동 테스트는 그대로).
3. **백그라운드에서도 돌게**: AI는 Claude Code 창에서 일하므로 에디터는 늘 백그라운드다. Preferences → General →
   Interaction Mode를 **No Throttling**으로, Player Settings → Resolution and Presentation → **Run In Background**를 켠다.

| 에이전트 | 명령 | 이벤트 |
|---|---|---|
| `UnityEditor` (에디터 브리지, 메인 에디터만) | `editor.status`, `editor.refresh`, `editor.compile_errors`, `editor.play`, `editor.stop`, `editor.console` | `editor.ready`(reason: startup/reload/no-changes), `editor.compile_failed`, `editor.play_mode` |
| `client` (게임 쪽 클라) | `client.state`, `client.login`, `client.join_match`, `client.cancel_match`, `shop.*`(state/buy/reroll/units/sell), `log.recent` | `client.login_result`, `client.lobby_state`, `client.match_found`, `client.match_failed`, `client.ingame_disconnected`, `phase.changed`, `gold.changed`, `shop.changed`, `network.role` |
| `server` (게임 쪽 인게임 서버) | `ingame.rooms`, `cheat.*`, `log.recent` | `room.drawing_opened`, `room.promoted`, `room.closed`, `server.load`, `player.health_changed` |

- `editor.refresh` 뒤에는 `editor.ready`(성공해서 스크립트를 다시 불러온 뒤 다시 붙음, 또는 바뀐 것 없음)나 `editor.compile_failed`가 온다.
  Play 중에는 거부한다(재컴파일이 Play를 깨뜨림).
- Unity 클라는 계정을 만들 수 없다 - 하네스의 `account_create`로 계정을 만든 뒤 `client.login`에 넘긴다.
- 실제 네트워크 끊김 흉내(재접속 시험)는 4주차 장애 주입에서 다룬다(DevTools `block-port.ps1` 방식, 관리자 권한 필요).

### 시나리오 (검증 절차)

"검증해 봐", "테스트 돌려 봐"처럼 정해진 절차를 확인할 때는 시나리오를 쓴다. 시나리오는 목표(노드)들의 그래프이고
(`HarnessScenarios/`, `[TH_Scenario]` 클래스), 하네스가 목표를 하나씩 내주면서 진행을 통제한다.

```
scenario_start(name)  → 전제·주의·규칙(한 번만) + 첫 목표 + expect
objective_begin(id)   → 목표 내용 + sinceSeq   (이 뒤의 이벤트만 근거)
  ... 아무 도구나 써서 수행/조사 (모든 호출이 이 목표에 묶여 기록됨)
objective_report(id, result, evidenceSeqs, note) → 하네스 판정 + 다음 목표 + expect
  ... 반복 ...
(진행 불가면) scenario_abort(reason, situation) → 정리 목표만
정리 목표 보고 → 하네스가 runs/<실행>/scenario-<이름>-<시각>-report.md 작성
```

- 모든 상호작용은 두 형태뿐이다: (가) 하네스가 질문 → AI가 대답 → 하네스가 기록, (나) AI가 출력 → 하네스가 기록.
- 하네스는 시나리오 도구의 **순서**만 통제한다(제시하지 않은 목표, 고르지 않은 목표의 보고는 거부 + `expect`).
  기능 도구는 언제든 쓸 수 있고 막지 않는다 - 대신 전부 기록된다.
- 판정은 목표에 판정 코드(judge)가 있으면 **하네스가** 그 목표 구간의 기록으로 한다. AI의 보고와 다르면 보고서에 "불일치".
  판정 코드가 없는 목표만 AI 판정이고, Pass에는 그 구간 안의 근거 이벤트 번호가 필수다.
- 분기는 가능하면 하네스가 기록으로 정하고(결과 분기), 정말 상황에 따라 다를 때만 AI가 고른다(선택 분기 - 이유를 남김).
- 보고서는 AI가 아니라 하네스가 쓴다(진행 경로, 목표별 판정/근거/AI 보고/도구 호출, 실행 안 된 목표, 순서 위반).

| 시나리오 | 내용 |
|---|---|
| `full-match` | 서버 기동 → 계정 → Unity Play → 로그인 → 매칭 → 인게임 → 게임 종료 → 로비 복귀 → 부하 복귀 → 정리 |
| `cheat-commands` | 지정 구매+배치 → (합성되면 확인) → 판매 → 재정비 시간 세 모드 → 데미지 → 빨리 끝내고 로비 복귀 → 정리 |

시나리오를 추가할 때의 규칙은 `HarnessCore/Scenarios/TH_Scenario.cs` 주석과 `CLAUDE.md`.

---

## 실행 기록 (`runs/<시각>/`)

| 파일 | 내용 |
|---|---|
| `logs/<서버>.log` | 서버 콘솔 출력. 한 줄에 JSON 하나(시각 UTC, 카테고리, 메시지, 구조화된 값) |
| `logs/<서버>.err.log` | 표준 에러. 처리 안 된 예외로 죽으면 여기 남는다 |
| `logs/<서버>.2.log` | `control-server.ps1`로 재시작한 뒤의 로그 |
| `logs/server-pids.json` | 하네스 모드로 띄운 서버 PID (`stop-servers.ps1 -LogDir`가 읽음) |
| `logs/BrokerInitializer.log` | 브로커 초기화 출력 |
| `logs/start-result.json`, `logs/control-result.json` | DevTools 스크립트의 단계별 결과(하네스가 읽음) |
| `harness-events.log` | (HarnessHost.Smoke) 하네스가 받은 이벤트(접속/끊김, 에이전트 이벤트) |
| `events.jsonl` | (HarnessDaemon) 타임라인 전체 - 에이전트/클라/하네스 이벤트와 AI 도구 호출 요약(source=ai)을 한 줄에 JSON 하나씩 |
| `mcp-calls.jsonl` | (HarnessDaemon) AI가 부른 모든 도구의 요청/응답 전문(seq, 진행 중이던 목표). 실행이 없을 때의 호출은 `runs/mcp-calls-outside-run.jsonl` |
| `scenario-<이름>-<시각>-state.json` / `-report.md` | (HarnessDaemon) 시나리오 진행 상태 / 하네스가 쓴 보고서 |
| `devtools/<번호>-<동작>.txt` | (HarnessDaemon) DevTools 스크립트 콘솔 출력 |
| `build/<번호>-<프로젝트>.txt` | (HarnessDaemon) `server_build`의 프로젝트별 빌드 출력 |

HarnessDaemon의 실행 폴더 이름은 `<날짜>-<시각>-<label>`이다(`run_begin(label)`, 안 열고 서버를 띄우면 `auto`).

**로그 안의 한글은 `\uXXXX`로 저장된다.** JSON 출력의 기본 동작이고, 콘솔 코드페이지 때문에 깨질 일이 없다. 사람이 읽을 땐 PowerShell로 풀어 보면 된다.

```powershell
# 하네스 관련 줄만 (패턴은 글자 그대로 찾도록 -SimpleMatch)
Select-String -Path MasterServer.log -SimpleMatch '하네스'

# 메시지만 한글로 풀어서 보기
Get-Content MasterServer.log | ForEach-Object { $j = $_ | ConvertFrom-Json; "$($j.Timestamp) [$($j.LogLevel)] $($j.Message)" }
```

---

## 디버그 명령 추가하기

규칙(서버/Unity 공통):

- 이름은 `<영역>.<동작>` 소문자 (예: `process.status`, `shop.buy`). 프로세스 안에서 중복 금지.
- **영역은 하네스에 등록된 것만** 쓴다(`HarnessCore/CommandAreas.cs` - editor, client, shop, cheat, ingame, log, process).
  영역은 "무엇을 다루나"(대상)로 정한다 - 동작 단어는 영역이 아니다(`buy.piece`가 아니라 `shop.buy`). 조회는 `state`(한 대상)나
  `list`(여러 개)로. 새 영역이 필요하면 `CommandAreas.cs`에 먼저 등록한다 - 등록 안 된 영역은 `agent_list`가 `unknownAreas`로 드러낸다.
  AI는 `agent_list(query=<영역>)`으로 명령을 찾으므로 영역 이름이 곧 검색 키워드다.
- 파라미터는 하네스가 보낸 인자에서 이름으로 채운다(대소문자 무시). 모르는 인자, 빠진 인자, 타입이 안 맞는 인자는 이유와 함께 거부된다.
- 반환값은 하네스로 간다. 예외를 던지면 `ok=false`와 예외 메시지가 간다.
- **클라 행동 명령은 UI 버튼이 부르는 지점을 부른다.** 그 아래(Statics 등)를 직접 부르면 RPC 구간을 건너뛰어, 진짜 클라에서 나는 버그를 놓친다.
- 행동 명령의 `ok`는 "요청을 보냈다"까지만 뜻한다. 실제로 됐는지는 이벤트로 판정한다.

### 서버

`<서버>/Test/Harness/` 폴더에 클래스를 만든다. 인스턴스 메서드면 호출할 때마다 DI로 생성되니, 필요한 서비스는 생성자로 받는다.

```csharp
using Harness.Agent;

internal sealed class GameAgentHarnessCommands
{
    private readonly GameServerHandler _handler;
    public GameAgentHarnessCommands(GameServerHandler handler) => _handler = handler;

    [HarnessCommand("process.status", Description = "게임 서버 연결 여부와 부하")]
    public object Status() => new { currentLoad = _handler.GetServerInfo()?.CurrentLoad ?? 0 };
}
```

등록 코드는 따로 필요 없다(서버 어셈블리를 자동으로 훑는다). 명령은 서버의 다른 스레드와 동시에 실행되니, 공유 상태는 원래 보호 방식(락 등)을 따른다.

### Unity

`Assets/Scripts/Test/Harness/`에 둔다. 차이점은 세 가지다.

- **static 메서드만** 된다(DI가 없음). 게임 객체는 기존 싱글턴(`Instance_ForClient` 등)으로 찾는다.
- **메인 스레드에서 실행**된다(Update 안). Unity API를 그대로 써도 되지만, 오래 걸리는 일을 하면 그 동안 게임이 멈춘다.
- 파일 전체를 `#if UNITY_EDITOR || DEVELOPMENT_BUILD`로 감싼다.

```csharp
#if UNITY_EDITOR || DEVELOPMENT_BUILD
internal static class AutoChessHarnessCommands
{
    [HarnessCommand("shop.buy", Description = "매물대 구매 요청(구매 버튼과 같은 경로)")]
    private static void Buy(int slotIndex) => AutoChessUIManager.Instance.RequestBuy(slotIndex);
}
#endif
```

Unity 오브젝트 null 검사는 `??` 대신 `== null`로 한다. 파괴된 오브젝트는 `??`에서 null로 잡히지 않는다.

### 기본 제공 명령과 이벤트

| 대상 | 명령 |
|---|---|
| 모든 서버 / Unity | `process.info`, `log.mark(text)` |
| GameAgent | `process.status`(게임 서버 연결, 부하) |
| Unity | `process.status`(네트워크 역할, 씬, 프레임), `log.recent`, 클라 `client.*`/`shop.*`, 서버 `ingame.rooms`/`cheat.*` (위 Unity 표) |

| 이벤트 | 보내는 쪽 | 데이터 |
|---|---|---|
| `harness.attached` / `harness.detached` / `harness.rejected` | 하네스 자신 | 종류, pid, 명령 목록 / 끊긴 사유 / 거부 사유 |
| `network.role` | Unity | none / server / host / client-connecting / client |
| `phase.changed` | Unity 클라 | phase, previous |
| `gold.changed` | Unity 클라 | gold, previous |
| `shop.changed` | Unity 클라 | slots |

Unity 이벤트는 게임 코드를 고치지 않고 매 프레임 상태를 읽어서 만든다(`HarnessWatchers`). 한 프레임 안에서 바뀌었다 돌아오는 값은 못 잡는다.

`log.mark`는 시나리오 단계마다 부르면 좋다. 서버 로그에 `===== 하네스 표시: ... =====` 한 줄이 남아서, 나중에 로그를 볼 때 "여기부터 3단계"가 보인다.

---

## 프로토콜을 바꿀 때

1. `TestHarness/Protos/harness.proto`만 고친다. Server 쪽 사본은 빌드할 때 덮어써진다.
2. 메시지 구조를 바꿨으면 같은 파일의 `PROTOCOL_VERSION_CURRENT` 값을 올린다.
3. TestHarness → Server → Unity 순서로 다시 빌드한다.

한쪽만 다시 빌드해서 버전이 어긋나면, 접속할 때 `프로토콜 버전 불일치`로 거부되고 대상 쪽 로그에 오류가 남는다.

---

## 문제 해결

| 증상 | 원인 / 해결 |
|---|---|
| `BrokerInitializer 실패` (RabbitMQ 연결 중 예외) | 컨테이너를 막 띄운 직후라 RabbitMQ가 아직 준비 중이었음. BrokerInitializer가 60초까지 재시도하게 바뀌었으니 다시 빌드했는지 확인 |
| Unity: `Harness.Protocol`을 찾을 수 없음 | Server를 아직 빌드하지 않음 → `Generated/`가 비어 있음. 최초 세팅 2번 |
| `같은 이름이 이미 접속 중`으로 거부 | 같은 `HARNESS_NAME`/`-harnessName`을 가진 프로세스가 이미 접속해 있음. 이전 실행에서 남은 프로세스가 있는지 확인 |
| `프로토콜 버전 불일치`로 거부 | 위 "프로토콜을 바꿀 때" 3번 |
| 하네스 포트(9000)를 이미 사용 중 | `--port`로 다른 포트 지정 |
| ChatServer가 시작하자마자 죽음 | RabbitMQ가 없으면 `FriendRemovedSubscriber.StartAsync`의 예외로 프로세스가 종료된다(기존 동작). Docker를 띄우거나 `--only`에서 제외 |
| 실패했는데 Docker 컨테이너가 남음 | 하네스 모드 `stop-servers.ps1 -LogDir`는 PID 파일이 없어도 Docker를 내린다. 수동 모드는 예전처럼 바로 끝나므로 `docker-compose -f DevTools\docker-compose.yml down` |
| Unity가 하네스에 접속 못 함 | Console의 `[Harness]` 로그 확인(하네스는 TLS 없는 평문 HTTP/2 - YetAnotherHttpHandler로 붙는 것은 PC에서 확인됨). 메뉴 체크 여부, 하네스가 떠 있는지 확인 |
| `UnityEditor`가 안 붙음 | 메뉴 **에디터 브리지 켜기** 체크 확인. 가상 플레이어(복제 에디터)에서는 일부러 안 켜진다(메인 에디터만) |
| AI가 Unity 명령을 보냈는데 반응이 늦음 | 에디터가 백그라운드라 업데이트가 느려짐. Interaction Mode를 No Throttling으로 |
| (Linux/macOS) 스크립트가 끝난 뒤 서버 로그가 끊김 | 비Windows PowerShell은 출력을 자기가 받아 옮겨 적어서 스크립트가 끝나면 끊긴다. Windows PowerShell은 해당 없음(확인됨) |
| Claude Code `/mcp`에 harness가 failed | HarnessDaemon이 떠 있는지 확인. 하네스를 나중에 띄웠으면 `/mcp`에서 다시 연결 |
| HarnessDaemon: 포트 9000/9100을 열지 못함 | HarnessHost.Smoke나 다른 HarnessDaemon이 떠 있음. 끄거나 `--agent-port`/`--mcp-port`로 바꿈 |
| `servers_start`가 already-running으로 실패 | 지난 실행의 서버가 남아 있음. `harness_status`의 orphans에 나온 runId로 `servers_stop(runId)` |
| `server_build`가 거부됨 | 서버가 떠 있으면 DLL을 덮어쓸 수 없어서 막는다. `servers_stop` 먼저 |

---

## 아직 없는 것

남은 작업은 [`TODO.md`](./TODO.md)에 모아둔다 - 4주 계획(에디터 브리지와 Unity 조작, 시나리오, 타임라인),
HeadlessClient 채팅 등과, 코드로만 확인한 서버/Unity 수정 중 런타임 검증이 필요한 항목.
