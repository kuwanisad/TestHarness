using System.Text;

namespace TestHarness.Core.Scenarios;

/// <summary>누가 경로(다음 노드)를 고르나.</summary>
public enum TH_RunMode
{
    /// <summary>AI가 전체 그래프를 보고 node_run으로 고르고, 충분하면 scenario_finish.</summary>
    Ai,

    /// <summary>하네스가 시나리오의 AutoPlan대로 돌고 끝낸다(사람/AI 없는 회귀 테스트 - mode=auto, 명령줄 run).</summary>
    Auto,
}

/// <summary>
/// 시나리오(노드 그래프) 진행기 + 기록 + 보고서. 노드는 하네스가 실행하고(TH_Node.Auto, 도구는 ITH_ToolInvoker로) 판정한다.
///
/// 상호작용 규칙: (가) 하네스가 질문(전체 그래프, 지금 상태, 고를 수 있는 노드) → AI가 대답(node_run / scenario_finish) → 하네스가
/// 기록, (나) AI가 출력(조사용 도구 호출, AI 판정 노드의 node_judge) → 하네스가 기록. 순서(노드 실행 중 다른 노드, 시작할 수
/// 없는 상태의 노드, 판정 보고 전 다음 노드)는 하네스가 통제한다 - 어기면 거부 + expect.
///
/// 진행 단계: Idle → Ready(AI 차례) ⇄ Running(노드 실행 중) [⇄ AwaitingJudge(AI 판정 노드)] → Finishing(정리 노드) → Finished.
/// 끝나는 경우: AI 종료 선언(scenario_finish), AI 지시 없음(IdleTimeout - 노드 실행 중은 빼고), 자동 실행 경로 끝.
/// 어떻게 끝나든 정리 노드 → 보고서(하네스가 씀) → 알림음.
///
/// 환경 변화(TH_EnvironmentChanges): 노드와 노드 사이에 테스트 대상이 바뀌면 환경 번호가 올라가고, 그 전 방문 결과는 "이전 환경"이
/// 된다(현재 환경에서는 안 돈 것으로 보임 - 결과는 보고서에 남음). 노드 실행 중의 변화는 그 노드의 의도된 행동이라 세지 않는다.
/// 변화 알림은 다음 응답(environment.changes)에 실린다.
/// </summary>
public sealed class TH_ScenarioRunner : IDisposable
{
    private enum Phase { Idle, Ready, Running, AwaitingJudge, Finishing, Finished }

    private const string ActorAuto = "auto";
    private const string ActorHarness = "harness";

    private readonly Timeline _timeline;
    private readonly RunManager _runs;
    private readonly ToolCallLog _calls;
    private readonly RunTrace _trace;
    private readonly TH_BuildProbe? _probe;
    private readonly OverrideManager? _overrides;
    private readonly Action<string>? _logMarker;
    private readonly Func<string?>? _runEndBlocker;
    private readonly object _lock = new();
    private readonly Timer _idleTimer;

    private Phase _phase = Phase.Idle;
    private RunState? _state;

