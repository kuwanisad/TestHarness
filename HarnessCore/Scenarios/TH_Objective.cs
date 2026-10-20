using System.Text.Json;

namespace TestHarness.Core.Scenarios;

// 시나리오 틀(TH_ = TestHarness). 시나리오 = 목표(노드)들의 그래프. 하네스가 목표를 하나씩(또는 분기 후보를) 내주고,
// AI가 고르고(objective_begin) → 수행하고 → 보고하면(objective_report) 하네스가 판정해 다음 목표를 내준다.
// 진행 규칙과 기록은 TH_ScenarioRunner, 시나리오 작성법은 TH_Scenario 주석.

/// <summary>목표 하나의 판정 결과 종류.</summary>
public enum TH_Result
{
    /// <summary>통과 기준을 만족.</summary>
    Pass,

    /// <summary>통과 기준을 만족하지 못함.</summary>
    Fail,

    /// <summary>판단할 수 없음(근거 부족, 확인 불가) - 모르는 걸 채우는 대신 쓰는 출구.</summary>
    Unverified,
}

/// <summary>통과하지 못했을 때(Fail/Unverified) 시나리오를 어떻게 할지.</summary>
public enum TH_FailPolicy
{
    /// <summary>뒤 목표가 이 결과에 의존 - 시나리오를 멈추고 정리 목표로.</summary>
    Stop,

    /// <summary>뒤 목표와 독립 - 기록만 하고 다음으로.</summary>
    Continue,
}

/// <summary>판정 하나: 결과, 이유, 근거 이벤트 번호.</summary>
public sealed record TH_Verdict(TH_Result Result, string Reason, IReadOnlyList<long> EvidenceSeqs)
{
    public bool Passed => Result == TH_Result.Pass;

    public static TH_Verdict Pass(string reason, params long[] evidence) => new(TH_Result.Pass, reason, evidence);
    public static TH_Verdict Fail(string reason, params long[] evidence) => new(TH_Result.Fail, reason, evidence);
    public static TH_Verdict Unverified(string reason, params long[] evidence) => new(TH_Result.Unverified, reason, evidence);

    /// <summary>조건이 참이면 Pass, 아니면 Fail(같은 근거).</summary>
    public static TH_Verdict PassIf(bool condition, string passReason, string failReason, params long[] evidence) =>
        condition ? Pass(passReason, evidence) : Fail(failReason, evidence);
}

/// <summary>
/// 목표(노드) 하나. AI에게 보이는 것은 Goal / PassCriteria / Hints뿐이다. Judge가 있으면 하네스가 기록(이 목표 구간의
/// 이벤트와 AI의 도구 호출)을 보고 스스로 판정하고, 없으면 AI의 보고(근거 이벤트 번호 필수)를 결과로 쓴다.
/// 만들기는 TH_ScenarioBuilder.Objective로만 한다.
/// </summary>
public sealed class TH_Objective
{
    internal TH_Objective(string id, string goal, string passCriteria, Func<TH_JudgeContext, TH_Verdict>? judge,
        TH_FailPolicy onFail, IReadOnlyList<string> hints, bool isCleanup)
    {
        Id = id;
        Goal = goal;
        PassCriteria = passCriteria;
        Judge = judge;
        OnFail = onFail;
        Hints = hints;
        IsCleanup = isCleanup;
    }

    public string Id { get; }

    /// <summary>AI에게 보여 줄 "무엇을 할지".</summary>
    public string Goal { get; }

    /// <summary>AI에게 보여 줄 "언제 통과인지" - 관찰 가능한 사실로 쓴다.</summary>
    public string PassCriteria { get; }

    /// <summary>하네스 판정. null이면 AI 판정(근거 필수).</summary>
    public Func<TH_JudgeContext, TH_Verdict>? Judge { get; }

    public TH_FailPolicy OnFail { get; }

    /// <summary>관련 명령/도구 힌트(제한이 아니라 출발점 - 다른 도구도 언제든 쓸 수 있다).</summary>
    public IReadOnlyList<string> Hints { get; }

    /// <summary>정리 목표(시나리오가 끝나거나 중단돼도 반드시 마지막에 내준다).</summary>
    public bool IsCleanup { get; }

    /// <summary>다음 목표 정하기. null이면 다음 없음(정리 목표로).</summary>
    internal Func<TH_JudgeContext, TH_Verdict, IReadOnlyList<TH_Objective>>? NextSelector { get; private set; }

    /// <summary>다음 후보가 여럿일 때 AI가 고르는 기준(선택 분기 - 고른 이유를 objective_begin에 적게 한다).</summary>
    internal string? ChoiceGuide { get; private set; }

    /// <summary>판정과 상관없이 항상 이 목표로.</summary>
    public TH_Objective Then(TH_Objective next)
    {
        ChoiceGuide = null;
        NextSelector = (_, _) => new[] { next };
        return this;
    }

    /// <summary>후보 여럿 중 AI가 고르는 선택 분기. guide에 "무엇을 보고 고르는지"를 쓴다(고른 이유는 objective_begin의
    /// reason으로 받아 기록). 하네스가 기록으로 정할 수 있으면 결과 분기(Then(select))를 쓸 것.</summary>
    public TH_Objective ThenChoice(string guide, params TH_Objective[] candidates)
    {
        if (string.IsNullOrWhiteSpace(guide)) throw new InvalidOperationException($"목표 {Id}: 선택 분기에는 고르는 기준(guide)이 필요함");
        if (candidates.Length < 2) throw new InvalidOperationException($"목표 {Id}: 선택 분기 후보는 둘 이상");
        ChoiceGuide = guide;
        NextSelector = (_, _) => candidates;
        return this;
    }

    /// <summary>결과 분기 - 판정과 기록을 보고 하네스가 다음 목표를 정한다(가능하면 선택 분기보다 이쪽).</summary>
    public TH_Objective Then(Func<TH_JudgeContext, TH_Verdict, TH_Objective?> select)
    {
        NextSelector = (context, verdict) => select(context, verdict) is { } next ? new[] { next } : Array.Empty<TH_Objective>();
        return this;
    }
}

/// <summary>JSON 값 읽기 도우미(판정 코드를 짧게 하려고). 없거나 타입이 다르면 기본값.</summary>
public static class TH_Json
{
    public static JsonElement? Get(this JsonElement element, string path)
    {
        var current = element;
        foreach (var part in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out var next)) return null;
            current = next;
        }
        return current;
    }

    public static string? Str(this JsonElement element, string path) =>
        element.Get(path) is { } v ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : null;

    public static bool Bool(this JsonElement element, string path) =>
        element.Get(path) is { ValueKind: JsonValueKind.True };

    public static double? Num(this JsonElement element, string path) =>
        element.Get(path) is { ValueKind: JsonValueKind.Number } v ? v.GetDouble() : null;
}
