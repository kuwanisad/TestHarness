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
//   --root <GameProject1>   (기본: 실행 위치에서 위로 찾음)
//   --runs <폴더>           (기본: TestHarness/runs)
//   --agent-port 9000  --mcp-port 9100
//   --login https://localhost:5400
//   --powershell <경로>     (기본: Windows는 powershell.exe, 그 외 pwsh)

var options = ParseOptions(args);
var mcpPort = int.Parse(GetArg(args, "--mcp-port") ?? "9100");
var mcpAddress = $"http://localhost:{mcpPort}/mcp";

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

var registry = new McpToolRegistry();
registry.Add(new HarnessTools(session, mcpAddress));
registry.Add(new ServerTools(session));
registry.Add(new AgentTools(session));
registry.Add(new ClientTools(session));
registry.Add(new ObserveTools(session));
registry.Add(new ScenarioTools(session));

// 시나리오 정의(HarnessScenarios 프로젝트의 [TH_Scenario] 클래스). 그래프 오류(ID 중복, 정리 목표 없음 등)면 여기서 멈춘다.
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

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0";
var (instructions, instructionsSource) = ReadInstructions(session.Paths);
var server = new McpServer(registry, instructions, version, line => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {line}"), session.ToolCalls);

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
// localhost에서만 연다 - 다른 PC에서 이 하네스를 조작할 수 없게.
builder.WebHost.ConfigureKestrel(kestrel =>
    kestrel.ListenLocalhost(mcpPort, listen => listen.Protocols = HttpProtocols.Http1));

var app = builder.Build();
McpHttpTransport.Map(app, "/mcp", server);

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
using (var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
{
    try { await app.StopAsync(shutdown.Token); }
    catch (OperationCanceledException) { }
}
await session.DisposeAsync();
foreach (var registration in signals) registration.Dispose();
return 0;

static HarnessSessionOptions ParseOptions(string[] args) => new()
{
    Root = GetArg(args, "--root"),
    RunsDir = GetArg(args, "--runs"),
    AgentPort = int.Parse(GetArg(args, "--agent-port") ?? "9000"),
    LoginAddress = GetArg(args, "--login") ?? "https://localhost:5400",
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
