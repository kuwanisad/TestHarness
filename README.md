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
│   ├─ HarnessScenarios/        시나리오(검증 절차) 정의 - [TH_Scenario] 클래스 하나 = 노드 그래프 하나, Nodes/ = 공유 노드
│   ├─ HarnessDaemon/           AI용 입구. HarnessCore를 MCP 도구로 노출하는 상주 프로세스
│   └─ runs/                    실행 기록(로그, 이벤트). git 제외
├─ DevTools/                    개발용 도구 - 하네스는 이 스크립트로 서버를 켜고 끈다
│   ├─ start-servers.ps1 / stop-servers.ps1 / control-server.ps1   (-LogDir를 주면 하네스 모드)
│   └─ server-common.ps1        세 스크립트가 같이 쓰는 함수
├─ Server/GameServer/
│   ├─ Shared/Test/Harness.Contracts/   harness.proto 사본 + 생성 코드(→ Unity로 복사)
│   ├─ Shared/Test/Harness.Agent/       서버 공통 에이전트 (접속, 명령 실행, 시작 훅/호스팅 시작으로 주입)
│   ├─ Shared/Test/<서버>.Harness/      서버별 디버그 명령 (예: GameAgent.Harness)
│   └─ Shared/Test/bin/                 하네스 DLL 출력 폴더 - 하네스 모드에서 DevTools가 서버에 주입
└─ Unity/Assets/Scripts/Test/Harness/   Unity 에이전트 + 클라/서버 디버그 명령 (+ Generated/, Editor/ = 에디터 브리지)
```

**동작 흐름**

```
HarnessHost (localhost:9000)
   ▲ 접속(HARNESS_ADDRESS)       ▲ 접속            ▲ 접속
   │  (하네스 모드에서만 주입)
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
- 테스트 코드는 실제 코드와 분리한다 - 걷어내도 아무 문제 없어야 한다(전체 기준: `CLAUDE.md` "테스트 코드 분리").
  서버 코드에는 하네스 흔적이 없다. 하네스 모드일 때만 DevTools가 환경 변수로 에이전트를 주입한다(아래 "서버에 하네스가 붙는 방식").
  Unity는 `Assets/Scripts/Test/`가 `#if UNITY_EDITOR || DEVELOPMENT_BUILD`로 정식 빌드에서 빠진다.

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
   서버 프로젝트는 하네스를 참조하지 않으므로 서버만 빌드하면 하네스 쪽은 안 빌드된다 - 솔루션 전체를 빌드하거나
   `Shared/Test/Harness.Agent`, `Shared/Test/<서버>.Harness`를 따로 빌드한다(하네스의 `server_build`는 같이 빌드한다).
   결과는 `Server/GameServer/Shared/Test/bin/<구성>/net8.0/`에 모인다.

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
| 상태/실행 | `harness_status`, `run_begin(label)`, `run_end`, `call_continue(callId)` - 응답 한도(50초)를 넘겨 `pending`으로 온 호출을 이어서 기다림 |
| 서버 | `infra_status`, `server_build(names)`, `servers_start(only, infra, readyTimeoutSeconds)`, `server_control(name, kill/start/restart)`, `servers_stop(stopInfra=true, runId)` - 기본은 Docker 컨테이너까지 제거 |
| 에이전트 | `agent_list(query, agent)` - query 없이 부르면 영역 요약, `query=<영역>`이면 그 영역 명령, `agent_command(agent, command, args)` |
| 헤드리스 클라 | `account_create`, `client_create`, `client_login`, `client_enter_lobby`, `client_join_match`, `client_cancel_match`, `client_logout`, `client_drop`, `client_reconnect`, `client_state`, `client_close` |
| 관찰 | `wait_for(source, names, sinceSeq, timeoutSeconds)`, `events_query`, `logs_query(server, minLevel, contains, sinceUtc)` |
| 시나리오 | `scenario_list`, `scenario_start(name, mode)`, `node_run(node, reason)`, `node_judge(node, result, evidenceSeqs, note)`, `scenario_finish(reason)` |

- 행동 도구는 전부 `sinceSeq`(행동 직전 이벤트 번호)를 돌려준다. 결과는 그 값으로 `wait_for`를 걸어 기다린다.
- **응답 시간 한도 50초**: Claude Code는 도구 호출 하나(HTTP 요청 하나)를 약 60초에 끊는다. 그래서 어떤 도구든 50초 안에 안 끝나면
  하네스가 `{pending: true, callId}`로 먼저 응답하고 작업은 계속 돌린다. AI가 `call_continue(callId)`로 다시 물으면 하네스가 그 요청을
  붙잡고 있다가 끝나는 순간 원래 결과로 응답한다(시간 정보는 알려주지 않는다). 끝난 결과는 원래 도구 이름으로도 기록된다
  (`HarnessDaemon/Mcp/McpPendingCalls.cs`). 도구를 만들 때 따로 할 일은 없다.
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
| `--response-seconds <n>` | (검증용) 도구 응답 시간 한도(기본 50). 평소엔 쓰지 않는다 - 60 이상이면 Claude Code가 끊는다 |

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
missing-harness, already-running, aborted 등 영문 코드), pid, 로그 경로가 한 줄씩 들어간다. 하네스는 콘솔 문구가 아니라 이 파일로 판단한다.

