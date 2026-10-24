namespace TestHarness.Core.Scenarios;

/// <summary>
/// 시나리오의 테스트 대상 하나(Unity 클라, 헤드리스 클라, Unity 인게임 서버, 에디터, .NET 서버 ...). 노드는 문자열 이름 대신 이 인스턴스를
/// 받는다 - 클라가 여럿이어도 노드(Login@client2), 상태 칸, 값 칸이 대상마다 따로다.
///
/// 이름(<see cref="Name"/>)은 하네스에서 부르는 이름 그대로다: 에이전트 이름(agent_command의 agent - MPPM 태그 Client1 → client1) 또는
/// 헤드리스 클라 별칭(client_* 도구의 alias). 이벤트 출처(wait_for의 source)도 이 이름이면 된다(agent:/client: 접두어는 하네스가 맞춘다).
///
/// 게임 쪽 동작(로그인 명령 이름 등)은 여기 두지 않는다 - 파생 클래스는 HarnessScenarios에 둔다(기능 코드와 시나리오 코드 분리).
/// 대상 인스턴스는 시나리오를 만들 때마다 새로 생긴다(TH_ScenarioBuilder.Target) - 대상별 노드 캐시도 같이 새로 생긴다.
/// 이름 "TH_Target": 게임 코드의 Actor/Player/RoomMember와 겹치지 않게.
/// </summary>
public abstract class TH_Target
{
    protected TH_Target(string name, string kind)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("대상 이름이 비어 있음");
        if (string.Equals(name, TH_StateMap.Global, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"대상 이름 '{name}'는 쓸 수 없음(전역 상태 칸 이름)");
        Name = name;
        Kind = kind;
    }

    /// <summary>하네스에서 부르는 이름(에이전트 이름 또는 헤드리스 클라 별칭). 상태 칸과 값 칸의 키도 이것.</summary>
    public string Name { get; }

    /// <summary>종류(UnityClient, HeadlessClient, UnityServer ...) - AI에게 보여 주는 설명용.</summary>
    public string Kind { get; }

    /// <summary>
    /// 이 대상이 상태 칸을 가지면 그 시작 상태(클라: 접속 전). null이면 상태 칸이 없다 - 노드의 From/To에 쓸 수 없다
    /// (서버/에디터처럼 전역 상태로 충분한 대상).
    /// </summary>
    public virtual string? InitialState => null;

    /// <summary>시나리오에 선언하면 전제(Precondition)로 자동으로 붙는 문구(예: MPPM 태그). 없으면 null.</summary>
    public virtual string? Precondition => null;

    /// <summary>
    /// 시나리오 시작 때(첫 노드 전) 하네스가 이 대상에 해 두는 준비의 설명(보고서, scenario_start 응답). null이면 준비 없음 -
    /// <see cref="PrepareAsync"/>를 부르지 않는다. 예: Unity 에디터 - Play 시작 씬을 출발 씬으로 정해 둔다(모든 에디터).
    /// </summary>
    public virtual string? Preparation => null;

    /// <summary>
    /// 시나리오 시작 때 하는 준비(<see cref="Preparation"/>이 있을 때만 하네스가 부른다). 노드와 같은 실행 문맥으로 도구를 부른다
    /// (기록의 objective는 "준비@이름"). 실패는 <see cref="TH_RunContext.Fail"/>로 던진다 - 시나리오는 그대로 시작하고 실패가 응답/보고서에
    /// 남는다(그 준비에 기대는 노드의 판정이 다시 잡는다). 켜 두면 남는 조작은 지속 조작(OverrideKinds)으로 - 시나리오 끝에 하네스가 되돌린다.
    /// </summary>
    public virtual Task PrepareAsync(TH_RunContext r) => Task.CompletedTask;

    /// <summary>대상별 노드 캐시 - 같은 대상으로 같은 노드 함수를 다시 부르면 같은 인스턴스(TH_Node.For).</summary>
    internal Dictionary<string, TH_Node> NodeCache { get; } = new(StringComparer.OrdinalIgnoreCase);

    public override string ToString() => Name;
}

/// <summary>
/// 시나리오 진행 상태 - 칸(범위)마다 상태 하나. 칸은 전역(<see cref="Global"/> - 서버/Unity Play 같은 전체 상태)과 상태 칸을 가진
/// 대상(클라)마다 하나. 예: { global: ServersUp, client1: Lobby, client2: Login }. 바뀌지 않는 값 - 바꾸면 새 인스턴스.
/// </summary>
public sealed class TH_StateMap
{
    public const string Global = "global";

    private readonly Dictionary<string, string> _states;
    private readonly List<string> _order;

    public TH_StateMap(IEnumerable<KeyValuePair<string, string>> states)
    {
        _states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _order = new List<string>();
        foreach (var (scope, state) in states)
        {
            if (!_states.ContainsKey(scope)) _order.Add(scope);
            _states[scope] = state;
        }
    }

    /// <summary>그 칸의 상태(없는 칸이면 null).</summary>
    public string? this[string scope] => _states.TryGetValue(scope, out var state) ? state : null;

    public bool Has(string scope) => _states.ContainsKey(scope);

    public IReadOnlyList<string> Scopes => _order;

    /// <summary>몇 칸을 바꾼 새 상태(없는 칸은 무시하지 않고 추가하지도 않는다 - 시나리오에 선언된 칸만).</summary>
    public TH_StateMap With(IEnumerable<KeyValuePair<string, string>> changes)
    {
        var next = _order.Select(s => new KeyValuePair<string, string>(s, _states[s])).ToList();
        foreach (var (scope, state) in changes)
        {
            var index = next.FindIndex(kv => string.Equals(kv.Key, scope, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) next[index] = new KeyValuePair<string, string>(next[index].Key, state);
        }
        return new TH_StateMap(next);
    }

    /// <summary>응답/기록용 - 칸 순서(전역 먼저, 선언 순서) 그대로의 사전.</summary>
    public IReadOnlyDictionary<string, string> View()
    {
        var view = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in _order) view[scope] = _states[scope];
        return view;
    }

    /// <summary>바뀐 칸만 "client1 Login→Lobby" 꼴로(보고서용). 같으면 "변화 없음".</summary>
    public string DiffTo(TH_StateMap after)
    {
        var parts = _order.Where(s => !string.Equals(this[s], after[s], StringComparison.OrdinalIgnoreCase))
            .Select(s => $"{s} {this[s]}→{after[s]}")
            .ToList();
        return parts.Count > 0 ? string.Join(", ", parts) : "변화 없음";
    }

    public override string ToString() => string.Join(", ", _order.Select(s => $"{s}={_states[s]}"));
}
