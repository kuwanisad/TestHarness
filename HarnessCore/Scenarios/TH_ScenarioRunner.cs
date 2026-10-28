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
/// 노드 실행 중(또는 자동 실행 중)의 scenario_finish는 일찍 끝내기 - 실행 중인 노드를 멈추고(Stopped, 판정 안 함) 노드를 더 돌리지 않는다.
/// 어떻게 끝나든 정리 노드 → 보고서(하네스가 씀 - 실제로 돈 방문만으로) → 알림음.
///
/// 환경 변화(TH_EnvironmentChanges): 노드와 노드 사이에 테스트 대상이 바뀌면 환경 번호가 올라가고, 그 전 방문 결과는 "이전 환경"이
/// 된다(현재 환경에서는 안 돈 것으로 보임 - 결과는 보고서에 남음). 노드 실행 중의 변화는 그 노드의 의도된 행동이라 세지 않는다.
/// 변화 알림은 다음 응답(environment.changes)에 실린다.
/// </summary>
public sealed partial class TH_ScenarioRunner : IDisposable
{
    private enum Phase { Idle, Ready, Running, AwaitingJudge, Finishing, Finished }


    private readonly Timeline _timeline;
    private readonly RunManager _runs;
    private readonly ToolCallLog _calls;
    private readonly RunTrace _trace;
    private readonly TH_BuildProbe? _probe;
    private readonly OverrideManager? _overrides;
    private readonly Action<string>? _logMarker;
    private readonly Func<string?>? _runEndBlocker;
    private readonly Func<string, IReadOnlyCollection<string>?>? _agentCommands;
    private readonly object _lock = new();
    private readonly Timer _idleTimer;

    private Phase _phase = Phase.Idle;
    private RunState? _state;