#### 서버에 하네스가 붙는 방식

서버 프로젝트(csproj, Program.cs)에는 하네스 참조가 없다. `-HarnessAddress`를 주면 `server-common.ps1`이 그 서버 프로세스에만 환경 변수를 더 넣는다.

| 환경 변수 | 누가, 언제 | 하는 일 |
|---|---|---|
| `DOTNET_STARTUP_HOOKS=<Shared/Test/bin/<구성>/net8.0/Harness.Agent.dll>` | .NET 런타임, 서버 `Main` 전 | 하네스 폴더에서 어셈블리를 이름으로 찾게 함 (`HarnessStartupHook.cs`) |
| `ASPNETCORE_HOSTINGSTARTUPASSEMBLIES=Harness.Agent` | ASP.NET Core, 서버의 `WebApplication.CreateBuilder` 안 | 에이전트 등록(`AddHarnessAgent`) + 서버별 명령 어셈블리(`<서버>.Harness`) 읽기 (`HarnessHostingStartup.cs`) |
| `HARNESS_ADDRESS`, `HARNESS_NAME` | 에이전트 | 접속할 하네스 주소, 하네스에서 부를 이름 |

하네스 DLL이 없으면 서버를 띄우지 않고 `detail=missing-harness`로 실패한다. `-Build`(dotnet run)면 하네스 쪽도 먼저 빌드한다
(출력은 `<폴더>\<서버>.harness-build.log`). 훅 없이 `ASPNETCORE_HOSTINGSTARTUPASSEMBLIES`만 있으면 오류 없이 하네스만 빠지므로,
에이전트가 안 붙으면 서버 로그에 `하네스 에이전트 시작` 줄이 있는지부터 본다.

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
2. **역할 태그**: Project Settings → Multiplayer → Playmode에서 태그 `Client1`, `Client2`, …, `Server`를 만들고, Multiplayer Play Mode
   창에서 메인 에디터(Player 1)에 `Client1`, 가상 플레이어 하나에 `Server`를 단다(클라가 더 필요한 시나리오면 다른 가상 플레이어에
   `Client2` …). 그러면
   - 하네스 이름이 태그를 소문자로 한 것(`client1`, `client2`, `server`)이 된다. 시나리오는 이 이름으로 대상을 선언한다
     (`s.UnityClient("client1", startScene: Scenes.Login)`).
   - **하네스 Play**는 빈 씬 `HarnessBoot`(`Unity/Assets/Test/HarnessBoot.unity`)에서 출발한다. 시나리오가 씬을 선언한다:
     `s.UnityEditor(bootScene: Scenes.HarnessBoot)`, `s.UnityClient("client1", startScene: Scenes.Login)`,
     `s.UnityServer(startScene: Scenes.ServerBoot)`. 흐름: 시나리오 시작 때 하네스가 모든 에디터(메인 + MPPM 가상 플레이어)의 Play 시작 씬을
     HarnessBoot로 정한다(준비 단계, `editor.set_play_start` - 에디터에 열린 씬은 바꾸지 않음) → `UnityPlay`가 Play → 플레이어마다
     `scene.load(시작 씬)` → 시나리오 끝에 시작 씬을 되돌림(지속 조작 `play_start`, `editor.clear_play_start`). 실제 빌드와 같은 시작 경로다(서버는
     `ServerBoot` → `IngameServerBootstrap`이 `Ingame` 씬을 열고 서버 오브젝트를 만든다). 열린 씬에 저장 안 한 변경이 있으면 Play 때 저장 창이
     뜨므로 `editor.play`가 거부한다. 왜 빈 씬인지, 왜 열린 씬을 바꾸지 않는지는 `Unity/ARCHITECTURE.md` "HarnessBoot 씬". 하네스가 되돌리지 못하고
     끝났으면 Unity 메뉴 Test → Harness → Play 시작 씬 되돌리기.
   - ⚠ **하네스의 씬 이름(`HarnessShared/HarnessConstants.cs`의 `HarnessConstants.Scenes`)과 Unity 씬 파일 이름이 같아야 한다**(확장자 뺀 이름,
     대소문자 포함). 에디터 브리지와 에이전트가 이름으로 찾는다. Unity에서 씬 이름을 바꾸면 `HarnessConstants.Scenes`도 같이 고친다. 확인은
     `agent_command(UnityEditor, editor.scenes)`. 플레이어 시작 씬은 Scene List에도 있어야 한다(`scene.load`).
   - **사람이 손으로 Play**할 때는 TestScene을 열고 Play한다 - `Tester`가 태그대로 역할 씬(클라 `Login`, 서버 `ServerBoot`)을 바로 연다
     (`Unity/Assets/Scripts/Test/Local/TestRoleSettings.cs`). 태그가 없으면 역할 버튼이 뜬다.
3. **백그라운드에서도 돌게**: AI는 Claude Code 창에서 일하므로 에디터는 늘 백그라운드다. Preferences → General →
   Interaction Mode를 **No Throttling**으로, Player Settings → Resolution and Presentation → **Run In Background**를 켠다.

