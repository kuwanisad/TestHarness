using System.Text;

namespace TestHarness.Core.Scenarios;

/// <summary>진행기의 상태 기록(시나리오 하나의 진행, 방문 하나, 준비 기록) - TH_ScenarioRunner.cs의 나머지.</summary>
public sealed partial class TH_ScenarioRunner
{
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

        /// <summary>
        /// 일찍 끝내기(scenario_finish) - 실행 중인 노드(자동 실행이면 AutoPlan까지)를 멈추는 신호. 행동 노드의 실행 코드가 받는 토큰에 묶인다
        /// (정리 노드는 묶지 않는다). StopReason이 있으면 노드를 더 돌리지 않는다.
        /// </summary>
        public CancellationTokenSource Stop { get; } = new();
        public string? StopReason { get; set; }

        /// <summary>멈춤 신호를 받았을 때 실행 중이던 노드 경로(없으면 null - 노드 사이에 끝냄).</summary>
        public string? StoppedPath { get; set; }

        /// <summary>실행 중인 최상위 노드가 끝나면(어떤 결과든) 완료 - 일찍 끝내기가 노드가 멈출 때까지 기다린다.</summary>
        public TaskCompletionSource? NodeDone { get; set; }

        /// <summary>마무리(정리 노드 → 보고서)가 끝나면 그 응답 - 자동 실행을 일찍 끝낼 때 scenario_finish가 기다린다.</summary>
        public TaskCompletionSource<object> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
