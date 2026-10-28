using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using TestHarness.Core;
using TestHarness.Daemon.Mcp;
using TestHarness.Daemon.Tools;

// 하네스 데몬 - AI가 하네스를 쓰는 입구. 띄워 두면:
//   - localhost:9000  테스트 대상(서버/Unity) 에이전트가 붙는 gRPC
//   - localhost:9100  AI가 붙는 MCP (http://localhost:9100/mcp)
// Claude Code 등록(한 번만): claude mcp add --transport http --scope user harness http://localhost:9100/mcp
//
// 사용법: dotnet run --project HarnessDaemon [-- 옵션]
//         dotnet run --project HarnessDaemon -- run <시나리오> [옵션]   (자동 실행 - AI 없이 정해진 경로로 끝까지 돌고 끝남.
//                                                                       종료 코드 0 = 전부 통과, 1 = 아님, 2 = 시작 못 함)
//   --root <GameProject1>   (기본: 실행 위치에서 위로 찾음)
//   --runs <폴더>           (기본: TestHarness/runs)
//   --agent-port 9000  --mcp-port 9100
//   --login https://localhost:5400
//   --powershell <경로>     (기본: Windows는 powershell.exe, 그 외 pwsh)
//   --response-seconds 50   (검증용 - 도구 응답 시간 한도. 평소엔 쓰지 말 것: Claude Code가 약 60초에 끊는다)

// 자동 실행 모드: "run <시나리오>" - MCP는 열지 않는다(에이전트 포트 9000만).
var runScenario = args.Length >= 2 && args[0] == "run" ? args[1] : null;
if (args.Length >= 1 && args[0] == "run" && runScenario == null)
{
    Console.Error.WriteLine("사용법: dotnet run --project HarnessDaemon -- run <시나리오 이름>");
    return 2;
}

var options = ParseOptions(args);
var mcpPort = GetArg(args, "--mcp-port") is { } mcpPortArg ? int.Parse(mcpPortArg) : HarnessConstants.Defaults.McpPort;
var mcpAddress = string.Format(HarnessConstants.Defaults.LocalAddressFormat, mcpPort) + HarnessConstants.Defaults.McpPath;

HarnessSession session;
try
{
    session = await HarnessSession.StartAsync(options);
}
catch (HarnessException ex)
{
    Console.Error.WriteLine($"하네스 시작 실패: {ex.Message}");
    if (ex.Hint != null) Console.Error.WriteLine($"  → {ex.Hint}");
    return 1;
}

// 도구 실행의 수명 - 요청(HTTP)이 끊겨도 작업은 계속되고, 하네스가 끝날 때만 멈춘다(McpServer 주석).
using var lifetime = new CancellationTokenSource();
var responseSeconds = GetArg(args, "--response-seconds");
var pending = new McpPendingCalls(responseSeconds != null ? TimeSpan.FromSeconds(double.Parse(responseSeconds)) : null);

var registry = new McpToolRegistry();
registry.Add(new HarnessTools(session, mcpAddress));
registry.Add(new CallTools(pending));
registry.Add(new ServerTools(session));
registry.Add(new AgentTools(session));
registry.Add(new ClientTools(session));
registry.Add(new ObserveTools(session));
registry.Add(new ScenarioTools(session));

// 시나리오 정의(HarnessScenarios 프로젝트의 [TH_Scenario] 클래스). 그래프 오류(ID 중복, 정리 노드 없음, 맥락 없음, 판정 없는 노드 등)면 여기서 멈춘다.
try
{
    session.Scenarios.Catalog.Register(typeof(TestHarness.Scenarios.ScenarioAssembly).Assembly);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"시나리오 정의 오류: {ex.Message}");
    await session.DisposeAsync();
    return 1;
}

// 시나리오 노드 실행은 AI와 같은 도구를 같은 프로세스 안에서 부른다(기록은 actor=runner).
Action<string> consoleLog = line => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
session.Scenarios.Tools = new McpToolInvoker(registry, session.ToolCalls, consoleLog);
session.Overrides.Tools = session.Scenarios.Tools; // 지속 조작 되돌리기도 같은 통로(기록 actor=runner)
// 오래 걸리는 호출의 pending 응답에 실행 중인 노드와 최근 이벤트를 싣는다.
pending.Progress = session.Scenarios.Progress;

if (runScenario != null) return await RunScenarioAsync(session, runScenario, lifetime);

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0";
var (instructions, instructionsSource) = ReadInstructions(session.Paths);
var server = new McpServer(registry, instructions, version, consoleLog, session.ToolCalls, pending, lifetime.Token);

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
// localhost에서만 연다 - 다른 PC에서 이 하네스를 조작할 수 없게.
builder.WebHost.ConfigureKestrel(kestrel =>
    kestrel.ListenLocalhost(mcpPort, listen => listen.Protocols = HttpProtocols.Http1));

var app = builder.Build();
McpHttpTransport.Map(app, HarnessConstants.Defaults.McpPath, server);

try
{
    await app.StartAsync();
}
catch (IOException ex)
{
    Console.Error.WriteLine($"MCP 포트 {mcpPort}를 열지 못함: {ex.Message} - 이미 하네스가 떠 있는지 확인");
    await session.DisposeAsync();
    return 1;
}

