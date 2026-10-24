using System.ComponentModel;
using TestHarness.Core;
using TestHarness.Daemon.Mcp;

namespace TestHarness.Daemon.Tools;

/// <summary>하네스 전체 상태와 실행(run) 단위.</summary>
public sealed class HarnessTools
{
    private readonly HarnessSession _session;
    private readonly string _mcpAddress;

    public HarnessTools(HarnessSession session, string mcpAddress)
    {
        _session = session;
        _mcpAddress = mcpAddress;
    }

    [McpTool("harness_status")]
    [Description("하네스 전체 상태를 한 번에 본다: 지금 실행(run)과 폴더, 서버별 프로세스/에이전트 상태, 지난 실행에 남은 서버(orphans), " +
                 "접속한 에이전트, 헤드리스 클라, 인프라(Redis/RabbitMQ/Kafka) 포트, 지금 켜진 지속 조작(overrides - 배속, 재정비 설정값 ...), " +
                 "진행 중인 시나리오와 기대하는 다음 행동, 마지막 이벤트 번호. 작업을 시작할 때와 뭔가 이상할 때 먼저 부를 것.")]
    public async Task<object> HarnessStatus(CancellationToken ct)
    {
        var run = _session.Runs.Current;
        var orphans = _session.Servers.FindOrphans();
        return new
        {
            root = _session.Paths.Root,
            agentAddress = _session.Host.Address,
            mcpAddress = _mcpAddress,
            startedUtc = _session.StartedUtc,
            run = run == null ? null : new { id = run.Id, dir = _session.Paths.Relative(run.Dir), startedUtc = run.StartedUtc },
            servers = _session.Servers.GetStatus(),
            orphans = orphans.Count == 0 ? null : orphans,
            agents = _session.Host.Agents.Select(a => new { a.Name, kind = a.ProcessKind, a.Pid, connected = a.IsConnected, commands = a.Commands.Count }),
            clients = _session.Clients.List().Select(c => new { c.Alias, c.PlayerId, stage = c.Stage }),
            infra = await InfraProbe.CheckAsync(ct),
            overrides = _session.Overrides.Active is { Count: > 0 } active ? active.Select(o => o.View()) : null,
            scenario = _session.Scenarios.Status(),
            lastSeq = _session.Timeline.LastSeq,
            hint = orphans.Count > 0 ? "지난 실행의 서버가 살아 있음 - servers_stop(runId=...)로 정리할 것(포트가 겹쳐 새 서버가 못 뜬다)" : null,
        };
    }

    [McpTool("run_begin")]
    [Description("새 실행(run)을 연다. 실행 하나 = runs/<시각>-<label>/ 폴더 하나(서버 로그, events.jsonl, 빌드/스크립트 출력). " +
                 "조사/재현 하나마다 새로 열면 나중에 폴더 하나로 돌아볼 수 있다. 서버가 떠 있으면 거부(로그 폴더가 바뀌므로 servers_stop 먼저). " +
                 "열지 않고 servers_start를 하면 'auto' 실행이 자동으로 열린다.")]
    public object RunBegin(
        [Description("폴더 이름에 붙일 짧은 이름(예: reconnect-bug). 영문/숫자/-/_")] string? label = null)
    {
        EnsureNoServers("새 실행을 열 수 없음");
        EnsureNoScenario("새 실행을 열 수 없음");
        var run = _session.Runs.Begin(label);
        return new { runId = run.Id, dir = _session.Paths.Relative(run.Dir), sinceSeq = _session.Timeline.LastSeq };
    }

    [McpTool("run_end")]
    [Description("지금 실행을 닫는다. 헤드리스 클라도 전부 닫는다. 서버가 떠 있으면 거부(servers_stop 먼저).")]
    public async Task<object> RunEnd()
    {
        EnsureNoServers("실행을 닫을 수 없음");
        EnsureNoScenario("실행을 닫을 수 없음");
        var run = _session.Runs.Current ?? throw new HarnessException("열린 실행이 없음");
        foreach (var client in _session.Clients.List()) await _session.Clients.CloseAsync(client.Alias);
        _session.Runs.End();
        return new { closed = run.Id, dir = _session.Paths.Relative(run.Dir) };
    }

    // 시나리오 기록(상태 파일, 보고서)은 시작할 때의 실행 폴더에 쌓인다 - 도중에 실행이 바뀌면 기록이 흩어진다.
    private void EnsureNoScenario(string what)
    {
        if (_session.Scenarios.IsActive)
            throw new HarnessException($"{what} - 시나리오가 진행 중", "scenario_finish로 끝낸 뒤에 할 것");
    }

    private void EnsureNoServers(string what)
    {
        var alive = _session.Servers.GetStatus().Where(s => s.ProcessAlive).Select(s => s.Name).ToList();
        if (alive.Count > 0)
        {
            throw new HarnessException($"{what} - 서버가 떠 있음: {string.Join(", ", alive)}", "servers_stop으로 먼저 끌 것");
        }
    }
}
