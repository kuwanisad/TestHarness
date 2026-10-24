using System.Text.Json;

namespace TestHarness.Core.Scenarios;

/// <summary>
/// 노드 실행이 도구를 부르는 통로. AI가 MCP로 부르는 것과 같은 도구 목록을 같은 프로세스 안에서 부른다(HarnessDaemon이 구현) -
/// 그래서 기록(mcp-calls.jsonl, 타임라인)과 판정 재료가 AI가 부를 때와 같다. 기록의 actor는 "runner".
/// </summary>
public interface ITH_ToolInvoker
{
    /// <param name="argumentsJson">도구 인자 JSON 객체(없으면 "{}").</param>
    /// <param name="objective">기록에 남길 실행 중 노드 경로.</param>
    Task<TH_ToolResult> InvokeAsync(string tool, string argumentsJson, string? objective, CancellationToken ct);
}

/// <summary>도구 한 번의 결과(응답 JSON과 오류 여부).</summary>
public sealed record TH_ToolResult(JsonElement Value, bool IsError, string Text)
{
    /// <summary>오류면 error와 hint를 합친 한 줄.</summary>
    public string ErrorText => IsError ? $"{Value.Str("error") ?? Text}{(Value.Str("hint") is { } hint ? $" ({hint})" : "")}" : "";
}

/// <summary>노드 실행을 그 자리에서 실패로 끝낸다 - 판정 코드를 돌리지 않고 이 사유로 Fail.</summary>
public sealed class TH_NodeFailure : Exception
{
    public TH_NodeFailure(string message) : base(message)
    {
    }
}

/// <summary>
/// 한 시나리오 실행 동안 노드끼리 넘기는 값. 전역 칸과 대상별 칸(<see cref="For"/> - 계정, playerId, 방처럼 대상마다 다른 값)이
/// 따로다. 아무 스레드에서나.
/// </summary>
public sealed class TH_RunValues
{
    private readonly object _lock = new();
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TH_RunValues> _targets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>그 대상의 값 칸(처음 부르면 만든다).</summary>
    public TH_RunValues For(TH_Target target)
    {
        lock (_lock)
        {
            if (!_targets.TryGetValue(target.Name, out var values)) _targets[target.Name] = values = new TH_RunValues();
            return values;
        }
    }

    public void Remove(string key)
    {
        lock (_lock) _values.Remove(key);
    }

    public void Set(string key, object? value)
    {
        lock (_lock) _values[key] = value;
    }

    public T? Get<T>(string key)
    {
        lock (_lock) return _values.TryGetValue(key, out var value) && value is T typed ? typed : default;
    }

    public bool Has(string key)
    {
        lock (_lock) return _values.ContainsKey(key);
    }

    /// <summary>보고서/상태 표시용 사본. 대상별 칸은 "@대상" 키 아래에.</summary>
    public IReadOnlyDictionary<string, object?> Snapshot()
    {
        lock (_lock)
        {
            var copy = new Dictionary<string, object?>(_values);
            foreach (var (name, values) in _targets) copy["@" + name] = values.Snapshot();
            return copy;
        }
    }
}

/// <summary>
/// 행동 노드의 Auto 함수가 받는 것 - AI가 하던 행동을 코드로 한다. 도구는 이름과 인자로 부른다(MCP와 같은 도구, 같은 결과).
/// 실패로 끝내려면 <see cref="Fail"/>(또는 TH_NodeFailure). 도구가 오류를 돌려주면 *Ok 계열은 바로 실패로 끝낸다.
/// </summary>
public sealed class TH_RunContext
{
    private readonly ITH_ToolInvoker _tools;
    private readonly Timeline _timeline;
    private readonly List<string> _notes;

    private readonly OverrideManager? _overrides;

    internal TH_RunContext(string nodePath, ITH_ToolInvoker tools, Timeline timeline, TH_RunValues values, List<string> notes, CancellationToken ct,
        OverrideManager? overrides = null)
    {
        _overrides = overrides;
        NodePath = nodePath;
        _tools = tools;
        _timeline = timeline;
        Values = values;
        _notes = notes;
        Cancellation = ct;
    }

    /// <summary>실행 중인 노드 경로(예: "ReadyIngame > Login").</summary>
    public string NodePath { get; }

