using System.Reflection;

namespace TestHarness.Core.Scenarios;

/// <summary>시나리오 클래스에 붙인다 - 이름(scenario_start에 쓰는 키)과 제목. TH_ScenarioCatalog가 리플렉션으로 모은다.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class TH_ScenarioAttribute : Attribute
{
    public TH_ScenarioAttribute(string name, string title)
    {
        Name = name;
        Title = title;
    }

    /// <summary>소문자 kebab-case(예: "full-match").</summary>
    public string Name { get; }

    public string Title { get; }
}

/// <summary>
/// 시나리오 하나 = 노드 그래프 + 맥락. 파생 클래스에 [TH_Scenario]를 붙이고 Build에서 그래프를 만든다
/// (HarnessScenarios 프로젝트에 둔다 - 기능 코드인 HarnessCore와 섞지 않는다). 노드는 HarnessScenarios/Nodes에서 공유한다.
///
/// 진행: 하네스가 AI에게 전체 그래프와 맥락을 준다 → AI가 지금 상태에서 시작할 수 있는 노드를 골라 node_run → 하네스가 실행하고
/// 판정 → AI가 결과를 보고 다음을 고르거나, 충분하면 scenario_finish. 특정 노드까지 갈 필요는 없다. 노드와 노드 사이에 테스트
/// 대상이 바뀌면(TH_EnvironmentChanges) 그 전 결과는 이전 환경이 되고 AI에게 알린다.
///
/// 작성 규칙:
/// - Context(맥락)에 왜, 언제 돌리는지, 어디까지 보면 되는지를 쓴다 - AI가 멈출 시점을 정하는 근거다.
/// - 노드의 PassCriteria는 관찰 가능한 사실로 쓴다. 판정은 판정 코드로(하네스). 코드로 판정할 수 없을 때만 AiJudged()
///   (그 시나리오의 AllowAiJudge가 true여야 한다).
/// - 노드 판정은 가능하면 다른 노드의 순서에 의존하지 않게(AI가 경로를 고르므로).
/// - 테스트 대상(클라, 서버 ...)은 s.Target(...)(HarnessScenarios의 s.UnityClient("client1") 등)으로 먼저 선언하고, 노드 함수에
///   그 인스턴스를 넘긴다. 선언 안 한 대상을 쓰는 노드는 시나리오를 만들 때 오류. 상태 칸은 전역 + 상태 칸을 가진 대상마다.
/// - 정리 노드(Cleanup)는 반드시 하나 - 어떻게 끝나든(AI 종료 선언, AI 지시 없음) 마지막에 하네스가 실행한다(ToInitial로 모든 칸을 처음으로).
/// - 사람 없이 돌릴 경로가 있으면 AutoPlan으로 적는다(mode=auto, 명령줄 run).
/// </summary>
public abstract class TH_Scenario
{
    protected abstract void Build(TH_ScenarioBuilder s);

    internal TH_ScenarioDefinition CreateDefinition(string name, string title)
    {
        var builder = new TH_ScenarioBuilder();
        Build(builder);
        return builder.Finish(name, title);
    }
}

/// <summary>시나리오 그래프 만들기(TH_Scenario.Build에서만 쓴다).</summary>
public sealed class TH_ScenarioBuilder
{
    private readonly List<string> _preconditions = new();
    private readonly List<string> _cautions = new();
    private readonly List<TH_Node> _nodes = new();
    private readonly List<TH_Target> _targets = new();
    private readonly List<string> _context = new();
    private TH_Node? _cleanup;
    private Func<TH_Planner, Task>? _autoPlan;

    internal TH_ScenarioBuilder()
    {
    }

    /// <summary>이 시나리오가 나오는 맥락 - 왜, 언제 돌리는지, 어디까지 보면 되는지(AI가 멈출 시점을 정하는 근거). 여러 번 부르면 이어진다.</summary>
    public void Context(string text) => _context.Add(text);

    /// <summary>시작 전에 갖춰져 있어야 하는 것(scenario_start 때 한 번 보여 줌).</summary>
    public void Precondition(string text) => _preconditions.Add(text);

    /// <summary>이 시나리오 동안 지킬 것(scenario_start 때 한 번 보여 줌).</summary>
    public void Caution(string text) => _cautions.Add(text);

