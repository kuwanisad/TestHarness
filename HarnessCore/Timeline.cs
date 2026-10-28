using System.Text.Json;
using TestHarness.HarnessHost;
using TestHarness.HeadlessClient;

namespace TestHarness.Core;

/// <summary>
/// 하네스가 관찰한 일 하나. 출처가 달라도(서버/Unity 에이전트, 헤드리스 클라, 하네스 자신) 한 줄에
/// 같은 모양으로 쌓인다 - "무슨 일이 어떤 순서로 일어났나"를 한 목록으로 보기 위해.
///
/// Seq는 하네스 안에서 단조 증가하는 번호다. 시각(TimestampUtc)은 프로세스마다 시계가 조금씩 다를 수
/// 있어서, "어떤 행동 이후에 온 것"을 고를 때는 시각이 아니라 Seq를 기준점으로 쓴다(sinceSeq).
/// </summary>
/// <param name="Source">"agent:LobbyServer", "client:p1", "harness" 형식.</param>
/// <param name="Name">에이전트 이벤트 이름(예: "harness.attached"), 클라 이벤트 종류(예: "MatchFound"),
/// 하네스 이벤트 이름(예: "servers.started").</param>
public sealed record TimelineEvent(
    long Seq,
    DateTimeOffset TimestampUtc,
    string Source,
    string Name,
    string? PlayerId,
    string Detail,
    JsonElement? Data);

