using System.ComponentModel;
using TestHarness.Core;
using TestHarness.Core.Scenarios;
using TestHarness.Daemon.Mcp;

namespace TestHarness.Daemon.Tools;

/// <summary>
/// 시나리오(노드 그래프) 진행. 노드는 하네스가 실행하고 판정한다 - AI는 전체 그래프를 보고 다음 노드를 고르고(node_run),
/// 충분하면 끝낸다(scenario_finish). 순서는 하네스가 통제하고(어기면 거부 + expect), 노드 사이의 조사용 도구 호출은 자유다.
/// 기능은 HarnessCore의 TH_ScenarioRunner.
/// </summary>
public sealed class ScenarioTools
{
    private readonly HarnessSession _session;

    public ScenarioTools(HarnessSession session) => _session = session;

    private TH_ScenarioRunner Runner => _session.Scenarios;

    [McpTool(HarnessConstants.Tools.ScenarioList)]
    [Description("실행할 수 있는 시나리오(검증 절차) 목록과, 진행 중인 시나리오가 있으면 그 상태. 사람이 '검증해 봐/테스트 돌려 봐'라고 하면 " +
                 "여기서 맞는 시나리오를 고르고 scenario_start로 시작할 것.")]
    public object ScenarioList() => Runner.List();

    [McpTool(HarnessConstants.Tools.ScenarioStart)]
    [Description("시나리오를 시작한다. mode=ai(기본): 맥락(context), 전제, 주의, 진행 규칙(rules) - 이건 이때 한 번만 준다 - 과 전체 그래프(graph), " +
                 "지금 상태, 고를 수 있는 노드(available), 다음 행동(expect)을 돌려준다. mode=auto: 하네스가 정해진 경로로 끝까지 돌고 " +
                 "결과 요약과 보고서 경로를 돌려준다(오래 걸리니 pending이 오면 call_continue로 기다릴 것). 실행(run)이 없으면 자동으로 연다.")]
    public Task<object> ScenarioStart(
        [Description("시나리오 이름(scenario_list의 name)")] string name,
        [Description("ai(AI가 노드를 고름) | auto(하네스가 정해진 경로로 끝까지)")] TH_RunMode mode = TH_RunMode.Ai,
        CancellationToken ct = default) =>
        Runner.StartAsync(name, mode, ct);

    [McpTool(HarnessConstants.Tools.NodeRun)]
    [Description("다음 노드를 고른다(하네스의 질문에 대한 대답) - 하네스가 그 노드를 실행하고 판정한다(묶음 노드는 자식까지). 지금 상태에서 " +
                 "시작할 수 있는 노드(available)만 된다. reason에 고른 이유를 쓸 것(보고서에 남음). 결과: 판정, 바뀐 상태, 환경(environment - " +
                 "changes가 있으면 테스트 대상이 바뀐 것), 다음에 고를 수 있는 노드, expect. 오래 걸리면 pending - call_continue로 기다릴 것.")]
    public Task<object> NodeRun(
        [Description("노드 ID(available 중 하나)")] string node,
        [Description("고른 이유")] string? reason = null,
        CancellationToken ct = default) =>
        Runner.RunNodeAsync(node, reason, ct);

    [McpTool(HarnessConstants.Tools.NodeJudge)]
    [Description("AI 판정 노드(judgedBy=ai)를 실행한 뒤 판정을 보고한다 - 보고 전에는 다음 노드로 못 간다. Pass에는 근거 이벤트 번호" +
                 "(evidenceSeqs - 그 노드 구간 안, node_run 응답의 seqRange)가 필수. 판단할 수 없으면 Unverified + note.")]
    public object NodeJudge(
        [Description("노드 ID(판정을 기다리는 것)")] string node,
        [Description("Pass | Fail | Unverified")] TH_Result result,
        [Description("근거 이벤트 번호들(seq)")] long[]? evidenceSeqs = null,
        [Description("판단 근거 요약")] string? note = null) =>
        Runner.Judge(node, result, evidenceSeqs, note);

    [McpTool(HarnessConstants.Tools.ScenarioFinish)]
    [Description("시나리오를 끝낸다 - 맥락상 충분히 봤을 때, 또는 더 진행할 수 없을 때(특정 노드까지 갈 필요 없음). 하네스가 정리 노드를 " +
                 "실행하고 보고서를 쓴 뒤 결과와 보고서 경로를 돌려준다(사람에게 경로를 알릴 것). 노드 실행 중(또는 자동 실행 중)이면 일찍 끝내기: " +
                 "실행 중인 노드를 멈추고(Stopped - 판정 안 함) 노드를 더 돌리지 않은 채 마무리한다. 보고서는 실제로 돈 방문만으로 쓴다.")]
    public Task<object> ScenarioFinish(
        [Description("끝내는 이유(보고서에 남음)")] string reason,
        CancellationToken ct = default) =>
        Runner.FinishByAiAsync(reason, ct);
}
