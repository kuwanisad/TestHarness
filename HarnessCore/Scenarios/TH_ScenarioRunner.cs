using System.Text;

namespace TestHarness.Core.Scenarios;

/// <summary>
/// 시나리오 진행기 - 한 번에 하나의 시나리오를 진행한다. 모든 상호작용은 두 형태 중 하나다:
///   (가) 하네스가 질문 → AI가 대답 → 하네스가 기록   (목표 제시 → objective_begin / 결과 요구 → objective_report)
///   (나) AI가 출력 → 하네스가 기록                    (scenario_start, scenario_abort, 그 외 모든 도구 호출 - ToolCallLog)
///
/// 진행 상태(스테이트머신):
///   Idle ─start→ Offering ─begin→ InProgress ─report→ Offering(다음 목표) … → Offering(정리) ─begin→ InProgress(정리)
///   ─report→ Finished.   중단(abort)이나 Stop 정책의 실패는 곧바로 Offering(정리)로.
/// 상태에 맞지 않는 시나리오 도구 호출은 거부하고(기록함) 지금 기대하는 행동(expect)을 알려 준다.
///
/// 이 클래스는 시나리오 도구의 "순서"만 통제한다. 기능 도구(agent_command, logs_query ...)는 언제든 쓸 수 있고,
/// ToolCallLog가 그 호출을 현재 목표에 묶어 남긴다(CurrentObjectiveId).
///
/// 판정: 목표에 judge가 있으면 하네스가 이 목표 구간의 기록으로 판정하고, AI의 보고와 다르면 "불일치"로 남긴다.
/// judge가 없으면 AI의 보고를 결과로 쓰되 Pass에는 근거 이벤트 번호가 필수다. 근거 번호는 실제로 있고 이 목표
/// 구간(begin 뒤) 안에 있어야 한다. 보고서(scenario-*-report.md)는 AI가 아니라 하네스가 이 기록으로 쓴다.
/// </summary>
public sealed class TH_ScenarioRunner
{
    private enum Phase { Idle, Offering, InProgress, Finished }

    private readonly Timeline _timeline;
    private readonly RunManager _runs;
    private readonly ToolCallLog _calls;
    private readonly object _lock = new();

    private Phase _phase = Phase.Idle;
    private RunState? _state;

    public TH_ScenarioRunner(Timeline timeline, RunManager runs, ToolCallLog calls)
    {
        _timeline = timeline;
        _runs = runs;
        _calls = calls;
        _calls.CurrentObjective = () => CurrentObjectiveId;
    }

    public TH_ScenarioCatalog Catalog { get; } = new();

    /// <summary>시나리오가 진행 중인지(시작 ~ 정리 목표 보고 전).</summary>
    public bool IsActive
    {
        get { lock (_lock) return _phase is Phase.Offering or Phase.InProgress; }
    }

    /// <summary>지금 진행 중인 목표 ID(목표를 고른 뒤 ~ 보고 전). 도구 호출 기록의 태그.</summary>
    public string? CurrentObjectiveId
    {
        get { lock (_lock) return _phase == Phase.InProgress ? _state?.Current?.Objective.Id : null; }
    }

    // ================================ 도구가 부르는 것 ================================

    public object List() => new
    {
        scenarios = Catalog.List.Select(s => new { name = s.Name, title = s.Title }),
        active = Status(),
    };

    /// <summary>(나) AI가 시작을 요청 → 기록하고 첫 질문(목표 제시)을 낸다.</summary>
    public object Start(string name)
    {
        lock (_lock)
        {
            if (_phase is Phase.Offering or Phase.InProgress)
                Reject("scenario_start", $"시나리오 '{_state!.Definition.Name}'가 진행 중", "진행 중인 시나리오를 끝내거나(objective_report) 중단(scenario_abort)한 뒤 시작할 것");

            var definition = Catalog.TryCreate(name)
                             ?? throw new HarnessException($"모르는 시나리오 '{name}'",
                                 $"있는 시나리오: {string.Join(", ", Catalog.List.Select(s => s.Name).DefaultIfEmpty("없음"))} (scenario_list)");

            var run = _runs.EnsureCurrent();
            _state = new RunState(definition, run, DateTimeOffset.Now);
            _timeline.RecordHarness("scenario.start", $"시나리오 시작: {definition.Name}", new { scenario = definition.Name, runId = run.Id });

            Offer(new[] { definition.Start }, guide: null);
            Save();

            return new
            {
                scenario = definition.Name,
                title = definition.Title,
                runId = run.Id,
                preconditions = definition.Preconditions,
                cautions = definition.Cautions,
                rules = Rules,
                offer = OfferView(),
                expect = Expect(),
            };
        }
    }

