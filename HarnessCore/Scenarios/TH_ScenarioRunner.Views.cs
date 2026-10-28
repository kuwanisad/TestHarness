using System.Text;

namespace TestHarness.Core.Scenarios;

/// <summary>진행기의 도구 응답 모양(그래프, 다음 노드, 방문, 요약, 거부) - TH_ScenarioRunner.cs의 나머지.</summary>
public sealed partial class TH_ScenarioRunner
{
    // ================================ 응답 모양 ================================

    private static string[] Rules(TH_ScenarioDefinition d) => new[]
    {
        "노드는 하네스가 실행하고 판정한다. AI는 node_run으로 다음 노드를 고른다 - 지금 상태에서 시작할 수 있는 것(available)만.",
        "맥락(context)을 보고 충분하다고 판단되면 scenario_finish로 끝낸다. 특정 노드까지 가거나 그래프를 다 돌 필요는 없다.",
        "노드 실행 중에도 scenario_finish로 일찍 끝낼 수 있다 - 실행 중인 노드는 멈추고(Stopped, 판정 안 함) 노드를 더 돌리지 않은 채 정리 노드와 보고서로 간다.",
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
        judgedBy = n.IsGroup ? HarnessConstants.Actors.HarnessByChildren : n.IsAiJudged ? HarnessConstants.Actors.Ai : HarnessConstants.Actors.Harness,
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
            Phase.Ready => new { actions = new[] { HarnessConstants.Tools.NodeRun, HarnessConstants.Tools.ScenarioFinish }, nodes = state.Definition.Nodes.Where(n => n.CanStartFrom(state.CurrentState)).Select(n => n.Id) },
            Phase.AwaitingJudge => new { actions = new[] { HarnessConstants.Tools.NodeJudge }, nodes = new[] { state.PendingJudge!.Node.Id } },
            Phase.Running => new { actions = new[] { "call_continue(실행 중인 호출)", $"{HarnessConstants.Tools.ScenarioFinish}(일찍 끝내기)" }, nodes = Array.Empty<string>() },
            Phase.Finishing => new { actions = new[] { "call_continue(정리 중)" }, nodes = Array.Empty<string>() },
            _ => (object)new { actions = Array.Empty<string>(), nodes = Array.Empty<string>() },
        };
    }

    private static object VisitView(VisitRecord v) => new
    {
        node = v.Path,
        environment = v.Environment,
        result = v.Verdict?.Result.ToString() ?? HarnessConstants.NodeMarker.AwaitingAiJudge,
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
        stopped = state.Visits.Count(v => v.Verdict?.Result == TH_Result.Stopped),
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
        Phase.Running => $"노드 실행 중({_state?.RunningPath}) - 실행 중인 호출을 call_continue로 기다리거나 scenario_finish로 일찍 끝낼 것",
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
}
