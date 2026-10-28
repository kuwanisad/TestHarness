namespace TestHarness.Shared;

/// <summary>
/// 하네스(TestHarness 솔루션)의 상수를 한곳에 모은다 - 문구 형식, 이벤트/상태/값 이름, 시간·개수 한도, 기본 주소. 새 상수는 여기에 넣는다
/// (코드에 직접 적지 않는다). 에이전트(서버/Unity)와 같아야 하는 값은 여기가 아니라 HarnessSharedConstants(같은 폴더 - 에이전트로 복사됨).
/// 에이전트 쪽에만 있는 값은 각 에이전트의 상수 파일(Unity Scripts/Test/Harness/HarnessUnityConstants.cs, 서버 Harness.Agent/HarnessAgentConstants.cs).
/// </summary>
public static class HarnessConstants
{
    /// <summary>기본 주소/포트(실행 인자로 바꿀 수 있음 - HarnessDaemon Program.cs 머리 주석).</summary>
    public static class Defaults
    {
        /// <summary>로그인 서버(헤드리스 클라가 접속).</summary>
        public const string LoginAddress = "https://localhost:5400";

        /// <summary>에이전트(서버/Unity)가 붙는 gRPC 포트 - 에이전트도 알아야 해서 원본은 HarnessSharedConstants.</summary>
        public const int AgentPort = Harness.Shared.HarnessSharedConstants.Connection.AgentPort;

        /// <summary>AI가 붙는 MCP 포트.</summary>
        public const int McpPort = 9100;

        /// <summary>run_begin 없이 서버를 띄우거나 시나리오를 시작하면 열리는 실행의 이름.</summary>
        public const string AutoRunLabel = "auto";

        /// <summary>에이전트 접속 서버 주소 형식 - {0} 포트.</summary>
        public const string LocalAddressFormat = "http://localhost:{0}";

        /// <summary>MCP 경로.</summary>
        public const string McpPath = "/mcp";
    }

    /// <summary>MCP 도구 이름(AI가 부르는 이름 - 바꾸면 McpInstructions.md와 README 도구 표도).</summary>
    public static class Tools
    {
        public const string AgentList = "agent_list";
        public const string AgentCommand = "agent_command";
        public const string CallContinue = "call_continue";
        public const string AccountCreate = "account_create";
        public const string ClientCreate = "client_create";
        public const string ClientLogin = "client_login";
        public const string ClientEnterLobby = "client_enter_lobby";
        public const string ClientJoinMatch = "client_join_match";
        public const string ClientCancelMatch = "client_cancel_match";
        public const string ClientLogout = "client_logout";
        public const string ClientDrop = "client_drop";
        public const string ClientReconnect = "client_reconnect";
        public const string ClientState = "client_state";
        public const string ClientClose = "client_close";
        public const string HarnessStatus = "harness_status";
        public const string RunBegin = "run_begin";
        public const string RunEnd = "run_end";
        public const string WaitFor = "wait_for";
        public const string EventsQuery = "events_query";
        public const string LogsQuery = "logs_query";
        public const string ScenarioList = "scenario_list";
        public const string ScenarioStart = "scenario_start";
        public const string NodeRun = "node_run";
        public const string NodeJudge = "node_judge";
        public const string ScenarioFinish = "scenario_finish";
        public const string InfraStatus = "infra_status";
        public const string ServerBuild = "server_build";
        public const string ServersStart = "servers_start";
        public const string ServerControl = "server_control";
        public const string ServersStop = "servers_stop";
    }

    /// <summary>지속 조작 종류 이름(노드의 Forbids/Needs, 보고서) - 종류 정의는 HarnessScenarios/Knowledge(TH_OverrideKinds).</summary>
    public static class OverrideKindNames
    {
        public const string TimeScale = "time_scale";
        public const string PlanningSetting = "planning_setting";
        public const string Fault = "fault";
        public const string PlayStart = "play_start";
        public const string NetCondition = "net_condition";
        public const string NetLink = "net_link";
    }

    /// <summary>지속 조작의 값 문자열(기록/보고서에 남는 값).</summary>
    public static class OverrideValues
    {
        public const string Connected = "connected";
        public const string Disconnected = "disconnected";
        public const string Off = "off";
        public const string None = "(없음)";
    }