    /// <summary>(가) 목표 제시에 대한 AI의 대답(고른 목표) → 기록.</summary>
    public object Begin(string objectiveId, string? reason)
    {
        lock (_lock)
        {
            if (_phase != Phase.Offering) Reject("objective_begin", PhaseError(), null);

            var state = _state!;
            var chosen = state.Offered.FirstOrDefault(o => string.Equals(o.Id, objectiveId, StringComparison.OrdinalIgnoreCase));
            if (chosen == null) Reject("objective_begin", $"'{objectiveId}'는 지금 제시된 목표가 아님", null);
            if (state.Offered.Count > 1 && string.IsNullOrWhiteSpace(reason))
                Reject("objective_begin", "후보가 여럿인 선택 분기 - 고른 이유(reason)가 필요함", $"고르는 기준: {state.OfferGuide}");

            var sinceSeq = _timeline.LastSeq;
            var step = new StepRecord(chosen!, sinceSeq, DateTimeOffset.Now,
                state.Offered.Count > 1 ? state.Offered.Select(o => o.Id).ToArray() : null, reason);
            state.Steps.Add(step);
            state.Current = step;
            state.Offered = Array.Empty<TH_Objective>();
            state.OfferGuide = null;
            _phase = Phase.InProgress;

            _timeline.RecordHarness("scenario.objective_begin", $"목표 시작: {chosen!.Id}",
                new { scenario = state.Definition.Name, objective = chosen.Id, sinceSeq, reason });
            Save();

            return new
            {
                objective = ObjectiveView(chosen),
                sinceSeq,
                expect = Expect(),
            };
        }
    }

