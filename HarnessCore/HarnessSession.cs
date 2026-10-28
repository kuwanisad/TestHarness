using TestHarness.Core.Scenarios;

namespace TestHarness.Core;

public sealed class HarnessSessionOptions
{
    /// <summary>GameProject1 폴더. 비면 자동으로 찾는다(<see cref="HarnessPaths.Detect"/>).</summary>
    public string? Root { get; init; }

    /// <summary>실행 기록 폴더. 비면 TestHarness/runs.</summary>
    public string? RunsDir { get; init; }

    /// <summary>테스트 대상(서버/Unity) 에이전트가 접속할 포트.</summary>
    public int AgentPort { get; init; } = HarnessConstants.Defaults.AgentPort;

    /// <summary>헤드리스 클라가 접속할 로그인 서버.</summary>
    public string LoginAddress { get; init; } = HarnessConstants.Defaults.LoginAddress;

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
        ServerManager servers, BuildRunner build, ClientPool clients, LogStore logs, ToolCallLog toolCalls, OverrideManager overrides,
        TH_ScenarioRunner scenarios)
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
        Overrides = overrides;
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

    /// <summary>지속 조작(배속, 설정값 ...) 감시와 수명 관리. 되돌리는 통로(Overrides.Tools)는 입구가 연결한다.</summary>
    public OverrideManager Overrides { get; }

    /// <summary>시나리오 진행기(노드 그래프). 시나리오 정의는 입구가 Scenarios.Catalog.Register로 등록하고, 노드 실행이 도구를 부르는
    /// 통로(Scenarios.Tools)도 입구가 연결한다.</summary>
    public TH_ScenarioRunner Scenarios { get; }
    public DateTimeOffset StartedUtc { get; }

    public static async Task<HarnessSession> StartAsync(HarnessSessionOptions options)
    {
        var paths = HarnessPaths.Detect(options.Root, options.RunsDir);
        var timeline = new Timeline();
        var runs = new RunManager(paths, timeline);
        var agentLogs = new AgentLogWriter(runs);

        HarnessHost.HarnessHost host;
        try
        {
            host = await HarnessHost.HarnessHost.StartAsync(new HarnessHost.HarnessHostOptions
            {
                Port = options.AgentPort,
                EventSink = timeline.RecordAgentEvent,
                LogSink = agentLogs.Write,
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
        var overrides = new OverrideManager(timeline, toolCalls);
        var scenarios = new TH_ScenarioRunner(timeline, runs, toolCalls, new RunTrace(timeline, logs),
            new TH_BuildProbe(paths, servers.ServerOrder), overrides, agentLogs.WriteMarker, () => RunEndBlocker(servers, clients),
            name => host.Agents.FirstOrDefault(a => a.Name == name && a.IsConnected)?.Commands.Keys.ToArray());

        timeline.RecordHarness(HarnessConstants.HarnessEvents.Started, $"하네스 시작(에이전트 {host.Address})", new { root = paths.Root });
        return new HarnessSession(paths, timeline, runs, host, servers, build, clients, logs, toolCalls, overrides, scenarios);
    }

    /// <summary>
    /// 실행을 닫으면 안 되는 사유 - run_end 도구와 같은 기준(서버가 떠 있으면 로그 폴더가 바뀌므로 안 됨). 헤드리스 클라가 남아 있어도 닫지 않는다
    /// (run_end는 닫으면서 클라도 끄지만, 시나리오 끝에서는 정리 노드가 못 끈 것을 하네스가 몰래 끄지 않는다).
    /// </summary>
    private static string? RunEndBlocker(ServerManager servers, ClientPool clients)
    {
        var alive = servers.GetStatus().Where(s => s.ProcessAlive).Select(s => s.Name).ToList();
        if (alive.Count > 0) return $"서버가 떠 있음: {string.Join(", ", alive)}";
        var open = clients.List().Select(c => c.Alias).ToList();
        return open.Count > 0 ? $"헤드리스 클라가 남아 있음: {string.Join(", ", open)}" : null;
    }

    public async ValueTask DisposeAsync()
    {
        Scenarios.Dispose();
        Overrides.Dispose();
        await Clients.DisposeAsync();
        Runs.End();
        await Host.DisposeAsync();
    }
}
