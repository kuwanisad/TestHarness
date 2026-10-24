using System.ComponentModel;
using Microsoft.Extensions.Logging;
using TestHarness.Core;
using TestHarness.Daemon.Mcp;

namespace TestHarness.Daemon.Tools;

/// <summary>
/// 관찰: 이벤트 대기/조회, 서버 로그 조회. 출력은 항상 개수 제한이 있고, 넘치면 전체 개수와 좁히는 방법을
/// 같이 돌려준다.
/// </summary>
public sealed class ObserveTools
{
    private const int MaxWaitSeconds = 300;

    private readonly HarnessSession _session;

    public ObserveTools(HarnessSession session) => _session = session;

    [McpTool("wait_for")]
    [Description("조건에 맞는 이벤트가 올 때까지 기다린다(sleep 대신 이것을 쓸 것). sinceSeq 뒤에 이미 와 있으면 즉시 돌려준다. " +
                 "sinceSeq는 행동 도구가 돌려준 값을 넣을 것 - 안 주면 '지금 이후'만 본다. 시간 초과면 matched=false와 함께 " +
                 "그동안 온 이벤트(eventsSince)와, 혹시 sinceSeq 전에 이미 왔던 같은 조건의 이벤트(matchedBefore)를 돌려준다. " +
                 "source 예: client:p1 / p1 / agent:LobbyServer / LobbyServer / client:* / harness. " +
                 "names 예: MatchFound, MatchFailed, harness.attached, harness.detached.")]
    public async Task<object> WaitFor(
        [Description("출처(비우면 전부)")] string? source = null,
        [Description("이벤트 이름들 중 하나(비우면 전부)")] string[]? names = null,
        [Description("이 playerId의 이벤트만")] string? playerId = null,
        [Description("detail/data에 이 문자열이 들어 있는 것만(대소문자 무시, JSON 공백 무시). 값만 넣는 게 가장 확실 - 예: Planning")] string? contains = null,
        [Description("이 번호 뒤의 이벤트만(행동 도구의 sinceSeq). 비우면 지금 이후")] long? sinceSeq = null,
        [Description("최대 대기(초), 최대 300")] int timeoutSeconds = 30,
        CancellationToken ct = default)
    {
        var filter = new EventFilter(source, names, playerId, contains);
        var since = sinceSeq ?? _session.Timeline.LastSeq;
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, MaxWaitSeconds));

        var started = DateTimeOffset.UtcNow;
        var matched = await _session.Timeline.WaitForAsync(filter, since, timeout, ct);
        var waited = Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 1);

        if (matched != null)
        {
            return new { matched = true, waitedSeconds = waited, @event = EventView.From(matched), lastSeq = _session.Timeline.LastSeq };
        }

        // 시간 초과: 무엇이 대신 왔는지(같은 출처 우선)와, 이미 지나간 같은 조건 이벤트를 보여준다.
        var sinceFilter = new EventFilter(Source: source);
        var (eventsSince, totalSince) = _session.Timeline.Query(sinceFilter, since, 20, fromEnd: true);
        var (before, _) = _session.Timeline.Query(filter, 0, 3, fromEnd: true);
        var matchedBefore = before.Where(e => e.Seq <= since).Select(EventView.From).ToList();

        return new
        {
            matched = false,
            waitedSeconds = waited,
            eventsSince = eventsSince.Select(EventView.From),
            eventsSinceTotal = totalSince,
            matchedBefore = matchedBefore.Count == 0 ? null : matchedBefore,
            lastSeq = _session.Timeline.LastSeq,
            hint = matchedBefore.Count > 0
                ? "같은 조건의 이벤트가 sinceSeq 전에 이미 왔음 - sinceSeq를 행동 도구가 돌려준 값으로 줬는지 확인"
                : "기대한 이벤트가 안 옴 - logs_query로 관련 서버 로그를 볼 것",
        };
    }

    [McpTool("events_query")]
    [Description("타임라인(에이전트/클라/하네스 이벤트를 한 줄로 합친 것)을 조회한다. 기본은 조건에 맞는 마지막 50개(시간순).")]
    public object EventsQuery(
        [Description("출처(비우면 전부)")] string? source = null,
        [Description("이벤트 이름들 중 하나")] string[]? names = null,
        [Description("이 playerId의 이벤트만")] string? playerId = null,
        [Description("detail/data에 이 문자열이 들어 있는 것만(대소문자 무시, JSON 공백 무시). 값만 넣는 게 가장 확실 - 예: Planning")] string? contains = null,
        [Description("이 번호 뒤의 이벤트만")] long sinceSeq = 0,
        [Description("최대 개수(최대 200)")] int limit = 50,
        [Description("true면 마지막 limit개, false면 처음 limit개")] bool fromEnd = true)
    {
        var filter = new EventFilter(source, names, playerId, contains);
        var (events, total) = _session.Timeline.Query(filter, sinceSeq, Math.Clamp(limit, 1, 200), fromEnd);
        var truncated = total > events.Count;
        return new
        {
            matched = total,
            returned = events.Count,
            truncated,
            lastSeq = _session.Timeline.LastSeq,
            events = events.Select(EventView.From),
            hint = truncated ? "잘림 - source/names/playerId/sinceSeq로 좁히거나 fromEnd를 바꿀 것" : null,
        };
    }

    [McpTool("logs_query")]
    [Description("지금 실행(또는 runId 실행)의 서버 로그와 Unity 로그(logs/<에이전트>.log)를 걸러서 본다. 기본은 Information 이상, 조건에 맞는 마지막 50줄(여러 서버를 시각순으로 합침). " +
                 "playerId나 roomId를 contains에 넣으면 그 사람/방 관련 줄만. 시각은 UTC HH:mm:ss.fff(이벤트와 같은 형식).")]
    public object LogsQuery(
        [Description("서버 이름 또는 Unity 에이전트 이름(client, server, UnityEditor). 비우면 전부")] string? server = null,
        [Description("최소 수준")] LogLevel minLevel = LogLevel.Information,
        [Description("메시지/예외에 들어 있는 문자열(대소문자 무시)")] string? contains = null,
        [Description("이 시각 이후(UTC). ISO 8601(2026-10-16T11:30:00Z) 또는 HH:mm:ss(오늘 날짜로 봄 - 이벤트/로그의 t 값을 그대로 넣어도 됨)")] string? sinceUtc = null,
        [Description("이 시각 이전(UTC, sinceUtc와 같은 형식)")] string? untilUtc = null,
        [Description("최대 줄 수(최대 500)")] int limit = 50,
        [Description("true면 마지막 limit줄, false면 처음 limit줄")] bool fromEnd = true,
        [Description("지난 실행 id(비우면 지금 실행)")] string? runId = null)
    {
        var result = _session.Logs.Query(new LogQuery(server, minLevel, contains, ParseTime(sinceUtc, "sinceUtc"),
            ParseTime(untilUtc, "untilUtc"), limit, fromEnd), runId);

        return new
        {
            result.Matched,
            result.Returned,
            result.Truncated,
            entries = result.Entries.Select(LogView.From),
            files = result.Files,
            result.Hint,
        };
    }

    private static DateTimeOffset? ParseTime(string? text, string name)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // 시각만 주면(HH:mm:ss[.fff]) 오늘 UTC 날짜로 본다. 파서에 맡기면 "오늘"이 로컬 날짜가 돼서, 한국 시간
        // 00~09시(UTC로는 전날)에 하루가 어긋난다. 미래가 되면 어제로 본다(자정 직후 직전 로그를 볼 때).
        if (TimeSpan.TryParseExact(text.Trim(), new[] { @"hh\:mm\:ss\.fff", @"hh\:mm\:ss", @"hh\:mm" },
                System.Globalization.CultureInfo.InvariantCulture, out var timeOfDay))
        {
            var candidate = new DateTimeOffset(DateTime.UtcNow.Date + timeOfDay, TimeSpan.Zero);
            return candidate > DateTimeOffset.UtcNow.AddMinutes(1) ? candidate.AddDays(-1) : candidate;
        }

        if (DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var value))
        {
            return value;
        }

        throw new HarnessException($"{name} '{text}'을(를) 시각으로 읽지 못함", "ISO 8601 형식(예: 2026-10-16T11:30:00Z)");
    }
}
