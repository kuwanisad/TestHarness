using System.Text.Json;
using TestHarness.Core;
using TestHarness.Core.Scenarios;
using TestHarness.Scenarios.Targets;
using static TestHarness.Scenarios.TH_CommonJudges;

namespace TestHarness.Scenarios.Nodes;

/// <summary>
/// 시나리오들이 같이 쓰는 상태 이름. 노드의 From/To에 쓴다(하네스가 칸별 지금 상태로 이동을 검사한다).
/// 칸은 전역(서버/Unity Play)과 클라마다 하나(TH_Client).
/// </summary>
internal static class TH_States
{
    // ---- 전역 칸 ----

    /// <summary>서버도 Unity Play도 꺼짐(시나리오 시작/정리 후).</summary>
    public const string ServersDown = "ServersDown";

    /// <summary>서버 8개가 떠 있고 Unity는 Play 전.</summary>
    public const string ServersUp = "ServersUp";

    /// <summary>서버가 떠 있고 Unity Play 중(인게임 서버와 Unity 클라들이 하네스에 붙음).</summary>
    public const string Playing = "Playing";

    // ---- 클라 칸 ----

    /// <summary>클라가 아직 없음/접속 전(Unity: Play 전, 헤드리스: 만들기 전이거나 로그인 전).</summary>
    public const string Offline = "Offline";

    /// <summary>Unity 클라가 로그인 화면(Play 직후).</summary>
    public const string Login = "Login";

    /// <summary>로그인해서 로비.</summary>
    public const string Lobby = "Lobby";

    /// <summary>Unity 클라가 인게임(한 판 진행 중).</summary>
    public const string Ingame = "Ingame";

    /// <summary>헤드리스 클라가 매칭은 잡혔지만 인게임에 못 들어간 상태(헤드리스는 인게임 접속 불가).</summary>
    public const string Matched = "Matched";

    /// <summary>클라의 NGO 연결이 끊겨(Cut - 하네스가 끊어 둠) 서버 재접속 유예 중. 다시 잇기(Restore), 유예 만료(GraceExpire), 끊긴 동안의 게임 일을 본다.</summary>
    public const string Disconnected = "Disconnected";

    /// <summary>
    /// Unity 클라가 끊긴 채 재접속 유예가 지나 서버에서 탈락함(GraceExpire). 탈주 뒤 클라가 어디로 가야 하는지는 아직 정하지 않아(서버 이동 중 가림막
    /// 등 미정) 이 상태에서 이어지는 노드가 없다 - 정리 노드로만 끝난다. 정해지면 그 동작을 판정에 넣고 이 상태를 그 상태로 바꾼다.
    /// </summary>
    public const string Abandoned = "Abandoned";
}

/// <summary>
/// 공유 노드 라이브러리 - 시나리오는 여기 노드들로 그래프를 짠다. 노드마다 Auto(하네스가 하는 행동 - AI가 하던 도구 호출을 코드로)와
/// 판정이 있다. 판정은 노드 구간의 기록만 보고, 가능하면 다른 노드의 순서에 의존하지 않는다(AI가 경로를 고르므로).
///
/// 대상을 받는 노드는 함수다(TH_Node.For - ID "이름@대상", 같은 대상이면 같은 인스턴스): TH_Client를 받는 것은 공통 동작(Unity/헤드리스
/// 모두), TH_UnityClient를 받는 것은 인게임 동작(헤드리스는 생략 - 넘기면 컴파일 안 됨). 대상 없는 노드(ServersUp, Cleanup)는 공유 인스턴스.
///
/// 노드 사이에 넘기는 값은 대상별 칸(TH_RunValues.For, 키는 TH_ValueKeys): 계정(AccountReady → Login), playerId(Login),
/// 방(EnterGame - cheat 명령의 roomId), merged(BuyPlace).
///
/// 지속 조작(배속, 재정비 설정값 - HarnessCore/OverrideKinds.cs)은 노드가 직접 되돌리지 않는다 - 노드가 끝나면 하네스가 처음 값으로
/// 되돌린다(다음 노드까지 남기려면 r.KeepOverrides()).
/// </summary>
internal static class TH_Nodes
{
    private static TH_UnityServer Server => TH_Unity.Server;

    // ================================ 준비 ================================

    public static readonly TH_Node ServersUp = TH_Node.Action("ServersUp",
            "서버 8개(+Docker 인프라)를 DevTools 스크립트로 띄운다", "servers_start 결과 report.ok=true",
            async r => await r.Call("servers_start"),
            ServersStarted)
        .From(TH_States.ServersDown).To(TH_States.ServersUp)
        .Hint("실패하면 report의 단계별 logTail에 원인");

    /// <summary>클라가 쓸 계정 준비(Unity: account_create, 헤드리스: client_create - 로그인 때 계정 생성).</summary>
    public static TH_Node AccountReady(TH_Client c) => TH_Node.For(nameof(AccountReady), c, id => TH_Node.Action(id,
            $"{c.Name}가 쓸 계정을 준비한다(테스트 대상의 데이터 변화)",
            c is TH_UnityClient ? "account_create 결과 ok=true" : $"client_create(alias={c.Name}) 성공",
            c.PrepareAccount,
            c.JudgeAccount)
        .From(TH_States.ServersUp, TH_States.Playing)
        .From(c, TH_States.Offline, TH_States.Login));

