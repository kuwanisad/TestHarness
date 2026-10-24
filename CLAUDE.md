# TestHarness 작업 규칙

이 폴더는 오토체스 서버/Unity를 테스트하는 하네스다. 하네스를 **쓰는** 법은 MCP 접속 때 하네스가 직접 알려준다
(`HarnessDaemon/McpInstructions.md`). 이 파일은 하네스 **자체를 고칠 때**의 규칙이다.

## 구조

- `HarnessCore/` - 기능은 전부 여기. 실행 단위(RunManager), 타임라인(Timeline), 서버 제어(ServerManager, DevTools 스크립트 호출),
  빌드(BuildRunner), 헤드리스 클라 풀(ClientPool), 로그 조회(LogStore). `HarnessSession`이 전부 묶는다.
- `HarnessDaemon/` - AI용 입구. `Tools/*.cs`는 인자를 받아 HarnessCore에 넘기고 결과를 돌려주는 얇은 층으로 유지한다.
  기능 로직을 도구 클래스에 넣지 않는다(3주차 C# 시나리오도 HarnessCore를 그대로 쓰므로).
- `HarnessCore/Scenarios/` - 시나리오 틀(`TH_*`): 노드(TH_Node - 행동/묶음, From/To 상태), 시나리오(TH_Scenario, TH_ScenarioBuilder -
  맥락, 정리 노드, AutoPlan), 노드 실행 문맥(TH_RunContext - 도구 호출, ITH_ToolInvoker), 판정 재료(TH_JudgeContext), 환경 변화 목록
  (TH_EnvironmentChanges), 진행기 + 보고서(TH_ScenarioRunner). `HarnessCore/ToolCallLog.cs` - 모든 도구 호출 기록(actor: ai/runner).
- `HarnessScenarios/` - 시나리오 정의(`[TH_Scenario]` 클래스 하나 = 노드 그래프 하나)와 공유 노드(`Nodes/TH_Nodes.cs`, 상태 이름 `TH_States`).
  데몬이 시작할 때 리플렉션으로 모은다.
- `HarnessDaemon/Mcp/` - MCP(도구 기능만) 직접 구현. 공식 SDK로 바꿀 때 손댈 곳은 `McpToolAttribute.cs` 맨 위 주석.
  도구 클래스는 `[McpTool]`, `[Description]`, `HarnessException`만 안다 - 전송 방식에 의존하는 코드를 도구 쪽에 넣지 않는다.
- `HarnessHost/`, `HeadlessClient/` - 에이전트 접속 서버, 화면 없는 클라. 스모크 테스트(`*.Smoke`)가 따로 있다.

## 도구를 추가/변경할 때

- 이름은 snake_case, `묶음_동작`(예: `client_join_match`). 바꾸면 `McpInstructions.md`와 README 도구 표도 같이 고친다.
- 설명(`[Description]`)에는 "언제 쓰는지 / 무엇을 돌려주는지 / 실패하면 무엇을 하면 되는지"를 쓴다 - AI가 읽는 유일한 사용법이다.
- 행동 도구는 `sinceSeq`(행동 직전 `Timeline.LastSeq`)를 돌려준다.
- 출력에는 한도를 둔다(목록은 limit + matched/truncated/hint, 긴 문자열은 `HarnessJson.Truncate`). 전문은 실행 폴더 파일로 남긴다.
- AI가 고칠 수 있는 실패는 `HarnessException(메시지, 힌트)`로 던진다. 힌트는 "그럼 무엇을 하면 되는지" 한 줄.
- 열거형은 이름으로 내보낸다(`HarnessJson.Options`). 거부 사유를 문장으로 바꾸지 않는다.
- 오래 걸려도 그냥 끝까지 기다려 결과를 돌려주면 된다 - 응답 한도(50초)는 MCP 층이 공통으로 처리한다(pending → call_continue,
  `Mcp/McpPendingCalls.cs`). 도구 실행은 요청이 아니라 하네스 종료 신호로만 취소된다(받는 CancellationToken).

## 명령 영역 (에이전트 명령 이름 `<영역>.<동작>`)

- 영역 목록의 원본은 `HarnessCore/CommandAreas.cs` 하나다. AI는 `agent_list(query=<영역>)`으로 명령을 찾으므로 영역 이름이 곧 검색 키워드다.
- 영역은 "무엇을 다루나"(대상)로 정한다. 동작 단어는 영역이 아니다(`buy.piece`가 아니라 `shop.buy`).
- 조회 명령은 `state`(한 대상의 현재 상태) 또는 `list`(여러 개 목록)로 이름을 맞춘다.
- 기존 영역에 안 맞을 때만 새 영역을 만들고, 만들면 `CommandAreas.cs`에 먼저 등록한 뒤 `McpInstructions.md`의 영역 줄과 README도 고친다.
  등록 안 된 영역의 명령은 `agent_list`가 `unknownAreas`로 드러낸다.

## 시나리오

- 시나리오 틀의 타입은 `TH_` 접두어(TestHarness)를 붙인다(일반 단어 `Objective`, `Scenario`와 겹치지 않게). 시나리오 클래스도
  `TH_<무엇>Scenario`.
- 모든 상호작용은 두 형태뿐이다: (가) 하네스가 질문 → AI가 대답 → 하네스가 기록, (나) AI가 출력 → 하네스가 기록.
  하네스 응답의 `expect`에는 시나리오 진행 행동과 노드 ID만 넣는다 - 기능 도구 목록을 넣지 않는다(기능 도구는 언제든 허용, 기록만).
- 노드는 하네스가 실행하고(Auto - AI가 하던 도구 호출을 코드로, `TH_RunContext`) 판정한다. AI는 경로만 고른다(node_run, scenario_finish).
- 판정은 판정 코드로. 판정 코드는 기록(TH_JudgeContext - 노드 구간의 이벤트와 runner의 도구 호출)만 본다 - 판정 중에 새로 묻지 않는다.
  "명령 뒤에 온 이벤트"는 `LastCommand(...).SinceSeq` 기준(효과 이벤트가 호출 기록보다 먼저 들어올 수 있음).
- 노드 판정은 가능하면 다른 노드의 순서에 의존하지 않게(AI가 경로를 고르므로) - 필요한 값은 응답에서 읽는다(예: Rescale의 settingBefore).
- 코드로 판정할 수 없을 때만 `AiJudged()`(시나리오 `AllowAiJudge=true`일 때만). 묶음 안에서 뒤가 앞에 의존하면 자식에 `OnFail(Stop)`(기본).
- `s.Context`(맥락)는 필수 - AI가 멈출 시점을 정하는 근거. 정리 노드(`Cleanup`)는 반드시 하나(From 없이, `ToInitial()`). 사람 없이 돌릴 경로는 `s.AutoPlan`.
- 테스트 대상(클라, 서버 ...)은 문자열 상수가 아니라 `TH_Target` 인스턴스다. 시나리오가 `s.Target(...)`(`s.UnityClient("client1")` 등,
  `HarnessScenarios/Targets`)으로 선언하고, 대상을 받는 노드는 `TH_Node.For(이름, 대상, id => ...)`로 만든다(ID `이름@대상`, 같은 대상이면
  같은 인스턴스). 선언 안 한 대상을 쓰면 시나리오를 만들 때 오류.
  - 게임 쪽 조작/판정(명령 이름 등)은 대상 클래스(HarnessScenarios)에 둔다 - HarnessCore의 `TH_Target`은 이름, 종류, 상태 칸만 안다.
  - 공통 동작은 `TH_Client`를 받는 노드, 한 종류만 되는 동작은 그 종류(`TH_UnityClient`)를 받는 노드 - 생략된 동작은 컴파일에서 막는다.
  - 상태는 칸별(`TH_StateMap` - 전역 + 상태 칸을 가진 대상마다). 노드 From/To에 대상을 주면 그 칸, 안 주면 전역 칸.
  - 노드끼리 넘기는 값도 대상별 칸(`r.Values.For(c)`, 키는 `TH_ValueKeys`). 판정과 기다리기는 대상의 이벤트만(`c.Last(target, ...)`,
    `LastCommand(target, ...)`) - 클라가 여럿이면 출처를 안 가리는 판정은 다른 클라의 이벤트로 통과해 버린다.
- Unity 씬은 시나리오가 선언한다 - 출발 씬 `s.UnityEditor(bootScene: TH_Scenes.HarnessBoot)`, 플레이어 시작 씬
  `s.UnityClient(name, startScene: ...)` / `s.UnityServer(startScene: ...)`. 에디터 브리지, 에이전트, 노드에 씬 이름을 박지 않는다.
  출발 씬을 비워 두는 이유는 `Unity/ARCHITECTURE.md` "HarnessBoot 씬".
  ⚠ **하네스의 씬 이름(`TH_Scenes`)은 Unity 씬 파일 이름(확장자 뺀 것)과 정확히 같아야 한다**(대소문자 포함) - 에디터 브리지/에이전트가 이름으로
  찾는다. Unity에서 씬 이름을 바꾸면 `TH_Scenes`도 같이 고친다. 확인은 `editor.scenes`. 씬 이름을 문자열로 흩어 쓰지 말고 `TH_Scenes` 상수로.
- 시나리오 시작 때 대상에 해 둘 일(예: 메인 에디터에 출발 씬 열기)은 대상의 준비 단계(`TH_Target.Preparation`/`PrepareAsync`)로 - 러너가 첫 노드
  전에 부르고 보고서에 남긴다. 켜 두면 남는 것은 지속 조작으로 등록해 하네스가 시나리오 끝에 되돌린다(`play_start` 종류).
- 지속 조작 종류마다 "대상이 끊기면 같이 사라지는가"(`EndsWithAgent`)를 정한다 - 프로세스/Play 세션 안의 값(배속 등)은 true, 에디터 설정처럼
  도메인 리로드나 재접속을 지나도 남는 값은 false(끊겨도 추적을 계속해 시나리오 끝에 되돌림).
- 환경 변화 = 테스트 대상의 변화만(`TH_EnvironmentChanges.cs`에 등록). 데이터를 바꾸는 에이전트 명령이 생기면 거기 `DataCommands`에 넣는다.
- 보고서는 하네스가 기록으로 쓴다(AI가 쓰지 않는다).
- 노드 판정은 노드 구간만 본다. 노드 밖(AI가 노드 사이에 조작/대기)에서 일어난 이상은 `HarnessCore/Scenarios/TH_OutsideEvents.cs`에 등록한 이벤트로
  모아 보고서 머리에 경고로 올린다(판정은 그대로, 요약 `outsideEvents`). 이상으로 볼 이벤트가 새로 생기면 거기 넣는다.
- 끝 알림음은 시나리오마다 `s.PlayFinishSound`로 정한다(소리는 하네스가 끝을 판단해 낸다 - AI용 도구로 만들지 않는다).
- 경계 장애는 `Harness.Agent/Fault`(gRPC 인터셉터 - 게임 코드 변경 없음)와 명령 `fault.*`로 한다. 판정은 `fault.hit`(장애가 실제로 걸렸는지)부터 본다.
  NGO(클라↔인게임 서버)는 Unity 에이전트의 `net.*`(Network Simulator) - 판정은 `net.link_changed`/`net.state`로 조작이 걸렸는지부터.
- 프레임워크 객체를 감싸는 래퍼(스트림 Writer/Reader, NGO 전송 등)는 인터페이스의 **모든 겹을 그대로 넘긴다** - 기본 구현이 있는 겹(예:
  `IAsyncStreamWriter.WriteAsync(T, CancellationToken)`)을 빠뜨리면 규칙이 없어도 게임 동작이 깨진다(2026-10-23 PC 실행에서 마스터/로그인
  스트림이 실제로 깨짐). 샌드박스 확인은 게임 코드가 실제로 부르는 겹으로 한다.
- 켜 두면 남는 조작(배속, 설정값, 장애 규칙 ...)은 노드가 직접 되돌리지 않는다(`finally`로 되돌리기 금지) - `HarnessCore/OverrideKinds.cs`에
  종류를 등록하면 하네스(`OverrideManager`)가 기록을 보고 추적하고 노드 끝/시나리오 끝에 되돌린다. 남겨야 하면 `r.KeepOverrides()`.
  - 종류에는 값을 다시 읽는 조회(`Probe`)도 적는다 - AI가 상태를 볼 때 쓰는 기존 조회 명령(state/list)으로, "사라짐"은 오류 문장이 아니라
    목록에 없음으로. 값이 어느 조회에도 안 나오면 그 대상의 state/list 명령에 칸을 더한다(하네스 전용 조회 명령을 만들지 않는다).
  - 명령 없이 값이 바뀔 수 있는 종류(예: hold=false로 끊은 연결이 재진입으로 이어짐)는 그 값을 알리는 이벤트를 `ValueEvent`로 적는다 - 하네스가 그
    시각에 기록을 맞춘다(처음 값이면 끝). 대상 쪽은 그 변화를 이벤트로 알려야 한다(net_link: `net.link_changed` cause=transport_recreated).
  - 조회는 필요할 때만: 되돌리기가 실패해 "확인 대기"인 조작을, 그 종류를 Forbids/Needs로 선언한 노드가 시작하기 직전에만. 기록(명령 응답,
    이벤트)으로 확실하면 조회하지 않는다.
  - 남아 있으면 결과가 무의미한 노드는 `.Forbids(종류)`, 켜져 있어야 하는 노드는 `.Needs(종류)`. 어긋날 때 처리는 시나리오 전역
    `s.OverrideMismatch`(기본 Reject, 필요한 시나리오만 Warn) - 노드마다 정하지 않는다.

## 지키는 원칙

- 판정은 바깥에서 관찰한 사실(이벤트, 응답 열거형, 결과 파일)로 한다. 서버 로그는 원인 찾기용.
- 고정 시간 대기 대신 조건 대기(`Timeline.WaitForAsync`).
- 서버 기동/종료는 DevTools 스크립트로만 한다(사람과 같은 방식). 판단은 스크립트 결과 파일(`*-result.json`) + 에이전트 접속.
- 외부 명령은 `ProcessRunner`로 실행한다(출력을 파일로 받는다 - 손자 프로세스가 파이프를 쥐어 멈추는 문제 때문. 클래스 주석 참고).

## 테스트 코드 분리 - 걷어내도 아무 문제 없어야 한다

하네스가 서버/Unity 안에 심는 코드(에이전트, 디버그 명령, cheat, 장애 주입 등)는 나중에 통째로 걷어낼 수 있어야 한다.
새 기능을 설계할 때 이 기준을 먼저 통과시킨다 - 통과 못 하면 구현 전에 사람과 다시 논의한다.

1. **위치** - 테스트 코드는 테스트 전용 위치에만 둔다. Unity `Assets/Scripts/Test/`, 서버 `Server/GameServer/Shared/Test/`
   (공통 에이전트 `Harness.Agent`, 서버별 명령 `<서버>.Harness` 프로젝트).
2. **방향** - 테스트 코드 → 게임 코드 참조는 된다. 게임 코드 → 테스트 코드 참조는 안 된다(게임 코드는 테스트 코드의 존재를 모른다).
3. **빌드** - 정식 빌드에는 테스트 코드가 들어가지 않는다. Unity는 `#if UNITY_EDITOR || DEVELOPMENT_BUILD`. 서버는 프로젝트가 하네스를
   참조하지 않고, 하네스 모드에서만 DevTools가 주입한다(시작 훅 `DOTNET_STARTUP_HOOKS` + 호스팅 시작
   `ASPNETCORE_HOSTINGSTARTUPASSEMBLIES` - `Harness.Agent.csproj` 주석, README "서버에 하네스가 붙는 방식").
4. **게임 코드를 꼭 고쳐야 할 때** - 테스트가 없어도 말이 되는 변경만 한다(예: 고정값을 설정에서 읽게 하는 일반화). 테스트 코드를
   지워도 게임은 그대로 동작해야 한다. 테스트를 위한 분기, 훅, 플래그를 게임 코드에 넣지 않는다.
5. **확인** - 정식 빌드 구성(Unity 비개발 빌드, 서버 `dotnet build -c Release`)이 테스트 코드 없이 컴파일되는지로 검사한다.

**동작을 바꾸는 테스트(장애, 잘못된 메시지 등)도 게임 코드에 훅을 넣지 않는다.** 두 갈래로 나눈다.
- **로컬(서버/클라 안에서 일어나는 일)** - 기존 테스트 코드의 에이전트 명령(`cheat.*` 등)으로 한다. 시점은 기다리지 않고 하네스가
  **유발**한다: 준비(로컬 명령으로 상태를 바꿔 둠) → 유발(하네스가 그 사건을 일으키는 명령 - 클라 행동, 국면 넘기기 등) → 관찰
  (그 명령의 `SinceSeq` 이후 이벤트로 판정). 저절로 일어나는 사건(타이머, 봇)은 기존 조작으로 하네스가 일으키는 쪽으로 바꾼다.
- **경계(서버↔서버, 클라↔서버로 오가는 메시지)** - 막기, 끼어들기, 순서 만들기는 여기서만 한다. 프레임워크가 열어 둔 자리만 쓴다:
  gRPC는 받는 서버의 인터셉터(호스팅 시작으로 주입 - 요청/응답 모두, 내용 고치기 가능), NGO는 Unity 공식 Network Simulator
  (Multiplayer Tools 패키지 - 테스트 코드 `NetHarnessCommands.cs`가 실행 중에 전용 오브젝트에 붙임, 명령 `net.*`). 플레이어 프로세스 단위로
  지연/흔들림/손실, 끊기/다시 잇기, 순간 끊김만 - RPC 단위 고르기는 없다(필요해지면 NGO 소스를 확인한 뒤 전송 래퍼를 검토; NGO 2.x의 UnityTransport
  디버그 시뮬레이터는 효과 없음으로 바뀌어 쓰지 않는다). 조작 코드는 테스트 코드에만 - 게임 오브젝트/씬/프리팹에 붙이지 않는다. 한 함수 안의 "처리 도중" 상황은
  경계에서 메시지 하나를 붙잡아(delay) 도착 순서를 만드는 것으로 대신한다.

**예외 하나 - 게임 코드의 상황 보고(Unity `Scripts/Test/DebugManager.cs`)**: 일방적인 조작이 아니라 게임 코드가 상황을 알려야 할 때(어떤 기물이
null, 로비 채널 정리 등)는 게임 코드가 `DebugManager.Log(GameLogType, 내용)`**만** 부를 수 있다(2번의 유일한 예외).
- 무엇을 하네스로 모으고 콘솔에 띄울지는 테스트 쪽이 정한다 - 시나리오의 `s.Logs(collect, console)`를 러너가 시작/대상 접속 때 Unity 에이전트에
  `log.filter`로 보내고 끝나면 전부(기본값)로 되돌린다. 선언 안 하면 전부. 로그는 원인 찾기용이다(판정은 이벤트로).
- 종류(`GameLogType`)는 `WarningBorder` 앞은 Info, 뒤는 Warning. Error는 쓰지 않는다(Error Pause로 게임이 멈춤). 하네스는 이름을 문자열로 쓰므로
  (`HarnessConstants.GameLogTypes`) 이름을 바꾸면 양쪽을 같이 고친다.
- 정식 빌드: `Log`는 `[Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]`라 호출이 컴파일에서 지워지고 몸체는 `#if` 안이다(열거형과
  빈 선언만 남음). ⚠ 자동 제거가 기대대로 안 될 수 있으니 패키징 전에 호출부를 찾아 수동으로 지울 수 있어야 한다(`Unity/TODO.md`).

## 하네스 상수

하네스의 상수(문구 형식, 명령/인자/이벤트 이름, 다른 프로세스와 맞춰야 하는 문자열)는 `HarnessCore/HarnessConstants.cs` 한 파일에 모은다 - 코드에
직접 적지 않는다. 다른 프로세스(Unity, 서버)와 같아야 하는 값은 주석에 상대 위치를 적는다.

## 확인

- 빌드: `dotnet build TestHarness.slnx`
- MCP 수동 확인: 하네스를 띄우고 `curl -X POST http://localhost:9100/mcp -H "Content-Type: application/json" -d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}"`
- 기존 스모크: `dotnet run --project HarnessHost.Smoke`(HarnessDaemon과 같은 9000 포트를 쓰니 동시에 띄우지 말 것), `dotnet run --project HeadlessClient.Smoke`