| 에이전트 | 명령 | 이벤트 |
|---|---|---|
| `UnityEditor` (에디터 브리지, 메인 에디터만) | `editor.status`, `editor.refresh`, `editor.compile_errors`, `editor.play`, `editor.set_play_start`(scene), `editor.clear_play_start`, `editor.scenes`, `editor.stop`, `editor.console` | `editor.ready`(reason: startup / reload_compile / reload_play / no-changes), `editor.compile_failed`, `editor.play_mode` |
| `client` (게임 쪽 클라) | `client.state`, `client.login`, `client.join_match`, `client.cancel_match`, `shop.*`(state/buy/reroll/units/sell), `log.recent` | `client.login_result`, `client.lobby_state`, `client.match_found`, `client.match_failed`, `client.ingame_disconnected`, `phase.changed`, `gold.changed`, `shop.changed`, `network.role` |
| `server` (게임 쪽 인게임 서버) | `ingame.rooms`, `cheat.*`, `log.recent` | `room.drawing_opened`, `room.promoted`, `room.closed`, `server.load`, `player.health_changed` |

- `editor.refresh` 뒤에는 `editor.ready`(성공: `reload_compile` - 재컴파일 뒤 다시 붙음, 바뀐 것 없음: `no-changes`)나 `editor.compile_failed`가 온다.
  Play 진입도 도메인 리로드를 일으켜 브리지가 잠깐 끊겼다 붙는다(`reload_play` - Unity 기본 설정 Reload Domain). 환경 변화(Unity 재컴파일)로 보는 것은 `reload_compile`뿐.
  Play 중에는 거부한다(재컴파일이 Play를 깨뜨림).
- Unity 클라는 계정을 만들 수 없다 - 하네스의 `account_create`로 계정을 만든 뒤 `client.login`에 넘긴다.
- 실제 네트워크 끊김 흉내(재접속 시험)는 4주차 장애 주입에서 다룬다(DevTools `block-port.ps1` 방식, 관리자 권한 필요).

### 시나리오 (검증 절차)

"검증해 봐", "테스트 돌려 봐"처럼 정해진 절차를 확인할 때는 시나리오를 쓴다. 시나리오는 **노드 그래프 + 맥락**이다(`HarnessScenarios/`,
`[TH_Scenario]` 클래스, 노드는 `HarnessScenarios/Nodes/TH_Nodes.cs`에서 시나리오끼리 공유). **노드는 하네스가 실행하고 판정하고,
AI는 전체 그래프를 보고 다음에 돌 노드를 고른다** - 실행(정확해야 하는 반복)은 코드가, 판단(경로, 이상 현상 조사)은 AI가.

```
scenario_start(name, mode=ai) → 맥락·전제·주의·규칙(한 번만) + 전체 그래프 + 지금 상태 + 고를 수 있는 노드 + expect
node_run(node, reason)        → 하네스가 실행(묶음 노드는 자식부터 차례로 - 후위순회) + 판정 → 결과, 바뀐 상태, 환경, 다음 후보
  (judgedBy=ai 노드면) node_judge(node, result, evidenceSeqs, note)
  ... AI가 맥락을 보고 반복 ...
scenario_finish(reason)       → 하네스가 정리 노드 → 보고서 → 알림음 → 실행(run) 닫기(서버/헤드리스 클라가 남았으면 열어 둠)
                                노드 실행 중(자동 실행 중)이면 일찍 끝내기 - 실행 중인 노드는 Stopped(판정 안 함), 그 뒤 노드는 안 돎
```

- **대상**: 시나리오는 테스트 대상을 인스턴스로 선언한다(`HarnessScenarios/Targets/TH_Targets.cs` - `s.UnityClient("client1")`,
  `s.HeadlessClient("hc1")`, `s.UnityServer()`, `s.UnityEditor()`). 대상을 받는 노드는 노드 함수이고 ID가 `이름@대상`이다
  (`Login@client2`, `EnterGame@client1+client2`). 공통 동작(계정, 로그인, 매칭) 노드는 Unity/헤드리스 클라 모두, 인게임 노드(cheat, 상점,
  게임 끝)는 Unity 클라만 받는다(헤드리스를 넘기면 컴파일 안 됨). 선언한 대상의 전제(MPPM 태그 등)는 자동으로 붙는다.
- **노드**: 행동 노드(Auto = 하네스가 하는 도구 호출 + 판정 코드)와 묶음 노드(자식을 순서대로). 상태는 **칸별**이다 - 전역 칸
  (ServersDown, ServersUp, Playing)과 클라마다 한 칸(Offline, Login, Lobby, Ingame, 헤드리스는 Matched). 예:
  `{ global: Playing, client1: Lobby, client2: Login }`. 노드의 From은 적은 칸이 전부 맞아야 시작, To는 통과하면 바뀌는 칸이고, 하네스가
  이동을 검사한다(시작할 수 없는 노드는 어느 칸이 안 맞는지와 함께 거부, 묶음의 자식과 정리 노드는 직접 못 고름). 묶음이 중간에
  실패하면 통과한 자식이 바꾼 칸까지는 반영한다.
- **값**: 노드끼리 넘기는 값도 대상별 칸이다(`r.Values.For(c)` - 계정, playerId, 방). 인게임 서버에 방이 여럿일 수 있어 cheat 명령은
  그 클라의 방(roomId - EnterGame이 `ingame.rooms`에서 playerId로 찾아 둠)으로 보낸다.