    /// <summary>
    /// Unity Play - 출발 씬(editor.BootScene = HarnessBoot - 시나리오 시작 때 준비@UnityEditor가 모든 에디터의 Play 시작 씬으로 정함)에서 Play하고,
    /// 플레이어가 전부 하네스에 붙으면 플레이어마다 시나리오가 선언한 시작 씬(StartScene)으로 넘기고(scene.load), 역할이 실제로 시작되는지 본다
    /// (Play는 MPPM 플레이어 전원이 같이 들어간다). 왜 빈 씬에서 출발하나: Unity/ARCHITECTURE.md "HarnessBoot 씬".
    /// </summary>
    public static TH_Node UnityPlay(TH_UnityEditor editor, TH_UnityServer server, params TH_UnityClient[] clients) => TH_Node.For(nameof(UnityPlay), clients, id =>
    {
        var players = clients.Select(c => (Target: (TH_Target)c, c.StartScene)).Append((Target: server, server.StartScene)).ToArray();
        var node = TH_Node.Action(id,
                $"Unity를 Play해서 클라({string.Join(", ", clients.Select(c => c.Name))})와 인게임 서버({server.Name})를 하네스에 붙이고 시작 씬으로 넘긴다",
                $"{string.Join(", ", players.Select(p => p.Target.Name))} 전부 harness.attached, 출발 씬 {editor.BootScene ?? "(열린 씬)"}에서 시작(test.role), " +
                $"시작 씬으로 넘어감({string.Join(", ", players.Select(p => $"{p.Target.Name}→{p.StartScene ?? "그대로"}"))} - scene.loaded), 그리고 역할이 시작됨 - " +
                $"{server.Name}: network.role=server, 클라: 로그인 화면(process.status sceneRole=Login)",
                async r =>
                {
                    var (since, _) = await r.Agent(editor, "editor.play");
                    foreach (var (target, _) in players) await r.Expect(target, new[] { "harness.attached" }, since, 120);

                    // 출발 씬에서 시작했는지 먼저 - 다른 씬(예: Login)에서 시작했으면 그 씬의 객체가 이미 남아 있어 넘겨도 실제와 다른 시작이 된다.
                    foreach (var (target, _) in players)
                    {
                        var role = await r.Expect(target, new[] { "test.role" }, since, 30);
                        if (BootSceneMismatch(editor, target.Name, role.Str("data.startScene")) is { } mismatch) throw TH_RunContext.Fail(mismatch);
                    }

                    // 시작 씬은 시나리오가 정한다(대상의 StartScene) - 에이전트가 scene.load로 연다.
                    foreach (var (target, scene) in players.Where(p => p.StartScene != null))
                    {
                        var (loadSince, ok, _, error) = await r.TryAgent(target, "scene.load", new { scene });
                        if (!ok) throw TH_RunContext.Fail($"{target.Name}: scene.load({scene}) 실패 - {error}");
                        await r.Expect(target, new[] { "scene.loaded" }, loadSince, 60, scene);
                    }

                    // 넘어간 것만으로는 부족하다 - 역할이 실제로 시작됐는지(서버는 NGO 서버 시작, 클라는 로그인 화면).
                    await r.WaitFor(server, new[] { "network.role" }, since, 60, "server"); // 안 오면 판정이 이유를 쓴다
                    foreach (var client in clients) await r.PollAgent(client, "process.status", v => v.Str("sceneRole") == "Login", 30);
                },
                c => UnityAttached(c, editor, server, clients))
            .From(TH_States.ServersUp).To(TH_States.Playing)
            .Hint("Unity 에디터가 열려 있고 에디터 브리지가 켜져 있어야 함(UnityEditor 에이전트). MPPM 태그가 맞아야 이름이 맞음. " +
                  "씬 이름(TH_Scenes)은 Unity 씬 파일 이름과 같아야 하고 시작 씬은 Scene List에 있어야 함(editor.scenes)")
            .Uses(editor)
            .Uses(server);
        foreach (var client in clients) node.From(client, TH_States.Offline).To(client, TH_States.Login);
        return node;
    });

    /// <summary>로그인 → 로비(Unity: 로그인 화면에서, 로비 화면 확인 / 헤드리스: client_login + 로비 입장).</summary>
    public static TH_Node Login(TH_Client c) => TH_Node.For(nameof(Login), c, id =>
    {
        var node = TH_Node.Action(id,
                $"{c.Name}를 로그인시켜 로비에 들어간다" + (c is TH_UnityClient ? "(로비 화면 확인)" : ""),
                c is TH_UnityClient ? "client.login_result success=true, 그 뒤 " + LobbyScreenText : "client_login 결과 login.success, lobby.success",
                c.Login,
                c.JudgeLogin)
            .From(c, c.LoginFrom).To(c, TH_States.Lobby);
        if (c is TH_HeadlessClient) node.From(TH_States.ServersUp, TH_States.Playing); // Unity 클라는 로그인 화면 상태가 곧 Play 중
        return node;
    });

    /// <summary>
    /// 매칭 - 넘긴 클라들이 같이 매칭을 요청한다. Unity 클라는 인게임 진입(첫 재정비 국면, 인게임 화면, 방)까지, 헤드리스는 MatchFound까지.
    /// 매칭 인원 규칙(MatchingConstants)에 따라 같은 방일 수도 다른 방일 수도 있다 - 지금 인원 1이라 각자 다른 방.
    /// </summary>
    public static TH_Node EnterGame(params TH_Client[] clients) => TH_Node.For(nameof(EnterGame), clients, id =>
    {
        var names = string.Join(", ", clients.Select(c => c.Name));
        var node = TH_Node.Action(id,
                $"{names}가 매칭을 시작해 매치를 잡는다(Unity 클라는 인게임에 들어가 첫 재정비 국면과 인게임 화면, 방까지 확인)",
                string.Join(" / ", clients.Select(c => c is TH_UnityClient
                    ? $"{c.Name}: client.match_found, 그 뒤 phase.changed(Planning), 그 뒤 {IngameScreenText}"
                    : $"{c.Name}: MatchFound")),
                async r =>
                {
                    var since = new Dictionary<TH_Client, long>();
                    foreach (var client in clients) since[client] = await client.JoinMatch(r); // 요청을 먼저 다 보낸다(같이 매칭)
                    foreach (var client in clients) await client.AfterJoin(r, since[client]);
                },
                c => TH_Verdict.All(clients.Select(client => client.JudgeMatch(c)).ToArray()));
        foreach (var client in clients) node.From(client, TH_States.Lobby).To(client, client.AfterMatchState);
        return node;
    });

    /// <summary>서버 기동 → 계정 → Play → 로그인 → 매칭까지(묶음 - 하네스가 자식을 차례로 돈다). Unity 클라 하나.</summary>
    public static TH_Node ReadyIngame(TH_UnityEditor editor, TH_UnityServer server, TH_UnityClient c) => TH_Node.For(nameof(ReadyIngame), c, id => TH_Node.Group(id,
            $"서버 기동 → 계정 생성 → Unity Play → {c.Name} 로그인 → 매칭까지 해서 첫 재정비 국면에 들어간다",
            new[] { ServersUp, AccountReady(c), UnityPlay(editor, server, c), Login(c), EnterGame(c) })
        .From(TH_States.ServersDown)
        .From(c, TH_States.Offline));

