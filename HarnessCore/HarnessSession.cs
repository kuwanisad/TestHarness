using TestHarness.Core.Scenarios;

namespace TestHarness.Core;

public sealed class HarnessSessionOptions
{
    /// <summary>GameProject1 폴더. 비면 자동으로 찾는다(<see cref="HarnessPaths.Detect"/>).</summary>
    public string? Root { get; init; }

    /// <summary>실행 기록 폴더. 비면 TestHarness/runs.</summary>
    public string? RunsDir { get; init; }

    /// <summary>테스트 대상(서버/Unity) 에이전트가 접속할 포트.</summary>
    public int AgentPort { get; init; } = 9000;

    /// <summary>헤드리스 클라가 접속할 로그인 서버.</summary>
    public string LoginAddress { get; init; } = "https://localhost:5400";

    /// <summary>DevTools 스크립트를 실행할 PowerShell. 비면 Windows는 powershell.exe, 그 외는 pwsh.</summary>
    public string? PowerShellPath { get; init; }

    /// <summary>DevTools 스크립트 프로세스에만 더 넣을 환경 변수(검증 환경용 - 평소엔 비워 둔다).</summary>
    public IReadOnlyDictionary<string, string>? ScriptEnvironment { get; init; }
}

/// <summary>
/// 하네스 기능 전체를 한데 묶은 것. 입구(MCP 도구, C# 시나리오)는 이것 하나만 들고 쓴다.
/// 에이전트 접속 서버(HarnessHost, gRPC)를 띄우고, 모든 이벤트를 <see cref="Timeline"/> 하나로 모은다.
/// </summary>
public sealed class HarnessSession : IAsyncDisposable
{
    private HarnessSession(
        HarnessPaths paths, Timeline timeline, RunManager runs, HarnessHost.HarnessHost host,
        ServerManager servers, BuildRunner build, ClientPool clients, LogStore logs, ToolCallLog toolCalls, TH_ScenarioRunner scenarios)
    {
        Paths = paths;
        Timeline = timeline;
        Runs = runs;
        Host = host;
        Servers = servers;
        Build = build;
        Clients = clients;
        Logs = logs;
        ToolCalls = toolCalls;
        Scenarios = scenarios;
        StartedUtc = DateTimeOffset.UtcNow;
    }

    public HarnessPaths Paths { get; }
    public Timeline Timeline { get; }
    public RunManager Runs { get; }
    public HarnessHost.HarnessHost Host { get; }
    public ServerManager Servers { get; }
    public BuildRunner Build { get; }
    public ClientPool Clients { get; }
    public LogStore Logs { get; }

    /// <summary>AI가 부른 모든 도구(요청+응답) 기록 - 입구(MCP)가 호출마다 남긴다.</summary>
    public ToolCallLog ToolCalls { get; }

    /// <summary>시나리오 진행기(목표 그래프). 시나리오 정의는 입구가 Scenarios.Catalog.Register로 등록한다.</summary>
    public TH_ScenarioRunner Scenarios { get; }
    public DateTimeOffset StartedUtc { get; }

    public static async Task<HarnessSession> StartAsync(HarnessSessionOptions options)
    {
        var paths = HarnessPaths.Detect(options.Root, options.RunsDir);
        var timeline = new Timeline();
        var runs = new RunManager(paths, timeline);

        HarnessHost.HarnessHost host;
        try
        {
            host = await HarnessHost.HarnessHost.StartAsync(new HarnessHost.HarnessHostOptions
            {
                Port = options.AgentPort,
                EventSink = timeline.RecordAgentEvent,
            });
        }
        catch (IOException ex)
        {
            throw new HarnessException($"에이전트용 포트 {options.AgentPort}를 열지 못함: {ex.Message}",
                "이미 다른 하네스(HarnessHost.Smoke 등)가 떠 있는지 확인", ex);
        }

        var servers = new ServerManager(paths, runs, timeline, host, options.PowerShellPath, options.ScriptEnvironment);
        var build = new BuildRunner(paths, runs, servers, timeline);
        var clients = new ClientPool(timeline, options.LoginAddress);
        var logs = new LogStore(runs, paths);
        var toolCalls = new ToolCallLog(timeline, runs, paths);
        var scenarios = new TH_ScenarioRunner(timeline, runs, toolCalls);

        timeline.RecordHarness("harness.started", $"하네스 시작(에이전트 {host.Address})", new { root = paths.Root });
        return new HarnessSession(paths, timeline, runs, host, servers, build, clients, logs, toolCalls, scenarios);
    }

    public async ValueTask DisposeAsync()
    {
        await Clients.DisposeAsync();
        Runs.End();
        await Host.DisposeAsync();
    }
}
