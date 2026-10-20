using System.ComponentModel;
using TestHarness.Core;
using TestHarness.Daemon.Mcp;

namespace TestHarness.Daemon.Tools;

/// <summary>서버 빌드/기동/제어/종료와 인프라 확인.</summary>
public sealed class ServerTools
{
    private readonly HarnessSession _session;

    public ServerTools(HarnessSession session) => _session = session;

    [McpTool("infra_status")]
    [Description("인프라(Redis 6379, RabbitMQ 5672, Kafka 9092)가 떠 있는지 포트로 확인한다. 꺼져 있으면 servers_start(infra=Start)가 docker compose로 올린다.")]
    public async Task<object> InfraStatus(CancellationToken ct)
    {
        var status = await InfraProbe.CheckAsync(ct);
        return new
        {
            services = status,
            hint = status.All(s => s.Up) ? null : "꺼진 것이 있음 - servers_start(infra=Start)로 올리거나 Docker Desktop이 켜져 있는지 확인",
        };
    }

    [McpTool("server_build")]
    [Description("서버 코드를 빌드한다(dotnet build, Debug). 서버 코드를 고친 뒤 servers_start 전에 부를 것. 결과: 프로젝트별 성공 여부와 " +
                 "중복을 뺀 오류 줄(최대 30개), 전체 출력 파일 경로. 서버가 떠 있으면 거부(실행 중인 DLL은 덮어쓸 수 없다).")]
    public async Task<object> ServerBuild(
        [Description("빌드할 이름들(서버 이름 또는 BrokerInitializer). 비우면 전부")] string[]? names,
        CancellationToken ct)
    {
        var sinceSeq = _session.Timeline.LastSeq;
        var report = await _session.Build.BuildAsync(names, ct);
        return new { sinceSeq, report };
    }

    [McpTool("servers_start")]
    [Description("DevTools/start-servers.ps1(하네스 모드)로 서버를 띄운다 - 사람이 띄우는 것과 같은 방식. 순서는 항상 Master → Matching → " +
                 "Account → Login → DummySub → Lobby → GameAgent → Chat. 각 서버가 준비('Application started')되고 하네스 에이전트로 붙을 때까지 " +
                 "기다린다. 결과: 단계별 status(ready/failed/skipped)와 agentAttached, 실패한 단계는 logTail(로그 끝 + err 로그 앞). " +
                 "실행(run)이 없으면 자동으로 연다. 이미 떠 있는 서버가 있으면 실패하니 servers_stop 먼저.")]
    public async Task<object> ServersStart(
        [Description("띄울 서버만(예: [\"MasterServer\",\"LoginServer\"]). 비우면 전부. 순서는 항상 위 순서")] string[]? only,
        [Description("Start: docker compose up + BrokerInitializer까지(이미 떠 있어도 안전). Skip: 인프라는 건드리지 않음")] InfraMode infra = InfraMode.Start,
        [Description("서버 하나당 준비 대기 시간(초)")] int readyTimeoutSeconds = 60,
        CancellationToken ct = default)
    {
        var sinceSeq = _session.Timeline.LastSeq;
        var report = await _session.Servers.StartAsync(only, infra, readyTimeoutSeconds, ct);
        return new { sinceSeq, runId = _session.Runs.Current?.Id, report };
    }

    [McpTool("server_control")]
    [Description("DevTools/control-server.ps1로 서버 하나를 kill / start / restart 한다(장애 주입, 재시작 후 복구 확인). " +
                 "kill이면 그 서버 에이전트의 harness.detached 이벤트가 타임라인에 찍힌다.")]
    public async Task<object> ServerControl(
        [Description("서버 이름(예: MatchingServer)")] string name,
        [Description("kill | start | restart")] string action,
        [Description("start/restart 때 준비 대기 시간(초)")] int readyTimeoutSeconds = 60,
        CancellationToken ct = default)
    {
        var sinceSeq = _session.Timeline.LastSeq;
        var report = await _session.Servers.ControlAsync(name, action, readyTimeoutSeconds, ct);
        return new { sinceSeq, report };
    }

    [McpTool("servers_stop")]
    [Description("DevTools/stop-servers.ps1로 지금 실행의 서버를 전부 끈다(역순, 강제 종료). runId를 주면 그 실행에 남은 서버를 끈다" +
                 "(harness_status의 orphans 정리). 기본은 Docker 컨테이너를 그대로 둔다.")]
    public async Task<object> ServersStop(
        [Description("Docker 컨테이너(Redis/RabbitMQ/Kafka)도 내릴지")] bool stopInfra = false,
        [Description("지난 실행 id(orphans 정리용). 비우면 지금 실행")] string? runId = null,
        CancellationToken ct = default)
    {
        var sinceSeq = _session.Timeline.LastSeq;
        var report = await _session.Servers.StopAsync(stopInfra, runId, ct);
        return new { sinceSeq, report };
    }
}