    // ================================ 게임 끝 ================================

    public static TH_Node WaitGameEnd(TH_UnityClient c) => TH_Node.For(nameof(WaitGameEnd), c, id => TH_Node.Action(id,
            $"조작 없이 {c.Name}의 한 판이 끝날 때까지 기다려 로비로 돌아오고, 화면이 로비로 돌아왔는지 본다(수 분 걸림)",
            "client.lobby_state(InLobby), 그 뒤 " + BackInLobbyScreenText,
            async r =>
            {
                await r.Expect(c, new[] { "client.lobby_state" }, r.LastSeq, 1800, "InLobby");
                await r.PollAgent(c, "process.status", BackInLobbyScreen, 30);
            },
            j => BackInLobby(j, c))
        .From(c, TH_States.Ingame).To(c, TH_States.Lobby)
        .Forbids(OverrideKinds.TimeScale, OverrideKinds.PlanningSetting)); // "조작 없이" - 배속이나 재정비 설정이 남아 있으면 자연스러운 한 판이 아님

    public static TH_Node FinishGameFast(TH_UnityClient c) => TH_Node.For(nameof(FinishGameFast), c, id => TH_Node.Action(id,
            $"테스트 조작으로 {c.Name}의 한 판을 빨리 끝내 로비로 돌아온다(그 방: 재정비 5초, 봇 전원 탈락 → 재정비면 종료 / 서버 배속 5)",
            "client.lobby_state(InLobby), 그 뒤 " + BackInLobbyScreenText,
            async r =>
            {
                var roomId = await TH_Rooms.Of(r, c);
                await r.TryAgent(Server, "cheat.planning_duration", new { mode = "SettingOnly", seconds = 5, roomId });
                await r.TryAgent(Server, "cheat.time_scale", new { scale = 5 });
                foreach (var bot in (await Players(r, c)).Where(p => p.Bool("isBot") && !p.Bool("eliminated")))
                    await r.Agent(Server, "cheat.damage", new { player = bot.Str("team"), amount = 1000, roomId });

                var since = r.LastSeq;
                await r.TryAgent(Server, "cheat.skip_planning", new { roomId }); // 재정비가 아니면 거부 - 괜찮다
                await r.Expect(c, new[] { "client.lobby_state" }, since, 300, "InLobby");
                await r.PollAgent(c, "process.status", BackInLobbyScreen, 30);
                // 배속과 재정비 설정값은 지속 조작(OverrideKinds) - 이 노드가 끝나면 하네스가 되돌린다(방이 닫혔으면 설정값은 되돌릴 필요 없음).
            },
            j => BackInLobby(j, c))
        .From(c, TH_States.Ingame).To(c, TH_States.Lobby)
        .Hint("재접속/탈주 테스트 중에는 쓰지 말 것(재정비와 배속 조건이 깨짐). 배속은 서버 프로세스 전체라 다른 방도 잠깐 빨라짐"));

    /// <summary>인게임 서버 부하가 0으로 돌아왔는지 - 넘긴 클라가 전부 로비일 때(진행 중인 방이 없어야 하는 때).</summary>
    public static TH_Node LoadZero(params TH_Client[] clients) => TH_Node.For(nameof(LoadZero), clients, id =>
    {
        var node = TH_Node.Action(id,
                "인게임 서버 부하가 0으로 돌아왔는지 본다(끝난 방이 남지 않았는지)", "ingame.rooms 결과 load=0, rooms 비어 있음",
                async r => await r.PollAgent(Server, "ingame.rooms", v => v.Num("load") == 0 && !v.Items("rooms").Any(), 15),
                j => j.LastCommandValue("ingame.rooms") is { } rooms
                    ? TH_Verdict.PassIf(rooms.Value.Num("load") == 0 && !rooms.Value.Items("rooms").Any(),
                        "load=0, 방 없음", $"남아 있음: load={rooms.Value.Num("load")}, rooms={rooms.Value.Items("rooms").Count()}", rooms.Seq)
                    : TH_Verdict.Fail("이 노드 구간에 성공한 ingame.rooms가 없음"))
            .From(TH_States.Playing);
        foreach (var client in clients) node.From(client, TH_States.Lobby);
        return node;
    });

    // ================================ 재접속 / 탈주 - Unity 클라만 (NGO 통신 조작 net.*) ================================
    // 측정값(2026-10-23 PC): 끊김 감지 약 30초(NGO ProtocolTimeout), 클라 재진입 1회·10초, 서버 재접속 유예 약 20초.
    // 기다림은 이벤트 조건으로 하고 아래 상한만 측정값에 여유를 둔다. 끊은 것(net_link)은 지속 조작 - 노드 끝에 하네스가 다시 잇는다.

    private const int DisconnectDetectSeconds = 45; // 끊은 뒤 끊김 감지까지
    private const int RejoinGiveUpSeconds = 20;     // 감지 뒤 재진입 포기까지
    private const int EliminationSeconds = 40;      // 재진입 포기 뒤 서버 탈락까지
    private const int PlanningWaitSeconds = 90;     // 진행 확인 전 재정비를 기다리는 상한

    /// <summary>짧은 끊김(5초 순간 끊김 - 끊김 감지 시간보다 짧음) 뒤에도 연결이 유지되고 게임 진행을 받는지.</summary>
    public static TH_Node BriefDrop(TH_UnityClient c) => TH_Node.For(nameof(BriefDrop), c, id => TH_Node.Action(id,
            $"{c.Name}의 NGO 연결을 5초 끊었다가(net.lag_spike - 끊김 감지 시간보다 짧음) 연결이 유지되고 게임 진행을 계속 받는지 본다",
            $"net.link_changed(lag_spike_end), 노드 구간에 client.ingame_disconnected 없음, 그 뒤 국면을 넘겼을 때(cheat.skip_planning) {c.Name}가 phase.changed(PvP)를 받음, " +
            "인게임 화면 유지",
            async r =>
            {
                var (since, _) = await r.Agent(c, "net.lag_spike", new { seconds = 5 });
                await r.Expect(c, new[] { "net.link_changed" }, since, 20, "lag_spike_end");
                await ConfirmProgress(r, c);
            },
            j => BriefDropped(j, c))
        .From(c, TH_States.Ingame).To(c, TH_States.Ingame)
        .Forbids(OverrideKinds.NetLink, OverrideKinds.NetCondition)
        .Hint("재정비가 아니면 다음 재정비를 기다렸다가 국면을 넘긴다(최대 90초)"));

