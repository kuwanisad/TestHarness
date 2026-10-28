using System.Net.Sockets;

namespace TestHarness.Core;

public sealed record InfraStatus(string Name, int Port, bool Up);

/// <summary>
/// 인프라(Docker 컨테이너: Redis/RabbitMQ/Kafka)가 떠 있는지 포트 접속으로만 확인한다. Docker 명령을 쓰지
/// 않는 이유: 컨테이너가 아니라 직접 설치한 Redis로 개발해도 똑같이 확인되게. 포트는
/// DevTools/docker-compose.yml과 같다.
/// </summary>
public static class InfraProbe
{
    private static readonly (string Name, int Port)[] Services =
    {
        ("Redis", 6379),
        ("RabbitMQ", 5672),
        ("Kafka", 9092),
    };

    public static async Task<IReadOnlyList<InfraStatus>> CheckAsync(CancellationToken ct)
    {
        var checks = Services.Select(async s => new InfraStatus(s.Name, s.Port, await IsListeningAsync(s.Port, ct)));
        return await Task.WhenAll(checks);
    }

    private static async Task<bool> IsListeningAsync(int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(HarnessConstants.Limits.InfraProbeMilliseconds));
        try
        {
            await client.ConnectAsync("127.0.0.1", port, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }
}