    /// <summary>지속 조작이 끝난 이유(OverrideManager).</summary>
    public static class OverrideReasons
    {
        public const string NodeEnd = "node_end";
        public const string ScenarioEnd = "scenario_end";
        public const string Manual = "manual";
        public const string AgentGone = "agent_gone";
        public const string ScopeGone = "scope_gone";
    }

    /// <summary>타임라인 이벤트의 출처(source).</summary>
    public static class Sources
    {
        /// <summary>하네스 자신이 남긴 이벤트.</summary>
        public const string Harness = "harness";

        /// <summary>AI가 부른 도구 호출 기록.</summary>
        public const string Ai = "ai";

        /// <summary>노드를 실행하는 하네스가 부른 도구 호출 기록.</summary>
        public const string Runner = "runner";

        /// <summary>에이전트가 낸 이벤트의 출처 접두어(출처 = 접두어 + 에이전트 이름).</summary>
        public const string AgentPrefix = "agent:";

        /// <summary>헤드리스 클라 이벤트의 출처 접두어(출처 = 접두어 + 별칭).</summary>
        public const string ClientPrefix = "client:";
    }

    /// <summary>도구 호출/판정의 주체(actor, judgedBy).</summary>
    public static class Actors
    {
        public const string Ai = "ai";
        public const string Runner = "runner";
        public const string Harness = "harness";

        /// <summary>auto 모드에서 AutoPlan이 고른 노드.</summary>
        public const string Auto = "auto";

        /// <summary>묶음 노드의 판정(자식 결과로).</summary>
        public const string HarnessByChildren = "harness(자식 결과)";

        /// <summary>auto 모드의 AI 판정 노드(판정하지 않음).</summary>
        public const string AutoUnjudged = "auto(판정 안 함)";
    }

    /// <summary>시나리오가 어떻게 끝났는지(보고서/요약의 how).</summary>
    public static class FinishHow
    {
        /// <summary>AI가 scenario_finish로 끝냄.</summary>
        public const string Ai = "ai";

        /// <summary>auto 모드에서 AutoPlan이 끝남.</summary>
        public const string Auto = "auto";

        /// <summary>AI 지시가 IdleTimeout 동안 없어 하네스가 끝냄.</summary>
        public const string Idle = "idle";

        /// <summary>AI가 노드 실행 중(또는 자동 실행 중)에 scenario_finish로 일찍 끝냄 - 실행 중이던 노드는 Stopped.</summary>
        public const string AiStop = "ai_stop";
    }

    /// <summary>하네스가 남기는 이벤트 이름(출처 harness). 에이전트 이벤트 이름은 HarnessSharedConstants.Events.</summary>
    public static class HarnessEvents
    {
        public const string Started = "harness.started";
        public const string RunBegin = "run.begin";
        public const string RunEnd = "run.end";
        public const string McpCall = "mcp.call";
        public const string BuildSucceeded = "build.succeeded";
        public const string BuildFailed = "build.failed";

        public const string ServersStarted = "servers.started";
        public const string ServersStartFailed = "servers.start_failed";
        public const string ServersStopped = "servers.stopped";
        public const string ServerKill = "server.kill";
        public const string ServerStart = "server.start";
        public const string ServerRestart = "server.restart";

        public const string ClientCreated = "client.created";
        public const string ClientClosed = "client.closed";
        public const string AccountCreated = "account.created";

        public const string ScenarioStart = "scenario.start";
        public const string ScenarioPrepared = "scenario.prepared";
        public const string ScenarioNodeBegin = "scenario.node_begin";
        public const string ScenarioNodeEnd = "scenario.node_end";
        public const string ScenarioNodeJudged = "scenario.node_judged";
        public const string ScenarioOverrideMismatch = "scenario.override_mismatch";
        public const string ScenarioProbeMissing = "scenario.probe_missing";
        public const string ScenarioEnvironmentChanged = "scenario.environment_changed";
        public const string ScenarioLogFilterFailed = "scenario.log_filter_failed";
        public const string ScenarioStopping = "scenario.stopping";
        public const string ScenarioFinishing = "scenario.finishing";
        public const string ScenarioEnd = "scenario.end";
        public const string ScenarioRunKept = "scenario.run_kept";

