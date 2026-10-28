using System.Text.Json;

namespace TestHarness.Core.Scenarios;

// 시나리오 틀(TH_ = TestHarness). 시나리오 = 노드들의 그래프. 노드는 하네스가 실행하고 판정한다. AI는 전체 그래프를 보고
// "다음에 어느 노드를 돌지"만 고르며(node_run), 충분하다고 판단하면 언제든 끝낸다(scenario_finish). 진행 규칙과 기록은
// TH_ScenarioRunner, 시나리오 작성법은 TH_Scenario 주석.

/// <summary>노드 하나의 판정 결과 종류.</summary>
public enum TH_Result
{
    /// <summary>통과 기준을 만족.</summary>
    Pass,

    /// <summary>통과 기준을 만족하지 못함.</summary>
    Fail,

    /// <summary>판단할 수 없음(근거 부족, 확인 불가, 자동 실행의 AI 판정 노드) - 모르는 걸 채우는 대신 쓰는 출구.</summary>
    Unverified,

    /// <summary>테스트를 일찍 끝내느라(scenario_finish) 실행 중에 멈춤 - 판정하지 않는다. 하네스만 정한다(AI 판정 결과로는 못 씀).</summary>
    Stopped,
}

/// <summary>묶음 노드 안에서 자식이 통과하지 못했을 때(Fail/Unverified) 나머지 자식을 어떻게 할지.</summary>
public enum TH_FailPolicy
{
    /// <summary>뒤 자식이 이 결과에 의존 - 묶음을 멈춘다(남은 자식은 실행 안 함).</summary>
    Stop,

    /// <summary>뒤 자식과 독립 - 기록만 하고 다음 자식으로.</summary>
    Continue,
}

/// <summary>판정 하나: 결과, 이유, 근거 이벤트 번호.</summary>
public sealed record TH_Verdict(TH_Result Result, string Reason, IReadOnlyList<long> EvidenceSeqs)
{
    public bool Passed => Result == TH_Result.Pass;

    public static TH_Verdict Pass(string reason, params long[] evidence) => new(TH_Result.Pass, reason, evidence);
    public static TH_Verdict Fail(string reason, params long[] evidence) => new(TH_Result.Fail, reason, evidence);
    public static TH_Verdict Unverified(string reason, params long[] evidence) => new(TH_Result.Unverified, reason, evidence);
    public static TH_Verdict Stopped(string reason) => new(TH_Result.Stopped, reason, Array.Empty<long>());

    /// <summary>조건이 참이면 Pass, 아니면 Fail(같은 근거).</summary>
    public static TH_Verdict PassIf(bool condition, string passReason, string failReason, params long[] evidence) =>
        condition ? Pass(passReason, evidence) : Fail(failReason, evidence);

    /// <summary>앞에서부터 차례로 - 하나라도 통과 못 하면 그 판정, 전부 통과면 이유와 근거를 합친 Pass.</summary>
    public static TH_Verdict All(params TH_Verdict[] verdicts)
    {
        var failed = verdicts.FirstOrDefault(v => !v.Passed);
        if (failed != null) return failed;
        return Pass(string.Join(" / ", verdicts.Select(v => v.Reason)), verdicts.SelectMany(v => v.EvidenceSeqs).Distinct().ToArray());
    }
}

