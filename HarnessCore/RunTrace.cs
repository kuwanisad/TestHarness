using System.Text;

namespace TestHarness.Core;

/// <summary>구간 기록의 한 줄 - 이벤트(타임라인) 또는 로그(서버/Unity).</summary>
/// <param name="Kind">"event" | "log".</param>
/// <param name="Source">이벤트 출처(agent:client, harness ...) 또는 로그를 남긴 서버/에이전트 이름. 합친 중복 로그는 "client+UnityEditor".</param>
/// <param name="Level">로그 수준(Information/Warning/Error ...). 이벤트는 "event".</param>
/// <param name="Seq">이벤트 번호(로그는 null).</param>
public sealed record TraceLine(DateTimeOffset Time, string Kind, string Source, string Level, string Text, long? Seq)
{
    public bool IsLog => Kind == "log";

    /// <summary>경고 이상 로그(Raw는 Information으로 친다, .err.log의 줄은 Error).</summary>
    public bool IsWarningOrAbove => IsLog && Level is HarnessConstants.LogLevels.Warning or HarnessConstants.LogLevels.Error or HarnessConstants.LogLevels.Critical;

    /// <summary>"HH:mm:ss.fff  #seq  출처  수준  내용" 한 줄. 시각은 이 PC의 지역 시각 - 사람이 보는 보고서/구간 기록은 지역 시각으로 통일한다
    /// (원본 events.jsonl, 로그 json은 UTC 그대로).</summary>
    public string Render()
    {
        var seq = Seq is { } s ? $"#{s}" : "";
        return $"{Time.ToLocalTime():HH:mm:ss.fff}  {seq,-7} {Source,-24} {Level,-11} {Text}";
    }
}

/// <summary>방문(노드 실행 한 번) 구간 하나의 기록 요약(보고서용).</summary>
/// <param name="File">전체 기록 파일(실행 폴더 기준 상대 경로). 쓰지 못했으면 null.</param>
/// <param name="Summary">요약 줄(이벤트 + 경고 이상 로그) 중 마지막 HarnessConstants.Limits.TraceSummaryMaxLines줄.</param>
/// <param name="SummaryTotal">잘리기 전 요약 줄 수.</param>
public sealed record TraceExcerpt(string? File, int Events, int Logs, int WarningLogs,
    IReadOnlyList<TraceLine> Summary, int SummaryTotal);

/// <summary>
/// 한 구간의 이벤트(타임라인)와 로그(서버 + Unity, 실행 폴더 logs/)를 시각순으로 합친 기록. 시나리오 보고서가 방문마다
/// 전체 기록을 trace/ 파일로 남기고, 보고서에는 요약(이벤트 + 경고 이상 로그)만 싣는다 - 평소엔 요약을 보고, 필요할 때만
/// 전체 기록(그래도 모자라면 logs/ 원본)을 본다.
///
/// - 구간: 이벤트는 seq(afterSeq 뒤 ~ toSeq), 로그는 시각(from ~ to). 모든 프로세스가 같은 PC라 시계가 같다는 전제.
/// - 정렬: 시각순. 같은 시각이면 이벤트가 먼저(LINQ OrderBy는 안정 정렬 - 이벤트를 먼저 넣는다).
/// - 노드 경계 줄(HarnessConstants.NodeMarker - 에이전트 로그 파일에 넣은 것)은 뺀다 - 보고서가 이미 노드별로 나뉘어 있다.
/// - 요약에서 빼는 이벤트: AI 도구 호출(source=ai - 보고서에 따로 나옴), 시나리오 진행 기록(harness의 scenario.*).
/// </summary>
public sealed class RunTrace
{

    private readonly Timeline _timeline;
    private readonly LogStore _logs;

    public RunTrace(Timeline timeline, LogStore logs)
    {
        _timeline = timeline;
        _logs = logs;
    }