    /// <summary>
    /// 끊김 감지 뒤 네트워크가 돌아온 경우: 지금 연결만 끊고(net.link hold=false - 새 전송에는 안 걸림) 끊김이 감지되면 클라가 스스로 재진입해
    /// 붙는지, 그 뒤 게임이 그대로 이어지는지(인게임 화면, 국면 진행 수신).
    /// </summary>
    public static TH_Node Reconnect(TH_UnityClient c) => TH_Node.For(nameof(Reconnect), c, id => TH_Node.Action(id,
            $"{c.Name}의 지금 NGO 연결만 끊어(hold=false) 끊김이 감지되게 하고, 클라가 스스로 재진입해 붙는지와 게임이 그대로 이어지는지 본다",
            $"client.ingame_disconnected(끊김 감지), 그 뒤 network.role=client(재진입), 인게임 화면 복귀, 국면을 넘겼을 때(cheat.skip_planning) " +
            $"{c.Name}가 phase.changed(PvP)를 받음",
            async r =>
            {
                var (cut, _) = await r.Agent(c, "net.link", new { connected = false, hold = false });
                var detected = await r.Expect(c, new[] { "client.ingame_disconnected" }, cut, DisconnectDetectSeconds);
                if (await r.WaitFor(c, new[] { "network.role" }, SeqOf(detected, cut), RejoinGiveUpSeconds, "\"client\"") == null)
                    throw TH_RunContext.Fail($"{c.Name}: 끊김 감지 뒤 {RejoinGiveUpSeconds}초 안에 재진입하지 못함(network.role=client 없음) - 클라 log.recent, 서버의 재접속 로그를 볼 것");
                await ConfirmProgress(r, c);
            },
            j => Reconnected(j, c))
        .From(c, TH_States.Ingame).To(c, TH_States.Ingame)
        .Forbids(OverrideKinds.NetLink, OverrideKinds.NetCondition)
        .Hint("끊김 감지까지 약 30초. 재진입하면 진행 확인 - 재정비가 아니면 다음 재정비를 기다렸다가 국면을 넘긴다(최대 90초)"));

    /// <summary>
    /// 재정비 중 재접속 점검(반복용): 재정비를 늘려 두고(이번 재정비만 - 오브젝트 수가 안 바뀌게) 지금 연결만 끊어 스스로 재진입하게 한 뒤,
    /// 반복해도 망가지지 않는지 본다 - 클라가 가진 오브젝트 수 = 서버 트리가 그 좌석에 보내야 하는 수, 트리 등록 수 = 방에 실제 스폰된 수
    /// (장부가 안 샘). 진행 확인(국면 넘기기)은 하지 않는다(Reconnect가 본다) - 재정비가 그대로 이어지므로 연달아 돌릴 수 있다.
    /// </summary>
    public static TH_Node ReconnectPlanning(TH_UnityClient c) => TH_Node.For(nameof(ReconnectPlanning), c, id => TH_Node.Action(id,
            $"재정비 중에 {c.Name}의 지금 NGO 연결만 끊어(hold=false) 스스로 재진입하게 하고, 오브젝트 수가 서버와 맞는지와 트리 장부가 새지 않는지 본다",
            $"client.ingame_disconnected 뒤 network.role=client(재진입), 인게임 화면, {c.Name} process.status spawnedObjects = server ingame.rooms " +
            "replication.visibleTo[그 플레이어], replication.objects = replication.roomSpawned",
            async r =>
            {
                await EnsurePlanning(r, c);
                var roomId = await TH_Rooms.Of(r, c);
                // 이번 재정비만 늘린다(CurrentOnly - 설정값은 그대로라 지속 조작이 아님). 재접속 한 바퀴(약 40초)가 재정비 안에 끝나게.
                await r.Agent(Server, "cheat.planning_duration", new { mode = "CurrentOnly", seconds = ReconnectPlanningHoldSeconds, roomId });

                var (cut, _) = await r.Agent(c, "net.link", new { connected = false, hold = false });
                var detected = await r.Expect(c, new[] { "client.ingame_disconnected" }, cut, DisconnectDetectSeconds);
                if (await r.WaitFor(c, new[] { "network.role" }, SeqOf(detected, cut), RejoinGiveUpSeconds, "\"client\"") == null)
                    throw TH_RunContext.Fail($"{c.Name}: 끊김 감지 뒤 {RejoinGiveUpSeconds}초 안에 재진입하지 못함(network.role=client 없음)");
                if (await r.PollAgent(c, "process.status", IngameScreen, 30) is not { } screen || !IngameScreen(screen)) return; // 판정이 화면으로 이유를 쓴다
                await r.Agent(c, "client.state"); // 판정이 playerId를 읽는다

                // 클라 수와 서버 수를 같은 때에 읽어 맞춰 본다 - 스폰 메시지가 아직 오는 중일 수 있어 잠깐 기다린다(판정은 마지막 쌍으로).
                for (var waited = 0; waited < ReplicationSettleSeconds; waited++)
                {
                    var (_, status) = await r.Agent(c, "process.status");
                    var (_, rooms) = await r.Agent(Server, "ingame.rooms");
                    if (status is { } s && rooms is { } v && ReplicationMatches(s, v, c.PlayerId(r))) break;
                    await Task.Delay(1000, r.Cancellation);
                }
            },
            j => ReconnectedConsistent(j, c))
        .From(c, TH_States.Ingame).To(c, TH_States.Ingame)
        .Forbids(OverrideKinds.NetLink, OverrideKinds.NetCondition)
        .Hint("재정비가 아니면 다음 재정비를 기다린다(최대 240초). 끊김 감지까지 약 30초 - 반복 점검용이라 여러 번 연달아 골라도 된다"));

