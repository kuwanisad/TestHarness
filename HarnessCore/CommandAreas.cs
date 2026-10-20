namespace TestHarness.Core;

/// <summary>명령 영역 하나 - 에이전트 명령 이름 "&lt;영역&gt;.&lt;동작&gt;"의 앞부분.</summary>
/// <param name="Name">영역 이름(= agent_list의 query 키워드).</param>
/// <param name="Agents">이 영역 명령을 받는 에이전트(설명용).</param>
/// <param name="Description">무엇을 다루는지.</param>
/// <param name="WhenToUse">언제 쓰는지.</param>
public sealed record CommandArea(string Name, string Agents, string Description, string WhenToUse);

/// <summary>
/// 에이전트 명령의 영역 목록 - 여기가 유일한 원본이다. agent_list(query=...)의 키워드는 이 이름만 받는다
/// (설명 문장 검색 같은 애매한 일치는 하지 않는다 - 같은 키워드면 언제나 같은 결과가 나오게).
///
/// 새 명령을 만들 때(TestHarness/CLAUDE.md "명령 영역" 참고):
/// - 영역은 "무엇을 다루나"(대상)로 정한다. 동작 단어는 영역이 아니다(buy.piece가 아니라 shop.buy).
/// - 조회 명령은 state(한 대상의 현재 상태) 또는 list(여러 개 목록)로 이름을 맞춘다.
/// - 기존 영역에 안 맞을 때만 새 영역을 만들고, 만들면 여기에 먼저 등록한다. 등록 안 된 영역의 명령이
///   에이전트에서 올라오면 agent_list가 unknownAreas로 드러낸다.
/// </summary>
public static class CommandAreas
{
    public static readonly IReadOnlyList<CommandArea> All = new[]
    {
        new CommandArea("editor", "UnityEditor", "Unity 에디터 자체(에디터 브리지)",
            "Unity 코드 반영(refresh), 컴파일 오류 확인, Play/Stop, 에디터 콘솔"),
        new CommandArea("client", "Unity 클라(보통 client)", "Unity 클라의 로그인/매칭/상태",
            "Unity 클라로 로그인하고 매칭을 시작할 때"),
        new CommandArea("shop", "Unity 클라", "매물대 - UI와 같은 경로로 조작/조회(구매, 리롤, 판매, 내 기물)",
            "실제 UI 경로를 검증할 때(규칙을 건너뛰지 않음)"),
        new CommandArea("cheat", "Unity 인게임 서버(보통 server)", "규칙을 건너뛰는 테스트 준비(시간, 데미지, 지정 구매, 참가자 상태)",
            "테스트 상황을 빨리 만들 때 - 검증 대상이 아니라 준비용"),
        new CommandArea("ingame", "Unity 인게임 서버", "인게임 서버의 방 상태 조회",
            "응접실/진행 중인 방, 접속자, 부하를 볼 때"),
        new CommandArea("log", "모든 에이전트", "그 프로세스의 콘솔 로그, 구간 표시",
            "Unity 콘솔(log.recent)을 보거나 로그에 표시(log.mark)를 남길 때"),
        new CommandArea("process", "모든 에이전트", "프로세스 정보와 상태",
            "프로세스 정보(process.info), 그 프로세스 고유의 현재 상태(process.status)"),
    };

    private static readonly Dictionary<string, CommandArea> ByName =
        All.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(string name, out CommandArea area) => ByName.TryGetValue(name, out area!);

    /// <summary>명령 이름의 영역 부분("shop.buy" → "shop"). 점이 없으면 이름 전체.</summary>
    public static string AreaOf(string commandName)
    {
        var dot = commandName.IndexOf('.');
        return dot < 0 ? commandName : commandName[..dot];
    }

    public static string Names => string.Join(", ", All.Select(a => a.Name));
}
