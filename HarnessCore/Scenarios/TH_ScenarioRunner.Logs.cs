using System.Text;

namespace TestHarness.Core.Scenarios;

/// <summary>진행기의 조회 선언(Probes) 확인, 노드 경계 줄, 게임 코드 로그 목록(log.filter) - TH_ScenarioRunner.cs의 나머지.</summary>
public sealed partial class TH_ScenarioRunner
{
    // ================================ 조회 선언(Probes) ================================

    /// <summary>
    /// 선언한 조회 중 접속 중인 대상에 없는 것 - 있으면 그 문장(없으면 null)을 돌려주고 타임라인에 남긴다. 아직 안 붙은 대상은 비교하지 않는다
    /// (붙는 순간 CheckProbesOnAttach가 본다. 안 붙은 채 부르면 도구가 "에이전트가 없음"으로 실패한다).
    /// </summary>
    private string? MissingProbes(RunState state, string where, IEnumerable<(TH_Target Target, string Command)> probes)
    {
        if (_agentCommands == null) return null;
        var missing = probes
            .Where(p => _agentCommands(p.Target.Name) is { } commands && !commands.Contains(p.Command))
            .Select(p => string.Format(HarnessSharedConstants.Errors.ProbeMissingFormat, p.Command, p.Target.Name))
            .Distinct()
            .ToList();
        if (missing.Count == 0) return null;

        var text = string.Join(" / ", missing);
        _timeline.RecordHarness(HarnessConstants.HarnessEvents.ScenarioProbeMissing, $"{where}: {text}",
            new { scenario = state.Definition.Name, where, missing });
        return text;
    }

    /// <summary>대상이 붙는 순간 - 시나리오의 모든 노드(묶음의 자식 포함)가 그 대상에 선언한 조회를 비교해 없는 것을 미리 남긴다.</summary>
    private void CheckProbesOnAttach(RunState state, string agent)
    {
        var probes = AllNodes(state.Definition.Nodes.Append(state.Definition.Cleanup))
            .SelectMany(n => n.ProbedCommands)
            .Where(p => string.Equals(p.Target.Name, agent, StringComparison.OrdinalIgnoreCase));
        MissingProbes(state, $"{agent} 접속", probes);
    }

    private static IEnumerable<TH_Node> AllNodes(IEnumerable<TH_Node> nodes) =>
        nodes.SelectMany(n => new[] { n }.Concat(AllNodes(n.Children)));

    // ================================ 노드 경계 줄 / 게임 코드 로그 목록 ================================

    private void MarkNode(string format, string path, string visit, string? verdict = null) =>
        _logMarker?.Invoke(string.Format(format, path, visit, verdict));

    private static string VerdictText(VisitRecord visit) => visit.Verdict?.Result.ToString() ?? HarnessConstants.NodeMarker.AwaitingAiJudge;

    /// <summary>
    /// 대상 하나에 게임 코드 상황 로그 목록을 보낸다(filter null = 전부로 되돌림). log.filter가 없는 대상(서버, 헤드리스)이나 아직 안 붙은
    /// 대상은 실패한다 - 붙어 있는 Unity 대상에서 난 실패만 타임라인에 남긴다(진행은 막지 않는다).
    /// </summary>
    private async Task SendLogFilterAsync(string agent, TH_LogFilter? filter, CancellationToken ct)
    {
        if (Tools == null) return;
        var args = new Dictionary<string, object?>
        {
            [HarnessSharedConstants.LogFilter.CollectArg] = Join(filter?.Collect),
            [HarnessSharedConstants.LogFilter.ConsoleArg] = Join(filter?.Console),
        };
        try
        {
            var result = await Tools.InvokeAsync(HarnessConstants.Tools.AgentCommand,
                HarnessJson.Serialize(new { agent, command = HarnessSharedConstants.Commands.LogFilter, args }), CurrentNodePath, ct);
            // 도구 오류 = 에이전트가 아직 안 붙음(붙으면 OnRecorded가 다시 보냄). 모르는 명령 = log.filter가 없는 대상(서버, 헤드리스).
            if (result.IsError) return;
            if (result.Value.Bool("ok")) return;
            var error = result.Value.Str("error") ?? "";
            if (error.Contains(HarnessSharedConstants.Errors.UnknownCommand, StringComparison.Ordinal)) return;
            _timeline.RecordHarness(HarnessConstants.HarnessEvents.ScenarioLogFilterFailed, $"게임 코드 로그 목록을 {agent}에 못 보냄: {HarnessJson.Truncate(error, 300)}",
                new { agent, error });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _timeline.RecordHarness(HarnessConstants.HarnessEvents.ScenarioLogFilterFailed, $"게임 코드 로그 목록을 {agent}에 못 보냄: {ex.Message}", new { agent, error = ex.Message });
        }

        static string Join(IReadOnlyList<string>? names) => names switch
        {
            null => "",
            { Count: 0 } => HarnessSharedConstants.LogFilter.None,
            _ => string.Join(HarnessSharedConstants.LogFilter.Separator, names),
        };
    }
}