    /// <summary>(가) 결과 요구에 대한 AI의 대답(보고) → 근거 검사 → 판정 → 기록 → 다음 질문.</summary>
    public object Report(string objectiveId, TH_Result result, IReadOnlyList<long>? evidenceSeqs, string? note)
    {
        lock (_lock)
        {
            if (_phase != Phase.InProgress) Reject("objective_report", PhaseError(), null);

            var state = _state!;
            var step = state.Current!;
            if (!string.Equals(step.Objective.Id, objectiveId, StringComparison.OrdinalIgnoreCase))
                Reject("objective_report", $"'{objectiveId}'는 진행 중인 목표가 아님(진행 중: {step.Objective.Id})", null);

            var evidence = (evidenceSeqs ?? Array.Empty<long>()).Distinct().ToList();
            var lastSeq = _timeline.LastSeq;
            var bad = evidence.Where(seq => seq <= step.SinceSeq || seq > lastSeq || _timeline.TryGet(seq) == null).ToList();
            if (bad.Count > 0)
                Reject("objective_report", $"근거 이벤트 번호가 이 목표 구간에 없음: {string.Join(", ", bad)}",
                    $"이 목표 구간은 seq {step.SinceSeq + 1}~{lastSeq} - events_query(sinceSeq={step.SinceSeq})로 확인할 것");
            if (step.Objective.Judge == null && result == TH_Result.Pass && evidence.Count == 0)
                Reject("objective_report", "이 목표는 AI 판정 - Pass에는 근거 이벤트 번호(evidenceSeqs)가 필요함",
                    "판단할 근거가 없으면 result=Unverified와 note로 사유를 적을 것");

            var report = new TH_AiReport(result, evidence, note);
            var context = new TH_JudgeContext(step.Objective.Id, step.SinceSeq,
                _timeline.Range(step.SinceSeq, lastSeq), _calls.Since(step.SinceSeq).Where(c => c.Seq <= lastSeq).ToList(), report);

            TH_Verdict verdict;
            string judgedBy;
            if (step.Objective.Judge is { } judge)
            {
                judgedBy = "harness";
                try
                {
                    verdict = judge(context);
                }
                catch (Exception ex)
                {
                    verdict = TH_Verdict.Unverified($"하네스 판정 코드 오류 {ex.GetType().Name}: {ex.Message}");
                    judgedBy = "harness(error)";
                }
            }
            else
            {
                judgedBy = "ai";
                verdict = new TH_Verdict(result, string.IsNullOrWhiteSpace(note) ? "AI 판정" : $"AI 판정: {note}", evidence);
            }

            step.End(report, verdict, judgedBy, lastSeq, DateTimeOffset.Now, context.Calls);
            _timeline.RecordHarness("scenario.objective_end", $"목표 판정: {step.Objective.Id} {verdict.Result}",
                new { scenario = state.Definition.Name, objective = step.Objective.Id, verdict = verdict.Result, judgedBy, ai = result, mismatch = step.Mismatch });

            var recorded = StepView(step);

            if (step.Objective.IsCleanup) return Finish("정리 목표 완료", recorded);

            string? stopped = null;
            IReadOnlyList<TH_Objective> next;
            if (!verdict.Passed && step.Objective.OnFail == TH_FailPolicy.Stop)
            {
                stopped = $"{step.Objective.Id}가 {verdict.Result} - 뒤 목표가 이 결과에 의존해서 멈춤(정리 목표만 남음)";
                state.StoppedBy = step.Objective.Id;
                next = Array.Empty<TH_Objective>();
            }
            else
            {
                try
                {
                    next = step.Objective.NextSelector?.Invoke(context, verdict) ?? Array.Empty<TH_Objective>();
                }
                catch (Exception ex)
                {
                    stopped = $"다음 목표를 정하는 코드 오류 {ex.GetType().Name}: {ex.Message} - 하네스 버그, 사람에게 알릴 것";
                    state.StoppedBy = step.Objective.Id;
                    next = Array.Empty<TH_Objective>();
                }
            }

            state.Current = null;
            if (next.Count == 0) Offer(new[] { state.Definition.Cleanup }, guide: null);
            else Offer(next, next.Count > 1 ? step.Objective.ChoiceGuide : null);
            Save();

            return new
            {
                recorded,
                stopped,
                offer = OfferView(),
                expect = Expect(),
            };
        }
    }

    /// <summary>(나) AI가 진행 불가를 보고 → 기록하고 정리 목표만 낸다(정리 중이면 바로 끝낸다).</summary>
    public object Abort(string reason, string situation)
    {
        lock (_lock)
        {
            if (_phase is not (Phase.Offering or Phase.InProgress)) Reject("scenario_abort", PhaseError(), null);

            var state = _state!;
            var at = state.Current?.Objective.Id ?? "(목표 선택 전)";
            state.Aborts.Add(new AbortRecord(reason, situation, at, _timeline.LastSeq, DateTimeOffset.Now));
            _timeline.RecordHarness("scenario.abort", $"시나리오 중단: {reason}", new { scenario = state.Definition.Name, at, reason, situation });

            var inCleanup = state.Current?.Objective.IsCleanup == true || state.Offered.Any(o => o.IsCleanup);
            if (state.Current is { } current)
            {
                current.Abandon(_timeline.LastSeq, DateTimeOffset.Now, _calls.Since(current.SinceSeq));
                state.Current = null;
            }

            if (inCleanup) return Finish("정리 중 중단", null);

            Offer(new[] { state.Definition.Cleanup }, guide: null);
            Save();
            return new
            {
                recorded = new { abortedAt = at, reason },
                offer = OfferView(),
                expect = Expect(),
            };
        }
    }

    /// <summary>harness_status/scenario_list용 - 진행 중이면 상태, 아니면 마지막 결과 요약(없으면 null).</summary>
    public object? Status()
    {
        lock (_lock)
        {
            if (_state == null) return null;
            return new
            {
                scenario = _state.Definition.Name,
                phase = _phase.ToString(),
                current = _state.Current?.Objective.Id,
                offered = _state.Offered.Select(o => o.Id).ToArray(),
                done = _state.Steps.Count(s => s.Verdict != null),
                expect = Expect(),
                report = _state.ReportPath,
            };
        }
    }

    // ================================ 내부 ================================

