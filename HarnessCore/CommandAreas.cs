namespace TestHarness.Core;

/// <summary>명령 영역 하나 - 에이전트 명령 이름 "&lt;영역&gt;.&lt;동작&gt;"의 앞부분.</summary>
/// <param name="Name">영역 이름(= agent_list의 query 키워드).</param>
/// <param name="Agents">이 영역 명령을 받는 에이전트(설명용).</param>
/// <param name="Description">무엇을 다루는지.</param>
/// <param name="WhenToUse">언제 쓰는지.</param>
public sealed record CommandArea(string Name, string Agents, string Description, string WhenToUse);

/// <summary>
/// 에이전트 명령의 영역 목록. agent_list(query=...)의 키워드는 등록된 이름만 받는다(설명 문장 검색 같은 애매한 일치는 하지 않는다 - 같은
/// 키워드면 언제나 같은 결과가 나오게). 모든 에이전트에 공통인 영역(process, log, status)은 여기, 그 프로젝트의 에이전트가 가진 영역(editor,
/// shop, cheat ...)은 시나리오 어셈블리가 등록한다(ITH_GameKnowledge - HarnessScenarios/Knowledge).
///
/// 새 명령을 만들 때(TestHarness/CLAUDE.md "명령 영역" 참고):
/// - 영역은 "무엇을 다루나"(대상)로 정한다. 동작 단어는 영역이 아니다(buy.piece가 아니라 shop.buy).
/// - 조회 명령은 state(한 대상의 현재 상태) 또는 list(여러 개 목록)로 이름을 맞춘다.
/// - 기존 영역에 안 맞을 때만 새 영역을 만들고, 만들면 먼저 등록한다. 등록 안 된 영역의 명령이 에이전트에서 올라오면 agent_list가
///   unknownAreas로 드러낸다.
/// </summary>
public static class CommandAreas
{
    private static readonly object Lock = new();

    private static IReadOnlyList<CommandArea> _all = new[]
    {
        new CommandArea("log", "모든 에이전트", "그 프로세스의 콘솔 로그, 구간 표시",
            "Unity 콘솔(log.recent)을 보거나 로그에 표시(log.mark)를 남길 때"),
        new CommandArea("process", "모든 에이전트", "프로세스 정보와 상태(게임과 무관한 것)",
            "프로세스 정보(process.info), 그 프로세스의 현재 상태(process.status)"),
        new CommandArea(HarnessSharedConstants.Areas.Status, "그 상태를 가진 에이전트", "게임 상태 조회 - 테스트/노드가 판정에 쓰는 값(화면, 레플리케이션 ...)",
            "노드가 판정할 값을 볼 때. 테스트 실행 전에 대상 쪽에 구현해 둬야 하는 조회라, 없으면 \"구현부가 없음\"으로 실패한다"),
    };

    public static IReadOnlyList<CommandArea> All
    {
        get { lock (Lock) return _all; }
    }

    /// <summary>영역을 등록한다(하네스 시작 때 - 시나리오 카탈로그 등록 중). 같은 이름을 두 번 등록하면 오류.</summary>
    public static void Register(CommandArea area)
    {
        lock (Lock)
        {
            if (_all.Any(a => string.Equals(a.Name, area.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"명령 영역 이름 중복: {area.Name}");
            _all = _all.Append(area).ToArray();
        }
    }

    public static bool TryGet(string name, out CommandArea area)
    {
        area = All.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))!;
        return area != null;
    }

    /// <summary>명령 이름의 영역 부분("shop.buy" → "shop"). 점이 없으면 이름 전체.</summary>
    public static string AreaOf(string commandName)
    {
        var dot = commandName.IndexOf('.');
        return dot < 0 ? commandName : commandName[..dot];
    }

    public static string Names => string.Join(", ", All.Select(a => a.Name));
}