    /// <summary>노드끼리 넘기는 값.</summary>
    public TH_RunValues Values { get; }

    public CancellationToken Cancellation { get; }

    public long LastSeq => _timeline.LastSeq;

    /// <summary>보고서의 그 노드 항목에 남길 메모(무엇을 골랐는지 등).</summary>
    public void Note(string text)
    {
        lock (_notes) _notes.Add(text);
    }

    public static TH_NodeFailure Fail(string reason) => new(reason);

    /// <summary>
    /// 이 노드가 지금까지 건 지속 조작(배속, 설정값 ... - OverrideKinds)을 노드가 끝나도 남긴다(기본은 노드 끝에 하네스가 되돌림).
    /// 남긴 것은 시나리오가 끝날 때 되돌린다. 남긴 개수를 돌려준다.
    /// </summary>
    public int KeepOverrides()
    {
        var kept = _overrides?.Keep(NodePath) ?? 0;
        if (kept > 0) Note($"지속 조작 {kept}개를 다음 노드까지 남김(시나리오 끝에 되돌림)");
        return kept;
    }

    /// <summary>도구를 부른다(오류여도 던지지 않음).</summary>
    public Task<TH_ToolResult> Call(string tool, object? args = null) =>
        _tools.InvokeAsync(tool, args == null ? "{}" : HarnessJson.Serialize(args), NodePath, Cancellation);

    /// <summary>도구를 부르고, 오류면 노드를 실패로 끝낸다.</summary>
    public async Task<JsonElement> CallOk(string tool, object? args = null)
    {
        var result = await Call(tool, args);
        if (result.IsError) throw Fail($"{tool} 실패: {result.ErrorText}");
        return result.Value;
    }

    /// <summary>에이전트 명령(agent_command). 명령이 실패(ok=false)하거나 도구 오류면 노드를 실패로 끝낸다.</summary>
    /// <returns>명령 직전 이벤트 번호(sinceSeq)와 명령 결과 값(value).</returns>
    public async Task<(long SinceSeq, JsonElement? Value)> Agent(string agent, string command, object? args = null, int timeoutSeconds = 15)
    {
        var (sinceSeq, ok, value, error) = await TryAgent(agent, command, args, timeoutSeconds);
        if (!ok) throw Fail($"{agent}의 {command} 실패: {error}");
        return (sinceSeq, value);
    }

    /// <summary>그 대상(에이전트)에 보내는 에이전트 명령 - <see cref="Agent(string, string, object?, int)"/>와 같다.</summary>
    public Task<(long SinceSeq, JsonElement? Value)> Agent(TH_Target target, string command, object? args = null, int timeoutSeconds = 15) =>
        Agent(target.Name, command, args, timeoutSeconds);

    public Task<(long SinceSeq, bool Ok, JsonElement? Value, string? Error)> TryAgent(TH_Target target, string command, object? args = null,
        int timeoutSeconds = 15) => TryAgent(target.Name, command, args, timeoutSeconds);

    /// <summary>그 대상의 이벤트를 기다린다(출처 = 대상 이름).</summary>
    public Task<JsonElement?> WaitFor(TH_Target target, string[] names, long? sinceSeq, int timeoutSeconds, string? contains = null) =>
        WaitFor(target.Name, names, sinceSeq, timeoutSeconds, contains);

    public Task<JsonElement> Expect(TH_Target target, string[] names, long? sinceSeq, int timeoutSeconds, string? contains = null) =>
        Expect(target.Name, names, sinceSeq, timeoutSeconds, contains);

    public Task<JsonElement?> PollAgent(TH_Target target, string command, Func<JsonElement, bool> until, int timeoutSeconds,
        int intervalMilliseconds = 1000) => PollAgent(target.Name, command, until, timeoutSeconds, intervalMilliseconds);

