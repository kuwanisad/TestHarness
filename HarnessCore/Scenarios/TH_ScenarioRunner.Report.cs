using System.Text;

namespace TestHarness.Core.Scenarios;

/// <summary>진행기의 기록 - 상태 파일, 보고서(구간 기록 포함). TH_ScenarioRunner.cs의 나머지.</summary>
public sealed partial class TH_ScenarioRunner
{
    // ================================ 기록 ================================

    private void Save()
    {
        if (_state is not { } state) return;
        try
        {
            var path = Path.Combine(state.Run.Dir, $"{state.FilePrefix}-state.json");
            File.WriteAllText(path, HarnessJson.Serialize(new
            {
                scenario = state.Definition.Name,
                mode = state.Mode.ToString(),
                phase = _phase.ToString(),
                startedAt = state.StartedAt,
                finishedAt = state.FinishedAt,
                state = state.CurrentState.View(),
                environment = state.Environment,
                environmentChanges = state.EnvChanges,
                preparations = state.Preparations.Select(PreparationView),
                visits = state.Visits.Select(VisitView),
                cleanup = state.Cleanup == null ? null : VisitView(state.Cleanup),
                finishReason = state.FinishReason,
                rejections = state.Rejections,
                values = state.Values.Snapshot(),
                report = state.ReportPath,
            }), new UTF8Encoding(false));
        }
        catch (IOException)
        {
            // 상태 파일 실패가 진행을 막으면 안 된다.
        }
    }

    /// <summary>최상위 방문(과 정리)마다 그 구간의 이벤트 + 로그를 trace/ 파일로 남기고 요약을 모은다(RunTrace).</summary>
    private Dictionary<VisitRecord, TraceExcerpt> BuildTraces(RunState state)
    {
        var traces = new Dictionary<VisitRecord, TraceExcerpt>();
        var visits = state.Visits.Concat(state.Cleanup != null ? new[] { state.Cleanup } : Array.Empty<VisitRecord>()).ToList();
        for (var i = 0; i < visits.Count; i++)
        {
            var visit = visits[i];
            if (visit.EndedAt is not { } ended) continue;
            var fileName = $"{state.FilePrefix}-{i + 1:D2}-{SafeFilePart(visit.Node.Id)}.txt";
            var header = $"# 시나리오 {state.Definition.Name} · {i + 1}. {visit.Path} (환경 #{visit.Environment}) - {visit.Node.Goal}";
            traces[visit] = _trace.Excerpt(state.Run, fileName, header, visit.SinceSeq, visit.EndSeq, visit.BeganAt, ended);
        }
        return traces;
    }

    private static string SafeFilePart(string text) =>
        new(text.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());