    /// <param name="logMarker">노드 경계 줄을 에이전트 로그 파일에 넣는 곳(AgentLogWriter.WriteMarker) - 없으면 넣지 않는다.</param>
    /// <param name="runEndBlocker">시나리오 끝에 실행을 닫으면 안 되는 사유(예: 서버가 떠 있음), 없으면 null - 없으면 늘 닫는다.</param>
    public TH_ScenarioRunner(Timeline timeline, RunManager runs, ToolCallLog calls, RunTrace trace, TH_BuildProbe? probe = null,
        OverrideManager? overrides = null, Action<string>? logMarker = null, Func<string?>? runEndBlocker = null)
    {
        _runEndBlocker = runEndBlocker;
        _overrides = overrides;
        _logMarker = logMarker;
        _timeline = timeline;
        _runs = runs;
        _calls = calls;
        _trace = trace;
        _probe = probe;
        _calls.CurrentObjective = () => CurrentNodePath;
        _timeline.Recorded += OnRecorded;
        _idleTimer = new Timer(_ => CheckIdle(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public TH_ScenarioCatalog Catalog { get; } = new();

    /// <summary>노드 실행이 도구를 부르는 통로 - 입구(HarnessDaemon)가 연결한다.</summary>
    public ITH_ToolInvoker? Tools { get; set; }

    /// <summary>시나리오가 진행 중인지(시작 ~ 정리 노드까지 끝나기 전).</summary>
    public bool IsActive
    {
        get { lock (_lock) return _phase is Phase.Ready or Phase.Running or Phase.AwaitingJudge or Phase.Finishing; }
    }

    /// <summary>실행 중인 노드 경로(없으면 null) - 도구 호출 기록의 objective.</summary>
    public string? CurrentNodePath
    {
        get { lock (_lock) return _phase is Phase.Running or Phase.Finishing ? _state?.RunningPath : null; }
    }

    public void Dispose()
    {
        _idleTimer.Dispose();
        _timeline.Recorded -= OnRecorded;
    }

    // ================================ 도구 쪽 입구 ================================

    public object List()
    {
        lock (_lock)
        {
            return new
            {
                scenarios = Catalog.List.Select(s => new { name = s.Name, title = s.Title }),
                active = Status(),
                hint = "scenario_start(name, mode)로 시작. mode=ai(기본 - AI가 노드를 고름) | auto(하네스가 정해진 경로로 끝까지)",
            };
        }
    }

    /// <summary>시나리오를 시작한다. Ai면 전체 그래프를 돌려주고 AI 차례, Auto면 정해진 경로를 끝까지 돌고 결과를 돌려준다.</summary>
    public async Task<object> StartAsync(string name, TH_RunMode mode, CancellationToken ct)
    {
        RunState state;
        lock (_lock)
        {
            if (_phase is Phase.Ready or Phase.Running or Phase.AwaitingJudge or Phase.Finishing)
                Reject("scenario_start", $"시나리오 '{_state?.Definition.Name}'가 진행 중", null);
            if (Tools == null) throw new HarnessException("하네스 내부 오류: 노드 실행 통로(Tools)가 연결되지 않음");

            var definition = Catalog.TryCreate(name)
                             ?? throw new HarnessException($"시나리오 '{name}'가 없음", $"있는 것: {string.Join(", ", Catalog.List.Select(s => s.Name))}");
            if (mode == TH_RunMode.Auto && definition.AutoPlan == null)
                throw new HarnessException($"시나리오 '{name}'에는 자동 실행 경로(AutoPlan)가 없음", "mode=ai로 시작할 것");

            var run = _runs.EnsureCurrent();
            state = new RunState(definition, run, mode, DateTimeOffset.Now) { CurrentState = definition.InitialState, StartSeq = _timeline.LastSeq };
            state.LastAiActivity = DateTimeOffset.UtcNow;
            state.BuildSnapshot = _probe?.Snapshot();
            _state = state;
            _phase = Phase.Ready;

            _timeline.RecordHarness("scenario.start", $"시나리오 시작: {definition.Name} ({mode})",
                new { scenario = definition.Name, mode = mode.ToString(), runId = run.Id });
            Save();
        }

        // 대상별 준비(첫 노드 전) - 예: Unity 에디터가 Play 출발 씬을 미리 연다.
        await PrepareTargetsAsync(state, ct);

        // 게임 코드 상황 로그 목록 - 지금 붙어 있는 대상에 보낸다(나중에 붙는 대상은 OnRecorded가 붙을 때 보낸다).
        if (state.Definition.LogFilter is { } logFilter)
        {
            foreach (var target in state.Definition.Targets) await SendLogFilterAsync(target.Name, logFilter, ct);
        }

        lock (_lock)
        {
            var definition = state.Definition;
            var run = state.Run;
            if (mode == TH_RunMode.Ai)
            {
                return new
                {
                    scenario = definition.Name,
                    title = definition.Title,
                    mode = "ai",
                    runId = run.Id,
                    context = definition.Context,
                    preconditions = definition.Preconditions,
                    cautions = definition.Cautions,
                    rules = Rules(definition),
                    targets = TargetsView(definition),
                    preparations = state.Preparations.Count == 0 ? null : state.Preparations.Select(PreparationView).ToArray(),
                    graph = GraphView(definition),
                    state = state.CurrentState.View(),
                    environment = EnvironmentView(state),
                    available = AvailableView(state),
                    expect = Expect(),
                };
            }
        }

        // 자동 실행 - AI 대신 AutoPlan이 노드를 고른다(노드 실행 경로는 node_run과 같다).
        var planner = new TH_Planner(async (node, reason) =>
        {
            try
            {
                var visit = await RunNodeCoreAsync(node.Id, reason, ActorAuto, ct);
                return new TH_NodeOutcome(visit.Verdict?.Result ?? TH_Result.Unverified, visit.Verdict?.Reason ?? "", visit.StateAfter);
            }
            catch (HarnessException ex)
            {
                return new TH_NodeOutcome(TH_Result.Unverified, $"실행 못 함: {ex.Message}", state.CurrentState);
            }
        }, state.Values);

        string finishReason;
        try
        {
            await state.Definition.AutoPlan!(planner);
            finishReason = "자동 실행 경로 끝";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            finishReason = $"자동 실행 경로 코드 오류 {ex.GetType().Name}: {ex.Message}";
        }

        return await FinishAsync(finishReason, "auto", ct);
    }

    /// <summary>
    /// 선언된 대상들의 준비(TH_Target.Preparation/PrepareAsync)를 차례로 한다. 실패해도 시나리오는 시작한다 - 실패는 응답/보고서/타임라인에
    /// 남고, 그 준비에 기대는 노드의 판정이 다시 잡는다.
    /// </summary>
    private async Task PrepareTargetsAsync(RunState state, CancellationToken ct)
    {
        foreach (var target in state.Definition.Targets.Where(t => t.Preparation != null))
        {
            var path = $"준비@{target.Name}";
            var notes = new List<string>();
            var record = new PreparationRecord(target.Name, target.Preparation!, _timeline.LastSeq);
            var context = new TH_RunContext(path, Tools!, _timeline, state.Values, notes, ct, _overrides);
            try
            {
                await target.PrepareAsync(context);
            }
            catch (TH_NodeFailure failure)
            {
                record.Error = failure.Message;
            }
            catch (HarnessException ex)
            {
                record.Error = ex.Message;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                record.Error = $"준비 코드 오류 {ex.GetType().Name}: {ex.Message}";
            }

            record.Notes.AddRange(notes);
            record.UntilSeq = _timeline.LastSeq;
            lock (_lock) state.Preparations.Add(record);
            _timeline.RecordHarness("scenario.prepared",
                record.Error == null ? $"준비 완료: {target.Name} - {record.Description}" : $"준비 실패: {target.Name} - {record.Error}",
                new { scenario = state.Definition.Name, target = target.Name, ok = record.Error == null, error = record.Error });
        }

        lock (_lock) Save();
    }

    private static object PreparationView(PreparationRecord p) => new
    {
        target = p.Target,
        what = p.Description,
        ok = p.Error == null,
        error = p.Error,
        notes = p.Notes.Count == 0 ? null : p.Notes,
    };

    /// <summary>AI가 고른 노드를 실행한다(하네스가 실행 + 판정).</summary>
    public async Task<object> RunNodeAsync(string nodeId, string? reason, CancellationToken ct)
    {
        var visit = await RunNodeCoreAsync(nodeId, reason, ToolCallLog.ActorAi, ct);
        lock (_lock)
        {
            var state = _state!;
            return new
            {
                visit = VisitView(visit),
                judgeRequired = visit.Verdict == null
                    ? $"AI 판정 노드 - node_judge(node={visit.Node.Id}, result, evidenceSeqs, note)로 판정을 보고할 것. 근거는 seq {visit.SinceSeq + 1}~{visit.EndSeq} 안에서(events_query(sinceSeq={visit.SinceSeq}))"
                    : null,
                state = state.CurrentState.View(),
                stateUncertain = state.StateUncertain ? "마지막 노드가 통과하지 못해 실제 상태가 다를 수 있음 - 필요하면 관찰 도구로 확인" : null,
                environment = EnvironmentView(state),
                available = AvailableView(state),
                expect = Expect(),
            };
        }
    }

    /// <summary>AI 판정 노드의 판정 보고.</summary>
    public object Judge(string nodeId, TH_Result result, IReadOnlyList<long>? evidenceSeqs, string? note)
    {
        lock (_lock)
        {
            if (_phase != Phase.AwaitingJudge) Reject("node_judge", PhaseError(), null);
            var state = _state!;
            var visit = state.PendingJudge!;
            if (!string.Equals(visit.Node.Id, nodeId, StringComparison.OrdinalIgnoreCase))
                Reject("node_judge", $"'{nodeId}'는 판정을 기다리는 노드가 아님(기다리는 노드: {visit.Node.Id})", null);

            var evidence = (evidenceSeqs ?? Array.Empty<long>()).Distinct().ToList();
            var bad = evidence.Where(seq => seq <= visit.SinceSeq || seq > visit.EndSeq || _timeline.TryGet(seq) == null).ToList();
            if (bad.Count > 0)
                Reject("node_judge", $"근거 이벤트 번호가 이 노드 구간에 없음: {string.Join(", ", bad)}",
                    $"이 노드 구간은 seq {visit.SinceSeq + 1}~{visit.EndSeq} - events_query(sinceSeq={visit.SinceSeq})로 확인할 것");
            if (result == TH_Result.Pass && evidence.Count == 0)
                Reject("node_judge", "Pass에는 근거 이벤트 번호(evidenceSeqs)가 필요함", "판단할 근거가 없으면 result=Unverified와 note로 사유를 적을 것");

            visit.AiReport = new TH_AiReport(result, evidence, note);
            visit.Verdict = new TH_Verdict(result, string.IsNullOrWhiteSpace(note) ? "AI 판정" : $"AI 판정: {note}", evidence);
            visit.JudgedBy = "ai";
            ApplyOutcome(state, visit);
            state.PendingJudge = null;
            _phase = Phase.Ready;
            state.LastAiActivity = DateTimeOffset.UtcNow;

            _timeline.RecordHarness("scenario.node_judged", $"AI 판정: {visit.Path} {result}",
                new { scenario = state.Definition.Name, node = visit.Path, result, evidence });
            Save();

            return new
            {
                visit = VisitView(visit),
                state = state.CurrentState.View(),
                environment = EnvironmentView(state),
                available = AvailableView(state),
                expect = Expect(),
            };
        }
    }

    /// <summary>AI의 종료 선언 - 정리 노드 → 보고서 → 알림음.</summary>
    public Task<object> FinishByAiAsync(string reason, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_phase is Phase.Running) Reject("scenario_finish", "노드 실행 중 - 끝난 뒤에 할 것(실행 중인 호출은 call_continue로 기다림)", null);
            if (_phase is Phase.Idle or Phase.Finishing or Phase.Finished) Reject("scenario_finish", PhaseError(), null);
        }

        return FinishAsync($"AI 종료 선언: {reason}", "ai", ct);
    }

    /// <summary>harness_status, 대기 응답용 - 진행 중인 시나리오 상태(없으면 null).</summary>
    public object? Status()
    {
        lock (_lock)
        {
            if (_state == null) return null;
            var state = _state;
            return new
            {
                scenario = state.Definition.Name,
                mode = state.Mode.ToString().ToLowerInvariant(),
                phase = _phase.ToString(),
                state = state.CurrentState.View(),
                running = _phase is Phase.Running or Phase.Finishing ? state.RunningPath : null,
                visits = state.Visits.Count,
                environment = state.Environment,
                pendingJudge = state.PendingJudge?.Node.Id,
                finishReason = state.FinishReason,
                report = state.ReportPath,
                expect = _phase is Phase.Idle or Phase.Finished ? null : Expect(),
            };
        }
    }

    /// <summary>
    /// 오래 걸리는 호출(pending)에 실을 진행 상황 - 실행 중인 노드 경로와 그 노드 구간의 최근 이벤트, 아직 안 알린 환경 변화.
    /// 시간 정보는 넣지 않는다. 시나리오가 없거나 노드를 실행 중이 아니면 null.
    /// </summary>
    public object? Progress()
    {
        lock (_lock)
        {
            if (_state is not { } state || _phase is not (Phase.Running or Phase.Finishing) || state.RunningVisit is not { } visit) return null;

            var recent = _timeline.Range(visit.SinceSeq, _timeline.LastSeq)
                .Where(e => e.Source is not (ToolCallLog.Source or ToolCallLog.RunnerSource) && !e.Name.StartsWith("scenario.", StringComparison.Ordinal))
                .TakeLast(3)
                .Select(e => $"{e.Source} {e.Name}{(e.Data is { } d ? " " + HarnessJson.Truncate(d.GetRawText(), 80) : "")}")
                .ToList();

            return new
            {
                scenario = state.Definition.Name,
                running = state.RunningPath,
                recentEvents = recent,
                environment = state.UndeliveredChanges.Count > 0 ? EnvironmentView(state) : null,
            };
        }
    }

    // ================================ 노드 실행 ================================

    private async Task<VisitRecord> RunNodeCoreAsync(string nodeId, string? reason, string actor, CancellationToken ct)
    {
        RunState state;
        TH_Node node;
        lock (_lock)
        {
            if (_phase == Phase.AwaitingJudge) Reject("node_run", PhaseError(), null);
            if (_phase != Phase.Ready) Reject("node_run", PhaseError(), null);
            state = _state!;

            node = state.Definition.Find(nodeId)
                   ?? Reject<TH_Node>("node_run", $"그래프에 '{nodeId}' 노드가 없음", $"고를 수 있는 노드: {AvailableIds(state)}");
            if (ReferenceEquals(node, state.Definition.Cleanup))
                Reject("node_run", "정리 노드는 직접 고르지 않는다 - 끝낼 때(scenario_finish) 하네스가 실행한다", null);
            if (!state.Definition.IsSelectable(node))
                Reject("node_run", $"'{node.Id}'는 묶음 노드의 자식이라 따로 고를 수 없음", $"고를 수 있는 노드: {AvailableIds(state)}");
            if (node.WhyCannotStart(state.CurrentState) is { } why)
                Reject("node_run", $"'{node.Id}'는 지금 상태에서 시작할 수 없음: {why} (지금 상태: {state.CurrentState})",
                    $"지금 고를 수 있는 노드: {AvailableIds(state)}");

            // 하네스 밖의 변화(사람이 직접 빌드 등) - 노드를 돌리기 직전에 확인한다.
            CheckExternalBuild(state);

            if (actor == ToolCallLog.ActorAi) state.LastAiActivity = DateTimeOffset.UtcNow;
            _phase = Phase.Running;
        }

        // 지속 조작 선언(Forbids/Needs) - 선언한 노드만, 시작 직전에 켜진 조작과 비교한다(어긋나면 시나리오의 OverrideMismatch대로).
        string? overrideWarning;
        try
        {
            overrideWarning = await OverrideMismatchAsync(node, ct);
        }
        catch
        {
            lock (_lock) _phase = Phase.Ready;
            throw;
        }

        if (overrideWarning != null && state.Definition.OverrideMismatch == TH_OverrideMismatch.Reject)
        {
            lock (_lock)
            {
                _timeline.RecordHarness("scenario.override_mismatch", $"지속 조작 조건 어긋남 - 거부: {node.Id}: {overrideWarning}",
                    new { scenario = state.Definition.Name, node = node.Id, action = "reject", mismatch = overrideWarning });
                _phase = Phase.Ready;
                Reject("node_run", $"'{node.Id}'의 지속 조작 조건이 맞지 않음: {overrideWarning}",
                    "남은 조작을 끄거나(harness_status의 overrides - 건 명령으로 처음 값을 다시 주거나 fault.remove) 필요한 조작을 켜는 노드를 먼저 돌릴 것. " +
                    "남은 조작이 있어도 돌려야 하는 시나리오면 시나리오의 OverrideMismatch를 Warn으로");
            }
        }

        var visitNumber = state.Visits.Count + 1;
        MarkNode(HarnessConstants.NodeMarker.StartFormat, node.Id, visitNumber.ToString());
        var visit = await ExecuteAsync(state, node, node.Id, reason, actor, state.CurrentState, ct, overrideWarning);
        MarkNode(HarnessConstants.NodeMarker.EndFormat, node.Id, visitNumber.ToString(), VerdictText(visit));

        lock (_lock)
        {
            state.Visits.Add(visit);
            state.RunningVisit = null;
            state.RunningPath = null;
            if (_probe != null) state.BuildSnapshot = _probe.Snapshot(); // 이 노드가 한 빌드를 다음에 "밖의 변화"로 세지 않게

            if (visit.Verdict == null)
            {
                state.PendingJudge = visit;
                _phase = Phase.AwaitingJudge;
            }
            else
            {
                ApplyOutcome(state, visit);
                _phase = Phase.Ready;
            }

            state.LastAiActivity = DateTimeOffset.UtcNow; // 결과를 받은 AI가 다음을 정할 시간을 IdleTimeout만큼 준다
            Save();
        }

        return visit;
    }

    /// <summary>노드 하나를 실행한다(묶음이면 자식부터 차례로 - 후위순회). 판정까지 해서 방문 기록으로 돌려준다.</summary>
    private async Task<VisitRecord> ExecuteAsync(RunState state, TH_Node node, string path, string? reason, string actor, TH_StateMap stateBefore,
        CancellationToken ct, string? overrideWarning = null)
    {
        var visit = new VisitRecord(node, path, state.Environment, reason, actor, _timeline.LastSeq, DateTimeOffset.Now, stateBefore);
        if (overrideWarning != null)
        {
            visit.Notes.Add($"⚠ 지속 조작 조건 어긋남(시나리오 OverrideMismatch=Warn이라 진행): {overrideWarning}");
            _timeline.RecordHarness("scenario.override_mismatch", $"지속 조작 조건 어긋남 - 경고 후 진행: {path}: {overrideWarning}",
                new { scenario = state.Definition.Name, node = path, action = "warn", mismatch = overrideWarning });
        }
        lock (_lock)
        {
            state.RunningPath = path;
            state.RunningVisit ??= visit;
        }

        _timeline.RecordHarness("scenario.node_begin", $"노드 시작: {path}",
            new { scenario = state.Definition.Name, node = path, sinceSeq = visit.SinceSeq, environment = visit.Environment, actor });

        TH_Verdict? verdict;
        if (node.IsGroup)
        {
            var groupState = stateBefore;
            var children = node.Children;
            for (var i = 0; i < children.Count; i++)
            {
                var child = children[i];
                if (child.WhyCannotStart(groupState) is { } why)
                {
                    var skipped = new VisitRecord(child, $"{path} > {child.Id}", state.Environment, null, actor, _timeline.LastSeq, DateTimeOffset.Now, groupState);
                    skipped.Finish(TH_Verdict.Fail($"묶음 안의 상태에서 시작할 수 없는 자식: {why}"),
                        "harness", _timeline.LastSeq, DateTimeOffset.Now, groupState);
                    visit.Children.Add(skipped);
                    if (child.FailPolicy == TH_FailPolicy.Stop)
                    {
                        visit.SkippedChildren.AddRange(children.Skip(i + 1).Select(c => c.Id));
                        break;
                    }
                    continue;
                }

                var childPath = $"{path} > {child.Id}";
                var childWarning = await OverrideMismatchAsync(child, ct);
                if (childWarning != null && state.Definition.OverrideMismatch == TH_OverrideMismatch.Reject)
                {
                    var rejected = new VisitRecord(child, childPath, state.Environment, null, actor, _timeline.LastSeq, DateTimeOffset.Now, groupState);
                    rejected.Finish(TH_Verdict.Fail($"지속 조작 조건이 맞지 않아 실행 안 함: {childWarning}"), "harness", _timeline.LastSeq, DateTimeOffset.Now, groupState);
                    _timeline.RecordHarness("scenario.override_mismatch", $"지속 조작 조건 어긋남 - 거부: {childPath}: {childWarning}",
                        new { scenario = state.Definition.Name, node = childPath, action = "reject", mismatch = childWarning });
                    visit.Children.Add(rejected);
                    if (child.FailPolicy == TH_FailPolicy.Stop)
                    {
                        visit.SkippedChildren.AddRange(children.Skip(i + 1).Select(c => c.Id));
                        break;
                    }
                    continue;
                }

                var childVisit = await ExecuteAsync(state, child, childPath, null, actor, groupState, ct, childWarning);
                visit.Children.Add(childVisit);
                lock (_lock) state.RunningPath = path;

                if (childVisit.Verdict?.Passed == true)
                {
                    groupState = child.Apply(groupState, state.Definition.InitialState);
                }
                else if (child.FailPolicy == TH_FailPolicy.Stop)
                {
                    visit.SkippedChildren.AddRange(children.Skip(i + 1).Select(c => c.Id));
                    break;
                }
            }

            var notPassed = visit.Children.FirstOrDefault(c => c.Verdict?.Passed != true);
            if (notPassed != null)
            {
                verdict = new TH_Verdict(notPassed.Verdict?.Result == TH_Result.Unverified ? TH_Result.Unverified : TH_Result.Fail,
                    $"자식 {notPassed.Node.Id}: {notPassed.Verdict?.Reason ?? "판정 없음"}" +
                    (visit.SkippedChildren.Count > 0 ? $" (실행 안 한 자식: {string.Join(", ", visit.SkippedChildren)})" : ""),
                    notPassed.Verdict?.EvidenceSeqs ?? Array.Empty<long>());
            }
            else
            {
                verdict = node.Judge != null ? RunJudge(node, visit) : TH_Verdict.Pass($"자식 {visit.Children.Count}개 전부 통과");
            }

            visit.Finish(verdict, "harness", _timeline.LastSeq, DateTimeOffset.Now, verdict.Passed ? node.Apply(groupState, state.Definition.InitialState) : groupState);
        }
        else
        {
            var context = new TH_RunContext(path, Tools!, _timeline, state.Values, visit.Notes, ct, _overrides);
            verdict = null;
            try
            {
                await node.Auto!(context);
            }
            catch (TH_NodeFailure failure)
            {
                verdict = TH_Verdict.Fail(failure.Message);
            }
            catch (HarnessException ex)
            {
                verdict = TH_Verdict.Fail(ex.Hint != null ? $"{ex.Message} ({ex.Hint})" : ex.Message);
            }
            catch (OperationCanceledException)
            {
                verdict = TH_Verdict.Unverified("실행이 중단됨(하네스 종료)");
            }
            catch (Exception ex)
            {
                verdict = TH_Verdict.Unverified($"노드 실행 코드 오류 {ex.GetType().Name}: {ex.Message} - 하네스/시나리오 쪽 버그일 수 있음");
            }

            string judgedBy;
            if (verdict != null)
            {
                judgedBy = "harness";
            }
            else if (node.IsAiJudged)
            {
                if (state.Mode == TH_RunMode.Auto)
                {
                    verdict = TH_Verdict.Unverified("AI 판정 노드 - 자동 실행에서는 판정하지 않음");
                    judgedBy = "auto(판정 안 함)";
                }
                else
                {
                    judgedBy = "ai"; // node_judge를 기다린다(verdict null)
                }
            }
            else
            {
                visit.EndSeq = _timeline.LastSeq;
                verdict = RunJudge(node, visit);
                judgedBy = "harness";
            }

            var after = verdict is { Passed: true } ? node.Apply(stateBefore, state.Definition.InitialState) : stateBefore;
            visit.Finish(verdict, judgedBy, _timeline.LastSeq, DateTimeOffset.Now, after);
        }

        // 이 노드가 건 지속 조작(배속, 설정값 ...)은 노드가 끝나면 되돌린다 - 판정이 끝난 뒤라 판정 재료에는 안 들어간다.
        if (_overrides != null) await _overrides.RevertOwnedBy(path, ct);

        _timeline.RecordHarness("scenario.node_end", $"노드 판정: {path} {visit.Verdict?.Result.ToString() ?? "AI 판정 대기"}",
            new { scenario = state.Definition.Name, node = path, verdict = visit.Verdict?.Result, judgedBy = visit.JudgedBy, reason = visit.Verdict?.Reason });
        visit.NodeEndSeq = _timeline.LastSeq; // 노드 구간 끝(판정 뒤 노드 끝 되돌리기까지) - 노드 밖 이상의 범위 계산용
        return visit;
    }

    /// <summary>
    /// 노드의 지속 조작 선언(Forbids/Needs)과 켜진 조작이 어긋나면 그 내용, 맞으면(또는 선언이 없으면) null. 선언한 종류 중 "확인 대기"(되돌리기
    /// 실패)인 조작만 먼저 조회로 확인한다 - 기록으로 확실한 조작은 조회하지 않는다.
    /// </summary>
    private async Task<string?> OverrideMismatchAsync(TH_Node node, CancellationToken ct)
    {
        if (_overrides == null || (node.ForbiddenOverrides.Count == 0 && node.NeededOverrides.Count == 0)) return null;

        await _overrides.ConfirmAsync(node.ForbiddenOverrides.Concat(node.NeededOverrides).ToList(), ct);
        var active = _overrides.Active;

        var problems = new List<string>();
        foreach (var kind in node.ForbiddenOverrides)
        {
            var on = active.Where(r => r.Kind.Name == kind).ToList();
            if (on.Count > 0)
                problems.Add($"{kind}가 남아 있음({string.Join(", ", on.Select(r => $"{r.Id} {r.Agent}{(r.Scope.Length > 0 ? $"[{r.Scope}]" : "")} {r.Before}→{r.After}" +
                                                                              (r.Unconfirmed != null ? $" - 되돌리기 실패 뒤 확인 못 함: {r.Unconfirmed}" : "")))})");
        }
        foreach (var kind in node.NeededOverrides.Where(kind => active.All(r => r.Kind.Name != kind)))
            problems.Add($"{kind}가 켜져 있어야 함");

        return problems.Count == 0 ? null : string.Join(" / ", problems);
    }

    private TH_Verdict RunJudge(TH_Node node, VisitRecord visit)
    {
        var lastSeq = _timeline.LastSeq;
        var context = new TH_JudgeContext(node.Id, visit.SinceSeq, _timeline.Range(visit.SinceSeq, lastSeq),
            _calls.Since(visit.SinceSeq).Where(c => c.Seq <= lastSeq).ToList());
        try
        {
            return node.Judge!(context);
        }
        catch (Exception ex)
        {
            return TH_Verdict.Unverified($"하네스 판정 코드 오류 {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 판정이 정해진 최상위 방문을 상태에 반영. 행동 노드: 통과면 To대로, 아니면 그대로 + 불확실 표시. 묶음 노드: 통과한 자식들이
    /// 바꾼 칸까지는 반영(실행 중에 계산한 StateAfter - 예: 서버 기동은 됐는데 로그인에서 실패하면 전역은 ServersUp), 통과 못 했으면 불확실 표시.
    /// </summary>
    private static void ApplyOutcome(RunState state, VisitRecord visit)
    {
        var passed = visit.Verdict?.Passed == true;
        if (visit.Node.IsGroup) state.CurrentState = visit.StateAfter;
        else if (passed) state.CurrentState = visit.Node.Apply(state.CurrentState, state.Definition.InitialState);
        state.StateUncertain = !passed;
        visit.StateAfter = state.CurrentState;
    }

    // ================================ 끝내기 ================================

    private async Task<object> FinishAsync(string reason, string how, CancellationToken ct)
    {
        RunState state;
        lock (_lock)
        {
            if (_phase is Phase.Idle or Phase.Finishing or Phase.Finished || _state == null) Reject("scenario_finish", PhaseError(), null);
            state = _state!;
            if (state.PendingJudge != null)
            {
                // 판정을 받지 못한 AI 판정 노드 - 미확인으로 남기고 끝낸다.
                state.PendingJudge.Verdict = TH_Verdict.Unverified("AI 판정 보고 전에 시나리오가 끝남");
                state.PendingJudge = null;
            }
            state.FinishReason = reason;
            state.FinishHow = how;
            _phase = Phase.Finishing;
            _timeline.RecordHarness("scenario.finishing", $"시나리오 마무리: {reason}", new { scenario = state.Definition.Name, reason, how });
        }

        // 시나리오 동안 남은 지속 조작(AI가 건 것, 노드가 남긴 것)은 정리 노드 전에 되돌린다.
        if (_overrides != null) await _overrides.RevertAll(OverrideManager.ReasonScenarioEnd, ct);

        // 게임 코드 상황 로그 목록을 기본값(전부)으로 - 끝나는 즉시(정리 노드 전, 대상이 아직 붙어 있을 때).
        if (state.Definition.LogFilter != null)
        {
            foreach (var target in state.Definition.Targets) await SendLogFilterAsync(target.Name, null, ct);
        }

        var cleanupId = state.Definition.Cleanup.Id;
        MarkNode(HarnessConstants.NodeMarker.StartFormat, cleanupId, HarnessConstants.NodeMarker.CleanupVisit);
        var cleanup = await ExecuteAsync(state, state.Definition.Cleanup, cleanupId, "마무리", ActorHarness, state.CurrentState, ct);
        MarkNode(HarnessConstants.NodeMarker.EndFormat, cleanupId, HarnessConstants.NodeMarker.CleanupVisit, VerdictText(cleanup));
        CollectOutsideEvents(state, cleanup.SinceSeq);

        object result;
        lock (_lock)
        {
            state.Cleanup = cleanup;
            state.RunningVisit = null;
            state.RunningPath = null;
            if (cleanup.Verdict?.Passed == true) state.CurrentState = state.Definition.Cleanup.Apply(state.CurrentState, state.Definition.InitialState);
            state.FinishedAt = DateTimeOffset.Now;
            _phase = Phase.Finished;

            state.ReportPath = Path.Combine(state.Run.Dir, $"{state.FilePrefix}-report.md");
            try
            {
                File.WriteAllText(state.ReportPath, BuildReport(state, BuildTraces(state), _overrides?.Since(state.StartSeq) ?? Array.Empty<OverrideRecord>()),
                    new UTF8Encoding(false));
            }
            catch (IOException ex)
            {
                state.ReportPath = $"(보고서 쓰기 실패: {ex.Message})";
            }

            Save();
            var allPassed = AllPassed(state);
            _timeline.RecordHarness("scenario.end", $"시나리오 종료: {state.Definition.Name} ({reason})",
                new { scenario = state.Definition.Name, report = state.ReportPath, allPassed, how });

            // 끝은 하네스가 판단한다(정리 노드까지 끝남) - 사람이 PC 앞에 없어도 끝난 걸 알게.
            if (state.Definition.PlayFinishSound) FinishSound.Play(allPassed);

            result = new
            {
                finished = true,
                reason,
                allPassed,
                summary = Summary(state),
                report = state.ReportPath,
                message = "시나리오 끝. 보고서는 하네스가 작성했다(사람에게 경로를 알려 줄 것). 실행(run)도 닫혔다(runClosed - 다음 시나리오/서버 기동은 새 실행).",
                runId = state.Run.Id, // 닫힌 뒤 로그는 logs_query(runId=...)로
                runClosed = CloseRun(state),
            };
        }
        return result;
    }

    /// <summary>
    /// 시나리오가 끝나면 그 실행을 닫는다(실행 하나 = 시나리오 하나 - 끝난 실행 폴더를 바로 지우거나 옮길 수 있게, 다음 기록이 섞이지 않게).
    /// 닫으면 안 되는 상태(정리가 실패해 서버가 남음 등 - runEndBlocker가 사유를 돌려줌)면 열어 두고 타임라인에 남긴다.
    /// </summary>
    private bool CloseRun(RunState state)
    {
        if (!ReferenceEquals(_runs.Current, state.Run)) return false; // 도중에 실행이 바뀜(그럴 수 없지만 남의 실행은 닫지 않는다)
        if (_runEndBlocker?.Invoke() is { } blocker)
        {
            _timeline.RecordHarness("scenario.run_kept", $"실행을 닫지 않음: {blocker}", new { runId = state.Run.Id, blocker });
            return false;
        }
        _runs.End();
        return true;
    }

    /// <summary>전부 통과(모든 방문 + 정리)이고 AI 지시 없음으로 끝난 게 아니면 true(완료음), 아니면 경고음.</summary>
    private static bool AllPassed(RunState state) =>
        state.FinishHow != "idle" && state.Visits.Count > 0 &&
        state.Visits.All(v => v.Verdict?.Passed == true) && state.Cleanup?.Verdict?.Passed == true;

    private void CheckIdle()
    {
        RunState? state;
        lock (_lock)
        {
            state = _state;
            if (state == null || state.Mode != TH_RunMode.Ai || _phase is not (Phase.Ready or Phase.AwaitingJudge) || state.IdleFinishing) return;
            if (DateTimeOffset.UtcNow - state.LastAiActivity < state.Definition.IdleTimeout) return;
            state.IdleFinishing = true;
        }

        var last = state.LastAiActivity.ToLocalTime();
        _ = Task.Run(async () =>
        {
            try { await FinishAsync($"AI 지시 없음({state.Definition.IdleTimeout.TotalSeconds:0}초) - 마지막 지시 {last:HH:mm:ss}", "idle", CancellationToken.None); }
            catch (HarnessException) { /* 그 사이 다른 경로로 끝남 */ }
        });
    }

    // ================================ 노드 경계 줄 / 게임 코드 로그 목록 ================================

    private void MarkNode(string format, string path, string visit, string? verdict = null) =>
        _logMarker?.Invoke(string.Format(format, path, visit, verdict));

    private static string VerdictText(VisitRecord visit) => visit.Verdict?.Result.ToString() ?? "AI 판정 대기";

    /// <summary>
    /// 대상 하나에 게임 코드 상황 로그 목록을 보낸다(filter null = 전부로 되돌림). log.filter가 없는 대상(서버, 헤드리스)이나 아직 안 붙은
    /// 대상은 실패한다 - 붙어 있는 Unity 대상에서 난 실패만 타임라인에 남긴다(진행은 막지 않는다).
    /// </summary>
    private async Task SendLogFilterAsync(string agent, TH_LogFilter? filter, CancellationToken ct)
    {
        if (Tools == null) return;
        var args = new Dictionary<string, object?>
        {
            [HarnessConstants.LogFilter.CollectArg] = Join(filter?.Collect),
            [HarnessConstants.LogFilter.ConsoleArg] = Join(filter?.Console),
        };
        try
        {
            var result = await Tools.InvokeAsync("agent_command",
                HarnessJson.Serialize(new { agent, command = HarnessConstants.LogFilter.Command, args }), CurrentNodePath, ct);
            // 도구 오류 = 에이전트가 아직 안 붙음(붙으면 OnRecorded가 다시 보냄). 모르는 명령 = log.filter가 없는 대상(서버, 헤드리스).
            if (result.IsError) return;
            if (result.Value.Bool("ok")) return;
            var error = result.Value.Str("error") ?? "";
            if (error.Contains(HarnessConstants.AgentErrors.UnknownCommand, StringComparison.Ordinal)) return;
            _timeline.RecordHarness("scenario.log_filter_failed", $"게임 코드 로그 목록을 {agent}에 못 보냄: {HarnessJson.Truncate(error, 300)}",
                new { agent, error });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _timeline.RecordHarness("scenario.log_filter_failed", $"게임 코드 로그 목록을 {agent}에 못 보냄: {ex.Message}", new { agent, error = ex.Message });
        }

        static string Join(IReadOnlyList<string>? names) => names switch
        {
            null => "",
            { Count: 0 } => HarnessConstants.LogFilter.None,
            _ => string.Join(HarnessConstants.LogFilter.Separator, names),
        };
    }

    // ================================ 환경 변화 / AI 활동 ================================

    private void OnRecorded(TimelineEvent e)
    {
        var change = TH_EnvironmentChanges.Describe(e);

        // 시나리오 도중 선언된 대상이 (다시) 붙으면 게임 코드 로그 목록을 보낸다 - Unity는 Play/도메인 리로드/재접속 때마다 새로 붙는다.
        if (e.Name == HarnessConstants.AgentEvents.Attached && e.Source.StartsWith(HarnessConstants.AgentEvents.SourcePrefix, StringComparison.Ordinal))
        {
            TH_LogFilter? filter = null;
            string? agent = null;
            lock (_lock)
            {
                if (_state is { } running && _phase is Phase.Ready or Phase.Running or Phase.AwaitingJudge && running.Definition.LogFilter is { } declared)
                {
                    var name = e.Source[HarnessConstants.AgentEvents.SourcePrefix.Length..];
                    if (running.Definition.Targets.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        filter = declared;
                        agent = name;
                    }
                }
            }
            if (agent != null) _ = Task.Run(() => SendLogFilterAsync(agent, filter, CancellationToken.None));
        }

        lock (_lock)
        {
            if (_state is not { } state) return;
            if (e.Source == ToolCallLog.Source && e.Name == ToolCallLog.EventName) state.LastAiActivity = DateTimeOffset.UtcNow;
            if (change == null) return;

            switch (_phase)
            {
                case Phase.Ready or Phase.AwaitingJudge:
                    ApplyEnvironmentChange(state, change, e.Seq);
                    break;
                case Phase.Running or Phase.Finishing when state.RunningVisit is { } visit:
                    lock (visit.Notes) visit.Notes.Add($"실행 중 테스트 대상 변화(이 노드의 행동이라 환경 변화로 세지 않음): {change} (seq {e.Seq})");
                    break;
            }
        }
    }

    private void CheckExternalBuild(RunState state)
    {
        if (_probe == null || state.BuildSnapshot == null) return;
        var now = _probe.Snapshot();
        var changed = TH_BuildProbe.Changed(state.BuildSnapshot, now);
        state.BuildSnapshot = now;
        if (changed.Count > 0) ApplyEnvironmentChange(state, $"하네스 밖에서 서버 빌드 결과가 바뀜: {string.Join(", ", changed)}", null);
    }

    /// <summary>락 안에서. 현재 환경에서 방문한 노드가 있을 때만 번호를 올린다(없으면 같은 환경에 원인만 덧붙임).</summary>
    private void ApplyEnvironmentChange(RunState state, string cause, long? seq)
    {
        if (state.Visits.Any(v => v.Environment == state.Environment)) state.Environment++;
        var change = new TH_EnvChange(state.Environment, cause, seq, DateTimeOffset.Now);
        state.EnvChanges.Add(change);
        state.UndeliveredChanges.Add(change);
        if (_probe != null) state.BuildSnapshot = _probe.Snapshot();
        _timeline.RecordHarness("scenario.environment_changed", $"환경 #{state.Environment}: {cause}",
            new { scenario = state.Definition.Name, environment = state.Environment, cause, causeSeq = seq });
    }

    // ================================ 응답 모양 ================================

    private static string[] Rules(TH_ScenarioDefinition d) => new[]
    {
        "노드는 하네스가 실행하고 판정한다. AI는 node_run으로 다음 노드를 고른다 - 지금 상태에서 시작할 수 있는 것(available)만.",
        "맥락(context)을 보고 충분하다고 판단되면 scenario_finish로 끝낸다. 특정 노드까지 가거나 그래프를 다 돌 필요는 없다.",
        "응답의 environment.changes가 오면 테스트 대상이 바뀐 것 - staleNodes(이전 환경 결과만 있는 노드) 중 맥락상 다시 볼 노드를 다시 돌린다.",
        "노드 사이에 관찰 도구(events_query, logs_query, agent_command 조회 등)로 조사하는 것은 자유다(기록됨, 판정에는 안 쓰임). " +
        "노드 실행 중(pending)에는 테스트 대상을 바꾸는 도구를 부르지 말 것.",
        "judgedBy=ai 노드는 실행 뒤 node_judge로 판정을 보고해야 다음으로 간다(Pass에는 그 노드 구간의 근거 seq 필수).",
        $"AI 지시가 {d.IdleTimeout.TotalSeconds:0}초 동안 없으면(노드 실행 중은 빼고) 하네스가 정리 노드를 돌리고 끝낸다.",
        "보고서는 하네스가 쓴다 - 끝나면 경로를 사람에게 알릴 것.",
        "노드의 forbidsOverrides/needsOverrides는 그 노드가 시작할 때 남아 있으면 안 되는/켜져 있어야 하는 지속 조작 종류 - " +
        (d.OverrideMismatch == TH_OverrideMismatch.Reject
            ? "이 시나리오는 어긋나면 노드를 거부한다(harness_status의 overrides를 보고 남은 조작을 끄거나 필요한 조작을 켤 것)."
            : "이 시나리오는 어긋나도 노드를 돌리고 경고만 남긴다(결과를 볼 때 감안할 것)."),
    };

    private static object TargetsView(TH_ScenarioDefinition d) =>
        d.Targets.Select(t => new { name = t.Name, kind = t.Kind, stateScope = t.InitialState != null, initialState = t.InitialState }).ToList();

    private static object GraphView(TH_ScenarioDefinition d) => new
    {
        initialState = d.InitialState.View(),
        stateNote = "상태는 칸(global + 클라마다)별로 하나씩. 노드의 from은 적은 칸이 전부 맞아야 시작, to는 통과하면 바뀌는 칸",
        nodes = d.Nodes.Select(NodeView),
        cleanup = new { id = d.Cleanup.Id, goal = d.Cleanup.Goal, note = "끝낼 때 하네스가 실행(직접 고르지 않음)" },
    };

    private static object NodeView(TH_Node n) => new
    {
        id = n.Id,
        goal = n.Goal,
        passCriteria = n.PassCriteria,
        from = n.FromView(),
        to = n.ToView(),
        judgedBy = n.IsGroup ? "harness(자식 결과)" : n.IsAiJudged ? "ai" : "harness",
        hints = n.Hints.Count > 0 ? n.Hints : null,
        forbidsOverrides = n.ForbiddenOverrides.Count > 0 ? n.ForbiddenOverrides : null,
        needsOverrides = n.NeededOverrides.Count > 0 ? n.NeededOverrides : null,
        children = n.IsGroup ? n.Children.Select(NodeView) : null,
    };

    private static object AvailableView(RunState state) =>
        state.Definition.Nodes.Where(n => n.CanStartFrom(state.CurrentState)).Select(n =>
        {
            var last = state.Visits.LastOrDefault(v => v.Node == n);
            return new
            {
                id = n.Id,
                visitedInCurrentEnvironment = state.Visits.Any(v => v.Node == n && v.Environment == state.Environment && v.Verdict != null),
                lastResult = last?.Verdict?.Result.ToString(),
                lastEnvironment = last?.Environment,
            };
        }).ToList();

    private static string AvailableIds(RunState state) =>
        string.Join(", ", state.Definition.Nodes.Where(n => n.CanStartFrom(state.CurrentState)).Select(n => n.Id).DefaultIfEmpty("(없음 - scenario_finish)"));

    /// <summary>환경 상태. 아직 안 알린 변화(changes)는 이번 응답에 싣고 알린 것으로 표시한다.</summary>
    private static object EnvironmentView(RunState state)
    {
        var changes = state.UndeliveredChanges.ToList();
        state.UndeliveredChanges.Clear();
        var stale = state.Definition.Nodes
            .Where(n => state.Visits.Any(v => v.Node == n && v.Environment < state.Environment) &&
                        !state.Visits.Any(v => v.Node == n && v.Environment == state.Environment))
            .Select(n => n.Id)
            .ToList();

        return new
        {
            number = state.Environment,
            changes = changes.Count > 0 ? changes.Select(c => new { environment = c.Environment, cause = c.Cause, seq = c.Seq }) : null,
            staleNodes = stale.Count > 0 ? stale : null,
            note = changes.Count > 0 ? "테스트 대상이 바뀜 - 그 전에 돈 노드의 결과는 이전 환경. 맥락상 다시 볼 노드를 다시 돌릴 것" : null,
        };
    }

    private object Expect()
    {
        var state = _state!;
        return _phase switch
        {
            Phase.Ready => new { actions = new[] { "node_run", "scenario_finish" }, nodes = state.Definition.Nodes.Where(n => n.CanStartFrom(state.CurrentState)).Select(n => n.Id) },
            Phase.AwaitingJudge => new { actions = new[] { "node_judge" }, nodes = new[] { state.PendingJudge!.Node.Id } },
            Phase.Running => new { actions = new[] { "call_continue(실행 중인 호출)" }, nodes = Array.Empty<string>() },
            Phase.Finishing => new { actions = new[] { "call_continue(정리 중)" }, nodes = Array.Empty<string>() },
            _ => (object)new { actions = Array.Empty<string>(), nodes = Array.Empty<string>() },
        };
    }

    private static object VisitView(VisitRecord v) => new
    {
        node = v.Path,
        environment = v.Environment,
        result = v.Verdict?.Result.ToString() ?? "AI 판정 대기",
        reason = v.Verdict?.Reason,
        evidence = v.Verdict?.EvidenceSeqs,
        judgedBy = v.JudgedBy,
        seqRange = $"{v.SinceSeq + 1}~{v.EndSeq}",
        stateAfter = v.StateAfter.View(),
        children = v.Children.Count > 0
            ? v.Children.Select(c => new { node = c.Node.Id, result = c.Verdict?.Result.ToString() ?? "-", reason = c.Verdict?.Reason })
            : null,
        skippedChildren = v.SkippedChildren.Count > 0 ? v.SkippedChildren : null,
        notes = v.Notes.Count > 0 ? v.Notes : null,
    };

    /// <summary>노드 구간 밖에서 일어난 이상을 모은다(시나리오 시작 ~ 정리 노드 시작, 노드 방문과 준비 구간 제외 - TH_OutsideEvents).</summary>
    private void CollectOutsideEvents(RunState state, long cleanupSinceSeq)
    {
        var excluded = state.Visits.Select(v => (v.SinceSeq, Math.Max(Math.Max(v.EndSeq, v.NodeEndSeq), v.SinceSeq)))
            .Concat(state.Preparations.Select(p => (p.SinceSeq, p.UntilSeq)))
            .ToList();
        var targets = new HashSet<string>(state.Definition.Targets.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        var found = TH_OutsideEvents.Find(_timeline.Range(state.StartSeq, cleanupSinceSeq), targets, excluded);
        lock (_lock)
        {
            state.OutsideEvents.Clear();
            state.OutsideEvents.AddRange(found);
        }
    }

    private static object Summary(RunState state) => new
    {
        visits = state.Visits.Count,
        pass = state.Visits.Count(v => v.Verdict?.Result == TH_Result.Pass),
        fail = state.Visits.Count(v => v.Verdict?.Result == TH_Result.Fail),
        unverified = state.Visits.Count(v => v.Verdict?.Result is TH_Result.Unverified or null),
        environments = state.Environment,
        cleanup = state.Cleanup?.Verdict?.Result.ToString(),
        rejections = state.Rejections.Count,
        outsideEvents = state.OutsideEvents.Count, // 노드 밖에서 일어난 이상(판정에는 안 들어감 - 보고서 머리 경고)
    };

    private string PhaseError() => _phase switch
    {
        Phase.Idle => "진행 중인 시나리오가 없음",
        Phase.Finished => $"시나리오 '{_state?.Definition.Name}'는 이미 끝났음",
        Phase.Finishing => "시나리오를 마무리하는 중(정리 노드 실행 중)",
        Phase.Ready => "지금은 다음 노드를 고를 차례(node_run) 또는 끝내기(scenario_finish)",
        Phase.Running => $"노드 실행 중({_state?.RunningPath}) - 실행 중인 호출을 call_continue로 기다릴 것",
        Phase.AwaitingJudge => $"AI 판정 노드 {_state?.PendingJudge?.Node.Id}의 판정을 먼저 보고할 것(node_judge)",
        _ => _phase.ToString(),
    };

    private void Reject(string tool, string message, string? extraHint) => Reject<object>(tool, message, extraHint);

    private T Reject<T>(string tool, string message, string? extraHint)
    {
        if (_state != null && _phase is not (Phase.Idle or Phase.Finished))
        {
            _state.Rejections.Add($"{DateTimeOffset.Now:HH:mm:ss} {tool}: {message}");
            Save();
        }

        var hint = _state != null && _phase is not (Phase.Idle or Phase.Finished)
            ? $"지금 기대하는 행동: {HarnessJson.Serialize(Expect())}"
            : "scenario_list → scenario_start로 시작할 것";
        throw new HarnessException(message, extraHint != null ? $"{extraHint} / {hint}" : hint);
    }

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
        if (d.Targets.Count > 0) sb.AppendLine($"- 대상: {string.Join(", ", d.Targets.Select(t => $"{t.Name}({t.Kind})"))}");
        foreach (var p in state.Preparations)
        {
            sb.AppendLine(p.Error == null
                ? $"- 준비: {p.Target} - {p.Description} (seq {p.SinceSeq + 1} ~ {p.UntilSeq}){(p.Notes.Count > 0 ? " · " + string.Join(" · ", p.Notes) : "")}"
                : $"- 준비 **실패**: {p.Target} - {p.Description}: {p.Error} (seq {p.SinceSeq + 1} ~ {p.UntilSeq})");
        }
        sb.AppendLine($"- 결과: **{(allPassed ? "전부 통과" : "통과하지 못한 것 있음")}** - 방문 {state.Visits.Count} (통과 {state.Visits.Count(v => v.Verdict?.Passed == true)} / " +
                      $"실패 {state.Visits.Count(v => v.Verdict?.Result == TH_Result.Fail)} / 미확인 {state.Visits.Count(v => v.Verdict?.Result is TH_Result.Unverified or null)}) · " +
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
                              $"(경고 이상 {trace.WarningLogs}줄)" + (trace.MergedDuplicates > 0 ? $" · 중복 로그 {trace.MergedDuplicates}줄 합침" : ""));
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

    // ================================ 상태 ================================

    private sealed class RunState
    {
        public RunState(TH_ScenarioDefinition definition, HarnessRun run, TH_RunMode mode, DateTimeOffset startedAt)
        {
            Definition = definition;
            Run = run;
            Mode = mode;
            StartedAt = startedAt;
            FilePrefix = $"scenario-{definition.Name}-{startedAt:HHmmss}";
        }

        public TH_ScenarioDefinition Definition { get; }
        public HarnessRun Run { get; }
        public TH_RunMode Mode { get; }
        public DateTimeOffset StartedAt { get; }
        public string FilePrefix { get; }
        public DateTimeOffset? FinishedAt { get; set; }

        public TH_StateMap CurrentState { get; set; } = null!;

        /// <summary>시작할 때의 마지막 이벤트 번호(보고서의 지속 조작 범위).</summary>
        public long StartSeq { get; init; }
        public bool StateUncertain { get; set; }
        public int Environment { get; set; } = 1;
        public List<TH_EnvChange> EnvChanges { get; } = new();
        public List<TH_EnvChange> UndeliveredChanges { get; } = new();
        public IReadOnlyDictionary<string, DateTime?>? BuildSnapshot { get; set; }

        public List<VisitRecord> Visits { get; } = new();
        public VisitRecord? Cleanup { get; set; }
        public VisitRecord? PendingJudge { get; set; }

        /// <summary>실행 중인 최상위 방문(자식 실행 중에도 최상위)과 지금 실행 중인 경로.</summary>
        public VisitRecord? RunningVisit { get; set; }
        public string? RunningPath { get; set; }

        public TH_RunValues Values { get; } = new();
        public DateTimeOffset LastAiActivity { get; set; }
        public bool IdleFinishing { get; set; }
        public string? FinishReason { get; set; }
        public string? FinishHow { get; set; }
        public List<string> Rejections { get; } = new();
        public string? ReportPath { get; set; }

        /// <summary>시나리오 시작 때 한 대상별 준비(TH_Target.PrepareAsync).</summary>
        public List<PreparationRecord> Preparations { get; } = new();

        /// <summary>노드 구간 밖에서 일어난 이상(TH_OutsideEvents - 끝날 때 모음).</summary>
        public List<TH_OutsideEvent> OutsideEvents { get; } = new();
    }

    private sealed class PreparationRecord
    {
        public PreparationRecord(string target, string description, long sinceSeq)
        {
            Target = target;
            Description = description;
            SinceSeq = sinceSeq;
        }

        public string Target { get; }
        public string Description { get; }
        public long SinceSeq { get; }
        public long UntilSeq { get; set; }
        public string? Error { get; set; }
        public List<string> Notes { get; } = new();
    }

    private sealed class VisitRecord
    {
        public VisitRecord(TH_Node node, string path, int environment, string? reason, string actor, long sinceSeq, DateTimeOffset beganAt, TH_StateMap stateBefore)
        {
            Node = node;
            Path = path;
            Environment = environment;
            Reason = reason;
            Actor = actor;
            SinceSeq = sinceSeq;
            EndSeq = sinceSeq;
            BeganAt = beganAt;
            StateBefore = stateBefore;
            StateAfter = stateBefore;
        }

        public TH_Node Node { get; }
        public string Path { get; }
        public int Environment { get; }
        public string? Reason { get; }

        /// <summary>누가 골랐나: ai | auto | harness(정리).</summary>
        public string Actor { get; }

        public long SinceSeq { get; }
        public long EndSeq { get; set; }

        /// <summary>scenario.node_end 이벤트의 seq(판정 뒤 노드 끝 되돌리기까지 포함한 노드 구간의 끝).</summary>
        public long NodeEndSeq { get; set; }
        public DateTimeOffset BeganAt { get; }
        public DateTimeOffset? EndedAt { get; private set; }
        public TH_StateMap StateBefore { get; }
        public TH_StateMap StateAfter { get; set; }
        public TH_Verdict? Verdict { get; set; }
        public string? JudgedBy { get; set; }
        public TH_AiReport? AiReport { get; set; }
        public List<VisitRecord> Children { get; } = new();
        public List<string> SkippedChildren { get; } = new();
        public List<string> Notes { get; } = new();

        public void Finish(TH_Verdict? verdict, string judgedBy, long endSeq, DateTimeOffset endedAt, TH_StateMap stateAfter)
        {
            Verdict = verdict;
            JudgedBy = judgedBy;
            EndSeq = endSeq;
            EndedAt = endedAt;
            StateAfter = stateAfter;
        }
    }
}
