using System.Globalization;
using System.Text.Json;
using TestHarness.Core;
using TestHarness.Core.Scenarios;

namespace TestHarness.Scenarios.Knowledge;

/// <summary>
/// 이 프로젝트(오토체스 게임과 그 테스트 에이전트 - Unity 테스트 코드, 서버 Harness.Agent)의 지식 - 하네스(HarnessCore)는 틀만 갖고 내용은
/// 여기서 등록한다(ITH_GameKnowledge - 시나리오 카탈로그가 이 어셈블리를 등록할 때 먼저 부른다).
///
/// - 명령 영역: 에이전트 명령 이름 "&lt;영역&gt;.&lt;동작&gt;"의 앞부분(agent_list의 키워드). 새 영역은 여기 먼저 등록한다(TestHarness/CLAUDE.md "명령 영역").
/// - 지속 조작 종류(TH_OverrideKinds): 켜 두면 남는 조작(배속, 설정값, 장애 규칙 ...) - 하네스가 기록을 보고 추적하고 되돌린다.
/// - 환경 변화: 에이전트 이벤트 중 테스트 대상의 변화(Unity 재컴파일), 데이터를 바꾸는 에이전트 명령(지금은 없음).
/// - 노드 밖 이상: 노드 구간 밖에서 일어나면 보고서 머리에 경고로 올릴 이벤트.
/// </summary>
internal sealed class TH_ProjectKnowledge : ITH_GameKnowledge
{
    public void Register(TH_GameKnowledge knowledge)
    {
        // ---------------- 명령 영역 ----------------
        knowledge
            .CommandArea(new CommandArea("editor", HarnessSharedConstants.Connection.EditorAgentName, "Unity 에디터 자체(에디터 브리지)",
                "Unity 코드 반영(refresh), 컴파일 오류 확인, Play/Stop, 에디터 콘솔, Play 시작 씬 정하기(set_play_start)/되돌리기(clear_play_start)"))
            .CommandArea(new CommandArea("scene", "모든 Unity 에이전트(client1, server ...)", "그 플레이어의 씬 전환",
                "Play 뒤 출발 씬(HarnessBoot)에서 역할의 시작 씬으로 넘길 때(scene.load - UnityPlay 노드가 함). 씬 이름은 Unity 씬 파일 이름과 같아야 함"))
            .CommandArea(new CommandArea("client", "Unity 클라(client1, client2 ... - MPPM 태그 Client1 ...)", "Unity 클라의 로그인/매칭/상태",
                "Unity 클라로 로그인하고 매칭을 시작할 때"))
            .CommandArea(new CommandArea("shop", "Unity 클라", "매물대 - UI와 같은 경로로 조작/조회(구매, 리롤, 판매, 내 기물)",
                "실제 UI 경로를 검증할 때(규칙을 건너뛰지 않음)"))
            .CommandArea(new CommandArea("cheat", "Unity 인게임 서버(보통 server)", "규칙을 건너뛰는 테스트 준비(시간, 데미지, 지정 구매, 참가자 상태)",
                "테스트 상황을 빨리 만들 때 - 검증 대상이 아니라 준비용"))
            .CommandArea(new CommandArea("ingame", "Unity 인게임 서버", "인게임 서버의 방 상태 조회",
                "응접실/진행 중인 방, 접속자, 부하를 볼 때"))
            .CommandArea(new CommandArea("net", "모든 Unity 에이전트(client1, server ...) - 그 플레이어 프로세스 단위",
                "NGO 통신 조건 - Network Simulator로 지연/흔들림/손실, 끊기/다시 잇기, 순간 끊김",
                "클라↔인게임 서버 경계의 장애(재접속, 탈주, 지연)를 만들 때. net.condition/net.link는 하네스가 지속 조작으로 추적해 되돌림"))
            .CommandArea(new CommandArea("fault", ".NET 서버(gRPC를 받는 서버)", "경계 장애 규칙 - 받는 gRPC 메시지를 고치기/버리기/늦추기/실패",
                "서버끼리/클라↔서버 메시지를 경계에서 조작할 때(fault.methods로 메서드와 경로 확인 → fault.add). 하네스가 지속 조작으로 추적해 되돌림"));

        // ---------------- 지속 조작 종류 ----------------
        foreach (var kind in TH_OverrideKinds.All) knowledge.OverrideKind(kind);

        // ---------------- 환경 변화 ----------------
        // Unity 재컴파일 - 에디터 브리지가 재컴파일 뒤 스크립트를 다시 불러오고 보낸다. Play 진입 때의 리로드(reload_play)는 코드가 그대로라
        // 테스트 대상의 변화가 아니다.
        knowledge.EnvironmentEvent(e =>
            e.Source == HarnessConstants.Sources.AgentPrefix + HarnessSharedConstants.Connection.EditorAgentName &&
            e.Name == HarnessSharedConstants.Events.EditorReady && e.Data is { } ready &&
            ready.Str("reason") == HarnessSharedConstants.Values.ReadyReloadCompile
                ? $"Unity 재컴파일({HarnessSharedConstants.Events.EditorReady} {HarnessSharedConstants.Values.ReadyReloadCompile})"
                : null);
        // 데이터를 바꾸는 에이전트 명령: 지금은 없다 - 계정 삭제, 친구 추가/삭제 같은 명령이 생기면 knowledge.DataCommand(이름).

        // ---------------- 노드 밖 이상 ----------------
        knowledge
            .OutsideEvent(HarnessSharedConstants.Events.ClientIngameDisconnected, "클라의 인게임(NGO) 접속이 끊김", TH_OutsideEvents.FromDeclaredAgent)
            // 사람 플레이어(playerId 있음 - 봇은 null)의 탈락. AI가 노드 사이에 일부러 탈락시켰어도 결과에 안 나오므로 경고로 올린다.
            .OutsideEvent(HarnessSharedConstants.Events.PlayerHealthChanged, "사람 플레이어가 탈락함",
                (e, targets) => TH_OutsideEvents.FromDeclaredAgent(e, targets) && e.Data is { } d && d.Bool("eliminated") &&
                                d.Get("playerId") is { ValueKind: JsonValueKind.String } p && !string.IsNullOrEmpty(p.GetString()));
    }
}

