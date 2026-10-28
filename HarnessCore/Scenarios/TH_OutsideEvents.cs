namespace TestHarness.Core.Scenarios;

/// <summary>노드 구간 밖에서 일어난 이상 하나(보고서 경고용 - 판정에는 안 들어감).</summary>
public sealed record TH_OutsideEvent(long Seq, DateTimeOffset At, string Source, string Name, string What, string Detail);

/// <summary>
/// "노드 밖에서 일어난 일" - 시나리오 진행 중 노드 구간 밖(AI가 노드 사이에 직접 조작하거나 기다리는 동안)에서 일어난 이상. 노드 판정은 노드
/// 구간만 보므로 여기서 일어난 일(서버가 죽음, 클라가 끊김 ...)은 결과에 안 나온다 - 보고서 머리에 경고로 올려 놓치지 않게 한다(판정은 그대로).
/// 하네스 연결이 끊김(harness.detached)은 여기, 그 게임의 이상 이벤트(클라 끊김, 사람 탈락 ...)는 시나리오 어셈블리가 등록한다
/// (ITH_GameKnowledge - HarnessScenarios/Knowledge).
///
/// 범위: 시나리오 시작 ~ 정리 노드 시작, 노드(최상위 방문) 구간과 시작 준비 구간은 뺀다.
/// </summary>
public static class TH_OutsideEvents
{
    private static readonly object Lock = new();

    /// <summary>(이벤트 이름, 설명, 이 이벤트가 해당하는지(이벤트, 선언된 대상 이름들)).</summary>
    private static IReadOnlyList<(string Name, string What, Func<TimelineEvent, IReadOnlySet<string>, bool> Applies)> _watched = new[]
    {
        (HarnessSharedConstants.Events.HarnessDetached, "선언된 대상의 하네스 연결이 끊김(프로세스 종료/멈춤/Play 정지)",
            (Func<TimelineEvent, IReadOnlySet<string>, bool>)((e, targets) => FromDeclaredAgent(e, targets))),
    };

    /// <summary>노드 밖 이상으로 볼 이벤트를 등록한다.</summary>
    public static void Register(string name, string what, Func<TimelineEvent, IReadOnlySet<string>, bool> applies)
    {
        lock (Lock) _watched = _watched.Append((name, what, applies)).ToArray();
    }

    /// <summary>이 이벤트가 선언된 대상의 에이전트가 낸 것인지 - 등록하는 쪽이 조건에 같이 쓴다.</summary>
    public static bool FromDeclaredAgent(TimelineEvent e, IReadOnlySet<string> targets) => AgentOf(e) is { } agent && targets.Contains(agent);

    /// <param name="events">시나리오 시작 ~ 정리 노드 시작 사이의 이벤트.</param>
    /// <param name="targets">선언된 대상 이름(대소문자 무시).</param>
    /// <param name="excluded">빼는 구간(노드 방문, 준비) - (첫 seq, 끝 seq) 포함.</param>
    public static IReadOnlyList<TH_OutsideEvent> Find(IEnumerable<TimelineEvent> events, IReadOnlySet<string> targets, IReadOnlyList<(long From, long To)> excluded)
    {
        IReadOnlyList<(string Name, string What, Func<TimelineEvent, IReadOnlySet<string>, bool> Applies)> watched;
        lock (Lock) watched = _watched;
        var found = new List<TH_OutsideEvent>();
        foreach (var e in events)
        {
            if (excluded.Any(r => e.Seq >= r.From && e.Seq <= r.To)) continue;
            foreach (var (name, what, applies) in watched)
            {
                if (e.Name != name || !applies(e, targets)) continue;
                var detail = e.Data is { } d ? HarnessJson.Truncate(d.GetRawText(), HarnessConstants.Limits.OutsideEventDetailMaxLength) : e.Detail;
                found.Add(new TH_OutsideEvent(e.Seq, e.TimestampUtc, e.Source, e.Name, what, detail));
            }
        }
        return found;
    }

    private static string? AgentOf(TimelineEvent e) =>
        e.Source.StartsWith(HarnessConstants.Sources.AgentPrefix, StringComparison.Ordinal) ? e.Source[HarnessConstants.Sources.AgentPrefix.Length..] : null;
}