    private static readonly string[] Rules =
    {
        "목표는 하네스가 제시한 것만 objective_begin으로 고른다. 후보가 여럿이면 reason에 고른 이유를 쓴다.",
        "objective_begin이 돌려준 sinceSeq 뒤의 이벤트만 그 목표의 근거가 된다.",
        "수행 중에는 어떤 도구든 써도 된다(조사 포함) - 모든 호출은 이 목표에 묶여 기록된다.",
        "결과는 objective_report로 보고한다. 판단할 수 없으면 Unverified + note. 통과 기준을 바꾸거나 목표를 지어내지 않는다.",
        "진행할 수 없으면 scenario_abort(reason, situation). 그 뒤 정리 목표가 나온다.",
        "하네스 밖에서 한 일(코드 수정, 파일 읽기)은 다음 보고의 note에 적는다.",
    };

    private void Offer(IReadOnlyList<TH_Objective> objectives, string? guide)
    {
        var state = _state!;
        state.Offered = objectives;
        state.OfferGuide = guide;
        _phase = Phase.Offering;
        _timeline.RecordHarness("scenario.offer", $"목표 제시: {string.Join(", ", objectives.Select(o => o.Id))}",
            new { scenario = state.Definition.Name, choices = objectives.Select(o => o.Id).ToArray(), guide });
    }

    private object? Expect()
    {
        if (_state == null) return null;
        return _phase switch
        {
            Phase.Offering => new
            {
                next = "objective_begin",
                choices = _state.Offered.Select(o => o.Id).ToArray(),
                chooseBy = _state.Offered.Count > 1 ? _state.OfferGuide : null,
                reasonRequired = _state.Offered.Count > 1,
                alsoAllowed = new[] { "scenario_abort" },
            },
            Phase.InProgress => new
            {
                next = "objective_report",
                objective = _state.Current!.Objective.Id,
                alsoAllowed = new[] { "scenario_abort" },
            },
            _ => null,
        };
    }

    private object OfferView() => new
    {
        guide = _state!.Offered.Count > 1 ? _state.OfferGuide : null,
        objectives = _state.Offered.Select(ObjectiveView).ToArray(),
    };

    private static object ObjectiveView(TH_Objective o) => new
    {
        id = o.Id,
        goal = o.Goal,
        passCriteria = o.PassCriteria,
        judgedBy = o.Judge != null ? "harness" : "ai(Pass에는 근거 seq 필수)",
        hints = o.Hints.Count > 0 ? o.Hints : null,
        cleanup = o.IsCleanup ? true : (bool?)null,
    };

    private static object StepView(StepRecord s) => new
    {
        objective = s.Objective.Id,
        verdict = s.Verdict!.Result,
        reason = s.Verdict.Reason,
        evidence = s.Verdict.EvidenceSeqs,
        judgedBy = s.JudgedBy,
        aiReported = s.AiReport!.Result,
        mismatch = s.Mismatch ? true : (bool?)null,
    };

    private string PhaseError() => _phase switch
    {
        Phase.Idle => "진행 중인 시나리오가 없음",
        Phase.Finished => $"시나리오 '{_state?.Definition.Name}'는 이미 끝났음",
        Phase.Offering => "지금은 목표를 고를 차례(objective_begin)",
        Phase.InProgress => $"지금은 목표 {_state?.Current?.Objective.Id}의 결과를 보고할 차례(objective_report)",
        _ => "",
    };

    /// <summary>순서 위반 - 기록하고 거부한다(지금 기대하는 행동을 힌트로).</summary>
    private void Reject(string tool, string message, string? extraHint)
    {
        var expect = Expect();
        _timeline.RecordHarness("scenario.rejected", $"{tool} 거부: {message}", new { tool, message, expect });
        _state?.Rejections.Add($"{DateTimeOffset.Now:HH:mm:ss} {tool}: {message}");
        Save();

        var hint = expect != null
            ? $"지금 기대하는 행동: {HarnessJson.Serialize(expect)}"
            : "scenario_list → scenario_start로 시작할 것";
        throw new HarnessException(message, extraHint != null ? $"{extraHint} / {hint}" : hint);
    }

    private object Finish(string how, object? recorded)
    {
        var state = _state!;
        state.Offered = Array.Empty<TH_Objective>();
        state.Current = null;
        state.FinishedAt = DateTimeOffset.Now;
        _phase = Phase.Finished;