    /// <summary>전역 칸의 시작 상태(기본 "ServersDown" - 서버도 Unity Play도 꺼진 상태). 대상 칸의 시작 상태는 대상이 정한다.</summary>
    public string InitialState { get; set; } = "ServersDown";

    /// <summary>
    /// 테스트 대상을 선언한다(이름은 시나리오 안에서 겹치면 안 됨). 대상의 Precondition 문구는 전제에 자동으로 붙는다.
    /// 상태 칸을 가진 대상(클라)은 그 칸이 생긴다.
    /// </summary>
    public T Target<T>(T target) where T : TH_Target
    {
        if (_targets.Contains(target)) return target;
        if (_targets.Any(t => string.Equals(t.Name, target.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"대상 이름 중복: {target.Name}");
        _targets.Add(target);
        if (target.Precondition is { } text && !_preconditions.Contains(text)) _preconditions.Add(text);
        return target;
    }

    /// <summary>
    /// 시나리오가 끝나면 하네스 PC에서 소리를 낼지(기본 true). 끝은 하네스가 판단한다(정리 노드까지 끝났을 때 - AI가 소리를
    /// 내라고 시키는 게 아니다). 전부 통과면 완료음, 아니면 경고음(FinishSound).
    /// </summary>
    public bool PlayFinishSound { get; set; } = true;

    /// <summary>AI 지시가 이 시간 동안 없으면(노드 실행 중은 빼고) 하네스가 정리하고 끝낸다 - 토큰이 떨어진 경우 등(기본 2분).</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(HarnessConstants.Limits.DefaultIdleTimeoutMinutes);

    private TH_LogFilter? _logs;

    /// <summary>
    /// 이 시나리오에서 볼 게임 코드 상황 로그(Unity DebugManager의 GameLogType 이름) - collect: 하네스로 모을 것, console: 콘솔에 띄울 것
    /// (null = 전부, 빈 목록 = 하나도 없음). 러너가 시나리오 시작과 대상 접속 때 선언된 대상들에 보내고(log.filter), 끝날 때 전부(기본값)로 되돌린다. 선언하지
    /// 않으면 아무것도 보내지 않는다(지금처럼 전부). ⚠ 이름은 Unity GameLogType과 같아야 한다(모르는 이름은 Unity가 거부 - 타임라인에 남음).
    /// 로그는 원인 찾기용이다(판정은 이벤트로).
    /// </summary>
    public void Logs(IReadOnlyCollection<string>? collect = null, IReadOnlyCollection<string>? console = null) =>
        _logs = new TH_LogFilter(collect?.ToArray(), console?.ToArray());

    /// <summary>AI 판정 노드(AiJudged)를 넣을 수 있는지(기본 true). false면 넣었을 때 시나리오를 만들 때 오류.</summary>
    public bool AllowAiJudge { get; set; } = true;

    /// <summary>
    /// 노드의 지속 조작 선언(Forbids/Needs)이 켜진 조작과 어긋날 때(기본 Reject - 노드를 돌리지 않음). 남은 조작이 있어도 진행해야 하는
    /// 시나리오만 Warn(돌리고 보고서/응답에 경고).
    /// </summary>
    public TH_OverrideMismatch OverrideMismatch { get; set; } = TH_OverrideMismatch.Reject;

    /// <summary>AI가 고를 수 있는 노드로 넣는다(묶음 노드의 자식은 따로 넣지 않아도 묶음과 함께 실행된다).</summary>
    public TH_Node Node(TH_Node node)
    {
        _nodes.Add(node);
        return node;
    }

    /// <summary>정리 노드(시나리오당 하나) - 어떻게 끝나든 마지막에 하네스가 실행한다. 아무 상태에서나 시작할 수 있어야 한다.</summary>
    public TH_Node Cleanup(TH_Node node)
    {
        if (_cleanup != null) throw new InvalidOperationException("정리 노드는 하나만");
        if (node.FromStates.Count > 0) throw new InvalidOperationException($"정리 노드 {node.Id}: From을 두지 않는다(아무 상태에서나)");
        if (!node.ResetsToInitial) throw new InvalidOperationException($"정리 노드 {node.Id}: ToInitial()이어야 한다(모든 칸을 처음 상태로)");
        _cleanup = node;
        return node;
    }

    /// <summary>자동 실행(mode=auto, 명령줄 run)의 경로 - AI 대신 코드가 노드를 고른다. 없으면 자동 실행을 시작할 수 없다.</summary>
    public void AutoPlan(Func<TH_Planner, Task> plan) => _autoPlan = plan;

    internal TH_ScenarioDefinition Finish(string name, string title)
    {
        if (_cleanup == null) throw new InvalidOperationException($"시나리오 {name}: Cleanup(정리 노드)이 없음");
        if (_nodes.Count == 0) throw new InvalidOperationException($"시나리오 {name}: 노드가 없음");
        if (_context.Count == 0) throw new InvalidOperationException($"시나리오 {name}: Context(맥락)가 없음 - AI가 멈출 시점을 정할 근거");

        var all = _nodes.Append(_cleanup).SelectMany(n => n.SelfAndDescendants()).ToList();
        var byId = new Dictionary<string, TH_Node>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in all)
        {
            if (byId.TryGetValue(node.Id, out var existing) && !ReferenceEquals(existing, node))
                throw new InvalidOperationException($"시나리오 {name}: 서로 다른 노드가 같은 ID '{node.Id}'");
            byId[node.Id] = node;

            if (!node.IsGroup && node.Judge == null && !node.IsAiJudged)
                throw new InvalidOperationException($"시나리오 {name}: 노드 {node.Id}에 판정이 없음 - 판정 코드를 주거나 AiJudged()");
            if (node.IsAiJudged && !AllowAiJudge)
                throw new InvalidOperationException($"시나리오 {name}: AllowAiJudge=false인데 AI 판정 노드 {node.Id}가 있음");
            foreach (var target in node.Targets.Where(t => !_targets.Contains(t)))
                throw new InvalidOperationException($"시나리오 {name}: 노드 {node.Id}가 선언 안 된 대상 {target.Name}({target.Kind})을 씀 - " +
                                                    "s.Target(...)(s.UnityClient 등)으로 선언한 인스턴스를 넘길 것");
        }

        if (_nodes.Distinct().Count() != _nodes.Count) throw new InvalidOperationException($"시나리오 {name}: 같은 노드를 두 번 넣음");

        var initial = new TH_StateMap(new[] { new KeyValuePair<string, string>(HarnessConstants.States.GlobalScope, InitialState) }
            .Concat(_targets.Where(t => t.InitialState != null).Select(t => new KeyValuePair<string, string>(t.Name, t.InitialState!))));

        return new TH_ScenarioDefinition(name, title, string.Join("\n", _context), _preconditions.ToArray(), _cautions.ToArray(),
            _nodes.ToArray(), _cleanup, initial, _targets.ToArray(), PlayFinishSound, IdleTimeout, AllowAiJudge, _autoPlan, byId)
        {
            OverrideMismatch = OverrideMismatch,
            LogFilter = _logs,
        };
    }
}

/// <summary>만들어진 시나리오 그래프(바뀌지 않음).</summary>
public sealed class TH_ScenarioDefinition
{
    private readonly IReadOnlyDictionary<string, TH_Node> _byId;

