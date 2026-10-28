using System.Text.Json;
using TestHarness.Core.Scenarios;

namespace TestHarness.Core;

/// <summary>지속 조작 하나(켜진 것 또는 끝난 것).</summary>
public sealed class OverrideRecord
{
    internal OverrideRecord(string id, OverrideKind kind, string agent, string scope, string before, string after, long seq, string actor, string? owner)
    {
        Id = id;
        Kind = kind;
        Agent = agent;
        Scope = scope;
        Before = before;
        After = after;
        AppliedSeq = seq;
        AppliedAt = DateTimeOffset.Now;
        Actor = actor;
        Owner = owner;
    }

    public string Id { get; }
    public OverrideKind Kind { get; }
    public string Agent { get; }

    /// <summary>범위("" = 프로세스 전체, 아니면 roomId 등).</summary>
    public string Scope { get; }

    /// <summary>처음 걸기 전 값 - 이 값으로 되돌린다.</summary>
    public string Before { get; }

    /// <summary>지금 값(같은 범위에 다시 걸면 바뀜).</summary>
    public string After { get; internal set; }

    public long AppliedSeq { get; }
    public DateTimeOffset AppliedAt { get; }

    /// <summary>건 쪽: ai | runner(시나리오 노드 실행).</summary>
    public string Actor { get; }

    /// <summary>노드가 건 것이면 그 노드 경로 - 그 노드가 끝날 때 되돌린다. null이면 노드 소유가 아님.</summary>
    public string? Owner { get; internal set; }

    /// <summary>실제로 걸린 횟수(종류에 HitEvent가 있을 때 - 장애 규칙).</summary>
    public int Hits { get; internal set; }

    public bool Active => EndReason == null;
    public string? EndReason { get; internal set; }
    public DateTimeOffset? EndedAt { get; internal set; }
    public long? EndSeq { get; internal set; }

    /// <summary>
    /// 되돌리기가 실패해 지금 값을 모름(그 실패 이유). 실패로 끝내지 않고 켜 둔다 - 범위가 사라지는 이벤트(room.closed 등)나 에이전트 끊김이 오면
    /// 그대로 끝나고, 그 조작을 Forbids/Needs로 선언한 노드가 시작하기 직전에만 조회로 확인한다(<see cref="OverrideManager.ConfirmAsync"/>).
    /// </summary>
    public string? Unconfirmed { get; internal set; }

    /// <summary>마지막 되돌리기 실패 이유(끝난 뒤에도 남김 - 보고서에 "되돌리기 실패 뒤 범위 사라짐"처럼 보이게).</summary>
    public string? RevertError { get; internal set; }

    /// <summary>하네스가 되돌리는 중(그 명령의 기록이 들어오면 이 이유로 끝낸다).</summary>
    internal string? Reverting { get; set; }

    /// <summary>
    /// 주인이 아닌 노드가 이 조작의 값을 바꾼 경우 - (그 노드 경로, 그 노드가 바꾸기 직전 값). 그 노드가 끝나면 조작을 끝내지 않고
    /// 직전 값으로만 돌린다(예: AI가 배속 2로 둔 뒤 노드가 5로 올렸으면 노드 끝에 2로).
    /// </summary>
    internal List<(string Owner, string Prior)> Layers { get; } = new();

    /// <summary>하네스가 노드 직전 값으로 맞추는 중(그 명령을 새 몫으로 기록하지 않게).</summary>
    internal bool Adjusting { get; set; }

    public object View() => new
    {
        id = Id,
        kind = Kind.Name,
        agent = Agent,
        scope = Scope.Length == 0 ? null : Scope,
        value = $"{Before} → {After}",
        actor = Actor,
        owner = Owner,
        hits = Kind.HitEvent != null ? Hits : (int?)null,
        unconfirmed = Unconfirmed,
        appliedSeq = AppliedSeq,
        ended = EndReason,
    };
}

