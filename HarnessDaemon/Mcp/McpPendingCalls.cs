using System.Collections.Concurrent;
using TestHarness.Core;

namespace TestHarness.Daemon.Mcp;

/// <summary>도구 한 번의 결과 - 응답에 그대로 싣는 글(JSON 문자열)과 오류 여부.</summary>
public sealed record ToolOutcome(string Text, bool IsError);

/// <summary>응답 시간 한도 안에 끝나지 않아 계속 돌고 있는(또는 끝났지만 결과를 아직 안 받아 간) 도구 호출 하나.</summary>
public sealed class PendingCall
{
    internal PendingCall(string id, string tool, Task<ToolOutcome> work)
    {
        Id = id;
        Tool = tool;
        Work = work;
    }

    public string Id { get; }
    public string Tool { get; }
    public Task<ToolOutcome> Work { get; }

    /// <summary>작업이 끝난 시각(아직이면 null) - 오래된 결과를 버릴 때 쓴다.</summary>
    public DateTimeOffset? CompletedAt { get; internal set; }
}

/// <summary>
/// 응답 시간 한도(<see cref="ResponseBudget"/>, 기본 50초)를 넘긴 도구 호출 목록.
///
/// 왜: Claude Code는 도구 호출 하나(HTTP 요청 하나)의 응답을 약 60초까지만 기다리고 끊는다. 하네스는 먼저 연락할
/// 수 없으므로(요청에 응답만 할 수 있음), 오래 걸리는 도구는 한도 안에서 "아직 진행 중"으로 먼저 응답하고 작업은 계속
/// 돌린다. AI는 call_continue(callId)로 다시 묻고, 하네스는 그 요청을 다시 붙잡고 있다가 끝나는 순간 원래 도구의 결과로
/// 응답한다. 모든 도구가 같은 경로다(McpServer.CallToolAsync) - 도구를 만들 때 따로 할 일은 없다.
///
/// - pending 응답에는 시간 정보(경과/남은 시간)를 싣지 않는다 - AI가 시간을 계산하지 않고 그냥 이어서 기다리게.
/// - 끝난 결과는 <see cref="KeepCompleted"/> 동안 보관하고, 받아 가지 않으면 버린다(원래 도구 기록은 mcp-calls.jsonl에 남아 있음).
/// </summary>
public sealed class McpPendingCalls
{
    public static readonly TimeSpan DefaultResponseBudget = TimeSpan.FromSeconds(50);
    public static readonly TimeSpan KeepCompleted = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, PendingCall> _calls = new(StringComparer.Ordinal);
    private long _nextId;

    public McpPendingCalls(TimeSpan? responseBudget = null) => ResponseBudget = responseBudget ?? DefaultResponseBudget;

    /// <summary>pending 응답에 함께 실을 진행 상황(예: 실행 중인 시나리오 노드와 최근 이벤트). 없으면 null - 입구가 연결한다.</summary>
    public Func<object?>? Progress { get; set; }

    /// <summary>도구 호출 하나의 응답을 기다리는 최대 시간(이걸 넘으면 pending으로 먼저 응답).</summary>
    public TimeSpan ResponseBudget { get; }

    public PendingCall Add(string tool, Task<ToolOutcome> work)
    {
        Sweep();
        var call = new PendingCall($"c{Interlocked.Increment(ref _nextId)}", tool, work);
        _calls[call.Id] = call;
        return call;
    }

    public bool TryGet(string id, out PendingCall call) => _calls.TryGetValue(id, out call!);

    public void Remove(string id) => _calls.TryRemove(id, out _);

    /// <summary>"아직 진행 중" 응답 - 시간 정보 없이, 다음에 할 일과 지금 진행 상황(있으면)만.</summary>
    public string PendingText(PendingCall call) => HarnessJson.Serialize(new
    {
        pending = true,
        callId = call.Id,
        tool = call.Tool,
        progress = SafeProgress(),
        message = $"아직 끝나지 않음 - call_continue(callId=\"{call.Id}\")로 이어서 기다릴 것. 끝나면 {call.Tool}의 결과가 그대로 온다. " +
                  "기다리는 동안 다른 도구를 불러도 이 작업은 계속된다.",
    });

    private object? SafeProgress()
    {
        try { return Progress?.Invoke(); }
        catch (Exception) { return null; } // 진행 상황은 부가 정보 - 실패해도 pending 응답은 나가야 한다
    }

    private void Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, call) in _calls)
        {
            if (call.CompletedAt is { } done && now - done > KeepCompleted) _calls.TryRemove(id, out _);
        }
    }
}