    /// <param name="logMarker">노드 경계 줄을 에이전트 로그 파일에 넣는 곳(AgentLogWriter.WriteMarker) - 없으면 넣지 않는다.</param>
    /// <param name="runEndBlocker">시나리오 끝에 실행을 닫으면 안 되는 사유(예: 서버가 떠 있음), 없으면 null - 없으면 늘 닫는다.</param>
    /// <param name="agentCommands">접속 중인 에이전트의 명령 이름들(안 붙었으면 null) - 노드가 선언한 조회(Probes)를 실행 전에 비교한다.</param>
    public TH_ScenarioRunner(Timeline timeline, RunManager runs, ToolCallLog calls, RunTrace trace, TH_BuildProbe? probe = null,
        OverrideManager? overrides = null, Action<string>? logMarker = null, Func<string?>? runEndBlocker = null,
        Func<string, IReadOnlyCollection<string>?>? agentCommands = null)
    {
        _agentCommands = agentCommands;
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
        _idleTimer = new Timer(_ => CheckIdle(), null, TimeSpan.FromSeconds(HarnessConstants.Limits.IdleCheckSeconds), TimeSpan.FromSeconds(HarnessConstants.Limits.IdleCheckSeconds));
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
                Reject(HarnessConstants.Tools.ScenarioStart, $"시나리오 '{_state?.Definition.Name}'가 진행 중", null);
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

            _timeline.RecordHarness(HarnessConstants.HarnessEvents.ScenarioStart, $"시나리오 시작: {definition.Name} ({mode})",
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
                    mode = HarnessConstants.Actors.Ai,
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
            // 일찍 끝내기 뒤에는 노드를 더 돌리지 않는다 - AutoPlan은 통과 못 한 결과를 보고 스스로 끝난다.
            lock (_lock)
            {
                if (state.StopReason != null) return new TH_NodeOutcome(TH_Result.Stopped, $"실행 안 함 - {StopText(state)}", state.CurrentState);
            }
            try
            {
                var visit = await RunNodeCoreAsync(node.Id, reason, HarnessConstants.Actors.Auto, ct);
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

        bool stopped;
        lock (_lock) stopped = state.StopReason != null;
        return stopped
            ? await FinishAsync(StopText(state), HarnessConstants.FinishHow.AiStop, ct)
            : await FinishAsync(finishReason, HarnessConstants.FinishHow.Auto, ct);
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
            _timeline.RecordHarness(HarnessConstants.HarnessEvents.ScenarioPrepared,
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
        var visit = await RunNodeCoreAsync(nodeId, reason, HarnessConstants.Actors.Ai, ct);
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
            if (_phase != Phase.AwaitingJudge) Reject(HarnessConstants.Tools.NodeJudge, PhaseError(), null);
            var state = _state!;
            var visit = state.PendingJudge!;
            if (!string.Equals(visit.Node.Id, nodeId, StringComparison.OrdinalIgnoreCase))
                Reject(HarnessConstants.Tools.NodeJudge, $"'{nodeId}'는 판정을 기다리는 노드가 아님(기다리는 노드: {visit.Node.Id})", null);

            var evidence = (evidenceSeqs ?? Array.Empty<long>()).Distinct().ToList();
            var bad = evidence.Where(seq => seq <= visit.SinceSeq || seq > visit.EndSeq || _timeline.TryGet(seq) == null).ToList();
            if (bad.Count > 0)
                Reject(HarnessConstants.Tools.NodeJudge, $"근거 이벤트 번호가 이 노드 구간에 없음: {string.Join(", ", bad)}",
                    $"이 노드 구간은 seq {visit.SinceSeq + 1}~{visit.EndSeq} - events_query(sinceSeq={visit.SinceSeq})로 확인할 것");
            if (result == TH_Result.Stopped)
                Reject(HarnessConstants.Tools.NodeJudge, "Stopped는 하네스만 정한다(일찍 끝내기로 멈춘 노드)", "result는 Pass | Fail | Unverified 중 하나");
            if (result == TH_Result.Pass && evidence.Count == 0)
                Reject(HarnessConstants.Tools.NodeJudge, "Pass에는 근거 이벤트 번호(evidenceSeqs)가 필요함", "판단할 근거가 없으면 result=Unverified와 note로 사유를 적을 것");

            visit.AiReport = new TH_AiReport(result, evidence, note);
            visit.Verdict = new TH_Verdict(result, string.IsNullOrWhiteSpace(note) ? "AI 판정" : $"AI 판정: {note}", evidence);
            visit.JudgedBy = HarnessConstants.Actors.Ai;
            ApplyOutcome(state, visit);
            state.PendingJudge = null;
            _phase = Phase.Ready;
            state.LastAiActivity = DateTimeOffset.UtcNow;

            _timeline.RecordHarness(HarnessConstants.HarnessEvents.ScenarioNodeJudged, $"AI 판정: {visit.Path} {result}",
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

    /// <summary>
    /// AI의 종료 선언 - 정리 노드 → 보고서 → 알림음. 노드 실행 중이거나 자동 실행 중이면 일찍 끝내기: 실행 중인 노드를 멈추고(Stopped)
    /// 그 노드가 멈출 때까지 기다린 뒤 마무리한다(자동 실행은 AutoPlan이 끝나며 하는 마무리를 기다린다). 노드를 이어서 진행하지는 않는다.
    /// </summary>
    public async Task<object> FinishByAiAsync(string reason, CancellationToken ct)
    {
        RunState state;
        Task? nodeDone;
        lock (_lock)
        {
            if (_phase is Phase.Idle or Phase.Finishing or Phase.Finished || _state == null) Reject(HarnessConstants.Tools.ScenarioFinish, PhaseError(), null);
            state = _state!;
            if (state.StopReason != null)
                Reject(HarnessConstants.Tools.ScenarioFinish, $"이미 끝내는 중({StopText(state)})", "먼저 부른 scenario_finish 호출을 call_continue로 기다릴 것");

            if (state.Mode != TH_RunMode.Auto && _phase != Phase.Running)
            {
                nodeDone = null;
            }
            else
            {
                state.StopReason = reason;
                state.StoppedPath = _phase == Phase.Running ? state.RunningPath : null;
                state.LastAiActivity = DateTimeOffset.UtcNow;
                nodeDone = state.NodeDone?.Task ?? Task.CompletedTask;
                _timeline.RecordHarness(HarnessConstants.HarnessEvents.ScenarioStopping, $"시나리오 일찍 끝내기: {StopText(state)}",
                    new { scenario = state.Definition.Name, reason, node = state.StoppedPath });
            }
        }

        if (nodeDone == null) return await FinishAsync($"AI 종료 선언: {reason}", HarnessConstants.FinishHow.Ai, ct);

        state.Stop.Cancel(); // 락 밖에서 - 취소 콜백이 노드 코드를 이어서 돌린다
        if (state.Mode == TH_RunMode.Auto) return await state.Finished.Task.WaitAsync(ct);

        await nodeDone.WaitAsync(ct);
        return await FinishAsync(StopText(state), HarnessConstants.FinishHow.AiStop, ct);
    }

    private static string StopText(RunState state) =>
        $"AI 종료 선언(일찍 끝내기): {state.StopReason}" + (state.StoppedPath != null ? $" - 실행 중이던 {state.StoppedPath} 중단" : "");

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
                .Where(e => e.Source is not (HarnessConstants.Sources.Ai or HarnessConstants.Sources.Runner) && !e.Name.StartsWith(HarnessConstants.HarnessEvents.ScenarioPrefix, StringComparison.Ordinal))
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
            if (_phase == Phase.AwaitingJudge) Reject(HarnessConstants.Tools.NodeRun, PhaseError(), null);
            if (_phase != Phase.Ready) Reject(HarnessConstants.Tools.NodeRun, PhaseError(), null);
            state = _state!;
            if (state.StopReason != null)
                Reject(HarnessConstants.Tools.NodeRun, $"시나리오를 끝내는 중({StopText(state)}) - 노드를 더 돌리지 않음", "scenario_finish 호출을 call_continue로 기다릴 것");

            node = state.Definition.Find(nodeId)
                   ?? Reject<TH_Node>(HarnessConstants.Tools.NodeRun, $"그래프에 '{nodeId}' 노드가 없음", $"고를 수 있는 노드: {AvailableIds(state)}");
            if (ReferenceEquals(node, state.Definition.Cleanup))
                Reject(HarnessConstants.Tools.NodeRun, "정리 노드는 직접 고르지 않는다 - 끝낼 때(scenario_finish) 하네스가 실행한다", null);
            if (!state.Definition.IsSelectable(node))
                Reject(HarnessConstants.Tools.NodeRun, $"'{node.Id}'는 묶음 노드의 자식이라 따로 고를 수 없음", $"고를 수 있는 노드: {AvailableIds(state)}");
            if (node.WhyCannotStart(state.CurrentState) is { } why)
                Reject(HarnessConstants.Tools.NodeRun, $"'{node.Id}'는 지금 상태에서 시작할 수 없음: {why} (지금 상태: {state.CurrentState})",
                    $"지금 고를 수 있는 노드: {AvailableIds(state)}");

            // 하네스 밖의 변화(사람이 직접 빌드 등) - 노드를 돌리기 직전에 확인한다.
            CheckExternalBuild(state);

            if (actor == HarnessConstants.Actors.Ai) state.LastAiActivity = DateTimeOffset.UtcNow;
            _phase = Phase.Running;
            state.NodeDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        try
        {
            return await RunStartedNodeAsync(state, node, reason, actor, ct);
        }
        finally
        {
            lock (_lock) state.NodeDone?.TrySetResult();
        }
    }

    /// <summary>RunNodeCoreAsync가 Running으로 바꾼 뒤의 실행 - 지속 조작 확인, 실행, 기록.</summary>
    private async Task<VisitRecord> RunStartedNodeAsync(RunState state, TH_Node node, string? reason, string actor, CancellationToken ct)
    {

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
                _timeline.RecordHarness(HarnessConstants.HarnessEvents.ScenarioOverrideMismatch, $"지속 조작 조건 어긋남 - 거부: {node.Id}: {overrideWarning}",
                    new { scenario = state.Definition.Name, node = node.Id, action = "reject", mismatch = overrideWarning });
                _phase = Phase.Ready;
                Reject(HarnessConstants.Tools.NodeRun, $"'{node.Id}'의 지속 조작 조건이 맞지 않음: {overrideWarning}",
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

    /// <summary>
    /// 노드 하나를 실행한다(묶음이면 자식부터 차례로 - 후위순회). 판정까지 해서 방문 기록으로 돌려준다.
    /// stoppable이면 일찍 끝내기(state.Stop)에 멈춘다 - 멈춘 노드는 Stopped, 안 돈 자식은 '실행 안 한 자식'. 정리 노드는 멈추지 않는다(false).
    /// </summary>
    private async Task<VisitRecord> ExecuteAsync(RunState state, TH_Node node, string path, string? reason, string actor, TH_StateMap stateBefore,
        CancellationToken ct, string? overrideWarning = null, bool stoppable = true)
    {
        // 일찍 끝내기 때문에 멈췄는지(하네스 종료로 취소된 것과 구분).
        bool StopHit() => stoppable && state.Stop.IsCancellationRequested && !ct.IsCancellationRequested;

        var visit = new VisitRecord(node, path, state.Environment, reason, actor, _timeline.LastSeq, DateTimeOffset.Now, stateBefore);
        if (overrideWarning != null)
        {
            visit.Notes.Add($"⚠ 지속 조작 조건 어긋남(시나리오 OverrideMismatch=Warn이라 진행): {overrideWarning}");
            _timeline.RecordHarness(HarnessConstants.HarnessEvents.ScenarioOverrideMismatch, $"지속 조작 조건 어긋남 - 경고 후 진행: {path}: {overrideWarning}",
                new { scenario = state.Definition.Name, node = path, action = "warn", mismatch = overrideWarning });
        }
        lock (_lock)
        {
            state.RunningPath = path;
            state.RunningVisit ??= visit;
        }

        _timeline.RecordHarness(HarnessConstants.HarnessEvents.ScenarioNodeBegin, $"노드 시작: {path}",
            new { scenario = state.Definition.Name, node = path, sinceSeq = visit.SinceSeq, environment = visit.Environment, actor });

        TH_Verdict? verdict;
        if (node.IsGroup)
        {
            var groupState = stateBefore;
            var children = node.Children;
            for (var i = 0; i < children.Count; i++)
            {
                var child = children[i];
                if (StopHit())
                {
                    visit.SkippedChildren.AddRange(children.Skip(i).Select(c => c.Id));
                    break;
                }

                if (child.WhyCannotStart(groupState) is { } why)
                {
                    var skipped = new VisitRecord(child, $"{path} > {child.Id}", state.Environment, null, actor, _timeline.LastSeq, DateTimeOffset.Now, groupState);
                    skipped.Finish(TH_Verdict.Fail($"묶음 안의 상태에서 시작할 수 없는 자식: {why}"),
                        HarnessConstants.Actors.Harness, _timeline.LastSeq, DateTimeOffset.Now, groupState);
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
                    rejected.Finish(TH_Verdict.Fail($"지속 조작 조건이 맞지 않아 실행 안 함: {childWarning}"), HarnessConstants.Actors.Harness, _timeline.LastSeq, DateTimeOffset.Now, groupState);
                    _timeline.RecordHarness(HarnessConstants.HarnessEvents.ScenarioOverrideMismatch, $"지속 조작 조건 어긋남 - 거부: {childPath}: {childWarning}",
                        new { scenario = state.Definition.Name, node = childPath, action = "reject", mismatch = childWarning });
                    visit.Children.Add(rejected);
                    if (child.FailPolicy == TH_FailPolicy.Stop)
                    {
                        visit.SkippedChildren.AddRange(children.Skip(i + 1).Select(c => c.Id));
                        break;
                    }
                    continue;
                }

                var childVisit = await ExecuteAsync(state, child, childPath, null, actor, groupState, ct, childWarning, stoppable);
                visit.Children.Add(childVisit);
                lock (_lock) state.RunningPath = path;

                if (childVisit.Verdict?.Result == TH_Result.Stopped)
                {
                    visit.SkippedChildren.AddRange(children.Skip(i + 1).Select(c => c.Id));
                    break;
                }

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

            var notPassed = visit.Children.FirstOrDefault(c => c.Verdict?.Result == TH_Result.Stopped)
                            ?? visit.Children.FirstOrDefault(c => c.Verdict?.Passed != true);
            if (notPassed == null && visit.SkippedChildren.Count > 0)
            {
                // 다음 자식을 시작하기 전에 멈춤 - 돈 자식은 전부 통과했지만 묶음은 끝나지 않았다.
                verdict = TH_Verdict.Stopped($"{StopText(state)} (실행 안 한 자식: {string.Join(", ", visit.SkippedChildren)})");
            }
            else if (notPassed != null)
            {
                verdict = new TH_Verdict(notPassed.Verdict?.Result switch
                    {
                        TH_Result.Stopped => TH_Result.Stopped,
                        TH_Result.Unverified => TH_Result.Unverified,
                        _ => TH_Result.Fail,
                    },
                    $"자식 {notPassed.Node.Id}: {notPassed.Verdict?.Reason ?? "판정 없음"}" +
                    (visit.SkippedChildren.Count > 0 ? $" (실행 안 한 자식: {string.Join(", ", visit.SkippedChildren)})" : ""),
                    notPassed.Verdict?.EvidenceSeqs ?? Array.Empty<long>());
            }
            else
            {
                verdict = node.Judge != null ? RunJudge(node, visit) : TH_Verdict.Pass($"자식 {visit.Children.Count}개 전부 통과");
            }

            visit.Finish(verdict, HarnessConstants.Actors.Harness, _timeline.LastSeq, DateTimeOffset.Now, verdict.Passed ? node.Apply(groupState, state.Definition.InitialState) : groupState);
        }
        else
        {
            // 실행 코드는 일찍 끝내기에도 멈춘다(정리 노드 제외). 판정 뒤 되돌리기 등은 하네스 종료(ct)에만 멈춘다.
            using var stop = stoppable ? CancellationTokenSource.CreateLinkedTokenSource(ct, state.Stop.Token) : null;
            var context = new TH_RunContext(path, Tools!, _timeline, state.Values, visit.Notes, stop?.Token ?? ct, _overrides);
            verdict = MissingProbes(state, path, node.ProbedCommands) is { } missing ? TH_Verdict.Fail(missing) : null;
            if (verdict == null)
            try
            {
                await node.Auto!(context);
            }
            catch (OperationCanceledException) when (StopHit())
            {
                verdict = TH_Verdict.Stopped(StopText(state));
            }
            catch (OperationCanceledException)
            {
                verdict = TH_Verdict.Unverified("실행이 중단됨(하네스 종료)");
            }
            // 멈추는 도중에 난 실패는 멈춤 때문일 수 있다 - 실패로 세지 않고 중단으로 남긴다(원래 내용은 사유에).
            catch (Exception ex) when (StopHit())
            {
                verdict = TH_Verdict.Stopped($"{StopText(state)} (멈추는 중에 난 오류: {ex.Message})");
            }
            catch (TH_NodeFailure failure)
            {
                verdict = TH_Verdict.Fail(failure.Message);
            }
            catch (HarnessException ex)
            {
                verdict = TH_Verdict.Fail(ex.Hint != null ? $"{ex.Message} ({ex.Hint})" : ex.Message);
            }
            catch (Exception ex)
            {
                verdict = TH_Verdict.Unverified($"노드 실행 코드 오류 {ex.GetType().Name}: {ex.Message} - 하네스/시나리오 쪽 버그일 수 있음");
            }

            string judgedBy;
            if (verdict != null)
            {
                judgedBy = HarnessConstants.Actors.Harness;
            }
            else if (node.IsAiJudged)
            {
                if (state.Mode == TH_RunMode.Auto)
                {
                    verdict = TH_Verdict.Unverified("AI 판정 노드 - 자동 실행에서는 판정하지 않음");
                    judgedBy = HarnessConstants.Actors.AutoUnjudged;
                }
                else
                {
                    judgedBy = HarnessConstants.Actors.Ai; // node_judge를 기다린다(verdict null)
                }
            }
            else
            {
                visit.EndSeq = _timeline.LastSeq;
                verdict = RunJudge(node, visit);
                judgedBy = HarnessConstants.Actors.Harness;
            }

            var after = verdict is { Passed: true } ? node.Apply(stateBefore, state.Definition.InitialState) : stateBefore;
            visit.Finish(verdict, judgedBy, _timeline.LastSeq, DateTimeOffset.Now, after);
        }

        // 이 노드가 건 지속 조작(배속, 설정값 ...)은 노드가 끝나면 되돌린다 - 판정이 끝난 뒤라 판정 재료에는 안 들어간다.
        if (_overrides != null) await _overrides.RevertOwnedBy(path, ct);

        _timeline.RecordHarness(HarnessConstants.HarnessEvents.ScenarioNodeEnd, $"노드 판정: {path} {visit.Verdict?.Result.ToString() ?? "AI 판정 대기"}",
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
            if (_phase is Phase.Idle or Phase.Finishing or Phase.Finished || _state == null) Reject(HarnessConstants.Tools.ScenarioFinish, PhaseError(), null);
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
            _timeline.RecordHarness(HarnessConstants.HarnessEvents.ScenarioFinishing, $"시나리오 마무리: {reason}", new { scenario = state.Definition.Name, reason, how });
        }

        // 시나리오 동안 남은 지속 조작(AI가 건 것, 노드가 남긴 것)은 정리 노드 전에 되돌린다.
        if (_overrides != null) await _overrides.RevertAll(HarnessConstants.OverrideReasons.ScenarioEnd, ct);

        // 게임 코드 상황 로그 목록을 기본값(전부)으로 - 끝나는 즉시(정리 노드 전, 대상이 아직 붙어 있을 때).
        if (state.Definition.LogFilter != null)
        {
            foreach (var target in state.Definition.Targets) await SendLogFilterAsync(target.Name, null, ct);
        }

        var cleanupId = state.Definition.Cleanup.Id;
        MarkNode(HarnessConstants.NodeMarker.StartFormat, cleanupId, HarnessConstants.NodeMarker.CleanupVisit);
        var cleanup = await ExecuteAsync(state, state.Definition.Cleanup, cleanupId, "마무리", HarnessConstants.Actors.Harness, state.CurrentState, ct, stoppable: false);
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
            _timeline.RecordHarness(HarnessConstants.HarnessEvents.ScenarioEnd, $"시나리오 종료: {state.Definition.Name} ({reason})",
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
        state.Finished.TrySetResult(result); // 자동 실행을 일찍 끝낸 scenario_finish가 이 응답을 기다린다
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
            _timeline.RecordHarness(HarnessConstants.HarnessEvents.ScenarioRunKept, $"실행을 닫지 않음: {blocker}", new { runId = state.Run.Id, blocker });
            return false;
        }
        _runs.End();
        return true;
    }

    /// <summary>전부 통과(모든 방문 + 정리)이고 AI 지시 없음으로 끝난 게 아니면 true(완료음), 아니면 경고음.</summary>
    private static bool AllPassed(RunState state) =>
        state.FinishHow != HarnessConstants.FinishHow.Idle && state.Visits.Count > 0 &&
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
            try { await FinishAsync($"AI 지시 없음({state.Definition.IdleTimeout.TotalSeconds:0}초) - 마지막 지시 {last:HH:mm:ss}", HarnessConstants.FinishHow.Idle, CancellationToken.None); }
            catch (HarnessException) { /* 그 사이 다른 경로로 끝남 */ }
        });
    }

    // ================================ 환경 변화 / AI 활동 ================================

    private void OnRecorded(TimelineEvent e)
    {
        var change = TH_EnvironmentChanges.Describe(e);

        // 시나리오 도중 선언된 대상이 (다시) 붙으면 게임 코드 로그 목록을 보낸다 - Unity는 Play/도메인 리로드/재접속 때마다 새로 붙는다.
        if (e.Name == HarnessSharedConstants.Events.HarnessAttached && e.Source.StartsWith(HarnessConstants.Sources.AgentPrefix, StringComparison.Ordinal))
        {
            TH_LogFilter? filter = null;
            string? agent = null;
            RunState? attachedTo = null;
            lock (_lock)
            {
                if (_state is { } running && _phase is Phase.Ready or Phase.Running or Phase.AwaitingJudge)
                {
                    var name = e.Source[HarnessConstants.Sources.AgentPrefix.Length..];
                    if (running.Definition.Targets.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        filter = running.Definition.LogFilter;
                        agent = name;
                        attachedTo = running;
                    }
                }
            }
            if (agent != null && attachedTo != null)
            {
                CheckProbesOnAttach(attachedTo, agent);
                if (filter != null) _ = Task.Run(() => SendLogFilterAsync(agent, filter, CancellationToken.None));
            }
        }

        lock (_lock)
        {
            if (_state is not { } state) return;
            if (e.Source == HarnessConstants.Sources.Ai && e.Name == HarnessConstants.HarnessEvents.McpCall) state.LastAiActivity = DateTimeOffset.UtcNow;
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
        _timeline.RecordHarness(HarnessConstants.HarnessEvents.ScenarioEnvironmentChanged, $"환경 #{state.Environment}: {cause}",
            new { scenario = state.Definition.Name, environment = state.Environment, cause, causeSeq = seq });
    }

}