/// <summary>
/// 지속 조작 감시와 수명 관리. 전용 도구 없이 도구 호출 기록(ToolCallLog)을 지켜본다 - 노드 실행이든 AI든 agent_command로 목록
/// (<see cref="OverrideKinds"/>)에 있는 명령이 성공하면 "켜짐", 값이 처음 값으로 돌아가면 "꺼짐".
///
/// 수명(누가 걸었나로):
/// - 노드 실행 중(runner): 그 노드가 끝날 때 하네스가 되돌린다(<see cref="RevertOwnedBy"/>). 다음 노드까지 남기려면 노드가 KeepOverrides.
/// - 시나리오 진행 중 AI(또는 남긴 것): 시나리오가 끝날 때(정리 노드 전) 되돌린다(<see cref="RevertAll"/>).
/// - 시나리오 없이 AI: 직접 되돌리거나 대상이 사라질 때까지.
/// - 공통: 대상 프로세스가 끊기면(harness.detached) "대상과 함께 사라짐", 범위가 사라지면(방 닫힘) "범위 사라짐" - 기록만.
/// 바뀔 때마다 타임라인에 override.applied / override.changed / override.reverted.
/// </summary>
public sealed class OverrideManager : IDisposable
{
    private readonly Timeline _timeline;
    private readonly ToolCallLog _calls;
    private readonly object _lock = new();
    private readonly List<OverrideRecord> _records = new();
    private int _nextId;

    public OverrideManager(Timeline timeline, ToolCallLog calls)
    {
        _timeline = timeline;
        _calls = calls;
        _calls.Recorded += OnCall;
        _timeline.Recorded += OnEvent;
    }

    /// <summary>되돌리는 명령을 보내는 통로(노드 실행과 같은 것 - 입구가 연결). 없으면 되돌리지 못하고 기록만 한다.</summary>
    public ITH_ToolInvoker? Tools { get; set; }

    public IReadOnlyList<OverrideRecord> Active
    {
        get { lock (_lock) return _records.Where(r => r.Active).ToList(); }
    }

    /// <summary>seq 뒤에 걸린 것(끝난 것 포함) - 시나리오 보고서용.</summary>
    public IReadOnlyList<OverrideRecord> Since(long seq)
    {
        lock (_lock) return _records.Where(r => r.AppliedSeq > seq).ToList();
    }

    public void Dispose()
    {
        _calls.Recorded -= OnCall;
        _timeline.Recorded -= OnEvent;
    }

    /// <summary>그 노드(경로)가 건(바꾼) 켜진 조작을 노드 소유에서 뺀다 - 노드가 끝나도 남고 시나리오 끝에 되돌린다.</summary>
    public int Keep(string nodePath)
    {
        lock (_lock)
        {
            var owned = _records.Where(r => r.Active && r.Owner == nodePath).ToList();
            foreach (var record in owned) record.Owner = null;
            var layered = _records.Where(r => r.Active && r.Layers.RemoveAll(l => l.Owner == nodePath) > 0).ToList();
            return owned.Count + layered.Count;
        }
    }

    /// <summary>
    /// 그 노드(경로)가 건 켜진 조작을 되돌린다(노드가 끝날 때 진행기가 부른다). 다른 쪽이 건 조작의 값을 그 노드가 바꿨으면 노드 직전
    /// 값으로만 돌린다(조작은 원래 주인의 수명대로 남음).
    /// </summary>
    public async Task RevertOwnedBy(string nodePath, CancellationToken ct)
    {
        List<(OverrideRecord Record, string Prior)> layers;
        lock (_lock)
        {
            layers = _records.Where(r => r.Active && r.Owner != nodePath)
                .SelectMany(r => r.Layers.Where(l => l.Owner == nodePath).Select(l => (r, l.Prior)))
                .ToList();
            foreach (var (record, _) in layers) record.Layers.RemoveAll(l => l.Owner == nodePath);
        }

        foreach (var (record, prior) in layers)
        {
            if (record.After == prior) continue;
            record.Adjusting = true;
            var error = await Send(record, prior, $"노드 끝 - {record.Id}를 노드 직전 값으로", ct);
            record.Adjusting = false;
            if (error != null)
                _timeline.RecordHarness(HarnessConstants.HarnessEvents.OverrideRevertFailed, $"지속 조작 {record.Id}를 노드 직전 값({prior})으로 못 돌림: {error}", record.View());
        }

        await RevertWhere(r => r.Owner == nodePath, HarnessConstants.OverrideReasons.NodeEnd, ct);
    }

