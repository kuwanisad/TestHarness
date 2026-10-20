using System.ComponentModel;
using TestHarness.Core;
using TestHarness.Core.Scenarios;
using TestHarness.Daemon.Mcp;

namespace TestHarness.Daemon.Tools;

/// <summary>
/// 시나리오(목표 그래프) 진행. 하네스가 목표를 내주고 → AI가 고르고(objective_begin) → 수행하고 → 보고하면
/// (objective_report) 하네스가 판정해 다음 목표를 내준다. 순서는 하네스가 통제하고(어기면 거부 + expect),
/// 수행 중의 다른 도구 호출은 자유다(전부 그 목표에 묶여 기록됨). 기능은 HarnessCore의 TH_ScenarioRunner.
/// </summary>
public sealed class ScenarioTools
{
    private readonly HarnessSession _session;

    public ScenarioTools(HarnessSession session) => _session = session;

    private TH_ScenarioRunner Runner => _session.Scenarios;

    [McpTool("scenario_list")]
    [Description("실행할 수 있는 시나리오(검증 절차) 목록과, 진행 중인 시나리오가 있으면 그 상태. 사람이 '검증해 봐/테스트 돌려 봐'라고 하면 " +
                 "여기서 맞는 시나리오를 고르고 scenario_start로 시작할 것.")]
    public object ScenarioList() => Runner.List();

    [McpTool("scenario_start")]
    [Description("시나리오를 시작한다. 결과: 전제(preconditions), 주의(cautions), 진행 규칙(rules) - 이건 이때 한 번만 준다 - 와 첫 목표, " +
                 "다음에 할 행동(expect). 이후 모든 시나리오 도구 응답의 expect를 따를 것. 실행(run)이 없으면 자동으로 연다.")]
    public object ScenarioStart([Description("시나리오 이름(scenario_list의 name)")] string name) => Runner.Start(name);

    [McpTool("objective_begin")]
    [Description("제시된 목표 중 하나를 고른다(하네스의 질문에 대한 대답). 결과: 목표 내용(goal, passCriteria, judgedBy, hints)과 sinceSeq - " +
                 "이 뒤의 이벤트만 이 목표의 근거가 된다. 후보가 여럿이면 reason에 고른 이유를 쓸 것. 제시되지 않은 목표는 거부된다.")]
    public object ObjectiveBegin(
        [Description("목표 ID(expect.choices 중 하나)")] string objective,
        [Description("고른 이유(후보가 여럿일 때 필수)")] string? reason = null) =>
        Runner.Begin(objective, reason);

    [McpTool("objective_report")]
    [Description("진행 중인 목표의 결과를 보고한다. judgedBy=harness인 목표는 하네스가 기록으로 판정하고 AI 보고와 다르면 불일치로 남긴다. " +
                 "judgedBy=ai인 목표는 Pass에 근거 이벤트 번호(evidenceSeqs - 이 목표의 sinceSeq 뒤, events_query/wait_for의 seq)가 필수. " +
                 "판단할 수 없으면 Unverified + note. 결과: 판정(recorded)과 다음 목표(offer), expect. 마지막(정리) 목표면 보고서 경로.")]
    public object ObjectiveReport(
        [Description("목표 ID(진행 중인 것)")] string objective,
        [Description("Pass | Fail | Unverified")] TH_Result result,
        [Description("근거 이벤트 번호들(seq)")] long[]? evidenceSeqs = null,
        [Description("한 일과 관찰 요약, 하네스 밖에서 한 일(코드 수정 등)")] string? note = null) =>
        Runner.Report(objective, result, evidenceSeqs, note);

    [McpTool("scenario_abort")]
    [Description("시나리오를 더 진행할 수 없을 때 보고한다(예: 서버가 안 뜸, 같은 실패 반복). 남은 목표는 실행 안 함으로 기록되고 정리 목표만 " +
                 "나온다(정리 중이면 바로 끝남). 이상 현상을 조사하는 것 자체는 중단 사유가 아니다 - 조사는 목표 수행 중에 자유롭게 할 것.")]
    public object ScenarioAbort(
        [Description("중단 사유(한 줄)")] string reason,
        [Description("현황 - 어디까지 했고 무엇이 관찰됐는지(근거 seq 포함)")] string situation) =>
        Runner.Abort(reason, situation);
}