    /// <summary>
    /// 끊기(유지): 다시 잇기 전까지 끊어 두고(net.link hold=true - 재진입도 막힘) 끊김이 감지될 때까지. 클라는 Disconnected(서버 재접속 유예 중) -
    /// 이 상태에서 다시 잇기(Restore), 유예 만료(GraceExpire), 끊긴 동안 일어나는 게임 일을 보는 노드를 고른다. 끊김은 다음 노드까지 남긴다.
    /// </summary>
    public static TH_Node Cut(TH_UnityClient c) => TH_Node.For(nameof(Cut), c, id => TH_Node.Action(id,
            $"{c.Name}의 NGO 연결을 다시 잇기 전까지 끊어 두고(hold - 재진입도 막힘) 끊김이 감지될 때까지 기다린다. 끊김은 다음 노드까지 남긴다",
            $"net.link_changed(link, connected=false), 그 뒤 {c.Name} client.ingame_disconnected(끊김 감지)",
            async r =>
            {
                var (cut, _) = await r.Agent(c, "net.link", new { connected = false });
                await r.Expect(c, new[] { "client.ingame_disconnected" }, cut, DisconnectDetectSeconds);
                r.KeepOverrides(); // 끊김(net_link)을 다음 노드까지 - Restore가 잇거나 시나리오 끝에 하네스가 잇는다
            },
            j => CutDone(j, c))
        .From(c, TH_States.Ingame).To(c, TH_States.Disconnected)
        .Forbids(OverrideKinds.NetLink, OverrideKinds.NetCondition)
        .Hint("끊김 감지 뒤 서버 재접속 유예는 약 20초, 클라 재진입 시도는 약 10초 - Disconnected에서 고르는 노드는 그 안에 끝나야 의미가 있다"));

    /// <summary>
    /// 다시 잇기: Disconnected에서 연결을 되살려 클라가 재진입해 붙는지, 그 뒤 게임이 그대로 이어지는지. 클라의 재진입 시도(감지 직후 한 번, 약 10초)
    /// 안에 이어야 한다 - Cut 바로 다음에 고를 것.
    /// </summary>
    public static TH_Node Restore(TH_UnityClient c) => TH_Node.For(nameof(Restore), c, id => TH_Node.Action(id,
            $"끊어 둔 {c.Name}의 NGO 연결을 다시 이어 재진입해 붙는지와 게임이 그대로 이어지는지 본다(Cut 바로 다음 - 재진입 시도 안에)",
            $"net.link 다시 잇기 뒤 {c.Name} network.role=client(재진입), 인게임 화면 복귀, 국면을 넘겼을 때(cheat.skip_planning) phase.changed(PvP)를 받음",
            async r =>
            {
                var (restored, _) = await r.Agent(c, "net.link", new { connected = true });
                await r.Agent(c, "client.state"); // 판정이 playerId를 기록에서 읽는다
                if (await r.WaitFor(c, new[] { "network.role" }, restored, RejoinGiveUpSeconds, "\"client\"") == null)
                    throw TH_RunContext.Fail($"{c.Name}: 다시 이었는데 {RejoinGiveUpSeconds}초 안에 재진입하지 못함 - 재진입 시도(약 10초)가 이미 끝났는지(Cut 직후에 골랐는지), " +
                                             "클라 log.recent를 볼 것");
                await ConfirmProgress(r, c);
            },
            j => Restored(j, c))
        .From(c, TH_States.Disconnected).To(c, TH_States.Ingame)
        .Needs(OverrideKinds.NetLink) // Cut이 남긴 끊김이 있어야 의미가 있다
        .Hint("재진입 시도는 끊김 감지 직후 한 번(약 10초) - Cut 바로 다음에 고를 것. 늦으면 클라가 포기해서 이어도 안 붙는다"));

    /// <summary>
    /// 유예 만료: Disconnected에서 끊긴 채 두어 클라가 재진입을 포기하고 서버가 재접속 유예 뒤 그 플레이어를 탈락시키는지. 탈락 뒤 클라 상태는 아직
    /// 정하지 않아 메모로만 남긴다(TH_States.Abandoned). 끊김은 시나리오 끝에 하네스가 잇는다.
    /// </summary>
    public static TH_Node GraceExpire(TH_UnityClient c) => TH_Node.For(nameof(GraceExpire), c, id => TH_Node.Action(id,
            $"끊어 둔 {c.Name}를 그대로 두어 서버의 탈락 처리(재접속 유예 만료)를 본다. 탈락 뒤 클라 상태는 메모만(처리 미정)",
            $"노드 구간에 재접속 없음(network.role=client 없음), {Server.Name} player.health_changed(eliminated, {c.Name}의 playerId - client.state)",
            async r =>
            {
                var since = r.LastSeq;
                var (_, state) = await r.Agent(c, "client.state"); // 판정이 playerId를 기록에서 읽는다
                var playerId = state?.Str("playerId") ?? c.PlayerId(r) ?? throw TH_RunContext.Fail($"{c.Name}의 playerId를 모름");

                // 그 플레이어의 체력 변화 중 탈락이 올 때까지(다른 변화는 넘긴다). 안 오면 판정이 이유를 쓴다.
                var deadline = DateTimeOffset.UtcNow.AddSeconds(EliminationSeconds + RejoinGiveUpSeconds);
                var from = since;
                while (DateTimeOffset.UtcNow < deadline)
                {
                    var left = (int)Math.Ceiling((deadline - DateTimeOffset.UtcNow).TotalSeconds);
                    if (await r.WaitFor(Server, new[] { "player.health_changed" }, from, Math.Max(1, left), playerId) is not { } changed) break;
                    if (changed.Bool("data.eliminated")) break;
                    from = SeqOf(changed, r.LastSeq);
                }

                var (_, _, screen, _) = await r.TryAgent(c, "process.status");
                r.Note($"탈락 뒤 {c.Name} 상태(판정 안 함 - 처리 미정): " +
                       (screen is { } s ? $"scene={s.Str("scene")}, stage={s.Str("stage")}, networkRole={s.Str("networkRole")}, uiManager={s.Str("uiManager")}" : "process.status 실패"));
                // 끊김(net_link)은 Cut이 남긴 것이라 시나리오 끝에 하네스가 잇는다
            },
            j => GraceExpired(j, c))
        .From(c, TH_States.Disconnected).To(c, TH_States.Abandoned)
        .Needs(OverrideKinds.NetLink) // 끊어 둔 채여야 유예 만료를 볼 수 있다
        .Hint("탈주 뒤 클라가 어디로 가야 하는지는 미정 - 정해지면 판정에 넣는다. 이 노드 뒤로는 정리만"));