    /// <summary>켜진 조작을 전부 되돌린다(시나리오가 끝날 때 - 정리 노드 전에).</summary>
    public Task RevertAll(string reason, CancellationToken ct) => RevertWhere(_ => true, reason, ct);

    private async Task RevertWhere(Func<OverrideRecord, bool> which, string reason, CancellationToken ct)
    {
        List<OverrideRecord> targets;
        lock (_lock)
        {
            targets = _records.Where(r => r.Active && r.Reverting == null && which(r)).ToList();
            foreach (var record in targets) record.Reverting = reason;
        }

        foreach (var record in targets)
        {
            var error = await Send(record, record.Before, $"되돌리기 {record.Id} ({record.Kind.Name})", ct);
            if (error == null)
            {
                // 되돌림은 응답 값으로 판단한다 - 응답이 처음 값이면(또는 끄는 명령이면) 기록 감시(OnCall)가 이미 끝냈다. 명령은 ok인데 아직 켜져
                // 있으면 값이 안 돌아온 것이다(예: 전송이 없을 때의 net.link) - 되돌림으로 기록하지 않고 확인 대기로.
                bool notRestored;
                lock (_lock) notRestored = record.Active;
                if (!notRestored) continue;
                error = $"되돌리는 명령은 성공했지만 값이 처음 값이 아님(지금 {record.After}, 처음 {record.Before})";
            }

            // 실패 - 끝내지 않고 "확인 대기"로 켜 둔다(범위가 이미 사라졌을 수 있음 - 그 이벤트가 곧 오면 scope_gone으로 끝남).
            bool stillActive;
            lock (_lock)
            {
                stillActive = record.Active;
                record.Reverting = null;
                if (stillActive)
                {
                    record.Unconfirmed = error;
                    record.RevertError = error;
                    record.Owner = null; // 노드 소유에서 빠짐 - 시나리오 끝에 다시 되돌려 본다
                }
            }
            if (stillActive)
                _timeline.RecordHarness(HarnessConstants.HarnessEvents.OverrideUnconfirmed, $"지속 조작 {record.Id}({record.Kind.Name}{ScopeText(record)}) 되돌리기 실패 - 확인 대기: {error}", record.View());
        }
    }

    /// <summary>
    /// 그 종류들 중 "확인 대기"(되돌리기 실패)인 켜진 조작만 조회해서 정리한다 - 노드가 Forbids/Needs로 선언한 종류에 대해 그 노드 시작 직전에만
    /// 부른다(기록으로 확실한 조작은 조회하지 않는다). 사라졌으면 scope_gone, 처음 값이면 되돌려진 것으로 끝내고, 다른 값이면 남아 있음(확인됨).
    /// 조회 자체가 안 되면(명령 실패, Probe 없음) 확인 대기로 둔다 - 남아 있다고 본다.
    /// </summary>
    public async Task ConfirmAsync(IReadOnlyCollection<string> kinds, CancellationToken ct)
    {
        List<OverrideRecord> targets;
        lock (_lock) targets = _records.Where(r => r.Active && r.Unconfirmed != null && r.Kind.Probe != null && kinds.Contains(r.Kind.Name)).ToList();

        foreach (var record in targets)
        {
            var probe = record.Kind.Probe!;
            OverrideProbeResult? read = null;
            if (Tools != null)
            {
                try
                {
                    var args = HarnessJson.Serialize(new { agent = record.Agent, command = probe.Command, args = probe.Args?.Invoke(record.Scope) });
                    var result = await Tools.InvokeAsync(HarnessConstants.Tools.AgentCommand, args, $"확인 {record.Id} ({record.Kind.Name})", ct);
                    if (!result.IsError && result.Value.Bool("ok") && result.Value.Get("value") is { ValueKind: JsonValueKind.Object } value)
                        read = probe.Read(value, record.Scope);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    read = null;
                }
            }

            if (read is not { } r) continue; // 모름 - 확인 대기 그대로
            if (r.Gone)
            {
                End(record, $"{HarnessConstants.OverrideReasons.ScopeGone}(확인)", null);
            }
            else if (r.Value == record.Before)
            {
                End(record, $"{HarnessConstants.OverrideReasons.Manual}(확인 - 이미 처음 값)", null);
            }
            else
            {
                lock (_lock)
                {
                    if (!record.Active) continue;
                    record.Unconfirmed = null;
                    if (r.Value != null) record.After = r.Value;
                }
                _timeline.RecordHarness(HarnessConstants.HarnessEvents.OverrideConfirmed, $"지속 조작 {record.Id}({record.Kind.Name}{ScopeText(record)})가 남아 있음(확인): {record.After}", record.View());
            }
        }
    }

