using TestHarness.Core.Scenarios;
using static TestHarness.Scenarios.TH_CommonJudges;

namespace TestHarness.Scenarios;

/// <summary>
/// 한 판 전체: 서버 기동 → 계정 → Unity Play → 로그인 → 매칭 → 인게임 → 게임 종료 → 로비 복귀 → 부하 복귀 → 정리.
/// 정상 경로 확인용이라 규칙을 건너뛰는 조작(cheat.*)은 사람이 빨리 끝내라고 했을 때만 쓴다.
/// </summary>
[TH_Scenario("full-match", "한 판 전체 (로그인부터 로비 복귀까지)")]
public sealed class TH_FullMatchScenario : TH_Scenario
{
    protected override void Build(TH_ScenarioBuilder s)
    {
        s.Precondition("Unity 에디터가 열려 있고 에디터 브리지가 켜져 있음(agent_list에 UnityEditor)");
        s.Precondition("Multiplayer Play Mode 태그: 메인 에디터 = Client, 가상 플레이어 = Server (하네스 이름 client / server)");
        s.Precondition("Unity는 Play가 꺼진 상태, 서버도 꺼진 상태(harness_status에 orphans 없음)");
        s.Caution("정상 진행 확인용 - cheat.*는 사람이 '빨리'라고 요청했을 때만 쓰고, 썼으면 그 목표 보고의 note에 적을 것");
        s.Caution("Unity 명령의 ok는 '요청을 보냈다'까지 - 결과는 이벤트(wait_for)로 확인");

        var servers = s.Objective("A1", "서버 8개를 띄운다", "servers_start 결과 report.ok=true",
            ServersStarted, TH_FailPolicy.Stop, "servers_start", "harness_status");
        var account = s.Objective("A2", "Unity 클라가 쓸 계정을 만든다", "account_create 결과 ok=true",
            AccountCreated, TH_FailPolicy.Stop, "account_create");
        var play = s.Objective("A3", "Unity를 Play해서 클라와 인게임 서버를 하네스에 붙인다",
            "이 목표 구간에 client, server 둘 다 harness.attached",
            UnityAttached, TH_FailPolicy.Stop, "agent_command(UnityEditor, editor.play)", "wait_for(names=[harness.attached])");
        var login = s.Objective("A4", "A2의 계정으로 Unity 클라를 로그인시키고 로비 화면이 떴는지 본다",
            "client.login_result success=true, 그 뒤 " + LobbyScreenText,
            LoggedIn, TH_FailPolicy.Stop, "agent_command(client, client.login)", "wait_for(source=client, names=[client.login_result])",
            "agent_command(client, process.status)");
        var match = s.Objective("A5", "매칭을 시작해 매치를 잡는다", "client.match_found (client.match_failed면 실패)",
            MatchFound, TH_FailPolicy.Stop, "agent_command(client, client.join_match)");
        var ingame = s.Objective("A6", "인게임에 들어가 첫 재정비 국면을 맞고, 화면이 인게임으로 바뀌었는지 본다",
            "client의 phase.changed(Planning), 그 뒤 " + IngameScreenText,
            InPlanning, TH_FailPolicy.Stop, "wait_for(source=client, names=[phase.changed])", "agent_command(client, process.status)",
            "agent_command(server, ingame.rooms)");
        var lobby = s.Objective("A7", "게임이 끝날 때까지 진행해 로비로 돌아오고, 화면이 로비로 돌아왔는지 본다",
            "client.lobby_state(InLobby), 그 뒤 " + BackInLobbyScreenText + " (씬 로드가 끝날 때까지 process.status를 다시 봐도 됨 - 마지막 것으로 판정)",
            BackInLobby, TH_FailPolicy.Stop, "wait_for(source=client, names=[client.lobby_state], timeoutSeconds=300)",
            "agent_command(client, process.status)");
        var load = s.Objective("A8", "인게임 서버 부하가 0으로 돌아왔는지 본다(방이 남지 않았는지)",
            "이 목표 구간의 ingame.rooms 결과 load=0, rooms 비어 있음",
            c => c.LastCommandValue("ingame.rooms") is { } r
                ? TH_Verdict.PassIf(r.Value.Num("load") == 0 && r.Value.Get("rooms") is { ValueKind: System.Text.Json.JsonValueKind.Array } rooms && rooms.GetArrayLength() == 0,
                    "load=0, 방 없음", $"남아 있음: load={r.Value.Num("load")}, rooms={r.Value.Get("rooms")?.GetArrayLength()}", r.Seq)
                : TH_Verdict.Fail("이 목표 구간에 ingame.rooms 호출이 없음"),
            TH_FailPolicy.Continue, "agent_command(server, ingame.rooms)");

        var cleanup = s.Cleanup("Play를 멈추고 서버를 끈다", "editor.play_mode(EnteredEditMode)와 servers.stopped (이미 꺼져 있었으면 editor.status / harness_status로)",
            CleanedUp, "agent_command(UnityEditor, editor.stop)", "servers_stop");

        servers.Then(account);
        account.Then(play);
        play.Then(login);
        login.Then(match);
        match.Then(ingame);
        ingame.Then(lobby);
        lobby.Then(load);
        s.Start(servers);
        _ = cleanup;
    }
}