        state.ReportPath = Path.Combine(state.Run.Dir, $"{state.FilePrefix}-report.md");
        try
        {
            File.WriteAllText(state.ReportPath, BuildReport(state), new UTF8Encoding(false));
        }
        catch (IOException ex)
        {
            state.ReportPath = $"(보고서 쓰기 실패: {ex.Message})";
        }

        Save();
        _timeline.RecordHarness("scenario.end", $"시나리오 종료: {state.Definition.Name} ({how})",
            new { scenario = state.Definition.Name, report = state.ReportPath });

        return new
        {
            recorded,
            finished = true,
            how,
            summary = Summary(state),
            report = state.ReportPath,
            message = "남은 목표 없음 - 시나리오 끝. 보고서는 하네스가 작성했다(사람에게 경로를 알려 줄 것).",
        };
    }

    private static object Summary(RunState state)
    {
        var ended = state.Steps.Where(s => s.Verdict != null).ToList();
        return new
        {
            outcome = state.Aborts.Count > 0 ? "aborted" : state.StoppedBy != null ? "stopped" : "completed",
            pass = ended.Count(s => s.Verdict!.Result == TH_Result.Pass),
            fail = ended.Count(s => s.Verdict!.Result == TH_Result.Fail),
            unverified = ended.Count(s => s.Verdict!.Result == TH_Result.Unverified),
            abandoned = state.Steps.Count(s => s.Abandoned),
            mismatch = ended.Count(s => s.Mismatch),
            rejections = state.Rejections.Count,
        };
    }

    private void Save()
    {
        if (_state == null) return;
        var state = _state;
        var file = Path.Combine(state.Run.Dir, $"{state.FilePrefix}-state.json");
        try
        {
            File.WriteAllText(file, HarnessJson.Serialize(new
            {
                scenario = state.Definition.Name,
                phase = _phase.ToString(),
                startedAt = state.StartedAt,
                finishedAt = state.FinishedAt,
                offered = state.Offered.Select(o => o.Id),
                current = state.Current?.Objective.Id,
                steps = state.Steps.Select(s => new
                {
                    objective = s.Objective.Id,
                    s.SinceSeq,
                    s.EndSeq,
                    candidates = s.Candidates,
                    s.ChoiceReason,
                    verdict = s.Verdict,
                    s.JudgedBy,
                    ai = s.AiReport,
                    s.Mismatch,
                    s.Abandoned,
                    calls = s.Calls.Count,
                }),
                stoppedBy = state.StoppedBy,
                aborts = state.Aborts,
                rejections = state.Rejections,
                report = state.ReportPath,
            }), new UTF8Encoding(false));
        }
        catch (IOException)
        {
            // 상태 파일 실패가 진행을 막으면 안 된다(타임라인에 같은 내용이 scenario.* 이벤트로 남아 있음).
        }
    }

    // ================================ 보고서 ================================

