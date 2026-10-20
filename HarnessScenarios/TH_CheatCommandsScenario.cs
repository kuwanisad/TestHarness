using System.Text.Json;
using TestHarness.Core.Scenarios;
using static TestHarness.Scenarios.TH_CommonJudges;

namespace TestHarness.Scenarios;

/// <summary>
/// 테스트 조작 명령(cheat.*)과 클라 판매(shop.sell) 검증: 지정 구매+배치 → (합성 확인) → 판매 → 재정비 시간 세 모드 →
/// 데미지 → 빨리 끝내고 로비 복귀 → 정리.
/// </summary>
[TH_Scenario("cheat-commands", "테스트 조작 명령(cheat.*)과 판매 검증")]
public sealed class TH_CheatCommandsScenario : TH_Scenario
{
    protected override void Build(TH_ScenarioBuilder s)
    {
        s.Precondition("Unity 에디터가 열려 있고 에디터 브리지가 켜져 있음(agent_list에 UnityEditor)");
        s.Precondition("Multiplayer Play Mode 태그: 메인 에디터 = Client, 가상 플레이어 = Server (하네스 이름 client / server)");
        s.Precondition("Unity는 Play가 꺼진 상태, 서버도 꺼진 상태(harness_status에 orphans 없음)");
        s.Caution("cheat.*는 server 에이전트에, shop.*는 client 에이전트에 보낸다. 플레이어 지정(player)은 cheat.players로 확인");
        s.Caution("재정비 시간을 재접속 유예(20초)보다 짧게 줄이는 건 이 시나리오 안에서만 - 끝나면 Play를 멈추므로 남지 않는다");
        s.Caution("배속(cheat.time_scale)을 바꿨으면 정리 전에 1로 되돌릴 것");

        var ready = s.Objective("A1", "서버 기동 → 계정 생성 → Unity Play → 로그인 → 매칭까지 해서 첫 재정비 국면에 들어간다",
            "client의 phase.changed(Planning), 그 뒤 " + IngameScreenText,
            InPlanning, TH_FailPolicy.Stop,
            "servers_start", "account_create", "agent_command(UnityEditor, editor.play)", "agent_command(client, client.login / client.join_match)",
            "agent_command(client, process.status)");

        var buy = s.Objective("B1", "재정비 중에 cheat.buy로 내 플레이어(사람)가 기물 하나를 사서 보드 0번 칸에 놓는다",
            "cheat.buy 결과 placed=true 또는 merged=true, 그리고 gold = goldBefore(골드 불변)",
            c => c.LastCommandValue("cheat.buy") is { } r
                ? TH_Verdict.PassIf(
                    (r.Value.Bool("placed") || r.Value.Bool("merged")) && r.Value.Num("gold") == r.Value.Num("goldBefore"),
                    $"placed={r.Value.Bool("placed")}, merged={r.Value.Bool("merged")}, 골드 {r.Value.Num("goldBefore")}→{r.Value.Num("gold")}",
                    $"기대와 다름: placed={r.Value.Bool("placed")}, merged={r.Value.Bool("merged")}, placeSkipped={r.Value.Str("placeSkipped")}, " +
                    $"골드 {r.Value.Num("goldBefore")}→{r.Value.Num("gold")}",
                    r.Seq)
                : TH_Verdict.Fail("이 목표 구간에 성공한 cheat.buy가 없음(실패 응답이면 error 확인)"),
            TH_FailPolicy.Stop, "agent_command(server, cheat.players)", "agent_command(server, cheat.buy)", "agent_command(client, shop.units)");

        var merged = s.Objective("B2", "B1에서 합성이 일어났다 - 합성된 기물을 확인한다",
            "client shop.units에 star=2 이상인 기물이 있다(근거: 그 shop.units 호출의 seq)",
            judge: null, TH_FailPolicy.Continue, "agent_command(client, shop.units)");

        var sell = s.Objective("C1", "client에서 shop.units로 내 기물 하나를 골라 shop.sell로 판다",
            "shop.sell 호출 성공 뒤 client gold.changed로 골드가 늘어남",
            c =>
            {
                if (c.LastCommandValue("shop.sell") is not { } sold) return TH_Verdict.Fail("이 목표 구간에 성공한 shop.sell이 없음");
                var gain = c.Events.FirstOrDefault(e => e.Seq > sold.Seq && e.Name == "gold.changed" && e.Source == "agent:" + Client &&
                                                        e.Data is { } d && d.Num("gold") > d.Num("previous"));
                return gain != null
                    ? TH_Verdict.Pass($"판매 후 골드 {gain.Data?.Num("previous")}→{gain.Data?.Num("gold")}", sold.Seq, gain.Seq)
                    : TH_Verdict.Fail("판매 요청 뒤 골드가 늘어난 gold.changed가 없음(재정비 중이었는지, 내 기물이었는지 확인)", sold.Seq);
            },
            TH_FailPolicy.Continue, "agent_command(client, shop.units)", "agent_command(client, shop.sell)", "wait_for(source=client, names=[gold.changed])");

        var currentOnly = s.Objective("D1", "재정비 중에 cheat.planning_duration(CurrentOnly, 짧게 - 예: 3초)로 이번 재정비만 줄여 PvP로 넘어가게 한다",
            "cheat.planning_duration(CurrentOnly) 성공 뒤 client phase.changed(PvP)",
            c => AfterCommand(c, "cheat.planning_duration", v => v.Str("mode") == "CurrentOnly", "phase.changed", d => d.Str("phase") == "PvP"),
            TH_FailPolicy.Stop, "agent_command(server, cheat.planning_duration)", "wait_for(source=client, names=[phase.changed])");

        var settingOnly = s.Objective("D2", "cheat.planning_duration(SettingOnly, 10)으로 설정값을 바꾸고 다음 재정비 국면이 올 때까지 기다린다",
            "SettingOnly 결과 settingAfter=10, 그 뒤 client phase.changed(Planning)",
            c => AfterCommand(c, "cheat.planning_duration", v => v.Str("mode") == "SettingOnly" && v.Num("settingAfter") == 10,
                "phase.changed", d => d.Str("phase") == "Planning"),
            TH_FailPolicy.Stop, "agent_command(server, cheat.planning_duration)", "cheat.time_scale로 전투를 빨리 넘겨도 됨");

        var rescale = s.Objective("D3", "재정비 중에 cheat.planning_duration(Rescale, 20)으로 설정값과 이번 재정비 길이를 비율대로 바꾼다",
            "Rescale 결과 settingBefore=10, settingAfter=20, currentAfter = 20 × currentBefore / 10",
            c => c.LastCommandValue("cheat.planning_duration", v => v.Str("mode") == "Rescale") is { } r
                ? JudgeRescale(r.Value, r.Seq)
                : TH_Verdict.Fail("이 목표 구간에 성공한 Rescale 호출이 없음(재정비 중이었는지 확인)"),
            TH_FailPolicy.Continue, "agent_command(server, cheat.planning_duration)");

        var damage = s.Objective("E1", "cheat.damage로 봇 하나에게 데미지를 준다",
            "server의 player.health_changed에서 health < previous",
            c => c.Last(Server, "player.health_changed", d => d.Num("health") < d.Num("previous")) is { } e
                ? TH_Verdict.Pass($"{e.Data?.Str("team")} 체력 {e.Data?.Num("previous")}→{e.Data?.Num("health")}", e.Seq)
                : TH_Verdict.Fail("체력이 줄어든 player.health_changed가 없음"),
            TH_FailPolicy.Continue, "agent_command(server, cheat.players)", "agent_command(server, cheat.damage)", "wait_for(source=server, names=[player.health_changed])");

        var finish = s.Objective("E2", "빨리 한 판을 끝내 로비로 돌아온다(봇 전원 탈락 → (재정비면 종료) → 전투 → 전투 결과에서 게임 종료 → 로비)",
            "client.lobby_state(InLobby), 그 뒤 " + BackInLobbyScreenText + " (씬 로드가 끝날 때까지 process.status를 다시 봐도 됨 - 마지막 것으로 판정)",
            BackInLobby, TH_FailPolicy.Stop,
            "agent_command(server, cheat.time_scale)", "agent_command(server, cheat.damage)", "agent_command(server, cheat.skip_planning)",
            "wait_for(source=client, names=[client.lobby_state], timeoutSeconds=300)", "agent_command(client, process.status)");

        var cleanup = s.Cleanup("배속을 1로 되돌리고(Play 중이면) Play를 멈추고 서버를 끈다",
            "editor.play_mode(EnteredEditMode)와 servers.stopped (이미 꺼져 있었으면 editor.status / harness_status로)",
            CleanedUp, "agent_command(server, cheat.time_scale)", "agent_command(UnityEditor, editor.stop)", "servers_stop");

        ready.Then(buy);
        // 결과 분기: 합성됐으면 합성 확인을 거친다(하네스가 cheat.buy 결과로 정함).
        buy.Then((c, _) => c.LastCommandValue("cheat.buy") is { } r && r.Value.Bool("merged") ? merged : sell);
        merged.Then(sell);
        sell.Then(currentOnly);
        currentOnly.Then(settingOnly);
        settingOnly.Then(rescale);
        rescale.Then(damage);
        damage.Then(finish);
        s.Start(ready);
        _ = cleanup;
    }