        /// <summary>시나리오 이벤트 이름의 접두어(보고서 구간 기록에서 거른다).</summary>
        public const string ScenarioPrefix = "scenario.";

        public const string OverrideApplied = "override.applied";
        public const string OverrideChanged = "override.changed";
        public const string OverrideConfirmed = "override.confirmed";
        public const string OverrideUnconfirmed = "override.unconfirmed";
        public const string OverrideRevertFailed = "override.revert_failed";
        public const string OverrideReverted = "override.reverted";
        public const string OverrideUnused = "override.unused";
    }

    /// <summary>노드 경계 줄 - 노드(최상위 방문과 정리 노드)를 시작하고 끝낼 때 Unity 에이전트 로그 파일(AgentLogWriter)에 넣는다.</summary>
    public static class NodeMarker
    {
        /// <summary>경계 줄의 분류(Category)와 에이전트 이름 자리.</summary>
        public const string Category = "Harness";

        /// <summary>{0} 노드 경로, {1} 방문 번호(정리 노드는 CleanupVisit).</summary>
        public const string StartFormat = "===== 노드 시작: {0} (방문 {1}) =====";

        /// <summary>{0} 노드 경로, {1} 방문 번호, {2} 판정.</summary>
        public const string EndFormat = "===== 노드 끝: {0} (방문 {1}) {2} =====";

        /// <summary>정리 노드의 방문 번호 자리.</summary>
        public const string CleanupVisit = "정리";

        /// <summary>AI 판정을 기다리는 노드의 판정 자리.</summary>
        public const string AwaitingAiJudge = "AI 판정 대기";
    }

    /// <summary>실행 폴더(runs/&lt;실행&gt;/) 안의 파일/폴더 이름.</summary>
    public static class RunFiles
    {
        /// <summary>타임라인 전체(한 줄에 이벤트 하나).</summary>
        public const string Events = "events.jsonl";

        /// <summary>도구 호출 요청/응답 전문.</summary>
        public const string McpCalls = "mcp-calls.jsonl";

        /// <summary>실행이 열려 있지 않을 때의 도구 호출(runs 폴더 바로 아래).</summary>
        public const string McpCallsOutsideRun = "mcp-calls-outside-run.jsonl";

        /// <summary>시나리오 보고서의 노드별 구간 기록 폴더.</summary>
        public const string TraceDir = "trace";

        /// <summary>서버/에이전트 로그 폴더.</summary>
        public const string LogsDir = "logs";

        /// <summary>로그 파일 확장자.</summary>
        public const string LogExtension = ".log";

        /// <summary>서버 표준 오류 로그 파일의 끝(DevTools가 &lt;서버&gt;.err.log로 받음).</summary>
        public const string ErrorLogSuffix = ".err.log";

        /// <summary>에이전트 이름에서 파일 이름을 못 만들 때 쓰는 이름.</summary>
        public const string FallbackLogName = "agent";
    }

    /// <summary>
    /// 로그 한 줄의 JSON 형식 - .NET 서버 로그(JSON 콘솔 출력)와 같은 이름. AgentLogWriter가 쓰고 LogStore가 읽는다.
    /// </summary>
    public static class LogFields
    {
        public const string Timestamp = "Timestamp";
        public const string LogLevel = "LogLevel";
        public const string Category = "Category";
        public const string Message = "Message";
        public const string Exception = "Exception";
        public const string Pid = "Pid";
        public const string Agent = "Agent";

        /// <summary>Timestamp 형식(UTC).</summary>
        public const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

        /// <summary>분류가 없는 Unity 로그의 분류.</summary>
        public const string DefaultUnityCategory = "Unity";
    }

    /// <summary>로그 수준 이름(.NET LogLevel 이름 + JSON이 아닌 줄).</summary>
    public static class LogLevels
    {
        public const string Information = "Information";
        public const string Warning = "Warning";
        public const string Error = "Error";
        public const string Critical = "Critical";

        /// <summary>JSON이 아닌 줄(서버가 콘솔에 그대로 찍은 것).</summary>
        public const string Raw = "Raw";
    }