    private static long SeqOf(JsonElement eventView, long fallback) => eventView.Num("seq") is { } n ? (long)n : fallback;

    /// <summary>
    /// 게임 진행을 받는지 확인 - 인게임 화면을 기다리고, 서버 쪽에서 재정비가 될 때까지 기다렸다가(최대 90초) 국면을 넘기고(cheat.skip_planning)
    /// 클라가 그 변화(phase.changed PvP)를 받는지 기다린다. 시점을 기다리지 않고 하네스가 유발한다(준비 → 유발 → 관찰).
    /// </summary>
    /// <summary>ReconnectPlanning이 늘려 두는 이번 재정비 길이(초) - 재접속 한 바퀴(감지 약 30초 + 재진입 + 확인)보다 넉넉히.</summary>
    private const int ReconnectPlanningHoldSeconds = 180;

    /// <summary>재진입 뒤 클라 오브젝트 수가 서버와 맞을 때까지 기다리는 최대 초.</summary>
    private const int ReplicationSettleSeconds = 15;

    private static async Task ConfirmProgress(TH_RunContext r, TH_UnityClient c)
    {
        // 30초 안에 인게임 화면이 안 되면 진행 확인은 하지 않는다 - 판정(ProgressReceived)이 실제 화면으로 실패 이유를 쓴다(재정비를 최대 90초 기다릴 필요 없음).
        if (await r.PollAgent(c, "process.status", IngameScreen, 30) is not { } screen || !IngameScreen(screen)) return;
        var roomId = await TH_Rooms.Of(r, c);
        for (var waited = 0; waited < PlanningWaitSeconds; waited++)
        {
            var (_, ok, players, _) = await r.TryAgent(Server, "cheat.players", new { roomId });
            if (!ok) throw TH_RunContext.Fail($"{c.Name}의 방 {roomId} 상태를 못 읽음(cheat.players) - 방이 닫혔는지 볼 것");
            if (players?.Str("phase") == "Planning") break;
            await Task.Delay(1000, r.Cancellation);
        }

        var (skip, skipped, _, error) = await r.TryAgent(Server, "cheat.skip_planning", new { roomId });
        if (!skipped) throw TH_RunContext.Fail($"{PlanningWaitSeconds}초 안에 재정비가 오지 않았거나 국면을 넘기지 못함(cheat.skip_planning): {error}");
        await r.WaitFor(c, new[] { "phase.changed" }, skip, 30, "PvP"); // 안 오면 판정이 이유를 쓴다
    }

    // ================================ 테스트 조작(cheat) - Unity 클라만 ================================

    public static TH_Node BuyPlace(TH_UnityClient c) => TH_Node.For(nameof(BuyPlace), c, id => TH_Node.Action(id,
            $"재정비 중에 cheat.buy로 {c.Name}의 플레이어가 기물(Warrior) 하나를 사서 보드 0번 칸에 놓는다",
            "cheat.buy 결과 placed=true 또는 merged=true, 그리고 gold = goldBefore(골드 불변)",
            async r =>
            {
                await EnsurePlanning(r, c);
                var (_, bought) = await r.Agent(Server, "cheat.buy",
                    new { player = await PlayerOf(r, c), piece = "Warrior", boardIndex = 0, roomId = await TH_Rooms.Of(r, c) });
                r.Values.For(c).Set(TH_ValueKeys.Merged, bought?.Bool("merged") == true);
                await r.Agent(c, "shop.units");
            },
            j => j.LastCommandValue("cheat.buy") is { } bought
                ? TH_Verdict.PassIf(
                    (bought.Value.Bool("placed") || bought.Value.Bool("merged")) && bought.Value.Num("gold") == bought.Value.Num("goldBefore"),
                    $"placed={bought.Value.Bool("placed")}, merged={bought.Value.Bool("merged")}, 골드 {bought.Value.Num("goldBefore")}→{bought.Value.Num("gold")}",
                    $"기대와 다름: placed={bought.Value.Bool("placed")}, merged={bought.Value.Bool("merged")}, placeSkipped={bought.Value.Str("placeSkipped")}, " +
                    $"골드 {bought.Value.Num("goldBefore")}→{bought.Value.Num("gold")}",
                    bought.Seq)
                : TH_Verdict.Fail("이 노드 구간에 성공한 cheat.buy가 없음"))
        .From(c, TH_States.Ingame));

    /// <summary>AI 판정 노드 - 하네스는 shop.units만 보고, 합성 결과가 맞는지는 AI가 node_judge로 판정한다.</summary>
    public static TH_Node MergeCheck(TH_UnityClient c) => TH_Node.For(nameof(MergeCheck), c, id => TH_Node.Action(id,
            $"BuyPlace@{c.Name}에서 합성이 일어났을 때 합성된 기물을 확인한다(shop.units)",
            $"{c.Name} shop.units에 star=2 이상인 기물이 있다(근거: 그 shop.units 호출의 seq)",
            async r => await r.Agent(c, "shop.units"))
        .AiJudged()
        .From(c, TH_States.Ingame)
        .Hint($"BuyPlace@{c.Name} 결과에 merged=true일 때만 의미 있음"));

