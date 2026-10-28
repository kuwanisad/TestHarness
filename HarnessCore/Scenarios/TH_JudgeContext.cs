using System.Text.Json;

namespace TestHarness.Core.Scenarios;

/// <summary>AI 판정 노드에 AI가 node_judge로 보낸 것.</summary>
public sealed record TH_AiReport(TH_Result Result, IReadOnlyList<long> EvidenceSeqs, string? Note);

/// <summary>
/// 판정 재료 - 한 노드 구간(실행 시작 직후 ~ 실행 끝)에 기록된 것만 담는다. 판정은 이 기록만 보고 한다
/// (판정 중에 에이전트에 새로 묻지 않는다 - 같은 기록이면 언제나 같은 판정이 나오게).
///
/// 도구 호출(Calls)은 노드를 실행한 하네스 자신(runner)의 것만 담는다 - 그 사이에 AI가 조사하려고 부른 도구가 판정을
/// 바꾸지 않게(판정은 노드의 정해진 행동 결과로만).
///
/// 출처(source) 표기는 wait_for와 같다: "client1"(Unity 클라 에이전트 = agent:client1), "server", "UnityEditor",
/// "agent:GameAgent", "hc1"(헤드리스 클라 = client:hc1), "harness". 대상 인스턴스를 받는 겹(Last(TH_Target ...) 등)은 대상 이름을 쓴다.
/// </summary>
public sealed class TH_JudgeContext
{
    internal TH_JudgeContext(string nodeId, long sinceSeq, IReadOnlyList<TimelineEvent> events, IReadOnlyList<ToolCallRecord> calls)
    {
        NodeId = nodeId;
        SinceSeq = sinceSeq;
        Events = events;
        Calls = calls.Where(c => c.Actor == HarnessConstants.Actors.Runner).ToList();
    }

    public string NodeId { get; }

    /// <summary>노드 실행을 시작한 시점의 마지막 이벤트 번호 - 이 뒤의 것만 근거가 된다.</summary>
    public long SinceSeq { get; }

    /// <summary>이 구간의 이벤트(도구 호출 기록 mcp.call 포함, 시간순).</summary>
    public IReadOnlyList<TimelineEvent> Events { get; }

    /// <summary>이 구간에 노드 실행(runner)이 부른 도구(시간순).</summary>
    public IReadOnlyList<ToolCallRecord> Calls { get; }


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

    /// <summary>그 대상(출처 = 대상 이름)의 조건에 맞는 마지막 이벤트.</summary>
    public TimelineEvent? Last(TH_Target target, string name, Func<JsonElement, bool>? where = null) => Last(target.Name, name, where);

    public TimelineEvent? First(TH_Target target, string name, Func<JsonElement, bool>? where = null) => First(target.Name, name, where);

    /// <summary>seq 뒤에 온, 그 대상의 조건에 맞는 첫 이벤트("명령 뒤에 온 이벤트" - 명령의 SinceSeq를 넘길 것).</summary>
    public TimelineEvent? FirstAfter(long seq, TH_Target target, string name, Func<JsonElement, bool>? where = null)
    {
        var filter = new EventFilter(target.Name, new[] { name });
        return Events.FirstOrDefault(e => e.Seq > seq && filter.Matches(e) && (where == null || (e.Data is { } d && where(d))));
    }

    // ================================ 노드 실행의 도구 호출 ================================

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

    /// <summary>
    /// LastCommandValue와 같고, 그 명령 직전의 이벤트 번호(sinceSeq)도 준다. "명령 뒤에 온 이벤트"를 고를 때는 Seq(호출 기록 번호 -
    /// 결과를 받은 뒤에 남음)가 아니라 SinceSeq를 기준으로 할 것: 명령의 효과 이벤트가 결과 기록보다 먼저 타임라인에 들어올 수 있다.
    /// </summary>
    public (JsonElement Value, long Seq, long SinceSeq)? LastCommand(string command, Func<JsonElement, bool>? where = null, string? agent = null)
    {
        foreach (var call in Calls.Reverse())
        {
            if (call.IsError || call.AgentCommand != command || call.Result is not { } result) continue;
            if (agent != null && !string.Equals(call.Arguments?.Str("agent"), agent, StringComparison.OrdinalIgnoreCase)) continue;
            if (!result.Bool("ok")) continue;
            var value = result.Get("value") ?? default;
            if (where != null && !where(value)) continue;
            return (value, call.Seq, (long)(result.Num("sinceSeq") ?? call.Seq));
        }
        return null;
    }

    /// <summary>그 대상에 보낸 agent_command(command)의 마지막 성공 결과(LastCommand와 같고 agent = 대상 이름).</summary>
    public (JsonElement Value, long Seq, long SinceSeq)? LastCommand(TH_Target target, string command, Func<JsonElement, bool>? where = null) =>
        LastCommand(command, where, target.Name);

    /// <summary>
    /// tool의 마지막 호출(성공/실패 모두) 중 인자 argument(예: "alias")가 value인 것 - 헤드리스 클라 도구처럼 대상을 인자로 받는 도구.
    /// </summary>
    public ToolCallRecord? LastCallWith(string tool, string argument, string value) =>
        Calls.LastOrDefault(c => c.Tool == tool && string.Equals(c.Arguments?.Str(argument), value, StringComparison.OrdinalIgnoreCase));

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