- **판정**: 판정 코드가 노드 구간의 기록만 보고 한다. 도구 호출은 노드 실행(runner)의 것만 본다 - AI가 조사하려고 부른 도구는 판정에 안 쓰인다.
  코드로 판정할 수 없는 노드만 `AiJudged()`(시나리오의 `AllowAiJudge`가 true일 때만) - AI가 `node_judge`로 보고, 근거 seq 필수.
- **AI가 언제든 끝낸다**: 맥락(`s.Context`)을 보고 충분하면 `scenario_finish`. 특정 노드까지 갈 필요 없다. 예정보다 일찍 끝났는지는
  보고서의 맥락과 "노드별 결과"로 본다.
- **일찍 끝내기**: 노드 실행 중(또는 자동 실행 중)에도 `scenario_finish`를 받는다. 실행 중인 행동 노드는 멈추고 `Stopped`(판정 안 함 - 실패도
  미확인도 아님), 묶음 노드는 돈 자식은 그 결과대로·멈춘 자식은 Stopped·안 돈 자식은 '실행 안 한 자식'. 노드를 더 돌리지 않고(이어서 진행하지 않음 -
  멈춘 노드가 게임을 어중간한 상태로 두므로) 정리 노드 → 보고서(실제로 돈 방문만으로, 머리에 "일찍 끝냄") → 경고음. 정리 노드는 멈추지 않는다.
- **환경 변화 = 테스트 대상의 변화**(분류기는 `HarnessCore/Scenarios/TH_EnvironmentChanges.cs`, 게임 쪽 항목은 `HarnessScenarios/Knowledge/TH_ProjectKnowledge.cs`에서 등록): 서버 빌드·기동·종료·개별 재기동, Unity 재컴파일,
  계정 생성(나중의 계정 삭제·친구 추가/삭제 명령), 그리고 **하네스 밖의 빌드**(노드 실행 직전마다 서버 DLL 수정 시각 비교). 노드와 노드 사이에
  생기면 환경 번호가 올라가고 그 전 결과는 "이전 환경"(방문 기록이 초기화된 것과 같음 - 결과는 보고서에 남음), 다음 응답에 알린다.
  노드 실행 중의 변화는 그 노드의 의도된 행동이라 세지 않는다. 테스트 조작(`cheat.time_scale` 등)은 환경 변화가 아니다.
- **AI 지시 없음**: 노드 실행 중이 아닌데 AI 호출이 `s.IdleTimeout`(기본 2분) 동안 없으면 하네스가 정리하고 끝낸다(토큰이 떨어진 경우 등).
- **자동 실행(AI 없이)**: 시나리오의 `s.AutoPlan`(코드로 쓴 경로)대로 하네스가 끝까지 돈다. AI 판정 노드는 미확인으로 남는다.
  - MCP: `scenario_start(name, mode=auto)` - 끝날 때까지 pending → call_continue.
  - 명령줄: `dotnet run --project HarnessDaemon -- run <시나리오>` - MCP는 열지 않는다(에이전트 9000만). 종료 코드 0 = 전부 통과, 1 = 아님, 2 = 시작 못 함.
    하네스 데몬이 떠 있으면 포트가 겹치니 먼저 끈다.
- 끝나면(어떤 경우든 정리 노드까지 - 하네스가 판단) 하네스 PC에서 소리: 전부 통과면 완료음(tada.wav), 아니면 경고음. 시나리오마다 `s.PlayFinishSound`.
- 보고서는 하네스가 쓴다: 맥락, 방문 순서(노드·환경·판정·고른 이유), 환경 변화, 노드별 결과(현재 환경/이전 환경), 방문 상세(자식 결과, 메모),
  순서 위반, 노드 밖에서 일어난 일(노드 사이에 대상 에이전트가 끊기거나 클라 인게임 접속이 끊기거나 사람 플레이어가 탈락 - 판정에 안 들어가므로 머리에 경고, 목록은
  `HarnessCore/Scenarios/TH_OutsideEvents.cs`의 등록부 - 게임 쪽 항목은 `TH_ProjectKnowledge`에서 등록). 방문마다 그 구간의 이벤트 + 서버/Unity 로그를 `trace/` 파일로 남기고, 통과 못 한 방문에는 요약(이벤트 + 경고 이상 로그, 마지막 30줄).
  보고서와 구간 기록의 시각은 이 PC의 지역 시각이다(원본 events.jsonl, 로그 json은 UTC - 맞춰 볼 때는 seq로).
  메인 에디터는 Play 중 게임 에이전트가 붙어 있으면 에디터 브리지가 로그를 보내지 않는다(같은 로그가 두 번 남지 않음).