    /// <summary>그 조작을 value로 맞추는 명령을 보낸다. 실패하면 이유, 성공하면 null.</summary>
    private async Task<string?> Send(OverrideRecord record, string value, string objective, CancellationToken ct)
    {
        if (Tools == null) return "되돌리는 통로가 없음";
        try
        {
            var args = HarnessJson.Serialize(new
            {
                agent = record.Agent,
                command = record.Kind.RevertCommand ?? record.Kind.Command,
                args = record.Kind.RevertArgs(record.Scope, value),
            });
            var result = await Tools.InvokeAsync(HarnessConstants.Tools.AgentCommand, args, objective, ct);
            if (result.IsError) return result.ErrorText;
            return result.Value.Bool("ok") ? null : result.Value.Str("error") ?? "명령 실패";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    // ================================ 감시 ================================

    private void OnCall(ToolCallRecord call)
    {
        if (call.IsError || call.AgentCommand is not { } command || call.Result is not { } result || !result.Bool("ok")) return;
        var args = call.Arguments?.Get("args") is { ValueKind: JsonValueKind.Object } a ? a : HarnessJson.ToElement(new { });
        var agent = call.Arguments?.Str("agent") ?? "";

        // 끄는 명령이 따로 있는 종류(fault.remove 등)
        foreach (var endingKind in OverrideKinds.All.Where(k => k.EndedByCall != null))
        {
            if (endingKind.EndedByCall!(command, args) is not { } endedScope) continue;
            OverrideRecord? target;
            lock (_lock)
            {
                target = _records.FirstOrDefault(r => r.Active && r.Kind == endingKind && r.Scope == endedScope &&
                                                     string.Equals(r.Agent, agent, StringComparison.OrdinalIgnoreCase));
            }
            if (target != null) End(target, target.Reverting ?? HarnessConstants.OverrideReasons.Manual, call.Seq);
            return;
        }

        var kind = OverrideKinds.Find(command, args);
        if (kind == null || result.Get("value") is not { ValueKind: JsonValueKind.Object } value) return;

        var scope = kind.Scope(value);
        var before = kind.Before(value);
        var after = kind.After(value);
        if (before == null || after == null) return;

        OverrideRecord? applied = null;
        OverrideRecord? changed = null;
        OverrideRecord? ended = null;
        lock (_lock)
        {
            var existing = _records.FirstOrDefault(r => r.Active && r.Kind == kind &&
                                                       string.Equals(r.Agent, agent, StringComparison.OrdinalIgnoreCase) && r.Scope == scope);
            if (existing == null)
            {
                if (after == before) return; // 바뀐 게 없음
                var owner = call.Actor == HarnessConstants.Actors.Runner ? call.Objective : null;
                applied = new OverrideRecord($"o{++_nextId}", kind, agent, scope, before, after, call.Seq, call.Actor, owner);
                _records.Add(applied);
            }
            else if (after == existing.Before)
            {
                ended = existing;
            }
            else if (after == existing.After)
            {
                // 값이 그대로 - 바뀐 게 없다(예: 되돌리는 명령이 ok인데 값이 안 돌아옴 - RevertWhere가 확인 대기로 남긴다)
            }
            else
            {
                // 주인이 아닌 노드가 값을 바꾸면 그 노드의 몫을 기록해 둔다(노드 끝에 직전 값으로).
                if (!existing.Adjusting && existing.Reverting == null && call.Actor == HarnessConstants.Actors.Runner && call.Objective is { } node && node != existing.Owner &&
                    existing.Layers.All(l => l.Owner != node))
                    existing.Layers.Add((node, existing.After));
                existing.After = after;
                changed = existing;
            }
        }

        if (applied != null)
            _timeline.RecordHarness(HarnessConstants.HarnessEvents.OverrideApplied, $"지속 조작 켜짐 {applied.Id}: {kind.Name} {applied.Agent}{ScopeText(applied)} {before}→{after}", applied.View());
        if (changed != null)
            _timeline.RecordHarness(HarnessConstants.HarnessEvents.OverrideChanged, $"지속 조작 바뀜 {changed.Id}: {kind.Name} → {after}", changed.View());
        if (ended != null) End(ended, ended.Reverting ?? HarnessConstants.OverrideReasons.Manual, call.Seq);
    }

    private void OnEvent(TimelineEvent e)
    {
        if (!e.Source.StartsWith(HarnessConstants.Sources.AgentPrefix, StringComparison.Ordinal)) return;
        var agent = e.Source[HarnessConstants.Sources.AgentPrefix.Length..];

        List<OverrideRecord> gone;
        string reason;
        var restored = new List<(OverrideRecord Record, string Reason)>();
        var changedByEvent = new List<OverrideRecord>();
        lock (_lock)
        {
            var mine = _records.Where(r => r.Active && string.Equals(r.Agent, agent, StringComparison.OrdinalIgnoreCase)).ToList();

            // 값을 알리는 이벤트(ValueEvent) - 명령 없이 바뀐 값도 그 시각에 맞춘다. 명령이 일으킨 것(cause=link)은 명령 기록(OnCall)이 처리한다.
            foreach (var record in mine.Where(r => r.Scope.Length == 0 && r.Kind.ValueEvent != null && r.Reverting == null))
            {
                if (record.Kind.ValueEvent!(e) is not { } reported || reported.Cause == "link") continue;
                if (reported.Value == record.Before) restored.Add((record, $"restored({reported.Cause})"));
                else if (reported.Value != record.After)
                {
                    record.After = reported.Value;
                    changedByEvent.Add(record);
                }
            }
            foreach (var record in mine.Where(r => r.Scope.Length > 0 && r.Kind.HitEvent?.Invoke(e) == r.Scope)) record.Hits++;

            if (e.Name == "harness.detached")
            {
                gone = mine.Where(r => r.Kind.EndsWithAgent).ToList(); // 끊겨도 남는 종류(에디터 설정)는 계속 추적
                reason = HarnessConstants.OverrideReasons.AgentGone;
            }
            else
            {
                gone = mine.Where(r => r.Scope.Length > 0 && r.Kind.ScopeEnded?.Invoke(e) == r.Scope).ToList();
                reason = e.Data?.Str("reason") is { } why ? $"{HarnessConstants.OverrideReasons.ScopeGone}({why})" : HarnessConstants.OverrideReasons.ScopeGone;
            }
        }

        foreach (var record in gone) End(record, record.Reverting ?? reason, e.Seq);
        foreach (var (record, why) in restored) End(record, why, e.Seq);
        foreach (var record in changedByEvent)
            _timeline.RecordHarness(HarnessConstants.HarnessEvents.OverrideChanged, $"지속 조작 바뀜 {record.Id}: {record.Kind.Name} → {record.After}(이벤트 {e.Name})", record.View());
    }

    private void End(OverrideRecord record, string reason, long? seq)
    {
        lock (_lock)
        {
            if (!record.Active) return;
            record.EndReason = reason;
            record.EndedAt = DateTimeOffset.Now;
            record.EndSeq = seq;
            record.Reverting = null;
            record.Unconfirmed = null;
        }

        _timeline.RecordHarness(HarnessConstants.HarnessEvents.OverrideReverted, $"지속 조작 끝 {record.Id}: {record.Kind.Name} {record.Agent}{ScopeText(record)} ({reason})", record.View());
        if (record.Kind.HitEvent != null && record.Hits == 0)
            _timeline.RecordHarness(HarnessConstants.HarnessEvents.OverrideUnused, $"지속 조작 {record.Id}({record.Kind.Name} {record.After})가 한 번도 안 걸리고 끝남 - " +
                                                       "조건(메서드, 방향, playerId, match)이 실제 메시지와 맞는지 확인", record.View());
    }

    private static string ScopeText(OverrideRecord r) => r.Scope.Length == 0 ? "" : $"[{r.Scope}]";
}