    public static TH_Node Sell(TH_UnityClient c) => TH_Node.For(nameof(Sell), c, id => TH_Node.Action(id,
            $"{c.Name}에서 shop.units로 내 기물 하나를 골라 shop.sell로 판다(없으면 먼저 cheat.buy로 하나 산다)",
            "shop.sell 호출 성공 뒤 그 클라의 gold.changed로 골드가 늘어남",
            async r =>
            {
                await EnsurePlanning(r, c);
                var unit = await FirstUnit(r, c);
                if (unit == null)
                {
                    await r.Agent(Server, "cheat.buy", new { player = await PlayerOf(r, c), piece = "Warrior", roomId = await TH_Rooms.Of(r, c) });
                    unit = await FirstUnit(r, c) ?? throw TH_RunContext.Fail("cheat.buy 뒤에도 내 기물이 없음");
                }

                var (since, _) = await r.Agent(c, "shop.sell", new { networkObjectId = (long)(unit.Value.Num("networkObjectId") ?? 0) });
                await r.WaitFor(c, new[] { "gold.changed" }, since, 15);
            },
            j =>
            {
                if (j.LastCommand(c, "shop.sell") is not { } sold) return TH_Verdict.Fail($"이 노드 구간에 성공한 {c.Name} shop.sell이 없음");
                var gain = j.FirstAfter(sold.SinceSeq, c, "gold.changed", d => d.Num("gold") > d.Num("previous"));
                return gain != null
                    ? TH_Verdict.Pass($"판매 후 골드 {gain.Data?.Num("previous")}→{gain.Data?.Num("gold")}", sold.Seq, gain.Seq)
                    : TH_Verdict.Fail("판매 요청 뒤 골드가 늘어난 gold.changed가 없음(재정비 중이었는지, 내 기물이었는지 확인)", sold.Seq);
            })
        .From(c, TH_States.Ingame));

    public static TH_Node PlanningCurrentOnly(TH_UnityClient c) => TH_Node.For(nameof(PlanningCurrentOnly), c, id => TH_Node.Action(id,
            $"재정비 중에 cheat.planning_duration(CurrentOnly, 3초)로 {c.Name} 방의 이번 재정비만 줄여 PvP로 넘어가게 한다",
            "cheat.planning_duration(CurrentOnly) 성공 뒤 그 클라의 phase.changed(PvP)",
            async r =>
            {
                await EnsurePlanning(r, c);
                var (since, _) = await r.Agent(Server, "cheat.planning_duration", new { mode = "CurrentOnly", seconds = 3, roomId = await TH_Rooms.Of(r, c) });
                await r.WaitFor(c, new[] { "phase.changed" }, since, 30, "PvP");
            },
            j => AfterCommand(j, c, "cheat.planning_duration", v => v.Str("mode") == "CurrentOnly", "phase.changed", d => d.Str("phase") == "PvP"))
        .From(c, TH_States.Ingame));

    public static TH_Node PlanningSettingOnly(TH_UnityClient c) => TH_Node.For(nameof(PlanningSettingOnly), c, id => TH_Node.Action(id,
            $"cheat.planning_duration(SettingOnly, 10)으로 {c.Name} 방의 설정값을 바꾸고 다음 재정비 국면이 올 때까지 기다린다(배속 3으로 빨리 넘김)",
            "SettingOnly 결과 settingAfter=10, 그 뒤 그 클라의 phase.changed(Planning)",
            async r =>
            {
                var (since, _) = await r.Agent(Server, "cheat.planning_duration", new { mode = "SettingOnly", seconds = 10, roomId = await TH_Rooms.Of(r, c) });
                await r.TryAgent(Server, "cheat.time_scale", new { scale = 3 });
                await r.WaitFor(c, new[] { "phase.changed" }, since, 240, "Planning");
                // 설정값(10)과 배속(3)은 지속 조작 - 노드가 끝나면 하네스가 처음 값으로 되돌린다.
            },
            j => AfterCommand(j, c, "cheat.planning_duration", v => v.Str("mode") == "SettingOnly" && v.Num("settingAfter") == 10,
                "phase.changed", d => d.Str("phase") == "Planning"))
        .From(c, TH_States.Ingame));

    public static TH_Node PlanningRescale(TH_UnityClient c) => TH_Node.For(nameof(PlanningRescale), c, id => TH_Node.Action(id,
            $"재정비 중에 cheat.planning_duration(Rescale, 20)으로 {c.Name} 방의 설정값을 20으로 바꾸고 이번 재정비 길이는 비율대로 바뀌는지 본다",
            "Rescale 결과 settingAfter=20, currentAfter = 20 × currentBefore / settingBefore",
            async r =>
            {
                await EnsurePlanning(r, c);
                await r.Agent(Server, "cheat.planning_duration", new { mode = "Rescale", seconds = 20, roomId = await TH_Rooms.Of(r, c) });
            },
            j => j.LastCommandValue("cheat.planning_duration", v => v.Str("mode") == "Rescale") is { } rescale
                ? JudgeRescale(rescale.Value, rescale.Seq)
                : TH_Verdict.Fail("이 노드 구간에 성공한 Rescale 호출이 없음(재정비 중이었는지 확인)"))
        .From(c, TH_States.Ingame));

    public static TH_Node Damage(TH_UnityClient c) => TH_Node.For(nameof(Damage), c, id => TH_Node.Action(id,
            $"cheat.damage로 {c.Name} 방의 살아 있는 봇 하나에게 데미지(10)를 준다", "그 방, 그 팀의 player.health_changed에서 health < previous",
            async r =>
            {
                var bot = (await Players(r, c)).FirstOrDefault(p => p.Bool("isBot") && !p.Bool("eliminated"));
                if (bot.ValueKind == JsonValueKind.Undefined) throw TH_RunContext.Fail("살아 있는 봇이 없음");
                var (since, _) = await r.Agent(Server, "cheat.damage", new { player = bot.Str("team"), amount = 10, roomId = await TH_Rooms.Of(r, c) });
                await r.WaitFor(Server, new[] { "player.health_changed" }, since, 15);
            },
            j =>
            {
                if (j.LastCommand("cheat.damage") is not { } damage) return TH_Verdict.Fail("이 노드 구간에 성공한 cheat.damage가 없음");
                var call = j.Calls.LastOrDefault(x => x.Seq == damage.Seq);
                var team = call?.Arguments?.Str("args.player");
                var roomId = call?.Arguments?.Str("args.roomId");
                var hit = j.FirstAfter(damage.SinceSeq, Server, "player.health_changed",
                    d => d.Num("health") < d.Num("previous") && (team == null || d.Str("team") == team) && (roomId == null || d.Str("roomId") == roomId));
                return hit != null
                    ? TH_Verdict.Pass($"{hit.Data?.Str("team")} 체력 {hit.Data?.Num("previous")}→{hit.Data?.Num("health")} (방 {roomId})", damage.Seq, hit.Seq)
                    : TH_Verdict.Fail($"cheat.damage({team}, 방 {roomId}) 뒤 체력이 줄어든 player.health_changed가 없음", damage.Seq);
            })
        .From(c, TH_States.Ingame));

