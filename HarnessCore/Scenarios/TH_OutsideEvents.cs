namespace TestHarness.Core.Scenarios;

/// <summary>노드 구간 밖에서 일어난 이상 하나(보고서 경고용 - 판정에는 안 들어감).</summary>
public sealed record TH_OutsideEvent(long Seq, DateTimeOffset At, string Source, string Name, string What, string Detail);

/// <summary>
/// "노드 밖에서 일어난 일" - 시나리오 진행 중 노드 구간 밖(AI가 노드 사이에 직접 조작하거나 기다리는 동안)에서 일어난 이상. 노드 판정은 노드
/// 구간만 보므로 여기서 일어난 일(서버가 죽음, 클라가 끊김 ...)은 결과에 안 나온다 - 보고서 머리에 경고로 올려 놓치지 않게 한다(판정은 그대로).
/// 목록은 여기 한 곳이다(환경 변화 TH_EnvironmentChanges, 지속 조작 OverrideKinds와 같은 방식). 2026-10-23 PC 실행: 노드 사이에 인게임 서버가
/// 멈췄다 죽었는데 allPassed=true로 끝남.
///
/// 범위: 시나리오 시작 ~ 정리 노드 시작, 노드(최상위 방문) 구간과 시작 준비 구간은 뺀다.
/// </summary>
public static class TH_OutsideEvents
{
    /// <summary>(이벤트 이름, 설명, 이 이벤트가 해당하는지(이벤트, 선언된 대상 이름들)).</summary>
    private static readonly (string Name, string What, Func<TimelineEvent, IReadOnlySet<string>, bool> Applies)[] Watched =
    {
        ("harness.detached", "선언된 대상의 하네스 연결이 끊김(프로세스 종료/멈춤/Play 정지)",
            (e, targets) => AgentOf(e) is { } agent && targets.Contains(agent)),
        ("client.ingame_disconnected", "클라의 인게임(NGO) 접속이 끊김",
            (e, targets) => AgentOf(e) is { } agent && targets.Contains(agent)),
        // 사람 플레이어(playerId 있음 - 봇은 null)의 탈락. AI가 노드 사이에 일부러 탈락시켰어도 결과에 안 나오므로 경고로 올린다.
        ("player.health_changed", "사람 플레이어가 탈락함",
            (e, targets) => AgentOf(e) is { } agent && targets.Contains(agent) &&
                            e.Data is { } d && d.Bool("eliminated") &&
                            d.Get("playerId") is { ValueKind: System.Text.Json.JsonValueKind.String } p && !string.IsNullOrEmpty(p.GetString())),
    };

    /// <param name="events">시나리오 시작 ~ 정리 노드 시작 사이의 이벤트.</param>
    /// <param name="targets">선언된 대상 이름(대소문자 무시).</param>
    /// <param name="excluded">빼는 구간(노드 방문, 준비) - (첫 seq, 끝 seq) 포함.</param>
    public static IReadOnlyList<TH_OutsideEvent> Find(IEnumerable<TimelineEvent> events, IReadOnlySet<string> targets, IReadOnlyList<(long From, long To)> excluded)
    {
        var found = new List<TH_OutsideEvent>();
        foreach (var e in events)
        {
            if (excluded.Any(r => e.Seq >= r.From && e.Seq <= r.To)) continue;
            foreach (var (name, what, applies) in Watched)
            {
                if (e.Name != name || !applies(e, targets)) continue;
                var detail = e.Data is { } d ? HarnessJson.Truncate(d.GetRawText(), 200) : e.Detail;
                found.Add(new TH_OutsideEvent(e.Seq, e.TimestampUtc, e.Source, e.Name, what, detail));
            }
        }
        return found;
    }

    private static string? AgentOf(TimelineEvent e) =>
        e.Source.StartsWith("agent:", StringComparison.Ordinal) ? e.Source["agent:".Length..] : null;
}