    private static string BuildReport(RunState state)
    {
        var sb = new StringBuilder();
        var d = state.Definition;
        var summary = state.Steps.Where(s => s.Verdict != null).ToList();
        string outcome = state.Aborts.Count > 0 ? $"중단 - {state.Aborts[0].Reason}"
            : state.StoppedBy != null ? $"실패로 멈춤 ({state.StoppedBy})"
            : "완료";

        sb.AppendLine($"# 시나리오 보고서: {d.Title} (`{d.Name}`)");
        sb.AppendLine();
        sb.AppendLine("> 이 보고서는 하네스가 기록(타임라인, AI 도구 호출)으로 작성했다. AI의 보고는 \"AI 보고\" 열에만 있다.");
        sb.AppendLine();
        sb.AppendLine($"- 실행: `{state.Run.Id}`");
        sb.AppendLine($"- 시간: {state.StartedAt:yyyy-MM-dd HH:mm:ss} ~ {state.FinishedAt:HH:mm:ss} ({Duration(state.StartedAt, state.FinishedAt)})");
        sb.AppendLine($"- 결과: **{outcome}**");
        sb.AppendLine($"- 집계: 통과 {summary.Count(s => s.Verdict!.Result == TH_Result.Pass)} / 실패 {summary.Count(s => s.Verdict!.Result == TH_Result.Fail)} / " +
                      $"미확인 {summary.Count(s => s.Verdict!.Result == TH_Result.Unverified)} / 중단 {state.Steps.Count(s => s.Abandoned)} · " +
                      $"판정 불일치 {summary.Count(s => s.Mismatch)} · 순서 위반 {state.Rejections.Count}");
        sb.AppendLine();

        sb.AppendLine("## 진행 경로");
        sb.AppendLine();
        sb.AppendLine("| # | 목표 | 판정 | 판정자 | AI 보고 | 일치 | 근거 seq | 도구 호출 | 소요 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        var index = 1;
        foreach (var s in state.Steps)
        {
            var verdict = s.Abandoned ? "중단" : s.Verdict?.Result.ToString() ?? "-";
            var ai = s.AiReport?.Result.ToString() ?? "-";
            var match = s.Verdict == null || s.JudgedBy == "ai" ? "-" : s.Mismatch ? "**불일치**" : "일치";
            var evidence = s.Verdict is { EvidenceSeqs.Count: > 0 } v ? string.Join(", ", v.EvidenceSeqs) : "-";
            sb.AppendLine($"| {index++} | {s.Objective.Id}. {Cell(s.Objective.Goal)} | {verdict} | {s.JudgedBy ?? "-"} | {ai} | {match} | {evidence} | {s.Calls.Count} | {Duration(s.BeganAt, s.EndedAt)} |");
        }
        sb.AppendLine();

        sb.AppendLine("## 목표별 상세");
        foreach (var s in state.Steps)
        {
            sb.AppendLine();
            sb.AppendLine($"### {s.Objective.Id}. {s.Objective.Goal}");
            sb.AppendLine($"- 통과 기준: {s.Objective.PassCriteria}");
            sb.AppendLine($"- 구간: seq {s.SinceSeq + 1} ~ {(s.EndSeq?.ToString() ?? "?")}");
            if (s.Candidates != null) sb.AppendLine($"- 선택 분기: 후보 {string.Join(", ", s.Candidates)} 중 선택 - 이유: {s.ChoiceReason}");
            if (s.Verdict != null)
            {
                sb.AppendLine($"- 판정({s.JudgedBy}): **{s.Verdict.Result}** - {s.Verdict.Reason}" +
                              (s.Verdict.EvidenceSeqs.Count > 0 ? $" (근거 {string.Join(", ", s.Verdict.EvidenceSeqs)})" : ""));
            }
            if (s.AiReport != null)
            {
                sb.AppendLine($"- AI 보고: {s.AiReport.Result}" +
                              (s.AiReport.EvidenceSeqs.Count > 0 ? $", 근거 {string.Join(", ", s.AiReport.EvidenceSeqs)}" : "") +
                              (string.IsNullOrWhiteSpace(s.AiReport.Note) ? "" : $" - {s.AiReport.Note}"));
            }
            if (s.Abandoned) sb.AppendLine("- 중단됨(보고 전에 scenario_abort)");

            if (s.Calls.Count > 0)
            {
                sb.AppendLine($"- AI 도구 호출 {s.Calls.Count}회:");
                foreach (var call in s.Calls.Take(40))
                {
                    sb.AppendLine($"  - seq {call.Seq} `{call.Tool}{(call.AgentCommand != null ? " " + call.AgentCommand : "")}`" +
                                  $"{(call.IsError ? " **error**" : "")} {Cell(Brief(call))}");
                }
                if (s.Calls.Count > 40) sb.AppendLine($"  - … (+{s.Calls.Count - 40}회, mcp-calls.jsonl 참고)");
            }
        }
        sb.AppendLine();

        if (state.Aborts.Count > 0)
        {
            sb.AppendLine("## 중단");
            foreach (var abort in state.Aborts)
            {
                sb.AppendLine();
                sb.AppendLine($"- 위치: {abort.At} (seq {abort.Seq}, {abort.When:HH:mm:ss})");
                sb.AppendLine($"- 사유: {abort.Reason}");
                sb.AppendLine($"- 현황(AI 보고): {abort.Situation}");
            }
            sb.AppendLine();
        }

        var visited = state.Steps.Select(s => s.Objective.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var notRun = d.Objectives.Where(o => !visited.Contains(o.Id)).ToList();
        if (notRun.Count > 0)
        {
            var why = state.Aborts.Count > 0 ? "중단" : state.StoppedBy != null ? $"{state.StoppedBy} 실패로 멈춤" : "다른 경로";
            sb.AppendLine("## 실행되지 않은 목표");
            sb.AppendLine();
            sb.AppendLine("| 목표 | 이유 |");
            sb.AppendLine("|---|---|");
            foreach (var o in notRun) sb.AppendLine($"| {o.Id}. {Cell(o.Goal)} | {why} |");
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
        sb.AppendLine("- 원본 기록: `events.jsonl`(타임라인, seq), `mcp-calls.jsonl`(AI 도구 호출 전문), " +
                      $"`{state.FilePrefix}-state.json`(진행 상태)");
        return sb.ToString();
    }

    private static string Brief(ToolCallRecord call)
    {
        if (call.Arguments is not { } args) return "";
        return HarnessJson.Truncate(args.GetRawText(), 100);
    }

    private static string Cell(string text) => text.Replace("|", "\\|").Replace("\n", " ");

    private static string Duration(DateTimeOffset from, DateTimeOffset? to)
    {
        if (to is not { } end) return "-";
        var span = end - from;
        return span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}분 {span.Seconds}초" : $"{span.TotalSeconds:0.0}초";
    }

    // ================================ 상태 ================================

    private sealed class RunState
    {
        public RunState(TH_ScenarioDefinition definition, HarnessRun run, DateTimeOffset startedAt)
        {
            Definition = definition;
            Run = run;
            StartedAt = startedAt;
            FilePrefix = $"scenario-{definition.Name}-{startedAt:HHmmss}";
        }

        public TH_ScenarioDefinition Definition { get; }
        public HarnessRun Run { get; }
        public DateTimeOffset StartedAt { get; }
        public string FilePrefix { get; }
        public DateTimeOffset? FinishedAt { get; set; }
        public IReadOnlyList<TH_Objective> Offered { get; set; } = Array.Empty<TH_Objective>();
        public string? OfferGuide { get; set; }
        public StepRecord? Current { get; set; }
        public List<StepRecord> Steps { get; } = new();
        public string? StoppedBy { get; set; }
        public List<AbortRecord> Aborts { get; } = new();
        public List<string> Rejections { get; } = new();
        public string? ReportPath { get; set; }
    }

    private sealed class StepRecord
    {
        public StepRecord(TH_Objective objective, long sinceSeq, DateTimeOffset beganAt, string[]? candidates, string? choiceReason)
        {
            Objective = objective;
            SinceSeq = sinceSeq;
            BeganAt = beganAt;
            Candidates = candidates;
            ChoiceReason = choiceReason;
        }

        public TH_Objective Objective { get; }
        public long SinceSeq { get; }
        public DateTimeOffset BeganAt { get; }
        public string[]? Candidates { get; }
        public string? ChoiceReason { get; }
        public long? EndSeq { get; private set; }
        public DateTimeOffset? EndedAt { get; private set; }
        public TH_AiReport? AiReport { get; private set; }
        public TH_Verdict? Verdict { get; private set; }
        public string? JudgedBy { get; private set; }
        public bool Mismatch { get; private set; }
        public bool Abandoned { get; private set; }
        public IReadOnlyList<ToolCallRecord> Calls { get; private set; } = Array.Empty<ToolCallRecord>();

        public void End(TH_AiReport report, TH_Verdict verdict, string judgedBy, long endSeq, DateTimeOffset endedAt, IReadOnlyList<ToolCallRecord> calls)
        {
            AiReport = report;
            Verdict = verdict;
            JudgedBy = judgedBy;
            Mismatch = judgedBy == "harness" && report.Result != verdict.Result;
            EndSeq = endSeq;
            EndedAt = endedAt;
            Calls = calls;
        }

        public void Abandon(long endSeq, DateTimeOffset endedAt, IReadOnlyList<ToolCallRecord> calls)
        {
            Abandoned = true;
            EndSeq = endSeq;
            EndedAt = endedAt;
            Calls = calls;
        }
    }

    private sealed record AbortRecord(string Reason, string Situation, string At, long Seq, DateTimeOffset When);
}
