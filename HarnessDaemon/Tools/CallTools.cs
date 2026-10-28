using System.ComponentModel;
using TestHarness.Core;
using TestHarness.Daemon.Mcp;

namespace TestHarness.Daemon.Tools;

/// <summary>
/// 응답 시간 한도(50초)를 넘긴 도구 호출 이어서 기다리기. 한도를 넘긴 호출은 어떤 도구든 pending(callId)으로 먼저 응답하고
/// 계속 돈다(McpPendingCalls 주석). 이 도구도 같은 한도 안에서 응답한다 - 그동안 안 끝나면 다시 pending.
/// </summary>
public sealed class CallTools
{
    // 이 도구 자신의 응답이 한도를 넘지 않게 조금 덜 기다린다.

    private readonly McpPendingCalls _pending;

    public CallTools(McpPendingCalls pending) => _pending = pending;

    [McpTool(HarnessConstants.Tools.CallContinue)]
    [Description("응답에 pending=true가 온 도구 호출을 이어서 기다린다. 끝나는 순간 원래 도구의 결과가 그대로 온다(isError 포함). " +
                 "아직이면 다시 pending - 같은 callId로 또 부를 것. 기다리는 동안 다른 도구를 불러도 그 작업은 계속된다. " +
                 "모르는 callId면 오류(이미 결과를 받았거나 끝난 뒤 10분이 지남 - 결과는 실행 폴더 mcp-calls.jsonl에 원래 도구 이름으로 남아 있음).")]
    public async Task<ToolOutcome> CallContinue(
        [Description("pending 응답의 callId(예: c12)")] string callId,
        CancellationToken ct)
    {
        if (!_pending.TryGet(callId, out var call))
        {
            return new ToolError($"callId '{callId}'인 진행 중 호출이 없음",
                "이미 결과를 받았거나 끝난 뒤 10분이 지나 버려짐 - 결과는 mcp-calls.jsonl(원래 도구 이름)과 events_query(source=ai)로 확인").ToOutcome();
        }

        var wait = _pending.ResponseBudget - TimeSpan.FromSeconds(HarnessConstants.Limits.CallContinueMarginSeconds);
        if (wait < TimeSpan.FromSeconds(HarnessConstants.Limits.CallContinueMinSeconds)) wait = TimeSpan.FromSeconds(HarnessConstants.Limits.CallContinueMinSeconds);

        await Task.WhenAny(call.Work, Task.Delay(wait, ct));
        if (!call.Work.IsCompleted) return new ToolOutcome(_pending.PendingText(call), false);

        _pending.Remove(call.Id);
        return await call.Work;
    }
}
