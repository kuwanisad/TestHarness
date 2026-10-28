using System.Text.Json;
using TestHarness.Core.Scenarios;

namespace TestHarness.Core;

/// <summary>
/// 지속 조작 종류 하나 - 한 번 보내면 끝나는 명령이 아니라 켜 두면 남는 조작(배속, 설정값, 장애 규칙 ...).
/// 종류마다 정하는 것은 "어느 명령이 걸기인지, 무엇의 조작인지(범위), 걸기 전/후 값, 되돌리는 인자"뿐이고 기록, 수명, 표시는
/// <see cref="OverrideManager"/>가 모든 종류에 같은 경로로 한다.
/// </summary>
/// <param name="Name">종류 이름(보고서/상태 표시).</param>
/// <param name="Command">걸기와 되돌리기에 쓰는 에이전트 명령.</param>
/// <param name="Matches">이 호출(명령 인자)이 이 종류의 조작인지(같은 명령의 다른 모드는 아님 - 예: CurrentOnly는 설정값을 안 바꿈).</param>
/// <param name="Scope">무엇의 조작인지(명령 결과 값에서) - 프로세스 전체면 "", 방이면 roomId. 같은 에이전트·범위에 다시 걸면 하나로 본다.</param>
/// <param name="Before">걸기 전 값(명령 결과에서). 처음 걸 때의 이 값으로 되돌린다.</param>
/// <param name="After">건 뒤의 값. 처음 값(Before)과 같아지면 꺼진 것(되돌려진 것).</param>
/// <param name="RevertArgs">되돌리는 명령 인자(범위, 처음 값).</param>
/// <param name="ScopeEnded">이 타임라인 이벤트가 그 범위가 사라진 것이면 그 범위(예: room.closed → roomId). 사라진 범위의 조작은 되돌릴 필요가 없다.</param>
/// <param name="RevertCommand">되돌리는 명령이 걸기와 다르면(예: fault.add ↔ fault.remove). null이면 Command.</param>
/// <param name="EndedByCall">이 명령 호출(이름, 인자)이 그 범위를 끄는 것이면 그 범위(되돌리는 명령이 따로 있는 종류). null이면 값 비교(After == Before)로만.</param>
/// <param name="HitEvent">이 이벤트가 그 조작이 실제로 걸린 것이면 그 범위(예: fault.hit → ruleId). 있으면 걸린 횟수를 세고, 한 번도 안 걸린 채
/// 끝나면 override.unused로 알린다("걸었다고 생각했는데 아무 일도 없었다"를 기록으로 드러냄).</param>
/// <param name="Probe">지금 값을 다시 읽는 법(<see cref="OverrideProbe"/>) - AI가 상태를 볼 때 부르는 조회 명령 그대로. 기록만으로 모를 때만 쓴다
/// (되돌리기가 실패해 "확인 대기"인 조작을, 그 조작을 Forbids/Needs로 선언한 노드가 시작하기 직전에). null이면 확인할 수 없음(남아 있다고 본다).</param>
/// <param name="EndsWithAgent">대상 에이전트가 끊기면(harness.detached) 이 조작도 같이 사라지는지(기본 true - 배속처럼 프로세스/Play 세션 안의 값).
/// false면 끊겨도 추적을 계속하고 시나리오 끝에 되돌린다(에디터 설정처럼 도메인 리로드나 재접속을 지나도 남는 값).</param>
/// <param name="ValueEvent">이 이벤트가 그 조작의 지금 값을 알리면 (값, 원인)(범위 없는 종류 - 대상 에이전트 출처). 명령 없이 값이 바뀌어도(예:
/// 재진입으로 끊김이 풀림) 그 시각에 기록을 맞춘다 - 처음 값이면 끝, 아니면 값만 바꿈.</param>
public sealed record OverrideKind(
    string Name,
    string Command,
    string Description,
    Func<JsonElement, bool> Matches,
    Func<JsonElement, string> Scope,
    Func<JsonElement, string?> Before,
    Func<JsonElement, string?> After,
    Func<string, string, object> RevertArgs,
    Func<TimelineEvent, string?>? ScopeEnded = null,
    string? RevertCommand = null,
    Func<string, JsonElement, string?>? EndedByCall = null,
    Func<TimelineEvent, string?>? HitEvent = null,
    OverrideProbe? Probe = null,
    bool EndsWithAgent = true,
    Func<TimelineEvent, (string Value, string Cause)?>? ValueEvent = null);

/// <summary>
/// 지속 조작의 지금 값을 다시 읽는 법. Command는 AI도 상태를 볼 때 부르는 조회 명령(state/list)이다 - 하네스 전용 조회 명령을 따로 만들지 않는다
/// (값이 어느 조회에도 안 나오면 그 대상의 state/list 명령에 칸을 더한다). "사라짐"은 오류 문장이 아니라 목록에 없음으로 판단한다.
/// </summary>
/// <param name="Command">조회 명령(조작을 건 에이전트에 보낸다).</param>
/// <param name="Read">(조회 응답 값, 범위) → 지금 값 또는 사라짐.</param>
/// <param name="Args">조회 인자(범위로). null이면 인자 없음.</param>
public sealed record OverrideProbe(string Command, Func<JsonElement, string, OverrideProbeResult> Read, Func<string, object?>? Args = null);

/// <summary>조회 결과 - 사라짐(범위가 목록에 없음) 또는 지금 값.</summary>
public readonly record struct OverrideProbeResult(bool Gone, string? Value)
{
    public static OverrideProbeResult Of(string? value) => new(false, value);
    public static readonly OverrideProbeResult GoneResult = new(true, null);
}

/// <summary>
/// 지속 조작 종류 목록. 종류(어느 명령이 걸기인지, 값을 어떻게 읽는지)는 그 게임/에이전트의 지식이라 시나리오 어셈블리가 등록한다
/// (ITH_GameKnowledge - HarnessScenarios/Knowledge). HarnessCore는 기록, 수명, 표시(OverrideManager)만 맡는다. 테스트 조작이라 환경 변화
/// (테스트 대상의 변화)가 아니다.
/// </summary>
public static class OverrideKinds
{
    private static readonly object Lock = new();
    private static IReadOnlyList<OverrideKind> _all = Array.Empty<OverrideKind>();

    public static IReadOnlyList<OverrideKind> All
    {
        get { lock (Lock) return _all; }
    }

    /// <summary>종류를 등록한다(하네스 시작 때 - 시나리오 카탈로그 등록 중). 같은 이름을 두 번 등록하면 오류.</summary>
    public static void Register(OverrideKind kind)
    {
        lock (Lock)
        {
            if (_all.Any(k => string.Equals(k.Name, kind.Name, StringComparison.Ordinal)))
                throw new InvalidOperationException($"지속 조작 종류 이름 중복: {kind.Name}");
            _all = _all.Append(kind).ToArray();
        }
    }

    /// <summary>이름으로 종류 찾기(노드의 Forbids/Needs 선언 검사).</summary>
    public static OverrideKind? ByName(string name) => All.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.Ordinal));

    public static OverrideKind? Find(string command, JsonElement args) =>
        All.FirstOrDefault(k => k.Command == command && k.Matches(args));
}