/// <summary>
/// 노드 하나. 두 종류:
/// - 행동 노드(<see cref="Action"/>): 하네스가 실행하는 행동(Auto - AI가 하던 도구 호출을 코드로)과 판정(Judge 또는 AiJudged).
/// - 묶음 노드(<see cref="Group"/>): 자식 노드들을 순서대로 품는다. 하네스가 자식을 먼저 끝까지 돌고(후위순회) 자식 결과를
///   모아 자기 결과를 정한다(전부 통과 + 선택적인 자기 판정).
///
/// 상태: 하네스는 칸(범위)별 상태를 들고 있다(TH_StateMap - 전역 + 상태 칸을 가진 대상마다). From은 칸마다 "시작할 수 있는 상태들"
/// (적은 칸은 전부 맞아야 시작 - 안 적은 칸은 보지 않음), To는 칸마다 "통과하면 될 상태"(안 적은 칸은 그대로). 대상 없이 쓰면 전역 칸.
/// 하네스는 시작할 수 없는 노드로의 이동을 거부한다. 상태 이름은 시나리오들이 같이 쓰는 문자열(HarnessConstants.States).
///
/// 대상을 받는 노드는 노드 함수로 만든다(<see cref="For"/> - ID는 "이름@대상", 같은 대상이면 같은 인스턴스). 대상 없는 노드는
/// 여러 시나리오가 공유한다(HarnessScenarios/Nodes). 어느 쪽이든 정의할 때 설정(From/To/...)을 끝내고 그 뒤로는 바꾸지 않는다.
/// 노드 결과는 가능하면 다른 노드의 실행 순서에 의존하지 않게 판정한다(AI가 경로를 고르므로).
/// </summary>
public sealed class TH_Node
{
    private readonly List<(string Scope, string[] States)> _from = new();
    private readonly List<(string Scope, string State)> _to = new();
    private readonly List<TH_Target> _targets = new();
    private readonly List<string> _forbids = new();
    private readonly List<string> _needs = new();
    private readonly List<string> _hints = new();
    private readonly List<(TH_Target Target, string Command)> _probes = new();