    // ================================ 정리 ================================

    public static readonly TH_Node Cleanup = TH_Node.Action("Cleanup",
            "헤드리스 클라를 닫고, Play 중이면 멈추고, 서버를 끄고 Docker 컨테이너를 제거한다(남은 지속 조작은 이 노드 전에 하네스가 되돌림)",
            "editor.play_mode(EnteredEditMode) 또는 Play가 아님, 그리고 servers.stopped 또는 서버 전부 꺼짐, 그리고 servers_stop 결과 Docker=removed",
            async r =>
            {
                var clients = await r.Call("client_state");
                foreach (var alias in clients.IsError ? Enumerable.Empty<string>() : clients.Value.Items("clients").Select(x => x.Str("alias")).OfType<string>())
                    await r.Call("client_close", new { alias });

                var (_, ok, status, _) = await r.TryAgent(TH_Unity.Editor, "editor.status");
                if (ok && status?.Bool("isPlaying") == true)
                {
                    var (since, _) = await r.Agent(TH_Unity.Editor, "editor.stop");
                    await r.WaitFor(TH_Unity.Editor, new[] { "editor.play_mode" }, since, 60, "EnteredEditMode");
                }
                await r.Call("servers_stop", new { stopInfra = true }); // Docker 컨테이너까지 제거(docker-compose down)
                await r.Call("harness_status");
            },
            CleanedUp)
        .ToInitial();

    // ================================ 도우미 ================================

    /// <summary>그 클라 방의 참가자 전원(cheat.players).</summary>
    private static async Task<List<JsonElement>> Players(TH_RunContext r, TH_UnityClient c)
    {
        var (_, value) = await r.Agent(Server, "cheat.players", new { roomId = await TH_Rooms.Of(r, c) });
        return value?.Items("players").ToList() ?? new List<JsonElement>();
    }

    /// <summary>cheat 명령의 player 인자로 넘길 그 클라의 플레이어(playerId). 방 참가자 중에서 확인한다.</summary>
    private static async Task<string> PlayerOf(TH_RunContext r, TH_UnityClient c)
    {
        var playerId = c.PlayerId(r) ?? throw TH_RunContext.Fail($"{c.Name}의 playerId를 모름 - Login@{c.Name}을 먼저 돌릴 것");
        var me = (await Players(r, c)).FirstOrDefault(p => p.Str("playerId") == playerId);
        return me.ValueKind == JsonValueKind.Undefined
            ? throw TH_RunContext.Fail($"{c.Name}(playerId {playerId})이 그 방 참가자(cheat.players)에 없음")
            : playerId;
    }

    /// <summary>그 클라 방이 재정비 국면이 아니면 다음 재정비가 올 때까지 기다린다(cheat 명령 다수가 재정비 중에만 된다).</summary>
    private static async Task EnsurePlanning(TH_RunContext r, TH_UnityClient c)
    {
        var (since, value) = await r.Agent(Server, "cheat.players", new { roomId = await TH_Rooms.Of(r, c) });
        if (value?.Str("phase") == "Planning") return;
        await r.Expect(c, new[] { "phase.changed" }, since, 240, "Planning");
    }

    private static async Task<JsonElement?> FirstUnit(TH_RunContext r, TH_UnityClient c)
    {
        var (_, units) = await r.Agent(c, "shop.units");
        var first = units?.Items("units").FirstOrDefault();
        return first is { ValueKind: JsonValueKind.Object } unit ? unit : null;
    }

    /// <summary>Rescale: 설정값 → 20, 이번 재정비 길이는 비율 유지(currentAfter = settingAfter × currentBefore / settingBefore).
    /// 앞 노드가 설정값을 무엇으로 해 뒀든 응답의 settingBefore로 계산한다(노드 순서에 의존하지 않게).</summary>
    private static TH_Verdict JudgeRescale(JsonElement v, long seq)
    {
        var settingBefore = v.Num("settingBefore");
        var settingAfter = v.Num("settingAfter");
        var currentBefore = v.Num("currentBefore");
        var currentAfter = v.Num("currentAfter");
        var detail = $"설정값 {settingBefore}→{settingAfter}, 이번 길이 {currentBefore}→{currentAfter}";

        if (settingAfter != 20 || settingBefore is not { } sb || sb <= 0) return TH_Verdict.Fail($"설정값이 기대(→20)와 다름: {detail}", seq);
        if (currentBefore is not { } cb || currentAfter is not { } ca)
            return TH_Verdict.Fail($"이번 재정비 길이(currentBefore/After)가 응답에 없음 - 재정비 중이었는지 확인: {detail}", seq);

        var expected = 20 * cb / sb;
        return TH_Verdict.PassIf(Math.Abs(ca - expected) < 0.01, $"{detail} (기대 {expected:0.##})", $"이번 길이가 비율대로 안 바뀜: {detail}, 기대 {expected:0.##}", seq);
    }

    /// <summary>명령(조건) 성공 뒤에 그 클라의 이벤트(조건)가 왔는지.</summary>
    private static TH_Verdict AfterCommand(TH_JudgeContext j, TH_UnityClient c, string command, Func<JsonElement, bool> commandWhere,
        string clientEvent, Func<JsonElement, bool> eventWhere)
    {
        if (j.LastCommand(command, commandWhere) is not { } r) return TH_Verdict.Fail($"이 노드 구간에 조건에 맞는 성공한 {command}가 없음");
        var e = j.FirstAfter(r.SinceSeq, c, clientEvent, eventWhere);
        return e != null
            ? TH_Verdict.Pass($"{command} 뒤 {c.Name} {clientEvent} 확인", r.Seq, e.Seq)
            : TH_Verdict.Fail($"{command}는 성공했지만 그 뒤 {c.Name}의 기대한 {clientEvent}가 없음", r.Seq);
    }
}
