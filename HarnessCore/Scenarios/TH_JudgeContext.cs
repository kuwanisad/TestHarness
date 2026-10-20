using System.Text.Json;

namespace TestHarness.Core.Scenarios;

/// <summary>AI가 objective_report로 보낸 것.</summary>
public sealed record TH_AiReport(TH_Result Result, IReadOnlyList<long> EvidenceSeqs, string? Note);

/// <summary>
/// 판정 재료 - 한 목표 구간(objective_begin 직후 ~ objective_report)에 기록된 것만 담는다. 판정은 이 기록만 보고 한다
/// (판정 중에 에이전트에 새로 묻지 않는다 - 같은 기록이면 언제나 같은 판정이 나오게).
///
/// 출처(source) 표기는 wait_for와 같다: "client"(Unity 클라 에이전트 = agent:client), "server", "UnityEditor",
/// "agent:GameAgent", "p1"(헤드리스 클라), "harness".
/// </summary>
public sealed class TH_JudgeContext
{
    internal TH_JudgeContext(string objectiveId, long sinceSeq, IReadOnlyList<TimelineEvent> events,
        IReadOnlyList<ToolCallRecord> calls, TH_AiReport report)
    {
        ObjectiveId = objectiveId;
        SinceSeq = sinceSeq;
        Events = events;
        Calls = calls;
        Report = report;
    }

    public string ObjectiveId { get; }

    /// <summary>목표를 고른 시점의 마지막 이벤트 번호 - 이 뒤의 것만 근거가 된다.</summary>
    public long SinceSeq { get; }

    /// <summary>이 구간의 이벤트(AI 도구 호출 기록 mcp.call 포함, 시간순).</summary>
    public IReadOnlyList<TimelineEvent> Events { get; }

    /// <summary>이 구간에 AI가 부른 도구(시간순).</summary>
    public IReadOnlyList<ToolCallRecord> Calls { get; }

    /// <summary>AI의 보고.</summary>
    public TH_AiReport Report { get; }

    // ================================ 이벤트 ================================

    /// <summary>조건에 맞는 마지막 이벤트(없으면 null).</summary>
    public TimelineEvent? Last(string source, string name, Func<JsonElement, bool>? where = null)
    {
        var filter = new EventFilter(source, new[] { name });
        return Events.LastOrDefault(e => filter.Matches(e) && (where == null || (e.Data is { } d && where(d))));
    }

    /// <summary>조건에 맞는 첫 이벤트(없으면 null).</summary>
    public TimelineEvent? First(string source, string name, Func<JsonElement, bool>? where = null)
    {
        var filter = new EventFilter(source, new[] { name });
        return Events.FirstOrDefault(e => filter.Matches(e) && (where == null || (e.Data is { } d && where(d))));
    }

    // ================================ AI의 도구 호출 ================================

    /// <summary>tool을 부른 마지막 성공 호출(isError 아님). command를 주면 agent_command의 그 명령만.</summary>
    public ToolCallRecord? LastCall(string tool, string? command = null) =>
        Calls.LastOrDefault(c => !c.IsError && c.Tool == tool && (command == null || c.AgentCommand == command));

    /// <summary>
    /// agent_command(command)의 마지막 성공 결과 값(응답의 value). 명령이 예외 없이 끝난(ok=true) 것만.
    /// where를 주면 그 조건에 맞는 마지막 것. agent를 주면 그 에이전트에 보낸 것만(같은 명령이 여러 에이전트에
    /// 있을 때 - 예: process.status).
    /// </summary>
    public (JsonElement Value, long Seq)? LastCommandValue(string command, Func<JsonElement, bool>? where = null, string? agent = null)
    {
        foreach (var call in Calls.Reverse())
        {
            if (call.IsError || call.AgentCommand != command || call.Result is not { } result) continue;
            if (agent != null && !string.Equals(call.Arguments?.Str("agent"), agent, StringComparison.OrdinalIgnoreCase)) continue;
            if (!result.Bool("ok")) continue;
            var value = result.Get("value") ?? default;
            if (where != null && !where(value)) continue;
            return (value, call.Seq);
        }
        return null;
    }

    /// <summary>tool의 마지막 성공 호출 응답(where를 주면 그 조건에 맞는 것).</summary>
    public (JsonElement Result, long Seq)? LastToolResult(string tool, Func<JsonElement, bool>? where = null)
    {
        foreach (var call in Calls.Reverse())
        {
            if (call.IsError || call.Tool != tool || call.Result is not { } result) continue;
            if (where != null && !where(result)) continue;
            return (result, call.Seq);
        }
        return null;
    }

    // ================================ 판정 도우미 ================================

    /// <summary>모든 이벤트가 있으면 Pass(근거 = 그 이벤트들), 하나라도 없으면 Fail(빠진 것 나열).</summary>
    public TH_Verdict RequireAll(params (string Label, TimelineEvent? Event)[] required)
    {
        var missing = required.Where(r => r.Event == null).Select(r => r.Label).ToList();
        var seqs = required.Where(r => r.Event != null).Select(r => r.Event!.Seq).ToArray();
        return missing.Count == 0
            ? TH_Verdict.Pass("확인: " + string.Join(", ", required.Select(r => r.Label)), seqs)
            : TH_Verdict.Fail("기록에 없음: " + string.Join(", ", missing), seqs);
    }
}