    private static string BuildReport(RunState state, IReadOnlyDictionary<VisitRecord, TraceExcerpt> traces, IReadOnlyList<OverrideRecord> overrides)
    {
        var sb = new StringBuilder();
        var d = state.Definition;
        var allPassed = AllPassed(state);

        sb.AppendLine($"# 시나리오 보고서: {d.Title} (`{d.Name}`)");
        sb.AppendLine();
        sb.AppendLine("> 이 보고서는 하네스가 기록(타임라인, 도구 호출)으로 작성했다. 노드는 하네스가 실행하고 판정했다(AI 판정 노드 제외).");
        sb.AppendLine();
        sb.AppendLine($"- 실행: `{state.Run.Id}` · 모드: {state.Mode.ToString().ToLowerInvariant()}");
        sb.AppendLine($"- 시간: {state.StartedAt:yyyy-MM-dd HH:mm:ss} ~ {state.FinishedAt:HH:mm:ss} ({Duration(state.StartedAt, state.FinishedAt)})");
        sb.AppendLine($"- 종료: {state.FinishReason}");
        if (state.FinishHow == HarnessConstants.FinishHow.AiStop)
            sb.AppendLine("- ⚠ **일찍 끝냄** - 실행 중이던 노드는 멈췄고(Stopped, 판정 안 함) 그 뒤 노드는 돌지 않았다. 아래 결과는 실제로 돈 방문만의 것이다.");
        if (d.Targets.Count > 0) sb.AppendLine($"- 대상: {string.Join(", ", d.Targets.Select(t => $"{t.Name}({t.Kind})"))}");
        foreach (var p in state.Preparations)
        {
            sb.AppendLine(p.Error == null
                ? $"- 준비: {p.Target} - {p.Description} (seq {p.SinceSeq + 1} ~ {p.UntilSeq}){(p.Notes.Count > 0 ? " · " + string.Join(" · ", p.Notes) : "")}"
                : $"- 준비 **실패**: {p.Target} - {p.Description}: {p.Error} (seq {p.SinceSeq + 1} ~ {p.UntilSeq})");
        }
        sb.AppendLine($"- 결과: **{(allPassed ? "전부 통과" : "통과하지 못한 것 있음")}** - 방문 {state.Visits.Count} (통과 {state.Visits.Count(v => v.Verdict?.Passed == true)} / " +
                      $"실패 {state.Visits.Count(v => v.Verdict?.Result == TH_Result.Fail)} / 미확인 {state.Visits.Count(v => v.Verdict?.Result is TH_Result.Unverified or null)}" +
                      $"{(state.Visits.Any(v => v.Verdict?.Result == TH_Result.Stopped) ? $" / 중단 {state.Visits.Count(v => v.Verdict?.Result == TH_Result.Stopped)}" : "")}) · " +
                      $"정리 {state.Cleanup?.Verdict?.Result.ToString() ?? "-"} · 환경 {state.Environment}개 · 순서 위반 {state.Rejections.Count}");
        if (state.OutsideEvents.Count > 0)
            sb.AppendLine($"- ⚠ **노드 밖에서 일어난 일 {state.OutsideEvents.Count}건** - 노드 판정에는 안 들어갔다(결과가 통과여도 확인할 것). 아래 \"노드 밖에서 일어난 일\" 절");
        sb.AppendLine();

        sb.AppendLine("## 맥락");
        sb.AppendLine();
        foreach (var line in d.Context.Split('\n')) sb.AppendLine($"> {line}");
        sb.AppendLine();

        sb.AppendLine("## 방문 순서");
        sb.AppendLine();
        sb.AppendLine("| # | 노드 | 환경 | 판정 | 판정자 | 소요 | 고른 쪽 / 이유 |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        var index = 1;
        foreach (var v in state.Visits)
        {
            sb.AppendLine($"| {index++} | {v.Node.Id} | #{v.Environment} | {v.Verdict?.Result.ToString() ?? "-"} | {v.JudgedBy ?? "-"} | " +
                          $"{Duration(v.BeganAt, v.EndedAt)} | {v.Actor}{(string.IsNullOrWhiteSpace(v.Reason) ? "" : ": " + Cell(v.Reason))} |");
        }
        if (state.Cleanup is { } c)
            sb.AppendLine($"| 정리 | {c.Node.Id} | #{c.Environment} | {c.Verdict?.Result.ToString() ?? "-"} | {c.JudgedBy ?? "-"} | {Duration(c.BeganAt, c.EndedAt)} | 하네스 |");
        sb.AppendLine();

        if (state.EnvChanges.Count > 0)
        {
            sb.AppendLine("## 환경 변화(테스트 대상의 변화)");
            sb.AppendLine();
            sb.AppendLine("| 환경 | 원인 | seq | 시각 |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var change in state.EnvChanges)
                sb.AppendLine($"| #{change.Environment} | {Cell(change.Cause)} | {change.Seq?.ToString() ?? "-"} | {change.At:HH:mm:ss} |");
            sb.AppendLine();
        }

        sb.AppendLine("## 노드별 결과(마지막 환경 기준)");
        sb.AppendLine();
        sb.AppendLine($"| 노드 | 환경 #{state.Environment} 결과 | 이전 환경 결과 |");
        sb.AppendLine("|---|---|---|");
        foreach (var node in d.Nodes)
        {
            var current = state.Visits.LastOrDefault(v => v.Node == node && v.Environment == state.Environment);
            var previous = state.Visits.Where(v => v.Node == node && v.Environment < state.Environment)
                .Select(v => $"#{v.Environment} {v.Verdict?.Result.ToString() ?? "-"}").ToList();
            sb.AppendLine($"| {node.Id} | {current?.Verdict?.Result.ToString() ?? "안 함"} | {(previous.Count > 0 ? string.Join(", ", previous) : "-")} |");
        }
        sb.AppendLine();

        sb.AppendLine("## 방문 상세");
        index = 1;
        foreach (var v in state.Visits.Concat(state.Cleanup != null ? new[] { state.Cleanup } : Array.Empty<VisitRecord>()))
        {
            sb.AppendLine();
            sb.AppendLine($"### {(ReferenceEquals(v, state.Cleanup) ? "정리" : (index++).ToString())}. {v.Node.Id} - {v.Node.Goal}");
            sb.AppendLine($"- 통과 기준: {v.Node.PassCriteria}");
            sb.AppendLine($"- 환경 #{v.Environment} · 상태 {v.StateBefore.DiffTo(v.StateAfter)} · 구간 seq {v.SinceSeq + 1} ~ {v.EndSeq}");
            if (v.Verdict != null)
            {
                sb.AppendLine($"- 판정({v.JudgedBy}): **{v.Verdict.Result}** - {v.Verdict.Reason}" +
                              (v.Verdict.EvidenceSeqs.Count > 0 ? $" (근거 {string.Join(", ", v.Verdict.EvidenceSeqs)})" : ""));
            }
            if (v.AiReport != null) sb.AppendLine($"- AI 판정 보고: {v.AiReport.Result}, 근거 {string.Join(", ", v.AiReport.EvidenceSeqs)} - {v.AiReport.Note}");
            foreach (var child in v.Children) AppendChild(sb, child, 0);
            if (v.SkippedChildren.Count > 0) sb.AppendLine($"- 실행 안 한 자식: {string.Join(", ", v.SkippedChildren)}");
            foreach (var note in v.Notes) sb.AppendLine($"- 메모: {note}");

            if (traces.TryGetValue(v, out var trace))
            {
                sb.AppendLine($"- 구간 기록: {(trace.File != null ? $"`{trace.File}`" : "(파일 쓰기 실패)")} - 이벤트 {trace.Events}개 · 로그 {trace.Logs}줄" +
                              $"(경고 이상 {trace.WarningLogs}줄)");
                if (v.Verdict?.Passed != true && trace.SummaryTotal > 0)
                {
                    var cut = trace.SummaryTotal - trace.Summary.Count;
                    sb.AppendLine($"- 요약(이벤트 + 경고 이상 로그{(cut > 0 ? $", 마지막 {trace.Summary.Count}줄 - 앞 {cut}줄은 구간 기록 파일" : "")}):");
                    sb.AppendLine("  ```");
                    foreach (var line in trace.Summary) sb.AppendLine("  " + line.Render());
                    sb.AppendLine("  ```");
                }
            }
        }
        sb.AppendLine();

        if (overrides.Count > 0)
        {
            sb.AppendLine("## 지속 조작(테스트 조작 - 배속, 설정값 ...)");
            sb.AppendLine();
            sb.AppendLine("| id | 종류 | 대상[범위] | 값 | 건 쪽 | 켜짐(seq) | 걸린 횟수 | 끝 |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var o in overrides)
            {
                sb.AppendLine($"| {o.Id} | {o.Kind.Name} | {o.Agent}{(o.Scope.Length > 0 ? $"[{o.Scope}]" : "")} | {o.Before} → {o.After} | " +
                              $"{o.Actor}{(o.Owner != null ? $" ({Cell(o.Owner)})" : "")} | {o.AppliedAt:HH:mm:ss} (#{o.AppliedSeq}) | " +
                              $"{(o.Kind.HitEvent == null ? "-" : o.Hits == 0 ? "**0 - 한 번도 안 걸림**" : o.Hits.ToString())} | " +
                              $"{(o.EndReason == null ? (o.Unconfirmed != null ? $"**켜진 채(확인 대기: {Cell(o.Unconfirmed)})**" : "**켜진 채**") : $"{Cell(o.EndReason)} {o.EndedAt:HH:mm:ss}")}" +
                              $"{(o.EndReason != null && o.RevertError != null ? $" (그 전에 되돌리기 실패: {Cell(o.RevertError)})" : "")} |");
            }
            sb.AppendLine();
        }

        if (state.OutsideEvents.Count > 0)
        {
            sb.AppendLine("## 노드 밖에서 일어난 일(판정에 안 들어감)");
            sb.AppendLine();
            sb.AppendLine("| seq | 시각 | 출처 | 이벤트 | 무엇 | 데이터 |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var o in state.OutsideEvents)
                sb.AppendLine($"| {o.Seq} | {o.At.ToLocalTime():HH:mm:ss.fff} | {o.Source} | {o.Name} | {o.What} | {Cell(o.Detail)} |");
            sb.AppendLine();
        }

        if (state.Rejections.Count > 0)
        {
            sb.AppendLine("## 순서 위반(거부된 시나리오 도구 호출)");
            sb.AppendLine();
            foreach (var r in state.Rejections) sb.AppendLine($"- {r}");
            sb.AppendLine();
        }

        sb.AppendLine("## 참고");
        sb.AppendLine();
        sb.AppendLine("- 전제: " + (d.Preconditions.Count > 0 ? string.Join(" / ", d.Preconditions) : "-"));
        sb.AppendLine("- 주의: " + (d.Cautions.Count > 0 ? string.Join(" / ", d.Cautions) : "-"));
        sb.AppendLine("- 원본 기록: `events.jsonl`(타임라인, seq), `mcp-calls.jsonl`(도구 호출 전문 - actor: ai/runner), " +
                      $"`{state.FilePrefix}-state.json`(진행 상태), `logs/`(서버 + Unity 로그), `trace/{state.FilePrefix}-*.txt`(방문별 이벤트 + 로그)");
        return sb.ToString();
    }

    private static void AppendChild(StringBuilder sb, VisitRecord child, int depth)
    {
        var indent = new string(' ', depth * 2);
        sb.AppendLine($"{indent}- 자식 {child.Node.Id}: {child.Verdict?.Result.ToString() ?? "-"} - {Cell(child.Verdict?.Reason ?? "")}" +
                      (child.Verdict is { EvidenceSeqs.Count: > 0 } verdict ? $" (근거 {string.Join(", ", verdict.EvidenceSeqs)})" : ""));
        foreach (var note in child.Notes) sb.AppendLine($"{indent}  - 메모: {note}");
        foreach (var grandchild in child.Children) AppendChild(sb, grandchild, depth + 1);
        if (child.SkippedChildren.Count > 0) sb.AppendLine($"{indent}  - 실행 안 한 자식: {string.Join(", ", child.SkippedChildren)}");
    }

    private static string Cell(string text) => text.Replace("|", "\\|").Replace("\n", " ");

    private static string Duration(DateTimeOffset from, DateTimeOffset? to)
    {
        if (to is not { } end) return "-";
        var span = end - from;
        return span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}분 {span.Seconds}초" : $"{span.TotalSeconds:0.0}초";
    }
}
