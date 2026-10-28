namespace TestHarness.Core.Scenarios;

/// <summary>
/// 그 프로젝트(게임과 그 테스트 에이전트)의 지식을 하네스에 등록한다 - 지속 조작 종류, 명령 영역, 환경 변화로 볼 에이전트 이벤트와 데이터 변경
/// 명령, 노드 밖 이상으로 볼 이벤트. HarnessCore는 틀만 갖고 내용은 시나리오 어셈블리(HarnessScenarios/Knowledge)가 이 인터페이스로 넣는다.
/// 시나리오 카탈로그가 어셈블리를 등록할 때 시나리오보다 먼저 부른다(노드의 Forbids/Needs 검사가 등록된 종류를 본다).
/// </summary>
public interface ITH_GameKnowledge
{
    void Register(TH_GameKnowledge knowledge);
}

/// <summary>ITH_GameKnowledge가 받는 등록 창구 - 각 목록(OverrideKinds, CommandAreas, TH_EnvironmentChanges, TH_OutsideEvents)에 넣는다.</summary>
public sealed class TH_GameKnowledge
{
    internal TH_GameKnowledge()
    {
    }

    public TH_GameKnowledge OverrideKind(OverrideKind kind)
    {
        OverrideKinds.Register(kind);
        return this;
    }

    public TH_GameKnowledge CommandArea(CommandArea area)
    {
        CommandAreas.Register(area);
        return this;
    }

    /// <summary>에이전트 이벤트 중 테스트 대상의 변화(변화면 설명, 아니면 null).</summary>
    public TH_GameKnowledge EnvironmentEvent(Func<TimelineEvent, string?> describe)
    {
        TH_EnvironmentChanges.RegisterAgentEvent(describe);
        return this;
    }

    /// <summary>테스트 대상의 데이터를 바꾸는 에이전트 명령.</summary>
    public TH_GameKnowledge DataCommand(string command)
    {
        TH_EnvironmentChanges.RegisterDataCommand(command);
        return this;
    }

    /// <summary>노드 밖에서 일어나면 보고서 머리에 경고로 올릴 이벤트.</summary>
    public TH_GameKnowledge OutsideEvent(string name, string what, Func<TimelineEvent, IReadOnlySet<string>, bool> applies)
    {
        TH_OutsideEvents.Register(name, what, applies);
        return this;
    }
}