| 시나리오 | 내용 |
|---|---|
| `full-match` | 대상 client1. 노드: ServersUp, AccountReady, UnityPlay, Login, EnterGame, WaitGameEnd, FinishGameFast, LoadZero (+ Cleanup, 대상 노드는 @client1). AI 판정 노드 없음 |
| `cheat-commands` | 대상 client1. 노드: ReadyIngame(묶음: ServersUp→AccountReady→UnityPlay→Login→EnterGame), BuyPlace, MergeCheck(AI 판정), Sell, PlanningCurrentOnly/SettingOnly/Rescale, Damage, FinishGameFast (+ Cleanup) |
| `multi-client` | 대상 client1, client2(Unity), hc1(헤드리스). 노드: ServersUp, AccountReady×3, UnityPlay@client1+client2, Login×3, EnterGame@client1+client2, FinishGameFast×2, LoadZero@client1+client2 (+ Cleanup). 헤드리스는 로그인까지 |
| `reconnect` | 대상 client1. 노드: ServersUp, AccountReady, UnityPlay, Login, EnterGame, BriefDrop(5초 순간 끊김 → 끊김 없이 진행 수신), Reconnect(지금 연결만 끊기 hold=false → 감지 뒤 스스로 재진입 → 진행 수신), Cut(끊어 두기 hold → 감지, Ingame→**Disconnected**, 끊김을 다음 노드까지 남김), Restore(Disconnected에서 다시 잇기 → 재진입 → 진행 수신, Cut 바로 다음 - 재진입 시도 약 10초 안에), GraceExpire(Disconnected에서 끊긴 채 → 서버 탈락, 클라 상태는 메모 - 처리 미정, 상태 Abandoned) (+ Cleanup). 진행 수신 = 재정비를 기다려 국면을 넘기고(cheat.skip_planning) 클라가 phase.changed(PvP)를 받는지. 끊긴 동안의 게임 일을 보는 노드는 Disconnected에서 고르게 만든다(서버 유예 약 20초 안에) |

시나리오와 노드를 추가할 때의 규칙은 `HarnessCore/Scenarios/TH_Scenario.cs`, `TH_Node.cs` 주석과 `CLAUDE.md`.

### 지속 조작 (배속, 설정값 ...)

한 번 보내면 끝나는 명령이 아니라 켜 두면 남는 조작을 하네스가 추적하고 되돌린다(`HarnessCore/OverrideManager.cs`). 전용 도구는 없다 -
도구 호출 기록을 지켜보다가 목록(`HarnessScenarios/Knowledge/TH_ProjectKnowledge.cs`의 `TH_OverrideKinds`)에 있는 명령이 성공하면 "켜짐", 값이 처음 값으로 돌아가면 "꺼짐".
그래서 노드 실행이든 AI의 `agent_command`든 같은 경로로 잡힌다.

| 종류 | 명령 | 범위 | 되돌리기 | 조회(확인 대기일 때만) |
|---|---|---|---|---|
| `time_scale` | `cheat.time_scale` | 인게임 서버 프로세스 전체 | 처음 배속(응답의 previous)으로 | `process.status`의 timeScale |
| `planning_setting` | `cheat.planning_duration`(SettingOnly, Rescale) | 그 방(roomId) | 처음 설정값(응답의 settingBefore)으로 | `ingame.rooms`의 그 방 planningDuration(목록에 없으면 사라짐) |
| `fault` | `fault.add` | 규칙(ruleId) | `fault.remove` | `fault.list`(목록에 없으면 사라짐) |
| `play_start` | `editor.set_play_start` | 모든 에디터의 Play 시작 씬(에디터 브리지가 끊겨도 남음) | `editor.clear_play_start` | `editor.status`의 playStartScene |
| `net_condition` | `net.condition` | 그 플레이어 프로세스의 NGO 연결 조건 | 처음 조건으로 `net.condition` | `net.state`의 condition |
| `net_link` | `net.link`(connected=false) | 그 플레이어 프로세스의 NGO 연결 | `net.link {connected: true}` | `net.state`의 connected |

- **수명**: 노드가 건 것은 그 노드가 끝날 때(판정 뒤) 되돌린다 - 다음 노드까지 남기려면 노드에서 `r.KeepOverrides()`. 다른 쪽이 건 조작의 값을
  노드가 바꿨으면 노드 끝에 노드 직전 값으로만 돌린다. 시나리오 중에 AI가 건 것(과 남긴 것)은 시나리오가 끝날 때 정리 노드 전에 되돌린다.
  시나리오 없이 AI가 건 것은 직접 되돌릴 때까지. 공통으로 대상 프로세스가 끊기면(`harness.detached`) "대상과 함께 사라짐", 범위가
  사라지면(방이 닫힘 `room.closed`) "범위 사라짐"으로 기록만 한다.
- **대상이 끊기면**: 종류마다 다르다(`EndsWithAgent`). 배속, 재정비 설정, 장애 규칙은 프로세스(Play 세션)와 함께 사라지므로 "대상과 함께
  사라짐"으로 끝낸다. `play_start`는 에디터 설정이라 Play 진입 도메인 리로드로 브리지가 잠깐 끊겨도 남는다 - 계속 추적해 시나리오 끝에 되돌린다
  (2026-10-23 PC 실행에서 끊김을 사라짐으로 봐서 되돌리지 못했던 것).
- **되돌리기가 실패하면**: 실패로 끝내지 않고 "확인 대기"(`override.unconfirmed`)로 켜 둔다 - 범위가 이미 사라졌을 수 있기 때문(예: FinishGameFast가
  방을 닫았는데 `room.closed`가 되돌리기보다 0.1초 늦게 옴 - 2026-10-23 PC 실행). 그 뒤 범위 사라짐/대상 끊김 이벤트가 오면 그대로 끝난다.