/// <summary>
/// 이 프로젝트의 지속 조작 종류. 종류 이름(HarnessConstants.OverrideKindNames)은 노드의 Forbids/Needs 선언에 쓴다. 새 지속 조작 명령이 생기면 여기에 넣는다
/// (TH_ProjectKnowledge가 하네스에 등록).
/// </summary>
internal static class TH_OverrideKinds
{
    public static readonly IReadOnlyList<OverrideKind> All = new[]
    {
        new OverrideKind(
            OverrideKindNames.TimeScale, HarnessSharedConstants.Commands.CheatTimeScale, "인게임 서버 프로세스 배속",
            Matches: _ => true,
            Scope: _ => "",
            Before: v => Number(v, "previous"),
            After: v => Number(v, "current"),
            RevertArgs: (_, before) => new { scale = double.Parse(before, CultureInfo.InvariantCulture) },
            Probe: new OverrideProbe(HarnessSharedConstants.Commands.ProcessStatus, (v, _) => OverrideProbeResult.Of(Number(v, "timeScale")))), // 프로세스가 사라지면 agent_gone

        new OverrideKind(
            OverrideKindNames.PlanningSetting, HarnessSharedConstants.Commands.CheatPlanningDuration, "그 방의 재정비 시간 설정값",
            // CurrentOnly는 이번 재정비만 - 설정값은 그대로
            Matches: a => a.Str("mode") is HarnessSharedConstants.Values.PlanningSettingOnly or HarnessSharedConstants.Values.PlanningRescale,
            Scope: v => v.Str("roomId") ?? "",
            Before: v => Number(v, "settingBefore"),
            After: v => Number(v, "settingAfter"),
            RevertArgs: (scope, before) => new
            {
                mode = HarnessSharedConstants.Values.PlanningSettingOnly, seconds = double.Parse(before, CultureInfo.InvariantCulture), roomId = scope,
            },
            ScopeEnded: e => e.Name == HarnessSharedConstants.Events.RoomClosed ? e.Data?.Str("roomId") : null,
            Probe: new OverrideProbe(HarnessSharedConstants.Commands.IngameRooms, (v, scope) =>
                v.Items("rooms").FirstOrDefault(room => room.Str("roomId") == scope) is { ValueKind: JsonValueKind.Object } room
                    ? OverrideProbeResult.Of(Number(room, "planningDuration"))
                    : OverrideProbeResult.GoneResult)),

        // 경계 장애 규칙(서버 Harness.Agent의 fault.*) - 규칙 하나가 범위 하나(ruleId). 횟수/시간이 다 되면 서버가 fault.ended를 낸다.
        new OverrideKind(
            OverrideKindNames.Fault, HarnessSharedConstants.Commands.FaultAdd, "경계 장애 규칙(gRPC 메시지 고치기/버리기/늦추기/실패)",
            Matches: _ => true,
            Scope: v => v.Str("ruleId") ?? "",
            Before: _ => OverrideValues.Off,
            After: v => $"{v.Str("action")} {v.Str("dir")} {v.Str("method")}",
            RevertArgs: (scope, _) => new { ruleId = scope },
            ScopeEnded: e => e.Name == HarnessSharedConstants.Events.FaultEnded ? e.Data?.Str("ruleId") : null,
            RevertCommand: HarnessSharedConstants.Commands.FaultRemove,
            EndedByCall: (command, args) => command == HarnessSharedConstants.Commands.FaultRemove ? args.Str("ruleId") : null,
            HitEvent: e => e.Name == HarnessSharedConstants.Events.FaultHit ? e.Data?.Str("ruleId") : null,
            Probe: new OverrideProbe(HarnessSharedConstants.Commands.FaultList, (v, scope) =>
                v.Items("rules").FirstOrDefault(rule => rule.Str("ruleId") == scope) is { ValueKind: JsonValueKind.Object } rule
                    ? OverrideProbeResult.Of($"{rule.Str("action")} {rule.Str("dir")} {rule.Str("method")}")
                    : OverrideProbeResult.GoneResult)),

        // NGO 연결 조건(Unity 에이전트의 net.condition - Multiplayer Tools Network Simulator, 그 플레이어 프로세스 전체). Play가 끝나면 사라짐.
        new OverrideKind(
            OverrideKindNames.NetCondition, HarnessSharedConstants.Commands.NetCondition, "그 플레이어의 NGO 연결 조건(지연/흔들림/손실)",
            Matches: _ => true,
            Scope: _ => "",
            Before: v => NetConditionText(v.Get("previous")),
            After: v => NetConditionText(v.Get("current")),
            RevertArgs: (_, before) => NetConditionArgs(before),
            Probe: new OverrideProbe(HarnessSharedConstants.Commands.NetState, (v, _) => OverrideProbeResult.Of(NetConditionText(v.Get("condition"))))),

        // NGO 연결 끊기(net.link connected=false - 송수신 손실 100%). 되돌리기는 다시 잇기. Play가 끝나면 사라짐. 재진입으로 전송이 새로 만들어지면
        // 끊김이 풀릴 수 있어 그 변화를 이벤트(net.link_changed)로 받는다(ValueEvent).
        new OverrideKind(
            OverrideKindNames.NetLink, HarnessSharedConstants.Commands.NetLink, "그 플레이어의 NGO 연결 끊김",
            Matches: _ => true,
            Scope: _ => "",
            Before: v => v.Get("previous") is { } p ? LinkText(p.ValueKind == JsonValueKind.True) : null,
            After: v => v.Get("current") is { } c ? LinkText(c.ValueKind == JsonValueKind.True) : null,
            RevertArgs: (_, before) => new { connected = before == OverrideValues.Connected },
            Probe: new OverrideProbe(HarnessSharedConstants.Commands.NetState, (v, _) => OverrideProbeResult.Of(LinkText(v.Bool("connected")))),
            ValueEvent: e => e.Name == HarnessSharedConstants.Events.NetLinkChanged && e.Data is { } d
                ? (LinkText(d.Bool("connected")), d.Str("cause") ?? "")
                : null),

        // Play 시작 씬(에디터 브리지 editor.set_play_start - 시나리오 시작 때 TH_UnityEditor 준비가 출발 씬 HarnessBoot로). 모든 에디터(메인 +
        // MPPM 가상 플레이어)가 공유 파일로 맞춘다. 에디터 설정이라 Play 진입 도메인 리로드로 브리지가 잠깐 끊겨도 남는다(EndsWithAgent=false).
        new OverrideKind(
            OverrideKindNames.PlayStart, HarnessSharedConstants.Commands.EditorSetPlayStart, "Play 시작 씬(모든 에디터)",
            Matches: _ => true,
            Scope: _ => "",
            Before: v => v.Get("previous") is { ValueKind: JsonValueKind.String } previous ? previous.GetString() : OverrideValues.None, // JSON null = 정한 적 없음
            After: v => v.Str("playStart"),
            RevertArgs: (_, _) => new { },
            RevertCommand: HarnessSharedConstants.Commands.EditorClearPlayStart,
            EndedByCall: (command, _) => command == HarnessSharedConstants.Commands.EditorClearPlayStart ? "" : null,
            Probe: new OverrideProbe(HarnessSharedConstants.Commands.EditorStatus, (v, _) => OverrideProbeResult.Of(v.Str("playStartScene") ?? OverrideValues.None)),
            EndsWithAgent: false),
    };