    internal TH_ScenarioDefinition(string name, string title, string context, IReadOnlyList<string> preconditions, IReadOnlyList<string> cautions,
        IReadOnlyList<TH_Node> nodes, TH_Node cleanup, TH_StateMap initialState, IReadOnlyList<TH_Target> targets, bool playFinishSound, TimeSpan idleTimeout, bool allowAiJudge,
        Func<TH_Planner, Task>? autoPlan, IReadOnlyDictionary<string, TH_Node> byId)
    {
        Name = name;
        Title = title;
        Context = context;
        Preconditions = preconditions;
        Cautions = cautions;
        Nodes = nodes;
        Cleanup = cleanup;
        InitialState = initialState;
        Targets = targets;
        PlayFinishSound = playFinishSound;
        IdleTimeout = idleTimeout;
        AllowAiJudge = allowAiJudge;
        AutoPlan = autoPlan;
        _byId = byId;
    }

    public string Name { get; }
    public string Title { get; }
    public string Context { get; }
    public IReadOnlyList<string> Preconditions { get; }
    public IReadOnlyList<string> Cautions { get; }

    /// <summary>AI가 고를 수 있는 노드(정리 노드 제외).</summary>
    public IReadOnlyList<TH_Node> Nodes { get; }

    public TH_Node Cleanup { get; }

