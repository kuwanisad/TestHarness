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
/// 시나리오 하나 = 목표 그래프. 파생 클래스에 [TH_Scenario]를 붙이고 Build에서 그래프를 만든다
/// (HarnessScenarios 프로젝트에 둔다 - 기능 코드인 HarnessCore와 섞지 않는다).
///
/// 작성 규칙:
/// - 목표마다 PassCriteria는 관찰 가능한 사실로 쓴다(이벤트 이름/값, 도구 응답 값).
/// - 판정은 가능하면 judge로 하네스가 한다. 코드로 판단할 수 없을 때만 judge를 비운다(AI 판정 + 근거 필수).
/// - 분기는 가능하면 결과 분기(Then(select) - 하네스가 기록을 보고 정함). 정말 상황에 따라 AI가 골라야 할 때만
///   ThenChoice(guide, ...).
/// - 뒤 목표가 앞 결과에 의존하면 onFail: Stop. 정리 목표(Cleanup)는 반드시 하나 - 중단돼도 마지막에 내준다.
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
    private readonly List<TH_Objective> _objectives = new();
    private TH_Objective? _start;
    private TH_Objective? _cleanup;

    internal TH_ScenarioBuilder()
    {
    }

    /// <summary>시작 전에 갖춰져 있어야 하는 것(AI에게 scenario_start 때 한 번 보여 줌).</summary>
    public void Precondition(string text) => _preconditions.Add(text);

    /// <summary>이 시나리오 동안 지킬 것(scenario_start 때 한 번 보여 줌).</summary>
    public void Caution(string text) => _cautions.Add(text);

    /// <summary>목표 하나를 만든다. 연결은 반환값의 Then/ThenChoice로.</summary>
    /// <param name="id">시나리오 안에서 유일(예: "A1").</param>
    /// <param name="goal">무엇을 할지.</param>
    /// <param name="passCriteria">언제 통과인지(관찰 가능한 사실).</param>
    /// <param name="judge">하네스 판정. null이면 AI 판정(근거 필수).</param>
    /// <param name="onFail">통과 못 하면 멈출지.</param>
    /// <param name="hints">관련 명령/도구(제한 아님).</param>
    public TH_Objective Objective(string id, string goal, string passCriteria,
        Func<TH_JudgeContext, TH_Verdict>? judge = null, TH_FailPolicy onFail = TH_FailPolicy.Stop, params string[] hints)
    {
        var objective = new TH_Objective(id, goal, passCriteria, judge, onFail, hints, isCleanup: false);
        _objectives.Add(objective);
        return objective;
    }

    /// <summary>정리 목표(시나리오당 하나) - 정상 종료든 중단이든 마지막에 내준다. ID는 "CLEANUP".</summary>
    public TH_Objective Cleanup(string goal, string passCriteria, Func<TH_JudgeContext, TH_Verdict>? judge = null, params string[] hints)
    {
        if (_cleanup != null) throw new InvalidOperationException("정리 목표는 하나만");
        _cleanup = new TH_Objective(TH_ScenarioDefinition.CleanupId, goal, passCriteria, judge, TH_FailPolicy.Continue, hints, isCleanup: true);
        _objectives.Add(_cleanup);
        return _cleanup;
    }

    /// <summary>첫 목표.</summary>
    public void Start(TH_Objective first) => _start = first;

    internal TH_ScenarioDefinition Finish(string name, string title)
    {
        if (_start == null) throw new InvalidOperationException($"시나리오 {name}: Start(첫 목표)가 없음");
        if (_cleanup == null) throw new InvalidOperationException($"시나리오 {name}: Cleanup(정리 목표)이 없음");

        var duplicate = _objectives.GroupBy(o => o.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null) throw new InvalidOperationException($"시나리오 {name}: 목표 ID 중복 '{duplicate.Key}'");

        return new TH_ScenarioDefinition(name, title, _preconditions.ToArray(), _cautions.ToArray(), _objectives.ToArray(), _start, _cleanup);
    }
}

/// <summary>만들어진 시나리오 그래프(바뀌지 않음).</summary>
public sealed class TH_ScenarioDefinition
{
    public const string CleanupId = "CLEANUP";

    internal TH_ScenarioDefinition(string name, string title, IReadOnlyList<string> preconditions, IReadOnlyList<string> cautions,
        IReadOnlyList<TH_Objective> objectives, TH_Objective start, TH_Objective cleanup)
    {
        Name = name;
        Title = title;
        Preconditions = preconditions;
        Cautions = cautions;
        Objectives = objectives;
        Start = start;
        Cleanup = cleanup;
    }

    public string Name { get; }
    public string Title { get; }
    public IReadOnlyList<string> Preconditions { get; }
    public IReadOnlyList<string> Cautions { get; }
    public IReadOnlyList<TH_Objective> Objectives { get; }
    public TH_Objective Start { get; }
    public TH_Objective Cleanup { get; }
}

/// <summary>[TH_Scenario] 클래스들을 모은 목록.</summary>
public sealed class TH_ScenarioCatalog
{
    private readonly Dictionary<string, (Type Type, TH_ScenarioAttribute Attribute)> _scenarios = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<(string Name, string Title)> List =>
        _scenarios.Values.OrderBy(s => s.Attribute.Name, StringComparer.Ordinal).Select(s => (s.Attribute.Name, s.Attribute.Title));

    /// <summary>어셈블리의 [TH_Scenario] 클래스를 등록한다. 그래프는 여기서 한 번 만들어 보고 오류(ID 중복 등)를 바로 알린다.</summary>
    public void Register(Assembly assembly)
    {
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

    /// <summary>시작할 때마다 새로 만든다(목표의 Then 연결이 실행마다 독립되게).</summary>
    public TH_ScenarioDefinition? TryCreate(string name) =>
        _scenarios.TryGetValue(name, out var entry) ? Create(entry.Type, entry.Attribute) : null;

    private static TH_ScenarioDefinition Create(Type type, TH_ScenarioAttribute attribute) =>
        ((TH_Scenario)Activator.CreateInstance(type)!).CreateDefinition(attribute.Name, attribute.Title);
}