    private static string LinkText(bool connected) => connected ? OverrideValues.Connected : OverrideValues.Disconnected;

    /// <summary>net.condition 값({delayMs, jitterMs, lossPercent, lossInterval}) → "지연 200ms ±50 손실 5% 간격 0"(되돌리기 인자로 다시 읽는다).</summary>
    private static string? NetConditionText(JsonElement? c) =>
        c is { ValueKind: JsonValueKind.Object } v
            ? $"지연 {Int(v, "delayMs")}ms ±{Int(v, "jitterMs")} 손실 {Int(v, "lossPercent")}% 간격 {Int(v, "lossInterval")}"
            : null;

    private static object NetConditionArgs(string text)
    {
        var n = System.Text.RegularExpressions.Regex.Matches(text, @"\d+").Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
        return n.Length == 4
            ? new { delayMs = n[0], jitterMs = n[1], lossPercent = n[2], lossInterval = n[3] }
            : new { delayMs = 0, jitterMs = 0, lossPercent = 0, lossInterval = 0 };
    }

    private static int Int(JsonElement v, string path) => v.Num(path) is { } n ? (int)n : 0;

    private static string? Number(JsonElement v, string path) =>
        v.Num(path) is { } n ? n.ToString("0.###", CultureInfo.InvariantCulture) : null;
}