    /// <summary>시작 상태(전역 + 상태 칸을 가진 대상마다).</summary>
    public TH_StateMap InitialState { get; }

    /// <summary>선언된 테스트 대상(선언 순서).</summary>
    public IReadOnlyList<TH_Target> Targets { get; }

    public bool PlayFinishSound { get; }
    public TimeSpan IdleTimeout { get; }
    public bool AllowAiJudge { get; }
    public TH_OverrideMismatch OverrideMismatch { get; init; } = TH_OverrideMismatch.Reject;

    /// <summary>게임 코드 상황 로그 목록(TH_ScenarioBuilder.Logs) - null이면 선언 없음.</summary>
    public TH_LogFilter? LogFilter { get; init; }
    public Func<TH_Planner, Task>? AutoPlan { get; }

    /// <summary>그래프의 모든 노드(자식과 정리 노드 포함) 중 ID로.</summary>
    public TH_Node? Find(string id) => _byId.TryGetValue(id, out var node) ? node : null;

    /// <summary>AI가 node_run으로 고를 수 있는 노드인지(묶음 안의 자식은 단독으로 못 고른다).</summary>
    public bool IsSelectable(TH_Node node) => Nodes.Contains(node);
}

/// <summary>[TH_Scenario] 클래스들을 모은 목록.</summary>
public sealed class TH_ScenarioCatalog
{
    private readonly Dictionary<string, (Type Type, TH_ScenarioAttribute Attribute)> _scenarios = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<(string Name, string Title)> List =>
        _scenarios.Values.OrderBy(s => s.Attribute.Name, StringComparer.Ordinal).Select(s => (s.Attribute.Name, s.Attribute.Title));

    private readonly HashSet<Type> _knowledge = new();

    /// <summary>
    /// 어셈블리를 등록한다 - 먼저 그 프로젝트의 지식(ITH_GameKnowledge 구현)을 넣고, 그다음 [TH_Scenario] 클래스를 모은다. 그래프는 여기서 한 번
    /// 만들어 보고 오류(ID 중복 등)를 바로 알린다.
    /// </summary>
    public void Register(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes().Where(t => typeof(ITH_GameKnowledge).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface))
        {
            if (!_knowledge.Add(type)) continue;
            ((ITH_GameKnowledge)Activator.CreateInstance(type, nonPublic: true)!).Register(new TH_GameKnowledge());
        }

        foreach (var type in assembly.GetTypes())
        {
            var attribute = type.GetCustomAttribute<TH_ScenarioAttribute>();
            if (attribute == null) continue;
            if (!typeof(TH_Scenario).IsAssignableFrom(type) || type.IsAbstract)
                throw new InvalidOperationException($"{type.FullName}: [TH_Scenario]는 TH_Scenario 파생 클래스에만");
            if (_scenarios.ContainsKey(attribute.Name))
                throw new InvalidOperationException($"시나리오 이름 중복: {attribute.Name}");

            _ = Create(type, attribute); // 그래프 검증
            _scenarios[attribute.Name] = (type, attribute);
        }
    }

    /// <summary>시작할 때마다 새로 만든다(정의 코드가 실행마다 독립되게).</summary>
    public TH_ScenarioDefinition? TryCreate(string name) =>
        _scenarios.TryGetValue(name, out var entry) ? Create(entry.Type, entry.Attribute) : null;

    private static TH_ScenarioDefinition Create(Type type, TH_ScenarioAttribute attribute) =>
        ((TH_Scenario)Activator.CreateInstance(type)!).CreateDefinition(attribute.Name, attribute.Title);
}

/// <summary>노드의 지속 조작 선언(Forbids/Needs)이 켜진 조작과 어긋날 때 하네스가 할 일(시나리오 전역 - TH_ScenarioBuilder.OverrideMismatch).</summary>
public enum TH_OverrideMismatch
{
    /// <summary>노드를 돌리지 않는다(상태 순서 위반과 같은 거부 - 묶음 자식이면 그 자식 Fail).</summary>
    Reject,

    /// <summary>노드를 돌리고 보고서/응답에 경고를 남긴다.</summary>
    Warn,
}

/// <summary>시나리오가 선언한 게임 코드 상황 로그 목록(null = 전부).</summary>
public sealed record TH_LogFilter(IReadOnlyList<string>? Collect, IReadOnlyList<string>? Console);