- **노드 선언(Forbids/Needs)**: 노드가 시작할 때 남아 있으면 안 되는/켜져 있어야 하는 종류(`.Forbids(OverrideKinds.TimeScale)` - 예: WaitGameEnd
  "조작 없이"). 선언한 노드만 시작 직전에 켜진 조작과 비교하고, 그때 선언한 종류 중 확인 대기인 조작만 조회 명령으로 확인한다(조회 명령은 AI도
  상태를 볼 때 쓰는 그 명령 - 하네스 전용 조회는 만들지 않는다). 기록으로 확실한 조작은 조회하지 않는다. 어긋나면 시나리오의
  `s.OverrideMismatch`대로 - 기본 Reject(노드 거부, 묶음 자식이면 그 자식 Fail), 남은 조작이 있어도 진행해야 하는 시나리오만 Warn(돌리고 경고).
- **보이는 곳**: `harness_status`의 overrides(켜진 것, 확인 대기면 unconfirmed), 타임라인 `override.applied / changed / reverted / unconfirmed /
  confirmed`, `scenario.override_mismatch`, 시나리오 보고서의 "지속 조작" 절(되돌리기 실패 뒤 끝난 것은 그 이유도).
- 테스트 조작이라 환경 변화(테스트 대상의 변화)가 아니다. 새 지속 조작 명령이 생기면 `TH_OverrideKinds`에 넣는다.
- 종류 `fault`(아래 경계 장애 규칙)는 되돌리기가 `fault.remove`이고, 실제로 걸린 횟수(`fault.hit`)를 센다 - 한 번도 안 걸린 채 끝나면
  `override.unused`로 알리고 보고서에 "0 - 한 번도 안 걸림"으로 남는다.

### 경계 장애 규칙 (fault.*)

서버끼리, 클라↔서버로 오가는 gRPC 메시지를 **경계에서** 고치거나 버리거나 늦추거나 실패시킨다 - 게임 코드는 그대로다
(`Server/GameServer/Shared/Test/Harness.Agent/Fault/`). 하네스 모드에서 주입된 에이전트가 gRPC 인터셉터를 서버 자신의 인터셉터(인증 등)
**안쪽**에 끼운다(`PostConfigure<GrpcServiceOptions>`). 모든 gRPC 호출의 받는 쪽이 우리 서버라 서버마다 이것 하나로 양방향이 덮인다.

| 명령(agent_command, 그 서버에) | 하는 일 |
|---|---|
| `fault.methods {query?}` | 그 서버가 받는 gRPC 메서드, 종류, 요청/응답 메시지의 필드 경로(protobuf JSON 이름) |
| `fault.add {method, dir, action, playerId?, match?, rewrite?, delayMs?, failStatus?, times?, seconds?}` | 규칙을 건다 → ruleId. 메서드와 경로는 걸기 전에 확인(오타면 거부) |
| `fault.remove {ruleId}` / `fault.list` | 끄기 / 켜진 규칙과 걸린 횟수 |

- `dir`: `request`(그 서버로 들어오는 것) / `response`(그 서버가 내보내는 것 - 스트림이면 메시지마다).
- `action`: `rewrite`(경로 값 바꿔 통과), `drop`(요청이면 처리기가 안 돌고 상대는 제한 시간까지 기다림, 응답이면 서버는 일을 했는데 응답만
  사라짐 - 스트림이면 그 메시지만), `delay`(delayMs 붙잡음), `fail`(gRPC 오류 - 응답이면 서버는 일을 했는데 상대는 오류).
- `playerId`: 그 플레이어의 호출만(서버의 인증 인터셉터가 남긴 `UserState["playerId"]` - 로비처럼 인증하는 서버에서). `match`:
  `{경로: 값 | {"$exists": bool} | {"$in": [...]}}`.
- 이벤트: 걸릴 때마다 `fault.hit {ruleId, method, dir, action, before, after}`, 끝나면 `fault.ended {ruleId, reason: times|expired|removed}`.
  판정 코드는 먼저 "장애가 실제로 걸렸나"(fault.hit)를 보고 그다음 시스템 반응을 본다.
- 수명은 지속 조작 틀이 맡는다(노드가 건 규칙은 노드 끝, 시나리오 중 AI가 건 규칙은 시나리오 끝에 `fault.remove`).
- 예: 잘못된 방 안내 = 로비 `/LobbyControl/RequestMatch` response, `match {"matchFound": {"$exists": true}}`, `rewrite {"matchFound.gameServerAddress": "..."}`, `times 1`.
- NGO(클라↔Unity 인게임) 구간은 아래 "NGO 통신 조건(net.*)". 받는 쪽의 거부 검증은 규칙 대신 헤드리스 클라가 직접 잘못된 입력을 보낸다.

### NGO 통신 조건 (net.* - Unity 에이전트)

클라↔인게임 서버(NGO) 경계의 장애는 Unity 공식 Network Simulator(Multiplayer Tools 패키지 `com.unity.multiplayer.tools`)로 만든다. 조작 코드는
`Unity/Assets/Scripts/Test/Harness/NetHarnessCommands.cs`에만 있고, 시뮬레이터 컴포넌트는 그 파일이 실행 중에 만드는 전용 오브젝트에 붙는다
(게임 오브젝트/씬/프리팹은 그대로). 조건은 **그 플레이어 프로세스의 모든 NGO 전송**에 걸린다 - MPPM에서는 곧 플레이어 단위.