    /// <summary>출력·보관 한도.</summary>
    public static class Limits
    {
        /// <summary>wait_for 한 번의 최대 대기(초) - 노드 실행(TH_RunContext.WaitFor)도 이 단위로 나눠 부른다.</summary>
        public const int MaxWaitSeconds = 300;

        /// <summary>wait_for 시간 초과 때 같이 돌려주는 "그동안 온 이벤트" 개수.</summary>
        public const int WaitForEventsSince = 20;

        /// <summary>wait_for 시간 초과 때 같이 돌려주는 "이미 지나간 같은 조건 이벤트" 개수.</summary>
        public const int WaitForMatchedBefore = 3;

        /// <summary>MCP 응답 한도(초) - Claude Code가 약 60초에 끊어서 그 전에 pending으로 답한다(McpPendingCalls).</summary>
        public const int McpResponseBudgetSeconds = 50;

        /// <summary>pending 호출의 끝난 결과를 보관하는 시간(분).</summary>
        public const int McpKeepCompletedMinutes = 10;

        /// <summary>call_continue가 응답 한도에서 빼 두는 여유(초).</summary>
        public const int CallContinueMarginSeconds = 2;

        /// <summary>에이전트 명령 결과를 기다리는 기본 시간(초) - agent_command, 노드의 r.Agent.</summary>
        public const int AgentCommandTimeoutSeconds = 15;

        /// <summary>에이전트 명령 결과를 기다리는 최대 시간(초) - agent_command의 timeoutSeconds 상한.</summary>
        public const int AgentCommandMaxTimeoutSeconds = 300;

        /// <summary>하네스를 끌 때 에이전트 접속 서버가 정리하기를 기다리는 시간(초).</summary>
        public const int HostShutdownSeconds = 2;

        /// <summary>Docker 인프라 포트를 두드려 볼 때 한 포트당 기다리는 시간(밀리초).</summary>
        public const int InfraProbeMilliseconds = 800;

        /// <summary>서버 빌드(dotnet build) 한 번의 최대 시간(분).</summary>
        public const int BuildTimeoutMinutes = 5;

        /// <summary>DevTools 서버 스크립트 한 번의 최대 시간(초).</summary>
        public const int ServerScriptTimeoutSeconds = 120;

        /// <summary>시나리오의 AI 지시 없음(IdleTimeout)을 확인하는 간격(초).</summary>
        public const int IdleCheckSeconds = 5;

        /// <summary>시나리오가 정하지 않았을 때의 IdleTimeout(분) - AI 지시가 이 시간 동안 없으면 하네스가 정리하고 끝낸다.</summary>
        public const int DefaultIdleTimeoutMinutes = 2;

        /// <summary>하네스를 끌 때 진행 중인 작업을 기다리는 시간(초).</summary>
        public const int DaemonShutdownSeconds = 3;

        /// <summary>call_continue가 기다리는 최소 시간(초).</summary>
        public const int CallContinueMinSeconds = 1;

        /// <summary>조건이 맞을 때까지 같은 조회를 다시 묻는 간격(밀리초) - 노드의 PollAgent, 대기 루프.</summary>
        public const int PollIntervalMilliseconds = 1000;

        /// <summary>헤드리스 클라 RPC 하나의 최대 시간(초).</summary>
        public const int HeadlessRpcTimeoutSeconds = 15;

        /// <summary>헤드리스 클라 gRPC 연결 유지 핑 간격과 응답 기다림(초).</summary>
        public const int HeadlessKeepAlivePingSeconds = 10;
        public const int HeadlessKeepAliveTimeoutSeconds = 5;

        /// <summary>서버 기동 뒤 에이전트 접속을 기다리는 시간(초).</summary>
        public const int AgentAttachTimeoutSeconds = 10;

        /// <summary>빌드 오류를 돌려주는 최대 개수.</summary>
        public const int BuildMaxErrors = 30;

        /// <summary>보고서 구간 기록의 요약 줄 수.</summary>
        public const int TraceSummaryMaxLines = 30;

        /// <summary>보고서 구간 기록에서 이벤트 한 줄의 최대 길이.</summary>
        public const int TraceEventTextLength = 240;