    /// <summary>에이전트 명령 - 실패해도 던지지 않는다(정리 노드처럼 "되면 좋고"인 경우).</summary>
    public async Task<(long SinceSeq, bool Ok, JsonElement? Value, string? Error)> TryAgent(string agent, string command, object? args = null,
        int timeoutSeconds = 15)
    {
        var result = await Call("agent_command", new { agent, command, args, timeoutSeconds });
        if (result.IsError) return (LastSeq, false, null, result.ErrorText);

        var sinceSeq = (long)(result.Value.Num("sinceSeq") ?? LastSeq);
        var ok = result.Value.Bool("ok");
        return (sinceSeq, ok, result.Value.Get("value") is { ValueKind: not JsonValueKind.Null } v ? v : null, result.Value.Str("error"));
    }

    /// <summary>
    /// 이벤트를 기다린다(wait_for). 오면 그 이벤트(seq, name, data ... - EventView 모양), 시간 안에 안 오면 null.
    /// wait_for 한 번의 한도(300초)보다 길면 같은 sinceSeq로 이어서 부른다(그 사이 온 이벤트도 안 놓침).
    /// </summary>
    public async Task<JsonElement?> WaitFor(string? source, string[] names, long? sinceSeq, int timeoutSeconds, string? contains = null)
    {
        var since = sinceSeq ?? LastSeq;
        var remaining = Math.Max(1, timeoutSeconds);
        while (remaining > 0)
        {
            var chunk = Math.Min(remaining, MaxWaitChunkSeconds);
            var result = await CallOk("wait_for", new { source, names, sinceSeq = since, timeoutSeconds = chunk, contains });
            if (result.Bool("matched")) return result.Get("event");
            remaining -= chunk;
        }
        return null;
    }

    private const int MaxWaitChunkSeconds = 300;

    /// <summary>이벤트를 기다리고, 안 오면 노드를 실패로 끝낸다.</summary>
    public async Task<JsonElement> Expect(string? source, string[] names, long? sinceSeq, int timeoutSeconds, string? contains = null)
    {
        return await WaitFor(source, names, sinceSeq, timeoutSeconds, contains)
               ?? throw Fail($"{timeoutSeconds}초 안에 {source}의 {string.Join("/", names)}{(contains != null ? $"({contains})" : "")}가 오지 않음");
    }

    /// <summary>
    /// 조건이 맞을 때까지 같은 에이전트 명령을 다시 본다(씬 전환처럼 이벤트 없이 상태만 바뀌는 것). 판정 코드는 마지막 응답을 보므로
    /// 맞으면 바로 멈춘다. 끝내 안 맞아도 던지지 않는다 - 판정 코드가 마지막 값으로 실패 이유를 쓴다.
    /// </summary>
    public async Task<JsonElement?> PollAgent(string agent, string command, Func<JsonElement, bool> until, int timeoutSeconds,
        int intervalMilliseconds = 1000)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        JsonElement? last = null;
        while (true)
        {
            var (_, ok, value, _) = await TryAgent(agent, command);
            if (ok && value is { } v)
            {
                last = v;
                if (until(v)) return v;
            }

            if (DateTimeOffset.UtcNow >= deadline) return last;
            await Task.Delay(intervalMilliseconds, Cancellation);
        }
    }
}

/// <summary>
/// 자동 실행(mode=auto)의 경로를 정하는 함수가 받는 것(TH_ScenarioBuilder.AutoPlan) - AI 대신 코드가 노드를 고른다.
/// 노드 실행은 AI가 node_run으로 부를 때와 같은 경로다.
/// </summary>
public sealed class TH_Planner
{
    private readonly Func<TH_Node, string?, Task<TH_NodeOutcome>> _run;

    internal TH_Planner(Func<TH_Node, string?, Task<TH_NodeOutcome>> run, TH_RunValues values)
    {
        _run = run;
        Values = values;
    }

    /// <summary>노드 실행이 남긴 값(예: 구매 노드의 merged).</summary>
    public TH_RunValues Values { get; }

    /// <summary>노드 하나를 실행하고 결과를 받는다. 지금 상태에서 시작할 수 없는 노드면 Unverified로 돌아온다(경로가 이상하다는 뜻).</summary>
    public Task<TH_NodeOutcome> Run(TH_Node node, string? reason = null) => _run(node, reason);
}

/// <summary>노드 실행 한 번의 결과(자동 실행 경로를 정하는 데 쓴다).</summary>
public sealed record TH_NodeOutcome(TH_Result Result, string Reason, TH_StateMap StateAfter);
