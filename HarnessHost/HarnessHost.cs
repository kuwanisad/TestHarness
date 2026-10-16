using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TestHarness.HarnessHost;

public sealed class HarnessHostOptions
{
    /// <summary>테스트 대상들이 접속할 포트. 대상에겐 HARNESS_ADDRESS로 <see cref="HarnessHost.Address"/>를 넘긴다.</summary>
    public int Port { get; init; } = 9000;

    /// <summary>명령 결과를 기다리는 기본 시간.</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>이벤트가 기록될 때마다 한 줄씩 받는다(콘솔/파일 출력용).</summary>
    public Action<HarnessEvent>? EventSink { get; init; }
}

/// <summary>
/// 테스트 하네스의 제어 서버. 테스트 대상(서버 프로세스, Unity)이 여기로 접속해 오면
/// 시나리오가 <see cref="AgentSession"/>으로 명령을 보내고 <see cref="Events"/>로 이벤트를 기다린다.
///
/// 반드시 테스트 대상보다 먼저 띄울 것 - 대상은 시작할 때 접속을 시도한다.
/// 로컬 전용이라 TLS 없이 평문 HTTP/2(h2c)로 연다.
/// </summary>
public sealed class HarnessHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly AgentRegistry _registry;

    private HarnessHost(WebApplication app, AgentRegistry registry, int port)
    {
        _app = app;
        _registry = registry;
        Address = $"http://localhost:{port}";
    }

    /// <summary>테스트 대상에게 HARNESS_ADDRESS로 넘길 주소.</summary>
    public string Address { get; }

    public HarnessEventLog Events => _registry.Events;

    /// <summary>지금 접속해 있는 대상들.</summary>
    public IReadOnlyList<AgentSession> Agents => _registry.Snapshot();

    public static async Task<HarnessHost> StartAsync(HarnessHostOptions? options = null)
    {
        options ??= new HarnessHostOptions();

        var builder = WebApplication.CreateSlimBuilder();
        // 하네스 자체 로그는 경고 이상만 - 테스트 출력이 ASP.NET 기동 로그로 묻히지 않게.
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.ListenLocalhost(options.Port, listen => listen.Protocols = HttpProtocols.Http2));

        var registry = new AgentRegistry(new HarnessEventLog(options.EventSink), options.CommandTimeout);
        builder.Services.AddSingleton(registry);
        builder.Services.AddGrpc();

        var app = builder.Build();
        app.MapGrpcService<HarnessControlService>();

        await app.StartAsync();
        return new HarnessHost(app, registry, options.Port);
    }

    /// <summary>
    /// 이름이 name인 대상이 접속할 때까지 기다린다(이미 접속해 있으면 즉시). 서버를 띄운 직후나
    /// 재시작 직후에 쓴다 - 재시작이면 새로 접속한 쪽을 돌려준다.
    /// </summary>
    public async Task<AgentSession> WaitForAgentAsync(string name, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var startSeq = Events.LastSeq;
        var existing = _registry.Find(name);
        if (existing is { IsConnected: true }) return existing;

        await Events.WaitForAsync(
            e => e.AgentName == name && e.Name == HarnessEventNames.Attached,
            timeout, startSeq, $"{name} 접속", cancellationToken);

        return _registry.Find(name)
               ?? throw new InvalidOperationException($"{name}이(가) 접속하자마자 끊김");
    }

    public async ValueTask DisposeAsync()
    {
        // 대상이 아직 접속해 있으면(시나리오가 중간에 실패해 서버를 못 끈 경우 등) Kestrel은 기본으로
        // 30초까지 스트림이 끝나길 기다린다 - 하네스 종료가 그만큼 멈추지 않게 짧게 끊는다.
        using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await _app.StopAsync(shutdownCts.Token); }
        catch (OperationCanceledException) { /* 남은 연결은 강제로 끊김 */ }
        await _app.DisposeAsync();
    }
}