| 명령 | 하는 일 |
|---|---|
| `net.condition {delayMs, jitterMs, lossPercent, lossInterval}` | 연결 조건(다 0 = 정상). NGO 연결 전에 걸어도 연결될 때 적용. 응답 previous/current |
| `net.link {connected, hold}` | false = 끊기(송수신 손실 100% - 소켓은 안 닫음, NGO 끊김 제한 시간(약 30초 - 10-23 PC 측정)보다 오래 두면 진짜 끊김), true = 다시 잇기. hold(기본 true)면 다시 잇기 전까지 유지 - 재진입으로 NGO 전송이 새로 생겨도 다시 끊는다(`cause=reapplied`, 실제 네트워크 단절처럼 재진입도 막힘). hold=false면 지금 연결에만(감지 뒤 네트워크가 돌아온 경우 - 클라가 스스로 재진입). NGO 연결 전이면 끊기 거부 |
| `net.lag_spike {seconds}` | 그 시간 동안 끊었다가 다시 잇기(스스로 끝남 - 지속 조작 아님) |
| `net.state` | available(NGO 전송이 있는지), connected, condition |

- 이벤트: `net.link_changed {connected, cause: link | lag_spike_start | lag_spike_end | reapplied}`. `net.state`의 holdDisconnected = 끊어 둔 채 유지 중.
- `net.condition`, `net.link`는 지속 조작(`net_condition`, `net_link`)이라 노드 끝/시나리오 끝에 하네스가 되돌린다. Play가 끝나면 같이 사라진다.
  재접속 판정처럼 끊긴 채 시작하면 안 되는 노드는 `.Forbids(OverrideKinds.NetLink, OverrideKinds.NetCondition)`.
- 에디터와 개발 빌드에서만 효과가 있다(NGO UnityTransport가 그때만 시뮬레이터 단계를 넣음). 하네스 코드도 같은 조건으로만 컴파일된다.
- NGO 2.x의 `UnityTransport` 디버그 시뮬레이터(`SetDebugSimulatorParameters`)는 효과 없음으로 바뀌어 쓰지 않는다. RPC 단위로 고르는 장애는 없다.

---

## 실행 기록 (`runs/<시각>/`)

| 파일 | 내용 |
|---|---|
| `logs/<서버>.log` | 서버 콘솔 출력. 한 줄에 JSON 하나(시각 UTC, 카테고리, 메시지, 구조화된 값) |
| `logs/<Unity 에이전트>.log` | (HarnessDaemon) Unity 로그 - `client.log`, `server.log`, `UnityEditor.log`. Unity가 하네스로 보낸 로그(AgentLog)를 서버 로그와 같은 형식(+Pid)으로 쓴다. 열린 실행이 없을 때의 로그는 버린다 |
| `logs/<서버>.err.log` | 표준 에러. 처리 안 된 예외로 죽으면 여기 남는다 |
| `logs/<서버>.2.log` | `control-server.ps1`로 재시작한 뒤의 로그 |
| `logs/server-pids.json` | 하네스 모드로 띄운 서버 PID (`stop-servers.ps1 -LogDir`가 읽음) |
| `logs/BrokerInitializer.log` | 브로커 초기화 출력 |
| `logs/start-result.json`, `logs/control-result.json` | DevTools 스크립트의 단계별 결과(하네스가 읽음) |
| `harness-events.log` | (HarnessHost.Smoke) 하네스가 받은 이벤트(접속/끊김, 에이전트 이벤트) |
| `events.jsonl` | (HarnessDaemon) 타임라인 전체 - 에이전트/클라/하네스 이벤트와 AI 도구 호출 요약(source=ai)을 한 줄에 JSON 하나씩 |
| `mcp-calls.jsonl` | (HarnessDaemon) 모든 도구 호출의 요청/응답 전문(seq, 실행 중이던 노드, actor: ai = AI / runner = 노드를 실행한 하네스). 응답 전에 클라가 끊은 호출도 오류("응답 전에 호출이 끊김")로 남는다. 실행이 없을 때의 호출은 `runs/mcp-calls-outside-run.jsonl` |
| `scenario-<이름>-<시각>-state.json` / `-report.md` | (HarnessDaemon) 시나리오 진행 상태 / 하네스가 쓴 보고서 |
| `trace/scenario-<이름>-<시각>-<번호>-<노드>.txt` | (HarnessDaemon) 방문(노드 실행 한 번) 구간의 이벤트 + 서버/Unity 로그(시각순) |
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
- **영역은 하네스에 등록된 것만** 쓴다(공통 영역 log, process, status는 `HarnessCore/CommandAreas.cs`, 게임 영역 editor, client, shop, cheat, ingame, net, scene 등은 `HarnessScenarios/Knowledge/TH_ProjectKnowledge.cs`에서 등록).
  영역은 "무엇을 다루나"(대상)로 정한다 - 동작 단어는 영역이 아니다(`buy.piece`가 아니라 `shop.buy`). 조회는 `state`(한 대상)나
  `list`(여러 개)로. 새 영역이 필요하면 `TH_ProjectKnowledge`에 먼저 등록한다 - 등록 안 된 영역은 `agent_list`가 `unknownAreas`로 드러낸다.
  AI는 `agent_list(query=<영역>)`으로 명령을 찾으므로 영역 이름이 곧 검색 키워드다.