    /// <summary>구간의 전체 기록(이벤트 + 로그, 노드 경계 줄은 뺌).</summary>
    public IReadOnlyList<TraceLine> Build(HarnessRun run, long afterSeq, long toSeq, DateTimeOffset from, DateTimeOffset to)
    {
        var events = _timeline.Range(afterSeq, toSeq).Select(FromEvent);
        var logs = _logs.ReadRange(run.LogsDir, from, to)
            .Where(e => e.Category != HarnessConstants.NodeMarker.Category)
            .Select(e => FromLog(e, e.Server));
        return events.Concat(logs).OrderBy(l => l.Time).ToList();
    }

    /// <summary>요약에 남길 줄: 이벤트(AI 호출, 시나리오 진행 기록 제외) + 경고 이상 로그.</summary>
    public static bool IsSummaryLine(TraceLine line) =>
        line.IsLog
            ? line.IsWarningOrAbove
            : line.Source != HarnessConstants.Sources.Ai && !(line.Source == HarnessConstants.Sources.Harness && line.Text.StartsWith(HarnessConstants.HarnessEvents.ScenarioPrefix, StringComparison.Ordinal));

    /// <summary>구간 기록을 실행 폴더의 trace/fileName에 쓰고 요약을 돌려준다. 파일을 못 쓰면 File=null(요약은 그대로).</summary>
    public TraceExcerpt Excerpt(HarnessRun run, string fileName, string header, long afterSeq, long toSeq, DateTimeOffset from, DateTimeOffset to)
    {
        var lines = Build(run, afterSeq, toSeq, from, to);

        string? relative = null;
        try
        {
            var dir = Path.Combine(run.Dir, HarnessConstants.RunFiles.TraceDir);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, fileName);
            var sb = new StringBuilder();
            sb.AppendLine(header);
            sb.AppendLine($"# 구간: 이벤트 seq {afterSeq + 1}~{toSeq}, 로그 {from.ToLocalTime():HH:mm:ss.fff}~{to.ToLocalTime():HH:mm:ss.fff}");
            sb.AppendLine($"# 이벤트 {lines.Count(l => !l.IsLog)}개 · 로그 {lines.Count(l => l.IsLog)}줄 · 원본: {HarnessConstants.RunFiles.Events}, {HarnessConstants.RunFiles.LogsDir}/");
            sb.AppendLine();
            foreach (var line in lines) sb.AppendLine(line.Render());
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            relative = $"{HarnessConstants.RunFiles.TraceDir}/{fileName}";
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        var summary = lines.Where(IsSummaryLine).ToList();
        return new TraceExcerpt(
            relative,
            lines.Count(l => !l.IsLog),
            lines.Count(l => l.IsLog),
            lines.Count(l => l.IsWarningOrAbove),
            summary.Skip(Math.Max(0, summary.Count - HarnessConstants.Limits.TraceSummaryMaxLines)).ToList(),
            summary.Count);
    }

    private static TraceLine FromEvent(TimelineEvent e)
    {
        var text = new StringBuilder(e.Name);
        if (!string.IsNullOrEmpty(e.PlayerId)) text.Append(" player=").Append(e.PlayerId);
        if (!string.IsNullOrEmpty(e.Detail)) text.Append(' ').Append(e.Detail);
        else if (e.Data is { } data && data.ValueKind != System.Text.Json.JsonValueKind.Null) text.Append(' ').Append(data.GetRawText());

        return new TraceLine(e.TimestampUtc, "event", e.Source, "event", HarnessJson.Truncate(text.ToString(), HarnessConstants.Limits.TraceEventTextLength), e.Seq);
    }

    private static TraceLine FromLog(LogEntry e, string source)
    {
        var text = (string.IsNullOrEmpty(e.Category) ? "" : $"[{e.Category}] ") + e.Message +
                   (string.IsNullOrEmpty(e.Exception) ? "" : " | " + e.Exception);
        return new TraceLine(e.Time ?? DateTimeOffset.MinValue, "log", source, e.Level, text, null);
    }
}