        /// <summary>도구 호출 기록에 남기는 결과의 최대 길이.</summary>
        public const int ToolResultMaxChars = 20_000;

        /// <summary>메모리에 두는 도구 호출 기록 수.</summary>
        public const int ToolCallsInMemory = 20_000;

        /// <summary>메모리에 두는 타임라인 이벤트 수(넘으면 오래된 것부터 버림 - 파일에는 다 남음).</summary>
        public const int TimelineInMemory = 100_000;

        /// <summary>로그 조회 응답의 메시지 최대 길이.</summary>
        public const int LogMessageMaxLength = 400;

        /// <summary>환경 변화 설명에 붙이는 이벤트 detail의 최대 길이.</summary>
        public const int EnvironmentDetailMaxLength = 80;

        /// <summary>노드 밖 이상 경고에 붙이는 이벤트 data의 최대 길이.</summary>
        public const int OutsideEventDetailMaxLength = 200;

        /// <summary>이벤트 detail을 응답에 실을 때의 최대 길이.</summary>
        public const int EventDetailMaxLength = 300;
    }

    /// <summary>테스트 대상 이름 규칙(MPPM 플레이어 태그 ↔ 하네스 이름).</summary>
    public static class TargetNames
    {
        /// <summary>Unity 클라 하네스 이름의 앞부분(태그 Client1 → 이름 client1).</summary>
        public const string UnityClientNamePrefix = "client";

        /// <summary>Unity 클라 MPPM 태그의 앞부분.</summary>
        public const string UnityClientTagPrefix = "Client";

        /// <summary>Unity 인게임 서버의 하네스 이름(태그 Server).</summary>
        public const string UnityServerName = "server";
    }

    /// <summary>
    /// DevTools 서버 스크립트(PowerShell)와 주고받는 이름 - 스크립트 이름, 인자, 결과 파일과 그 칸, 단계 상태. ⚠ DevTools 스크립트와 같아야 한다.
    /// </summary>
    public static class DevTools
    {
        public const string WindowsPowerShell = "powershell.exe";
        public const string PowerShellCore = "pwsh";
        public static readonly string[] PowerShellArguments = { "-NoProfile" };
        public static readonly string[] WindowsPowerShellArguments = { "-ExecutionPolicy", "Bypass" };
        public const string FileArgument = "-File";

        // 스크립트
        public const string CommonScript = "server-common.ps1";
        public const string StartScript = "start-servers.ps1";
        public const string ControlScript = "control-server.ps1";
        public const string StopScript = "stop-servers.ps1";

        // 인자
        public const string LogDirArg = "-LogDir";
        public const string HarnessAddressArg = "-HarnessAddress";
        public const string ReadyTimeoutArg = "-ReadyTimeoutSeconds";
        public const string SkipDockerArg = "-SkipDocker";
        public const string OnlyArg = "-Only";
        public const string ServerNameArg = "-ServerName";
        public const string ActionArg = "-Action";

        /// <summary>server_control의 action(소문자) → control-server.ps1의 -Action 값.</summary>
        public static readonly IReadOnlyDictionary<string, string> ControlActions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["kill"] = "Kill",
            ["start"] = "Start",
            ["restart"] = "Restart",
        };

        /// <summary>control-server.ps1이 끝나기를 기다리는 시간 = 준비 대기 + 이 여유(초).</summary>
        public const int ControlExtraSeconds = 60;

        // 결과 파일(실행 폴더 logs/)과 칸
        public const string StartResult = "start-result.json";
        public const string ControlResult = "control-result.json";
        public const string PidFile = "server-pids.json";
        public const string ResultOk = "ok";
        public const string ResultSteps = "results";
        public const string StepName = "name";
        public const string StepStatus = "status";
        public const string StepPid = "pid";
        public const string StepLog = "log";
        public const string StepErrLog = "errLog";
        public const string StepDetail = "detail";

        // 단계 상태와 사유
        public const string StatusReady = "ready";
        public const string StatusFailed = "failed";
        public const string StatusKilled = "killed";
        public const string StatusRemoved = "removed";
        public const string StatusUnknown = "unknown";
        public const string DetailStillAlive = "still-alive";
        public const string DetailMissingDll = "missing-dll";
        public const string DetailMissingHarness = "missing-harness";
        public const string DetailAlreadyRunning = "already-running";