- 파라미터는 하네스가 보낸 인자에서 이름으로 채운다(대소문자 무시). 모르는 인자, 빠진 인자, 타입이 안 맞는 인자는 이유와 함께 거부된다.
- 반환값은 하네스로 간다. 예외를 던지면 `ok=false`와 예외 메시지가 간다.
- **클라 행동 명령은 UI 버튼이 부르는 지점을 부른다.** 그 아래(Statics 등)를 직접 부르면 RPC 구간을 건너뛰어, 진짜 클라에서 나는 버그를 놓친다.
- 행동 명령의 `ok`는 "요청을 보냈다"까지만 뜻한다. 실제로 됐는지는 이벤트로 판정한다.
- **결과를 보내기 전에 연결이 사라질 수 있는 동작**(Unity Play 시작/중지, 재컴파일처럼 도메인 리로드를 일으키는 것, 자기 프로세스 종료 등)은
  명령 안에서 바로 하지 말고 `HarnessCommandContext.AfterReply(() => ...)`로 등록한다. 모든 명령이 같은 경로(실행 → 결과 전송 → 등록한 일)를
  타고, 등록한 일은 결과가 실제로 전송된 뒤(연결이 없으면 2초 뒤) 실행된다. 명령이 실패하면 실행하지 않는다. 서버는 스레드 풀, Unity는 메인 스레드에서 실행.

### 서버

`Server/GameServer/Shared/Test/<서버>.Harness/` 프로젝트에 클래스를 만든다(없으면 `GameAgent.Harness`처럼 만든다 - 프로젝트 이름이
`<서버 어셈블리 이름>.Harness`여야 에이전트가 찾는다. 서버 프로젝트를 `Private="false"`로 참조하고 `AutoBattlerServer.slnx`에 추가).
서버는 이 프로젝트를 모른다 - 서버의 공개된 것만 들여다본다. 인스턴스 메서드면 호출할 때마다 서버의 DI로 생성되니, 필요한 서비스는 생성자로 받는다.

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

등록 코드는 따로 필요 없다(`<서버>.Harness` 어셈블리를 자동으로 훑는다). 명령은 서버의 다른 스레드와 동시에 실행되니, 공유 상태는 원래 보호 방식(락 등)을 따른다.

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

**Unity 로그와 하네스**: Unity 로그는 하네스로도 가서 실행 폴더 `logs/<에이전트>.log`와 시나리오 보고서(구간 기록, 경고 이상 요약)에 들어간다.
게임 코드가 상황을 알리는 로그는 `DebugManager.Log(GameLogType, 내용)`(`Assets/Scripts/Test/DebugManager.cs` - 게임 코드가 부를 수 있는 유일한
테스트 코드)로 찍는다. 무엇을 하네스로 모으고(collect) 콘솔에 띄울지(console)는 시나리오가 `s.Logs(...)`로 정한다 - 러너가 시작과 Unity 대상 접속 때
`log.filter`를 보내고 끝나면 전부로 되돌린다(선언 안 하면 전부, 빈 목록이면 하나도 없음). 지금 목록은 `log.state`. 노드마다 보는 구간은 로그 파일의
노드 경계 줄(`===== 노드 시작: … =====` - 형식은 `HarnessShared/HarnessConstants.cs`)로 나눈다.

### 기본 제공 명령과 이벤트

| 대상 | 명령 |
|---|---|
| 모든 서버 / Unity | `process.info`, `log.mark(text)` |
| GameAgent | `process.status`(게임 서버 연결, 부하) |
| Unity | `process.status`(프로세스 정보만 - 네트워크 역할, 씬, 프레임, 배속), `status.screen(types)`(클라 화면: 단계·씬 역할·Pawn·UIManager·타입별 개수), `status.replication`(클라 received / 서버 expected·observed), `log.recent`, `scene.load(scene)`(그 플레이어의 씬 전환 → `scene.loaded`), `net.*`(NGO 통신 조건 - 아래 절), 클라 `client.*`/`shop.*`, 서버 `ingame.rooms`/`cheat.*` (위 Unity 표) |

| 이벤트 | 보내는 쪽 | 데이터 |
|---|---|---|
| `harness.attached` / `harness.detached` / `harness.rejected` | 하네스 자신 | 종류, pid, 명령 목록 / 끊긴 사유 / 거부 사유 |
| `network.role` | Unity | none / server / host / client-connecting / client |
| `test.role` | Unity(첫 프레임) | intended(MPPM 태그가 정한 역할 Server/Client/None - 참고용), source, startScene - `UnityPlay`가 출발 씬(HarnessBoot)에서 시작했는지 확인 |
| `scene.loaded` | Unity | scene, path, from - `scene.load`가 연 씬이 다 열림 |
| `net.link_changed` | Unity | connected, cause(link / lag_spike_start / lag_spike_end / reapplied) - NGO 연결 조작 |
| `scenario.prepared` | 하네스 | 시나리오 시작 때 대상별 준비(예: 에디터가 출발 씬을 엶)의 결과 - target, ok, error |
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

남은 작업은 [`TODO.md`](./TODO.md)에 모아둔다 - 실제 버그 후보, 진행 중/논의 대기, 하네스 개선, 시나리오로 만들 서버 수정, 미룬 것,
PC 확인 안 된 것, 환경 메모. 끝난 일과 PC 확인 기록은 [`HISTORY.md`](./HISTORY.md).