/// <summary>이벤트 고르기 조건. 비어 있는 항목은 조건 없음.</summary>
public sealed record EventFilter(
    string? Source = null,
    IReadOnlyList<string>? Names = null,
    string? PlayerId = null,
    string? Contains = null)
{
    public bool Matches(TimelineEvent e)
    {
        // 도구 호출 기록(source="ai" - AI, "runner" - 노드를 실행하는 하네스, ToolCallLog)은 출처를 지정했을 때만 - 그냥 "다음 이벤트"를
        // 기다리거나 조회할 때 방금 자기가 부른 도구가 걸리지 않게.
        if (string.IsNullOrEmpty(Source) && (e.Source == HarnessConstants.Sources.Ai || e.Source == HarnessConstants.Sources.Runner)) return false;
        if (!string.IsNullOrEmpty(Source) && !SourceMatches(e.Source, Source)) return false;
        if (Names is { Count: > 0 } && !Names.Any(n => string.Equals(n, e.Name, StringComparison.OrdinalIgnoreCase))) return false;
        if (!string.IsNullOrEmpty(PlayerId) && !string.Equals(e.PlayerId, PlayerId, StringComparison.Ordinal)) return false;

        if (!string.IsNullOrEmpty(Contains))
        {
            var inDetail = e.Detail.Contains(Contains, StringComparison.OrdinalIgnoreCase);
            var inData = e.Data is { } data && DataContains(data, Contains);
            if (!inDetail && !inData) return false;
        }

        return true;
    }

    // 공백 없는 JSON(한글은 그대로) - 원문 형식과 상관없이 같은 비교를 하려고.
    private static readonly System.Text.Json.JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 데이터 원문 또는 공백 없는 형태에 들어 있는지. 원문은 보낸 쪽 형식 그대로라(에이전트 이벤트는 protobuf JSON -
    /// {"phase": "Planning"}처럼 콜론 뒤 공백) AI가 공백 없이 "phase":"Planning"으로 찾으면 못 잡던 문제 때문에 둘 다 본다.
    /// </summary>
    private static bool DataContains(JsonElement data, string text) =>
        data.GetRawText().Contains(text, StringComparison.OrdinalIgnoreCase) ||
        JsonSerializer.Serialize(data, CompactJson).Contains(text, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// "agent:LobbyServer" 정확히, "LobbyServer"(접두어 생략), "agent:*"/"client:*"(종류 전체),
    /// "p1"(클라 별칭) 모두 받는다 - AI가 접두어를 빼먹어도 의도대로 걸리게.
    /// </summary>
    private static bool SourceMatches(string actual, string wanted)
    {
        if (wanted.EndsWith('*')) return actual.StartsWith(wanted[..^1], StringComparison.OrdinalIgnoreCase);
        if (string.Equals(actual, wanted, StringComparison.OrdinalIgnoreCase)) return true;

        var colon = actual.IndexOf(':');
        return colon >= 0 && string.Equals(actual[(colon + 1)..], wanted, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 모든 출처의 이벤트를 모으는 곳 + 조건 대기. HarnessEventLog/ClientEventLog와 같은 방식(고정 시간 대기
/// 대신 "N초 안에 오면 통과")을 출처 구분 없이 한 곳에서 한다.
///
/// 실행(run)이 열려 있으면 이벤트를 그 폴더의 events.jsonl에도 한 줄씩 쓴다 - 하네스를 껐다 켜도,
/// 나중에 AI가 파일로 직접 grep해도 남아 있게.
/// </summary>
public sealed class Timeline
{
    private readonly object _lock = new();
    private readonly List<TimelineEvent> _events = new();
    private readonly List<Waiter> _waiters = new();
    private long _nextSeq = 1;
    private string? _eventsFile;

    public long LastSeq
    {
        get { lock (_lock) return _nextSeq - 1; }
    }

    /// <summary>
    /// 이벤트가 기록될 때마다(락 밖에서, 기록한 스레드에서) 불린다 - 시나리오 진행기가 환경 변화와 AI 활동을 알아채는 데 쓴다.
    /// 구독자는 빨리 돌아와야 하고 예외를 던지지 않아야 한다(던지면 무시).
    /// </summary>
    public event Action<TimelineEvent>? Recorded;

    /// <summary>events.jsonl 경로를 바꾼다(null이면 파일 기록 중지). 실행이 바뀔 때 RunManager가 부른다. 이벤트마다 열고 닫는다(HarnessFiles).</summary>
    public void SetEventsFile(string? path)
    {
        lock (_lock) _eventsFile = path;
    }

    public TimelineEvent Record(
        DateTimeOffset timestampUtc, string source, string name, string? playerId, string detail, JsonElement? data)
    {
        TimelineEvent evt;
        List<Waiter>? satisfied = null;

        lock (_lock)
        {
            evt = new TimelineEvent(_nextSeq++, timestampUtc, source, name, string.IsNullOrEmpty(playerId) ? null : playerId, detail, data);
            _events.Add(evt);
            if (_events.Count > HarnessConstants.Limits.TimelineInMemory) _events.RemoveRange(0, _events.Count - HarnessConstants.Limits.TimelineInMemory);

            // 파일 기록 실패가 판정(메모리 이벤트)까지 막으면 안 된다(AppendLine은 던지지 않음).
            if (_eventsFile != null) HarnessFiles.AppendLine(_eventsFile, HarnessJson.Serialize(evt));

            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                var waiter = _waiters[i];
                if (evt.Seq <= waiter.AfterSeq || !waiter.Filter.Matches(evt)) continue;

                _waiters.RemoveAt(i);
                (satisfied ??= new List<Waiter>()).Add(waiter);
            }
        }

        if (satisfied != null)
        {
            foreach (var waiter in satisfied) waiter.Tcs.TrySetResult(evt);
        }

        if (Recorded is { } recorded)
        {
            foreach (var handler in recorded.GetInvocationList().Cast<Action<TimelineEvent>>())
            {
                try { handler(evt); }
                catch (Exception) { /* 구독자 오류가 기록을 막으면 안 된다 */ }
            }
        }

        return evt;
    }

    /// <summary>하네스 자신이 남기는 이벤트(서버 기동 결과 등).</summary>
    public TimelineEvent RecordHarness(string name, string detail, object? data = null) =>
        Record(DateTimeOffset.UtcNow, HarnessConstants.Sources.Harness, name, null, detail, data == null ? null : HarnessJson.ToElement(data));

    /// <summary>HarnessHost(에이전트 접속/이벤트) → 타임라인.</summary>
    public void RecordAgentEvent(HarnessEvent e) =>
        Record(e.TimestampUtc, HarnessConstants.Sources.AgentPrefix + e.AgentName, e.Name, e.PlayerId, "", HarnessJson.TryParse(e.DataJson));

    /// <summary>헤드리스 클라 이벤트 → 타임라인. 출처 이름은 클라 이름(= 하네스 별칭).</summary>
    public void RecordClientEvent(ClientEvent e) =>
        Record(e.TimestampUtc, HarnessConstants.Sources.ClientPrefix + e.ClientName, e.Kind.ToString(), e.PlayerId, e.Detail,
            e.Data == null ? null : HarnessJson.ToElement(e.Data));

    /// <summary>sinceSeq 뒤의 이벤트 중 조건에 맞는 것. fromEnd면 마지막 limit개(시간순 정렬은 유지).</summary>
    public (IReadOnlyList<TimelineEvent> Events, int Total) Query(EventFilter filter, long sinceSeq, int limit, bool fromEnd)
    {
        List<TimelineEvent> matched;
        lock (_lock)
        {
            matched = _events.Where(e => e.Seq > sinceSeq && filter.Matches(e)).ToList();
        }

        var page = fromEnd ? matched.Skip(Math.Max(0, matched.Count - limit)) : matched.Take(limit);
        return (page.ToList(), matched.Count);
    }

    /// <summary>번호로 이벤트 하나(메모리에서 밀려났거나 없으면 null).</summary>
    public TimelineEvent? TryGet(long seq)
    {
        lock (_lock)
        {
            // Seq는 단조 증가라 이분 탐색.
            int lo = 0, hi = _events.Count - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                var value = _events[mid].Seq;
                if (value == seq) return _events[mid];
                if (value < seq) lo = mid + 1;
                else hi = mid - 1;
            }
            return null;
        }
    }

    /// <summary>afterSeq 뒤 ~ toSeq까지의 이벤트 전부(출처 구분 없이 - AI 도구 호출 기록 포함).</summary>
    public IReadOnlyList<TimelineEvent> Range(long afterSeq, long toSeq)
    {
        lock (_lock) return _events.Where(e => e.Seq > afterSeq && e.Seq <= toSeq).ToList();
    }

    /// <summary>
    /// sinceSeq 뒤에 조건에 맞는 이벤트가 오기를 기다린다(이미 와 있으면 즉시). 시간 초과면 null -
    /// 예외가 아니라 값으로 돌려준다(도구가 "그동안 온 것"을 같이 보여주려고).
    /// </summary>
    public async Task<TimelineEvent?> WaitForAsync(EventFilter filter, long sinceSeq, TimeSpan timeout, CancellationToken ct)
    {
        Waiter waiter;
        lock (_lock)
        {
            var existing = _events.FirstOrDefault(e => e.Seq > sinceSeq && filter.Matches(e));
            if (existing != null) return existing;

            waiter = new Waiter(filter, sinceSeq);
            _waiters.Add(waiter);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await waiter.Tcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            lock (_lock) _waiters.Remove(waiter);
            return null;
        }
    }

    private sealed class Waiter
    {
        public Waiter(EventFilter filter, long afterSeq)
        {
            Filter = filter;
            AfterSeq = afterSeq;
        }

        public EventFilter Filter { get; }
        public long AfterSeq { get; }
        public TaskCompletionSource<TimelineEvent> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