Console.WriteLine("=== 테스트 하네스 준비 완료 ===");
Console.WriteLine($"  GameProject1 : {session.Paths.Root}");
Console.WriteLine($"  실행 기록     : {session.Paths.RunsDir}");
Console.WriteLine($"  에이전트(gRPC): {session.Host.Address}");
Console.WriteLine($"  MCP          : {mcpAddress}  (도구 {registry.Tools.Count}개, 시나리오 {session.Scenarios.Catalog.List.Count()}개)");
Console.WriteLine($"  사용 안내     : {instructionsSource} (AI가 접속할 때 읽는다 - 고친 뒤엔 AI 쪽에서 다시 연결)");
Console.WriteLine($"  Claude Code 등록(한 번만): claude mcp add --transport http --scope user harness {mcpAddress}");
var orphans = session.Servers.FindOrphans();
foreach (var orphan in orphans)
{
    Console.WriteLine($"  ! 지난 실행 {orphan.RunId}의 서버가 아직 떠 있음: {string.Join(", ", orphan.AlivePids.Keys)} (servers_stop runId로 정리)");
}
Console.WriteLine("종료: Ctrl+C (띄운 서버는 끄지 않는다 - 끄려면 먼저 servers_stop)");

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.TrySetResult();
};
// 종료 요청(SIGTERM 등)은 ASP.NET Core 호스트(ConsoleLifetime)가 먼저 가로채서 호스트만 멈추고 Main은 계속
// 기다리게 된다 - 호스트가 멈추기 시작하면 우리도 정리하고 끝내도록 연결한다.
app.Lifetime.ApplicationStopping.Register(() => stop.TrySetResult());

// 콘솔 창 닫기(SIGHUP)도 Ctrl+C처럼 정리하고 끝낸다(호스트는 이걸 안 받는다). 지원하지 않는 환경이면 건너뜀.
var signals = new List<System.Runtime.InteropServices.PosixSignalRegistration>();
try
{
    signals.Add(System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGHUP, context =>
    {
        context.Cancel = true;
        stop.TrySetResult();
    }));
}
catch (PlatformNotSupportedException)
{
}

await stop.Task;
Console.WriteLine("하네스 종료 중...");
lifetime.Cancel();
using (var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(HarnessConstants.Limits.DaemonShutdownSeconds)))
{
    try { await app.StopAsync(shutdown.Token); }
    catch (OperationCanceledException) { }
}
await session.DisposeAsync();
foreach (var registration in signals) registration.Dispose();
return 0;

// 자동 실행: 정해진 경로(AutoPlan)로 끝까지 돌고 보고서 경로와 결과를 찍는다. Ctrl+C면 멈춘다(정리 노드는 그래도 시도).
static async Task<int> RunScenarioAsync(HarnessSession session, string name, CancellationTokenSource lifetime)
{
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        lifetime.Cancel();
    };

    Console.WriteLine($"=== 자동 실행: {name} (에이전트 {session.Host.Address}) ===");
    Console.WriteLine("  Unity 시나리오면 에디터를 켜 두고 에디터 브리지를 켤 것(Test → Harness 메뉴).");
    int exitCode;
    try
    {
        var result = await session.Scenarios.StartAsync(name, TestHarness.Core.Scenarios.TH_RunMode.Auto, lifetime.Token);
        var json = HarnessJson.ToElement(result);
        var allPassed = json.TryGetProperty("allPassed", out var passed) && passed.ValueKind == System.Text.Json.JsonValueKind.True;
        Console.WriteLine(HarnessJson.Serialize(result));
        Console.WriteLine(allPassed ? "결과: 전부 통과" : "결과: 통과하지 못한 것 있음 - 보고서 확인");
        exitCode = allPassed ? 0 : 1;
    }
    catch (HarnessException ex)
    {
        Console.Error.WriteLine($"시작 못 함: {ex.Message}");
        if (ex.Hint != null) Console.Error.WriteLine($"  → {ex.Hint}");
        exitCode = 2;
    }

    await session.DisposeAsync();
    return exitCode;
}

static HarnessSessionOptions ParseOptions(string[] args) => new()
{
    Root = GetArg(args, "--root"),
    RunsDir = GetArg(args, "--runs"),
    AgentPort = GetArg(args, "--agent-port") is { } agentPortArg ? int.Parse(agentPortArg) : HarnessConstants.Defaults.AgentPort,
    LoginAddress = GetArg(args, "--login") ?? HarnessConstants.Defaults.LoginAddress,
    PowerShellPath = GetArg(args, "--powershell"),
};

static string? GetArg(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

// 사용 안내: 디스크의 TestHarness/HarnessDaemon/McpInstructions.md를 먼저 읽는다(고쳐도 다시 빌드할 필요 없음 - 하네스를
// 재시작하고 AI 쪽에서 다시 연결하면 반영). 파일이 없을 때만(빌드 결과만 옮긴 경우 등) 실행 파일에 넣어 둔 사본.
static (string Text, string Source) ReadInstructions(HarnessPaths paths)
{
    var file = Path.Combine(paths.TestHarness, "HarnessDaemon", "McpInstructions.md");
    if (File.Exists(file)) return (File.ReadAllText(file), paths.Relative(file));

    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("McpInstructions.md");
    if (stream == null) return ("테스트 하네스. harness_status부터 부를 것.", "(없음)");
    using var reader = new StreamReader(stream);
    return (reader.ReadToEnd(), "(실행 파일에 넣은 사본)");
}