    /// <summary>Rescale: 설정값 10→20, 이번 재정비 길이는 비율 유지(currentAfter = settingAfter × currentBefore / settingBefore).</summary>
    private static TH_Verdict JudgeRescale(JsonElement v, long seq)
    {
        var settingBefore = v.Num("settingBefore");
        var settingAfter = v.Num("settingAfter");
        var currentBefore = v.Num("currentBefore");
        var currentAfter = v.Num("currentAfter");
        var detail = $"설정값 {settingBefore}→{settingAfter}, 이번 길이 {currentBefore}→{currentAfter}";

        if (settingBefore != 10 || settingAfter != 20) return TH_Verdict.Fail($"설정값이 기대(10→20)와 다름: {detail}", seq);
        if (currentBefore is not { } cb || currentAfter is not { } ca)
            return TH_Verdict.Fail($"이번 재정비 길이(currentBefore/After)가 응답에 없음 - 재정비 중이었는지 확인: {detail}", seq);

        var expected = 20 * cb / 10;
        return TH_Verdict.PassIf(Math.Abs(ca - expected) < 0.01, $"{detail} (기대 {expected})", $"이번 길이가 비율대로 안 바뀜: {detail}, 기대 {expected}", seq);
    }

    /// <summary>명령(조건) 성공 뒤에 클라 이벤트(조건)가 왔는지.</summary>
    private static TH_Verdict AfterCommand(TH_JudgeContext c, string command, Func<JsonElement, bool> commandWhere,
        string clientEvent, Func<JsonElement, bool> eventWhere)
    {
        if (c.LastCommandValue(command, commandWhere) is not { } r) return TH_Verdict.Fail($"이 목표 구간에 조건에 맞는 성공한 {command}가 없음");
        var e = c.Events.FirstOrDefault(x => x.Seq > r.Seq && x.Name == clientEvent && x.Source == "agent:" + Client && x.Data is { } d && eventWhere(d));
        return e != null
            ? TH_Verdict.Pass($"{command} 뒤 {clientEvent} 확인", r.Seq, e.Seq)
            : TH_Verdict.Fail($"{command}는 성공했지만 그 뒤 기대한 {clientEvent}가 없음", r.Seq);
    }
}