    private TH_Node(string id, string goal, string passCriteria, Func<TH_RunContext, Task>? auto,
        Func<TH_JudgeContext, TH_Verdict>? judge, IReadOnlyList<TH_Node> children)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("노드 ID가 비어 있음");
        Id = id;
        Goal = goal;
        PassCriteria = passCriteria;
        Auto = auto;
        Judge = judge;
        Children = children;
    }

    /// <summary>행동 노드. judge가 없으면 .AiJudged()를 붙여야 한다(아니면 시나리오를 만들 때 오류).</summary>
    public static TH_Node Action(string id, string goal, string passCriteria, Func<TH_RunContext, Task> auto,
        Func<TH_JudgeContext, TH_Verdict>? judge = null) =>
        new(id, goal, passCriteria, auto ?? throw new ArgumentNullException(nameof(auto)), judge, Array.Empty<TH_Node>());

    /// <summary>묶음 노드 - 자식을 순서대로 실행(후위순회). judge는 자식이 전부 통과했을 때 추가로 보는 판정(선택).</summary>
    public static TH_Node Group(string id, string goal, IReadOnlyList<TH_Node> children, Func<TH_JudgeContext, TH_Verdict>? judge = null)
    {
        if (children.Count == 0) throw new ArgumentException($"묶음 노드 {id}: 자식이 없음");
        var criteria = "자식 노드 전부 통과: " + string.Join(" → ", children.Select(c => c.Id));
        return new TH_Node(id, goal, criteria, null, judge, children.ToArray());
    }

    /// <summary>
    /// 대상을 받는 노드 함수용 - ID는 "<paramref name="name"/>@대상1+대상2", 같은 대상(들)로 다시 부르면 처음 만든 인스턴스를 돌려준다
    /// (시나리오의 s.Node(Login(c1))와 자동 실행의 p.Run(Login(c1))가 같은 노드). create는 그 ID를 받아 노드를 만든다.
    /// 캐시는 첫 대상에 있다 - 대상은 시나리오를 만들 때마다 새로 생기므로 실행끼리 섞이지 않는다.
    /// </summary>
    public static TH_Node For(string name, IReadOnlyList<TH_Target> targets, Func<string, TH_Node> create)
    {
        if (targets.Count == 0) throw new ArgumentException($"노드 함수 {name}: 대상이 없음");
        if (targets.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count)
            throw new ArgumentException($"노드 함수 {name}: 같은 대상을 두 번 넘김");

        var id = $"{name}@{string.Join("+", targets.Select(t => t.Name))}";
        if (targets[0].NodeCache.TryGetValue(id, out var cached)) return cached;

        var node = create(id);
        if (!string.Equals(node.Id, id, StringComparison.Ordinal))
            throw new InvalidOperationException($"노드 함수 {name}: 만든 노드의 ID({node.Id})가 받은 ID({id})와 다름");
        foreach (var target in targets) node.Uses(target);
        targets[0].NodeCache[id] = node;
        return node;
    }

    /// <inheritdoc cref="For(string, IReadOnlyList{TH_Target}, Func{string, TH_Node})"/>
    public static TH_Node For(string name, TH_Target target, Func<string, TH_Node> create) => For(name, new[] { target }, create);

    public string Id { get; }

    /// <summary>무엇을 하는 노드인지(AI에게 보여 줌).</summary>
    public string Goal { get; }

    /// <summary>언제 통과인지 - 관찰 가능한 사실로(AI에게 보여 줌).</summary>
    public string PassCriteria { get; }

    /// <summary>하네스가 실행하는 행동(행동 노드). 묶음 노드는 null.</summary>
    public Func<TH_RunContext, Task>? Auto { get; }

    /// <summary>하네스 판정. 행동 노드에서 null이면 AI 판정 노드여야 한다.</summary>
    public Func<TH_JudgeContext, TH_Verdict>? Judge { get; }

    /// <summary>묶음 노드의 자식(순서대로). 행동 노드는 빈 목록.</summary>
    public IReadOnlyList<TH_Node> Children { get; }

    public bool IsGroup => Children.Count > 0;

    /// <summary>하네스가 실행만 하고 판정은 AI가 node_judge로 보고하는 노드(근거 seq 필수). 자동 실행에서는 Unverified.</summary>
    public bool IsAiJudged { get; private set; }

    /// <summary>칸별 시작 가능 상태(비면 아무 상태에서나).</summary>
    public IReadOnlyList<(string Scope, string[] States)> FromStates => _from;

    /// <summary>칸별 통과 후 상태(비면 상태를 바꾸지 않음).</summary>
    public IReadOnlyList<(string Scope, string State)> ToStates => _to;

    /// <summary>통과하면 모든 칸을 시작 상태로(정리 노드).</summary>
    public bool ResetsToInitial { get; private set; }

    /// <summary>이 노드가 쓰는 대상(시나리오에 선언됐는지 검사용).</summary>
    public IReadOnlyList<TH_Target> Targets => _targets;

    /// <summary>묶음 노드 안에서 이 노드가 통과 못 하면 묶음을 멈출지(기본 Stop).</summary>
    public TH_FailPolicy FailPolicy { get; private set; } = TH_FailPolicy.Stop;

    /// <summary>AI가 노드를 이해하는 데 도움이 되는 설명(관련 명령 등).</summary>
    public IReadOnlyList<string> Hints => _hints;

    /// <summary>전역 칸이 이 상태들 중 하나일 때만 시작할 수 있다.</summary>
    public TH_Node From(params string[] states) => AddFrom(HarnessConstants.States.GlobalScope, states);

    /// <summary>그 대상의 칸이 이 상태들 중 하나일 때만 시작할 수 있다.</summary>
    public TH_Node From(TH_Target target, params string[] states) => AddFrom(StateScope(target), states);

    /// <summary>통과하면 전역 칸이 이 상태가 된다.</summary>
    public TH_Node To(string state) => AddTo(HarnessConstants.States.GlobalScope, state);

    /// <summary>통과하면 그 대상의 칸이 이 상태가 된다.</summary>
    public TH_Node To(TH_Target target, string state) => AddTo(StateScope(target), state);

    /// <summary>통과하면 모든 칸이 시나리오 시작 상태로 돌아간다(정리 노드).</summary>
    public TH_Node ToInitial()
    {
        if (_to.Count > 0) throw new InvalidOperationException($"노드 {Id}: ToInitial과 To를 같이 쓰지 않는다");
        ResetsToInitial = true;
        return this;
    }

    /// <summary>이 노드가 그 대상을 쓴다고 표시(From/To에 안 나오는 대상 - 시나리오 선언 검사용).</summary>
    public TH_Node Uses(TH_Target target)
    {
        if (!_targets.Contains(target)) _targets.Add(target);
        return this;
    }

    private string StateScope(TH_Target target)
    {
        if (target.InitialState == null)
            throw new InvalidOperationException($"노드 {Id}: 대상 {target.Name}({target.Kind})은 상태 칸이 없음 - 전역 From/To를 쓸 것");
        Uses(target);
        return target.Name;
    }

    private TH_Node AddFrom(string scope, string[] states)
    {
        if (states.Length == 0) throw new ArgumentException($"노드 {Id}: From에 상태가 없음");
        if (_from.Any(f => string.Equals(f.Scope, scope, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"노드 {Id}: 칸 {scope}의 From을 두 번 씀 - 한 번에 상태들을 다 적을 것");
        _from.Add((scope, states));
        return this;
    }

    private TH_Node AddTo(string scope, string state)
    {
        if (ResetsToInitial) throw new InvalidOperationException($"노드 {Id}: ToInitial과 To를 같이 쓰지 않는다");
        if (_to.Any(t => string.Equals(t.Scope, scope, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"노드 {Id}: 칸 {scope}의 To를 두 번 씀");
        _to.Add((scope, state));
        return this;
    }

    /// <summary>AI 판정 노드로 만든다(시나리오의 AllowAiJudge가 true여야 넣을 수 있다).</summary>
    public TH_Node AiJudged()
    {
        if (IsGroup) throw new InvalidOperationException($"묶음 노드 {Id}는 AI 판정으로 만들 수 없음(자식 결과로 정해짐)");
        if (Judge != null) throw new InvalidOperationException($"노드 {Id}: 판정 코드가 있으면 AI 판정으로 만들 수 없음");
        IsAiJudged = true;
        return this;
    }

    public TH_Node OnFail(TH_FailPolicy policy)
    {
        FailPolicy = policy;
        return this;
    }

    public TH_Node Hint(params string[] hints)
    {
        _hints.AddRange(hints);
        return this;
    }

    /// <summary>
    /// 이 노드가 시작할 때 남아 있으면 안 되는 지속 조작 종류(OverrideKinds 이름 - 예: 배속이 남아 있으면 시간 검증이 무의미). 노드 시작 직전에
    /// 하네스가 켜진 조작과 비교한다 - 어긋나면 시나리오의 OverrideMismatch(기본 Reject)대로. 선언이 없는 노드는 남은 조작을 보지 않는다.
    /// </summary>
    public TH_Node Forbids(params string[] overrideKinds)
    {
        _forbids.AddRange(CheckKinds(overrideKinds, nameof(Forbids)));
        return this;
    }

    /// <summary>이 노드가 시작할 때 켜져 있어야 하는 지속 조작 종류(앞 노드가 KeepOverrides로 남긴 설정 위에서 확인하는 노드 등).</summary>
    public TH_Node Needs(params string[] overrideKinds)
    {
        _needs.AddRange(CheckKinds(overrideKinds, nameof(Needs)));
        return this;
    }

    /// <summary>
    /// 이 노드가 그 대상에 부르는 게임 상태 조회(status.* - 테스트 실행 전에 대상 쪽에 구현해 둬야 하는 것). 하네스가 노드를 돌리기 전에 대상의 명령
    /// 목록(접속할 때 알린 것)과 비교해 없으면 실행하지 않고 실패로 남긴다("구현부가 없음"). 대상이 아직 안 붙었으면 그때 비교하지 못하고, 붙는 순간
    /// 시나리오 전체의 선언과 비교해 타임라인에 남긴다(scenario.probe_missing).
    /// </summary>
    public TH_Node Probes(TH_Target target, params string[] commands)
    {
        if (commands.Length == 0) throw new ArgumentException($"노드 {Id}: Probes에 명령이 없음");
        // 대상 선언 검사(Uses)는 하지 않는다 - 비교는 대상 이름으로 하고, 인게임 서버처럼 노드가 공유 인스턴스(TH_Unity.Server)로 부르는 대상도 있다.
        foreach (var command in commands)
            if (!_probes.Contains((target, command))) _probes.Add((target, command));
        return this;
    }

    public IReadOnlyList<(TH_Target Target, string Command)> ProbedCommands => _probes;

    public IReadOnlyList<string> ForbiddenOverrides => _forbids;
    public IReadOnlyList<string> NeededOverrides => _needs;

    private IEnumerable<string> CheckKinds(string[] kinds, string what)
    {
        if (kinds.Length == 0) throw new ArgumentException($"노드 {Id}: {what}에 종류가 없음");
        foreach (var kind in kinds)
        {
            if (OverrideKinds.ByName(kind) == null)
                throw new ArgumentException($"노드 {Id}: {what}의 '{kind}'는 지속 조작 종류가 아님 - 있는 것: {string.Join(", ", OverrideKinds.All.Select(k => k.Name))}");
            if (_forbids.Contains(kind) || _needs.Contains(kind))
                throw new InvalidOperationException($"노드 {Id}: 종류 '{kind}'를 Forbids/Needs에 두 번 씀");
        }
        return kinds;
    }

    /// <summary>이 상태에서 시작할 수 있는지(적은 칸이 전부 맞아야).</summary>
    public bool CanStartFrom(TH_StateMap state) =>
        _from.All(f => state[f.Scope] is { } current && f.States.Contains(current, StringComparer.OrdinalIgnoreCase));

    /// <summary>시작할 수 없는 이유(칸별로 안 맞는 것). 시작할 수 있으면 null.</summary>
    public string? WhyCannotStart(TH_StateMap state)
    {
        var bad = _from.Where(f => !(state[f.Scope] is { } current && f.States.Contains(current, StringComparer.OrdinalIgnoreCase)))
            .Select(f => $"{f.Scope}={state[f.Scope] ?? "(칸 없음)"}인데 {string.Join("/", f.States)} 필요")
            .ToList();
        return bad.Count > 0 ? string.Join(", ", bad) : null;
    }

    /// <summary>통과했을 때의 다음 상태.</summary>
    public TH_StateMap Apply(TH_StateMap state, TH_StateMap initial) =>
        ResetsToInitial ? initial : _to.Count == 0 ? state : state.With(_to.Select(t => new KeyValuePair<string, string>(t.Scope, t.State)));

    /// <summary>응답용 - 칸별 From(없으면 null).</summary>
    public IReadOnlyDictionary<string, string[]>? FromView() =>
        _from.Count == 0 ? null : _from.ToDictionary(f => f.Scope, f => f.States, StringComparer.OrdinalIgnoreCase);

    /// <summary>응답용 - 칸별 To(없으면 null, 정리 노드는 "initial").</summary>
    public object? ToView() =>
        ResetsToInitial ? "initial" : _to.Count == 0 ? null : _to.ToDictionary(t => t.Scope, t => t.State, StringComparer.OrdinalIgnoreCase);

    /// <summary>이 노드와 모든 자손(깊이 우선).</summary>
    public IEnumerable<TH_Node> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var node in child.SelfAndDescendants()) yield return node;
        }
    }
}

/// <summary>JSON 값 읽기 도우미(판정/행동 코드를 짧게 하려고). 없거나 타입이 다르면 기본값.</summary>
public static class TH_Json
{
    public static JsonElement? Get(this JsonElement element, string path)
    {
        var current = element;
        foreach (var part in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out var next)) return null;
            current = next;
        }
        return current;
    }

    public static string? Str(this JsonElement element, string path) =>
        element.Get(path) is { } v ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : null;

    public static bool Bool(this JsonElement element, string path) =>
        element.Get(path) is { ValueKind: JsonValueKind.True };

    public static double? Num(this JsonElement element, string path) =>
        element.Get(path) is { ValueKind: JsonValueKind.Number } v ? v.GetDouble() : null;

    /// <summary>배열이면 원소들, 아니면 빈 목록.</summary>
    public static IEnumerable<JsonElement> Items(this JsonElement element, string path) =>
        element.Get(path) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : Enumerable.Empty<JsonElement>();
}
