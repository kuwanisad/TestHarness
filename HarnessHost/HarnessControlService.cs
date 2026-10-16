using Google.Protobuf;
using Grpc.Core;
using Harness.Protocol;

namespace TestHarness.HarnessHost;

/// <summary>
/// 테스트 대상의 Attach 스트림 하나를 처리한다: Hello 확인 → Welcome → (송신 루프 ∥ 수신 루프)
/// → 끊기면 정리. 접속마다 gRPC가 이 메서드를 따로 부른다.
/// </summary>
internal sealed class HarnessControlService : HarnessControl.HarnessControlBase
{
    private readonly AgentRegistry _registry;

    public HarnessControlService(AgentRegistry registry) => _registry = registry;

    public override async Task Attach(
        IAsyncStreamReader<AgentMessage> requestStream,
        IServerStreamWriter<HarnessMessage> responseStream,
        ServerCallContext context)
    {
        var ct = context.CancellationToken;

        // 1. 첫 메시지는 반드시 Hello.
        if (!await requestStream.MoveNext(ct)) return;
        if (requestStream.Current.PayloadCase != AgentMessage.PayloadOneofCase.Hello)
        {
            await responseStream.WriteAsync(Reject("첫 메시지가 Hello가 아님"), ct);
            return;
        }

        var hello = requestStream.Current.Hello;
        var rejectReason = _registry.CheckHello(hello);
        if (rejectReason != null)
        {
            _registry.RecordRejected(hello, rejectReason);
            await responseStream.WriteAsync(Reject(rejectReason), ct);
            return;
        }

        var session = _registry.Register(hello);
        await responseStream.WriteAsync(new HarnessMessage { Welcome = new Welcome { Accepted = true } }, ct);

        var reason = "대상이 스트림을 닫음";
        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sendLoop = SendLoopAsync(session, responseStream, sendCts.Token);

        try
        {
            // 2. 수신 루프 - 명령 결과와 이벤트.
            while (await requestStream.MoveNext(ct))
            {
                var message = requestStream.Current;
                switch (message.PayloadCase)
                {
                    case AgentMessage.PayloadOneofCase.Result:
                        session.CompleteCommand(message.Result);
                        break;
                    case AgentMessage.PayloadOneofCase.Event:
                        _registry.RecordAgentEvent(session, message.Event);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { reason = "연결 끊김(취소)"; }
        catch (IOException ex) { reason = $"연결 끊김: {ex.Message}"; }
        catch (RpcException ex) { reason = $"연결 끊김: {ex.StatusCode}"; }
        finally
        {
            // 3. 정리 - 대기 중이던 명령은 전부 실패로 끝내고, 송신 루프를 멈춘다.
            sendCts.Cancel();
            _registry.Unregister(session, reason);
            try { await sendLoop; } catch { /* 이미 끊긴 연결 */ }
        }
    }

    private static async Task SendLoopAsync(
        AgentSession session, IServerStreamWriter<HarnessMessage> responseStream, CancellationToken ct)
    {
        await foreach (var message in session.Outbound.Reader.ReadAllAsync(ct))
        {
            await responseStream.WriteAsync(message, ct);
        }
    }

    private static HarnessMessage Reject(string reason) =>
        new() { Welcome = new Welcome { Accepted = false, Reason = reason } };
}