        /// <summary>Docker 인프라 단계의 이름(servers_stop 결과의 steps).</summary>
        public const string DockerStep = "Docker";

        // 작업 이름(보고서 action)과 실행 폴더 안 기록
        public const string OperationStart = "start";
        public const string OperationStop = "stop";
        public const string TranscriptDir = "devtools";
        public const string StopTranscript = "stop.txt";
        public const string StopTranscriptOutsideRun = "stop-transcript.txt";
    }

    /// <summary>.NET 서버 빌드 결과 위치(DevTools server-common.ps1과 같은 경로) - Server/GameServer/&lt;이름&gt;/OutputDir/&lt;이름&gt;.dll.</summary>
    public static class ServerBuild
    {
        public static readonly string OutputDir = Path.Combine("bin", "Debug", "net8.0");
        public const string AssemblyExtension = ".dll";
        public const string ProjectExtension = ".csproj";

        /// <summary>서버 말고도 같이 빌드하는 DevTools 프로젝트(메시지 브로커 초기화).</summary>
        public const string BrokerInitializer = "BrokerInitializer";

        /// <summary>빌드 명령과 인자(server_build).</summary>
        public const string BuildCommand = "dotnet";
        public static readonly string[] BuildArguments = { "build", "{0}", "-c", "Debug", "-nologo", "-v", "q", "-clp:NoSummary" };

        /// <summary>빌드 결과 파일이 쌓이는 실행 폴더 안 하위 폴더.</summary>
        public const string BuildOutputDir = "build";

        /// <summary>컴파일러/MSBuild 메시지를 영어로 - 한국어 SDK면 메시지가 콘솔 코드 페이지로 나와 섞이고, 오류 줄 형식도 영어 기준으로 거른다.</summary>
        public static readonly IReadOnlyDictionary<string, string> BuildEnvironment = new Dictionary<string, string>
        {
            ["DOTNET_CLI_UI_LANGUAGE"] = "en",
            ["DOTNET_NOLOGO"] = "1",
        };
    }

    /// <summary>.NET 서버 이름(DevTools server-common.ps1의 $ServerOrder와 같은 이름 - 기동 순서는 그 파일을 읽는다).</summary>
    public static class Servers
    {
        public const string Master = "MasterServer";
        public const string Matching = "MatchingServer";
        public const string Account = "AccountServer";
        public const string Login = "LoginServer";
        public const string DummySub = "DummySubServer";
        public const string Lobby = "LobbyServer";
        public const string GameAgent = "GameAgent";
        public const string Chat = "ChatServer";
    }

    /// <summary>
    /// Unity 씬 이름 - ⚠ Unity 씬 파일 이름(확장자 뺀 것)과 정확히 같아야 한다(대소문자 포함). 에디터 브리지/에이전트가 이름으로 찾는다.
    /// Unity에서 씬 이름을 바꾸면 여기도 같이 고친다(확인은 editor.scenes).
    /// </summary>
    public static class Scenes
    {
        /// <summary>하네스 Play 출발 씬 - 비어 있다(이유는 Unity/ARCHITECTURE.md "HarnessBoot 씬").</summary>
        public const string HarnessBoot = "HarnessBoot";

        /// <summary>클라 시작 씬(로그인 화면).</summary>
        public const string Login = "Login";

        /// <summary>인게임 서버 시작 씬(IngameServerBootstrap이 Ingame 씬을 열고 서버 오브젝트를 만든다).</summary>
        public const string ServerBoot = "ServerBoot";
    }

    /// <summary>판정이 status.screen으로 세는 Unity 컴포넌트 타입 이름(짧은 이름 - Unity StatusHarnessCommands가 이름으로 찾는다).</summary>
    public static class GameTypes
    {
        public const string LoginPawn = "LoginPawn";
        public const string LobbyPawn = "LobbyPawn";
        public const string UIManager = "UIManager";
        public const string LobbyUIManager = "LobbyUIManager";
        public const string AutoChessUIManager = "AutoChessUIManager";
        public const string AutoChessPlayerController = "AutoChessPlayerController";
        public const string AutoChessPawn = "AutoChessPawn";
        public const string PawnView = "PawnView";
    }

    /// <summary>노드 상태 이름(From/To). 전역 칸과 클라 칸.</summary>
    public static class States
    {
        /// <summary>전역 칸(서버/Unity Play처럼 특정 대상에 묶이지 않는 상태)의 이름.</summary>
        public const string GlobalScope = "global";

        // ---- 전역 칸 ----

        /// <summary>서버도 Unity Play도 꺼짐(시나리오 시작/정리 후).</summary>
        public const string ServersDown = "ServersDown";

        /// <summary>서버가 떠 있고 Unity는 Play 전.</summary>
        public const string ServersUp = "ServersUp";

        /// <summary>서버가 떠 있고 Unity Play 중(인게임 서버와 Unity 클라들이 하네스에 붙음).</summary>
        public const string Playing = "Playing";

        // ---- 클라 칸 ----

        /// <summary>클라가 아직 없음/접속 전(Unity: Play 전, 헤드리스: 만들기 전이거나 로그인 전).</summary>
        public const string Offline = "Offline";

        /// <summary>Unity 클라가 로그인 화면(Play 직후).</summary>
        public const string Login = "Login";

        /// <summary>로그인해서 로비.</summary>
        public const string Lobby = "Lobby";

        /// <summary>Unity 클라가 인게임(한 판 진행 중).</summary>
        public const string Ingame = "Ingame";

        /// <summary>헤드리스 클라가 매칭은 잡혔지만 인게임에 못 들어간 상태(헤드리스는 인게임 접속 불가).</summary>
        public const string Matched = "Matched";

        /// <summary>클라의 NGO 연결이 끊겨(Cut - 하네스가 끊어 둠) 서버 재접속 유예 중.</summary>
        public const string Disconnected = "Disconnected";

        /// <summary>재접속 유예가 끝나 서버가 탈락 처리함. 탈락 뒤 클라가 어디로 가야 하는지 미정이라 이어지는 노드가 없다(정리 노드로만 끝남).</summary>
        public const string Abandoned = "Abandoned";
    }

    /// <summary>노드끼리 넘기는 값의 키(TH_RunValues.For).</summary>
    public static class ValueKeys
    {
        public const string AccountId = "accountId";
        public const string Password = "password";
        public const string PlayerId = "playerId";
        public const string RoomId = "roomId";
        public const string Created = "created";
        public const string Merged = "merged";
    }

    /// <summary>
    /// 노드가 조건을 기다리는 상한(초). 조건이 오면 바로 넘어가므로 넉넉하게 둔다 - 상한은 "이만큼 지나도 안 오면 실패"의 기준이다.
    /// 끊김/재접속 쪽 상한은 게임 설정에서 나온다: 끊김 감지는 NGO ProtocolTimeout(Unity NetworkTimeouts), 서버 재접속 유예는
    /// ReconnectableGameMode.ReconnectGracePeriod, 클라 재진입 시도는 ClientInstance의 재진입 대기 - 그 값을 바꾸면 여기도 본다.
    /// </summary>
    public static class Waits
    {
        /// <summary>Play 뒤 각 플레이어 에이전트가 붙을 때까지.</summary>
        public const int AgentAttachSeconds = 120;

        /// <summary>붙은 플레이어가 역할/시작 씬(test.role)을 알릴 때까지.</summary>
        public const int TestRoleSeconds = 30;

        /// <summary>씬 이동이 끝날 때까지.</summary>
        public const int SceneLoadSeconds = 60;

        /// <summary>인게임 서버가 NGO 서버 역할을 시작할 때까지.</summary>
        public const int ServerRoleSeconds = 60;

        /// <summary>화면(status.screen)이 기대한 상태가 될 때까지 다시 묻는 상한.</summary>
        public const int ScreenSeconds = 30;

        /// <summary>로그인 결과가 올 때까지.</summary>
        public const int LoginResultSeconds = 60;

        /// <summary>매칭이 잡히거나 실패할 때까지.</summary>
        public const int MatchSeconds = 120;

        /// <summary>매칭 뒤 첫 재정비(phase.changed Planning)가 올 때까지.</summary>
        public const int FirstPlanningSeconds = 120;

        /// <summary>다음 재정비 국면이 올 때까지(PvP 한 판과 배속을 고려).</summary>
        public const int NextPlanningSeconds = 240;

        /// <summary>국면을 넘긴 뒤 클라가 그 국면을 받을 때까지.</summary>
        public const int PhaseChangeSeconds = 30;

        /// <summary>조작으로 빨리 끝낸 게임이 로비로 돌아올 때까지.</summary>
        public const int LobbyReturnSeconds = 300;

        /// <summary>조작 없이 한 판이 끝나 로비로 돌아올 때까지.</summary>
        public const int GameEndSeconds = 1800;

        /// <summary>인게임 서버의 방이 다 정리될 때까지.</summary>
        public const int RoomsClearSeconds = 15;

        /// <summary>ingame.rooms에서 클라의 방을 찾을 때까지.</summary>
        public const int RoomLookupSeconds = 15;

        /// <summary>명령의 효과 이벤트(골드/체력 변화 등)가 올 때까지.</summary>
        public const int EffectEventSeconds = 15;

        /// <summary>순간 끊김(net.lag_spike)이 끝날 때까지.</summary>
        public const int LagSpikeEndSeconds = 20;

        /// <summary>Play 상태가 바뀔 때까지(editor.play_mode).</summary>
        public const int PlayModeSeconds = 60;

        /// <summary>끊은 뒤 클라가 끊김을 감지할 때까지(NGO ProtocolTimeout 약 30초 + 여유).</summary>
        public const int DisconnectDetectSeconds = 45;

        /// <summary>감지 뒤 재진입을 포기할 때까지(클라 재진입 시도 + 여유).</summary>
        public const int RejoinGiveUpSeconds = 20;

        /// <summary>재진입 포기 뒤 서버가 탈락 처리할 때까지(서버 재접속 유예 + 여유).</summary>
        public const int EliminationSeconds = 40;

        /// <summary>재접속 뒤 클라가 받은 오브젝트 수가 서버와 같아질 때까지 다시 묻는 상한.</summary>
        public const int ReplicationSettleSeconds = 15;
    }

    /// <summary>노드가 대상에 거는 값(조작의 크기 - 결과 판정에도 같은 값을 쓴다).</summary>
    public static class TestValues
    {
        /// <summary>reconnect 시나리오 자동 실행에서 반복 재접속 점검(ReconnectPlanning)을 연달아 돌리는 횟수.</summary>
        public const int ReconnectPlanningRepeats = 3;

        /// <summary>반복 재접속 점검 동안 이번 재정비 길이(초) - 끊김 감지와 재진입이 재정비 안에 끝나게.</summary>
        public const int ReconnectPlanningHoldSeconds = 180;

        /// <summary>cheat.planning_duration SettingOnly로 바꿀 설정값(초).</summary>
        public const int PlanningSettingSeconds = 10;

        /// <summary>cheat.planning_duration Rescale로 바꿀 설정값(초).</summary>
        public const int PlanningRescaleSeconds = 20;

        /// <summary>cheat.damage로 줄 데미지.</summary>
        public const int Damage = 10;

        /// <summary>다음 재정비를 빨리 오게 할 때의 배속.</summary>
        public const int FastTimeScale = 3;

        /// <summary>cheat.planning_duration CurrentOnly로 줄일 이번 재정비 길이(초) - 곧 PvP로 넘어가게.</summary>
        public const int PlanningCurrentOnlySeconds = 3;

        /// <summary>순간 끊김(net.lag_spike) 길이(초) - 끊김 감지 시간보다 짧게.</summary>
        public const int LagSpikeSeconds = 5;

        /// <summary>한 판을 빨리 끝낼 때(FinishGameFast)의 재정비 설정값(초).</summary>
        public const int FinishPlanningSeconds = 5;

        /// <summary>한 판을 빨리 끝낼 때의 서버 배속.</summary>
        public const int FinishTimeScale = 5;

        /// <summary>한 판을 빨리 끝낼 때 봇에게 주는 데미지(한 번에 탈락).</summary>
        public const int FinishDamage = 1000;
    }
}
