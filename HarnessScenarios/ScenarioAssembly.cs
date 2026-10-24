using System.Text.Json;
using AutoBattler.Shared;
using TestHarness.Core;
using TestHarness.Core.Scenarios;
using TestHarness.Scenarios.Targets;

namespace TestHarness.Scenarios;

/// <summary>이 어셈블리를 가리키는 표지(하네스 데몬이 typeof(ScenarioAssembly).Assembly로 시나리오를 등록한다).</summary>
public static class ScenarioAssembly
{
}

/// <summary>
/// 여러 노드가 같이 쓰는 판정 조각(판정은 노드 구간의 기록만 본다 - 노드 실행(runner)의 도구 호출과 이벤트). 클라는 대상 인스턴스로
/// 받는다(출처 = 대상 이름, 예: client1). Unity 인게임 서버/에디터는 TH_Unity.Server/Editor.
/// </summary>
internal static class TH_CommonJudges
{
    private static string Server => TH_Unity.Server.Name;
    private static string Editor => TH_Unity.Editor.Name;

    // 단계/씬 역할 값은 Unity와 같은 파일(Unity/Assets/Scripts/Shared/ClientScreenState.cs, 링크 컴파일)에서 온다 - 씬 이름에
    // 의존하지 않는다(씬은 Unity 인스펙터에서 에셋으로 지정되고, 클라가 그 씬인지를 sceneRole로 알려준다).
    private static readonly string StageLobby = nameof(ClientStage.Lobby);
    private static readonly string StageIngame = nameof(ClientStage.Ingame);
    private static readonly string SceneLogin = nameof(ClientSceneRole.Login);
    private static readonly string SceneIngame = nameof(ClientSceneRole.Ingame);

    /// <summary>화면 기대값의 설명(통과 기준 문구와 판정 이유에 같이 쓴다).</summary>
    public static readonly string LobbyScreenText =
        $"process.status: stage={StageLobby}, localPawn=LobbyPawn, uiManager=LobbyUIManager, LoginPawn 0개, UIManager 1개";
    public static readonly string IngameScreenText =
        $"process.status: sceneRole={SceneIngame}, stage={StageIngame}, autoChessController=true, autoChessView=true, uiManager=AutoChessUIManager, LobbyPawn/LoginPawn 0개, UIManager 1개";
    public static readonly string BackInLobbyScreenText =
        $"process.status: sceneRole={SceneLogin}, stage={StageLobby}, localPawn=LobbyPawn, uiManager=LobbyUIManager, autoChessPawn=false, UIManager 1개";

    private static bool OneUiManager(JsonElement v) => v.Num("pawnCounts.uiManagers") == 1;

    /// <summary>process.status 값이 로비 화면인지(노드 실행이 씬 전환을 기다릴 때도 쓴다 - PollAgent).</summary>
    public static bool LobbyScreen(JsonElement v) =>
        v.Str("stage") == StageLobby && v.Str("localPawn") == "LobbyPawn" && v.Str("uiManager") == "LobbyUIManager" &&
        v.Num("pawnCounts.login") == 0 && OneUiManager(v);

    public static bool IngameScreen(JsonElement v) =>
        v.Str("sceneRole") == SceneIngame && v.Str("stage") == StageIngame && v.Bool("autoChessController") && v.Bool("autoChessView") &&
        v.Str("uiManager") == "AutoChessUIManager" &&
        v.Num("pawnCounts.lobby") == 0 && v.Num("pawnCounts.login") == 0 && OneUiManager(v);

    public static bool BackInLobbyScreen(JsonElement v) =>
        v.Str("sceneRole") == SceneLogin && LobbyScreen(v) && !v.Bool("autoChessPawn");

    /// <summary>
    /// 기본 판정(이벤트)이 통과면, 그 뒤에 노드 실행이 본 그 클라의 process.status(마지막 것)로 화면까지 확인한다 - 이벤트만 보면
    /// "로비 UI가 인게임에 남거나 인게임 화면이 안 생기는" 버그를 못 잡는다(실제로 그렇게 놓친 적이 있음).
    /// </summary>
    private static TH_Verdict WithScreen(TH_JudgeContext c, TH_UnityClient client, TH_Verdict basis, long afterSeq, string expectText,
        Func<JsonElement, bool> expected)
    {
        if (!basis.Passed) return basis;

        var seqs = basis.EvidenceSeqs.ToList();
        var screen = c.LastCommandValue("process.status", agent: client.Name);
        if (screen is not { } s || s.Seq <= afterSeq)
            return TH_Verdict.Fail($"{basis.Reason} - 하지만 그 뒤 화면 확인({client.Name} process.status)이 없음(기대: {expectText})", seqs.ToArray());

        seqs.Add(s.Seq);
        return expected(s.Value)
            ? TH_Verdict.Pass($"{basis.Reason}, {client.Name} 화면 확인({expectText})", seqs.ToArray())
            : TH_Verdict.Fail($"{basis.Reason} - 하지만 {client.Name} 화면이 기대와 다름. 기대: {expectText} / 실제: {DescribeScreen(s.Value)}", seqs.ToArray());
    }

    private static string DescribeScreen(JsonElement v) =>
        $"scene={v.Str("scene")}, sceneRole={v.Str("sceneRole")}, stage={v.Str("stage")}, localPawn={v.Str("localPawn")}, uiManager={v.Str("uiManager")}, " +
        $"autoChessController={v.Bool("autoChessController")}, autoChessPawn={v.Bool("autoChessPawn")}, autoChessView={v.Bool("autoChessView")}, " +
        $"pawnCounts(login={v.Num("pawnCounts.login")}, lobby={v.Num("pawnCounts.lobby")}, uiManagers={v.Num("pawnCounts.uiManagers")})";

    /// <summary>servers_start 결과가 report.ok=true.</summary>
    public static TH_Verdict ServersStarted(TH_JudgeContext c) =>
        c.LastToolResult("servers_start", r => r.Bool("report.ok")) is { } ok
            ? TH_Verdict.Pass("servers_start report.ok=true", ok.Seq)
            : c.LastToolResult("servers_start") is { } any
                ? TH_Verdict.Fail("servers_start가 실패(report.ok=false) - 실패 단계의 logTail 확인", any.Seq)
                : TH_Verdict.Fail("이 노드 구간에 servers_start 호출이 없음");

    /// <summary>account_create 결과가 ok=true.</summary>
    public static TH_Verdict AccountCreated(TH_JudgeContext c) =>
        c.LastToolResult("account_create", r => r.Bool("ok")) is { } ok
            ? TH_Verdict.Pass($"계정 {ok.Result.Str("accountId")} 생성", ok.Seq)
            : c.Calls.LastOrDefault(x => x.Tool == "account_create") is { } call
                ? TH_Verdict.Fail($"account_create가 실패: login={call.Result?.Get("login")?.GetRawText()}", call.Seq)
                : TH_Verdict.Fail("이 노드 구간에 account_create 호출이 없음");

    /// <summary>
    /// UnityPlay 판정: 전원 harness.attached → 전원 출발 씬(HarnessBoot)에서 시작(test.role startScene) → 시작 씬으로 넘어감(scene.loaded) →
    /// 역할이 실제로 시작됨(서버 network.role=server, 클라 로그인 화면).
    /// </summary>
    public static TH_Verdict UnityAttached(TH_JudgeContext c, TH_UnityEditor editor, TH_UnityServer server, IReadOnlyList<TH_UnityClient> clients)
    {
        var attached = c.RequireAll(
            clients.Select(client => ($"{client.Name} 접속(harness.attached)", c.Last(client, "harness.attached")))
                .Append(($"{server.Name} 접속(harness.attached)", c.Last(server, "harness.attached")))
                .ToArray());
        if (!attached.Passed) return attached;

        var verdicts = new List<TH_Verdict>
        {
            attached,
            RoleStarted(c, editor, server.Name, server.StartScene, "Server",
                c.Last(server, "network.role", d => d.Str("role") == "server")?.Seq, "network.role=server(NGO 서버 시작)"),
        };
        foreach (var client in clients)
        {
            var screen = c.LastCommandValue("process.status", v => v.Str("sceneRole") == "Login", client.Name);
            verdicts.Add(RoleStarted(c, editor, client.Name, client.StartScene, "Client", screen?.Seq, "로그인 화면(process.status sceneRole=Login)"));
        }
        return TH_Verdict.All(verdicts.ToArray());
    }

    /// <summary>
    /// 출발 씬이 맞지 않으면 그 이유(맞으면 null) - 그 플레이어의 에디터가 Play 시작 씬(HarnessBoot)을 맞추지 못한 경우.
    /// UnityPlay 실행(씬을 넘기기 전에 멈춤)과 판정이 같이 쓴다.
    /// </summary>
    public static string? BootSceneMismatch(TH_UnityEditor editor, string agent, string? startScene)
    {
        if (editor.BootSceneName is not { } expected || startScene == null || string.Equals(startScene, expected, StringComparison.Ordinal)) return null;
        return $"{agent}: 출발 씬 {expected}에서 시작해야 하는데 {startScene}에서 시작함 - 그 플레이어의 에디터가 Play 시작 씬을 맞추지 못함. " +
               "준비(준비@UnityEditor, editor.set_play_start)가 성공했는지, 가상 플레이어 에디터 콘솔에 HarnessPlayStartSync 경고가 있는지, " +
               "씬 이름이 Unity 씬 파일 이름과 같은지(editor.scenes) 확인";
    }

    /// <summary>출발 씬에서 시작했고(test.role의 startScene), 시작 씬으로 넘어갔고(scene.loaded), 그 역할이 실제로 시작됐는지(startedSeq).</summary>
    private static TH_Verdict RoleStarted(TH_JudgeContext c, TH_UnityEditor editor, string agent, string? startScene, string expectedRole, long? startedSeq,
        string startedText)
    {
        var role = c.Last(agent, "test.role");
        if (BootSceneMismatch(editor, agent, role?.Data?.Str("startScene")) is { } mismatch) return TH_Verdict.Fail(mismatch, role!.Seq);

        var loaded = startScene == null ? null : c.Last(agent, "scene.loaded", d => d.Str("scene") == startScene);
        if (startScene != null && loaded == null)
            return TH_Verdict.Fail($"{agent}: 시작 씬 {startScene}으로 넘어가지 않음(scene.loaded 없음) - scene.load 응답 오류(Scene List에 있는지, 이름이 같은지)를 볼 것",
                role != null ? new[] { role.Seq } : Array.Empty<long>());
        if (startedSeq == null)
            return TH_Verdict.Fail($"{agent}: {startScene ?? "열린 씬"}에 들어갔지만 {expectedRole} 역할로 시작하지 않음(기대: {startedText}) - " +
                                   "시나리오가 선언한 시작 씬이 그 역할의 씬인지, 그 플레이어의 log.recent 확인",
                loaded != null ? new[] { loaded.Seq } : Array.Empty<long>());
        return TH_Verdict.Pass($"{agent} {expectedRole} 역할 시작({startScene ?? "열린 씬"} → {startedText})", startedSeq.Value);
    }

    // ================================ 재접속 / 탈주 ================================

    /// <summary>노드 구간에 그 클라의 인게임 끊김이 없음(짧은 끊김은 끊김 감지 시간보다 짧아야 연결이 유지된다).</summary>
    public static TH_Verdict NoIngameDisconnect(TH_JudgeContext c, TH_UnityClient client) =>
        c.Last(client, "client.ingame_disconnected") is { } e
            ? TH_Verdict.Fail($"{client.Name}: 짧은 끊김인데 인게임 접속이 끊김 - {e.Data?.Str("reason")}", e.Seq)
            : TH_Verdict.Pass($"{client.Name} 인게임 접속 유지(끊김 없음)");

    /// <summary>
    /// 게임 진행을 받음 - 노드가 국면을 넘긴 뒤(cheat.skip_planning) 그 클라가 phase.changed(PvP)를 받았고, afterSeq 뒤의 화면이 인게임 화면.
    /// </summary>
    public static TH_Verdict ProgressReceived(TH_JudgeContext c, TH_UnityClient client, long afterSeq)
    {
        // 화면부터 - 인게임 화면이 없으면 진행(phase.changed)도 못 받는 게 당연해 "진행이 안 옴"이 원인처럼 보인다(10-23 PC: 재진입 뒤 기물/UI가 없었는데
        // 실패 이유는 phase.changed 못 받음). 화면이 기대와 다르면 그것을 실패 이유로(실제 화면 설명 포함).
        var screen = c.LastCommandValue("process.status", agent: client.Name);
        if (screen is not { } s || s.Seq <= afterSeq)
            return TH_Verdict.Fail($"{client.Name}: 진행 확인 전 화면 확인(process.status)이 없음(기대: {IngameScreenText})");
        if (!IngameScreen(s.Value))
            return TH_Verdict.Fail($"{client.Name}: 인게임 화면이 아님 - 기대: {IngameScreenText} / 실제: {DescribeScreen(s.Value)}", s.Seq);

        var skip = c.LastCall("agent_command", "cheat.skip_planning");
        if (skip?.Result is not { } result || !result.Bool("ok"))
            return TH_Verdict.Fail($"{client.Name}: 진행 확인을 못 함 - 국면을 넘기지 못함(재정비가 안 왔거나 방이 없음, cheat.skip_planning)",
                skip != null ? new[] { skip.Seq } : Array.Empty<long>());

        var since = result.Num("sinceSeq") is { } n ? (long)n : skip.Seq;
        if (c.FirstAfter(since, client, "phase.changed", d => d.Str("phase") == "PvP") is not { } phase)
            return TH_Verdict.Fail($"{client.Name}: 국면을 넘겼는데 phase.changed(PvP)를 못 받음 - 게임 진행이 클라에 안 옴(서버 쪽 국면은 넘어감)", skip.Seq);

        return TH_Verdict.Pass($"{client.Name} 화면 확인({IngameScreenText}), 넘긴 국면을 받음(phase.changed PvP)", s.Seq, phase.Seq);
    }

    /// <summary>BriefDrop: 순간 끊김이 끝났고, 끊김 없이 진행을 받음.</summary>
    public static TH_Verdict BriefDropped(TH_JudgeContext c, TH_UnityClient client)
    {
        if (c.Last(client, "net.link_changed", d => d.Str("cause") == "lag_spike_end") is not { } end)
            return TH_Verdict.Fail($"{client.Name}: 순간 끊김이 끝나지 않음(net.link_changed lag_spike_end 없음)");
        return TH_Verdict.All(NoIngameDisconnect(c, client), ProgressReceived(c, client, end.Seq));
    }

    /// <summary>Reconnect: 끊김이 감지됐고, 클라가 스스로 재진입해 붙었고(network.role=client), 그 뒤 인게임 화면과 진행을 받음.</summary>
    public static TH_Verdict Reconnected(TH_JudgeContext c, TH_UnityClient client)
    {
        if (c.First(client, "client.ingame_disconnected") is not { } detected)
            return TH_Verdict.Fail($"{client.Name}: 끊었는데 끊김이 감지되지 않음(client.ingame_disconnected 없음)");
        if (c.FirstAfter(detected.Seq, client, "network.role", d => d.Str("role") == "client") is not { } rejoined)
            return Unrejoined(c, client, detected.Seq);
        return TH_Verdict.All(
            TH_Verdict.Pass($"{client.Name} 끊김 감지 뒤 재진입(network.role=client)", detected.Seq, rejoined.Seq),
            ProgressReceived(c, client, rejoined.Seq));
    }

    /// <summary>
    /// ReconnectPlanning: 재진입했고, 인게임 화면이고, 클라 오브젝트 수(process.status spawnedObjects) = 서버 트리가 그 좌석에 보내야 하는 수
    /// (ingame.rooms replication.visibleTo[playerId]), 트리 등록 수 = 방에 실제 스폰된 수(replication.objects = roomSpawned). 마지막 쌍으로 본다.
    /// </summary>
    public static TH_Verdict ReconnectedConsistent(TH_JudgeContext c, TH_UnityClient client)
    {
        if (c.First(client, "client.ingame_disconnected") is not { } detected)
            return TH_Verdict.Fail($"{client.Name}: 끊었는데 끊김이 감지되지 않음(client.ingame_disconnected 없음)");
        if (c.FirstAfter(detected.Seq, client, "network.role", d => d.Str("role") == "client") is not { } rejoined)
            return Unrejoined(c, client, detected.Seq);

        if (c.LastCommandValue("process.status", agent: client.Name) is not { } screen || screen.Seq <= rejoined.Seq)
            return TH_Verdict.Fail($"{client.Name}: 재진입 뒤 화면 확인(process.status)이 없음", rejoined.Seq);
        if (!IngameScreen(screen.Value))
            return TH_Verdict.Fail($"{client.Name}: 재진입 뒤 인게임 화면이 아님 - 기대: {IngameScreenText} / 실제: {DescribeScreen(screen.Value)}", screen.Seq);

        var playerId = c.LastCommandValue("client.state", agent: client.Name)?.Value.Str("playerId");
        if (playerId == null) return TH_Verdict.Fail($"{client.Name}: playerId를 못 읽음(client.state)", screen.Seq);
        if (c.LastCommandValue("ingame.rooms", agent: TH_Unity.Server.Name) is not { } rooms)
            return TH_Verdict.Fail("서버 방 상태(ingame.rooms)를 못 읽음", screen.Seq);

        var detail = DescribeReplication(screen.Value, rooms.Value, playerId);
        return ReplicationMatches(screen.Value, rooms.Value, playerId)
            ? TH_Verdict.Pass($"{client.Name} 재진입 뒤 인게임 화면, 오브젝트 수 서버와 일치, 트리 장부 일치 - {detail}", detected.Seq, rejoined.Seq, screen.Seq, rooms.Seq)
            : TH_Verdict.Fail($"{client.Name} 재진입 뒤 오브젝트 수가 맞지 않음 - {detail}", screen.Seq, rooms.Seq);
    }

    /// <summary>클라 spawnedObjects = 서버 visibleTo[playerId], 그리고 서버 replication.objects = roomSpawned인지.</summary>
    public static bool ReplicationMatches(JsonElement clientStatus, JsonElement serverRooms, string? playerId)
    {
        if (playerId == null || RoomReplicationOf(serverRooms, playerId) is not { } rep) return false;
        var clientCount = clientStatus.Num("spawnedObjects");
        var visible = rep.Get("visibleTo") is { } v && v.TryGetProperty(playerId, out var n) && n.ValueKind == JsonValueKind.Number ? n.GetDouble() : (double?)null;
        return clientCount != null && clientCount == visible && rep.Num("objects") is { } objects && objects == rep.Num("roomSpawned");
    }

    private static string DescribeReplication(JsonElement clientStatus, JsonElement serverRooms, string playerId)
    {
        if (RoomReplicationOf(serverRooms, playerId) is not { } rep) return $"서버 ingame.rooms에 playerId {playerId}가 접속 중인 방이 없음";
        var visible = rep.Get("visibleTo") is { } v && v.TryGetProperty(playerId, out var n) ? n.GetRawText() : "?";
        return $"클라 spawnedObjects={clientStatus.Num("spawnedObjects")}, 서버 visibleTo={visible}, 트리 objects={rep.Num("objects")}, " +
               $"roomSpawned={rep.Num("roomSpawned")}, 마지막 접속 Show={rep.Num("lastConnectShows")}, 누적 Show={rep.Num("shows")}";
    }

    private static JsonElement? RoomReplicationOf(JsonElement serverRooms, string playerId) =>
        serverRooms.Items("rooms")
            .Select(room => room.Get("replication"))
            .FirstOrDefault(rep => rep is { } r && r.Get("visibleTo") is { ValueKind: JsonValueKind.Object } v && v.TryGetProperty(playerId, out _));

    /// <summary>Cut: 끊었고(net.link_changed link) 끊김이 감지됨.</summary>
    public static TH_Verdict CutDone(TH_JudgeContext c, TH_UnityClient client)
    {
        if (c.First(client, "net.link_changed", d => d.Str("cause") == "link" && !d.Bool("connected")) is not { } cut)
            return TH_Verdict.Fail($"{client.Name}: 끊기(net.link_changed link) 기록이 없음");
        return c.FirstAfter(cut.Seq, client, "client.ingame_disconnected") is { } detected
            ? TH_Verdict.Pass($"{client.Name} 끊김 감지({detected.Data?.Str("reason")})", cut.Seq, detected.Seq)
            : TH_Verdict.Fail($"{client.Name}: 끊었는데 끊김이 감지되지 않음(client.ingame_disconnected 없음)", cut.Seq);
    }

    /// <summary>Restore: 다시 이은 뒤 재진입해 붙었고(network.role=client), 그 뒤 인게임 화면과 진행을 받음.</summary>
    public static TH_Verdict Restored(TH_JudgeContext c, TH_UnityClient client)
    {
        if (c.First(client, "net.link_changed", d => d.Str("cause") == "link" && d.Bool("connected")) is not { } restored)
            return TH_Verdict.Fail($"{client.Name}: 다시 잇기(net.link_changed link, connected=true) 기록이 없음");
        if (c.FirstAfter(restored.Seq, client, "network.role", d => d.Str("role") == "client") is not { } rejoined)
            return Unrejoined(c, client, restored.Seq);
        return TH_Verdict.All(
            TH_Verdict.Pass($"{client.Name} 다시 이은 뒤 재진입(network.role=client)", restored.Seq, rejoined.Seq),
            ProgressReceived(c, client, rejoined.Seq));
    }

    private static TH_Verdict Unrejoined(TH_JudgeContext c, TH_UnityClient client, long afterSeq)
    {
        var giveUp = c.FirstAfter(afterSeq, client, "client.ingame_disconnected");
        return TH_Verdict.Fail($"{client.Name}: 재진입하지 못함" + (giveUp != null ? $"(재진입 포기: {giveUp.Data?.Str("reason")})" : ""),
            giveUp != null ? new[] { afterSeq, giveUp.Seq } : new[] { afterSeq });
    }

    /// <summary>GraceExpire: 노드 구간에 재접속 없음(끊어 둔 채), 서버가 그 플레이어(client.state의 playerId)를 탈락시킴. 탈락 뒤 클라 상태는 판정하지 않음(미정).</summary>
    public static TH_Verdict GraceExpired(TH_JudgeContext c, TH_UnityClient client)
    {
        if (c.First(client, "network.role", d => d.Str("role") == "client") is { } rejoined)
            return TH_Verdict.Fail($"{client.Name}: 끊어 둔 채인데 재접속됨 - 끊김 유지(net.link reapplied)가 안 됨", rejoined.Seq);
        if (c.LastCommandValue("client.state", agent: client.Name)?.Value.Str("playerId") is not { Length: > 0 } playerId)
            return TH_Verdict.Fail($"{client.Name}: playerId를 모름(client.state 기록 없음) - 탈락을 확인할 수 없음");
        if (c.Last(TH_Unity.Server, "player.health_changed", d => d.Bool("eliminated") && d.Str("playerId") == playerId) is not { } eliminated)
            return TH_Verdict.Fail($"{client.Name}: 끊긴 채인데 서버가 탈락시키지 않음(player.health_changed eliminated, playerId {playerId}) - " +
                                   "유예가 노드 시작 전에 이미 지났는지(Cut 뒤 바로 골랐는지) 볼 것");
        var giveUp = c.First(client, "client.ingame_disconnected");
        return TH_Verdict.Pass($"{client.Name} {(giveUp != null ? "재진입 포기 → " : "")}{Server} 탈락 확정(유예 만료)",
            giveUp != null ? new[] { giveUp.Seq, eliminated.Seq } : new[] { eliminated.Seq });
    }

    /// <summary>Unity 클라 로그인 성공 + 로비 화면(LobbyPawn).</summary>
    public static TH_Verdict LoggedIn(TH_JudgeContext c, TH_UnityClient client)
    {
        if (c.Last(client, "client.login_result", d => d.Bool("success")) is { } ok)
            return WithScreen(c, client, TH_Verdict.Pass($"{client.Name} client.login_result success=true", ok.Seq), ok.Seq, LobbyScreenText, LobbyScreen);
        return c.Last(client, "client.login_result") is { } failed
            ? TH_Verdict.Fail($"{client.Name} 로그인 실패: {failed.Data?.Str("failure")}", failed.Seq)
            : TH_Verdict.Fail($"{client.Name}의 client.login_result가 없음");
    }

    /// <summary>매치 확정.</summary>
    public static TH_Verdict MatchFound(TH_JudgeContext c, TH_UnityClient client)
    {
        if (c.Last(client, "client.match_found") is { } found) return TH_Verdict.Pass($"{client.Name} client.match_found", found.Seq);
        return c.Last(client, "client.match_failed") is { } failed
            ? TH_Verdict.Fail($"{client.Name} 매칭 실패: {failed.Data?.Str("reason")}", failed.Seq)
            : TH_Verdict.Fail($"{client.Name}의 client.match_found가 없음");
    }

    /// <summary>클라가 재정비 국면에 들어감(인게임 진입) + 인게임 화면(AutoChessView, 로비 UI 없음).</summary>
    public static TH_Verdict InPlanning(TH_JudgeContext c, TH_UnityClient client) =>
        c.Last(client, "phase.changed", d => d.Str("phase") == "Planning") is { } e
            ? WithScreen(c, client, TH_Verdict.Pass($"{client.Name} phase.changed(Planning)", e.Seq), e.Seq, IngameScreenText, IngameScreen)
            : TH_Verdict.Fail($"{client.Name}의 phase.changed(Planning)가 없음 - 인게임 접속/방 승격을 {Server} 쪽 room.*와 log.recent로 볼 것");

    /// <summary>게임이 끝나 로비로 돌아옴 + 로비 화면(로그인 씬, LobbyPawn).</summary>
    public static TH_Verdict BackInLobby(TH_JudgeContext c, TH_UnityClient client) =>
        c.Last(client, "client.lobby_state", d => d.Str("state") == "InLobby") is { } e
            ? WithScreen(c, client, TH_Verdict.Pass($"{client.Name} client.lobby_state(InLobby)", e.Seq), e.Seq, BackInLobbyScreenText, BackInLobbyScreen)
            : TH_Verdict.Fail($"{client.Name}의 client.lobby_state(InLobby)가 없음");

    /// <summary>
    /// 정리: Play가 멈췄고 서버가 꺼짐. 시나리오가 Play 전에 끝났을 수도 있으니 이벤트가 없으면 마지막 상태 조회로도 인정한다
    /// (editor.status의 isPlaying=false, 또는 harness_status에 Unity 게임 쪽 에이전트(client*/server)가 없음 / harness_status의
    /// servers 전부 processAlive=false).
    /// </summary>
    public static TH_Verdict CleanedUp(TH_JudgeContext c)
    {
        long? playSeq = c.Last(Editor, "editor.play_mode", d => d.Str("state") == "EnteredEditMode")?.Seq
                        ?? c.LastCommandValue("editor.status", v => v.Get("isPlaying") is { ValueKind: JsonValueKind.False })?.Seq
                        ?? c.LastToolResult("harness_status", NoUnityGameAgents)?.Seq;
        long? serverSeq = c.Last("harness", "servers.stopped")?.Seq
                          ?? c.LastToolResult("harness_status", AllServersDown)?.Seq;

        // Docker 컨테이너 제거(servers_stop 기본 stopInfra=true → 결과 steps의 Docker=removed).
        var stop = c.LastToolResult("servers_stop");
        var docker = stop?.Result.Items("report.steps").FirstOrDefault(s => s.Str("name") == "Docker");
        long? dockerSeq = docker is { ValueKind: JsonValueKind.Object } d && d.Str("status") == "removed" ? stop!.Value.Seq : null;

        var missing = new List<string>();
        if (playSeq == null) missing.Add("Play 멈춤(editor.play_mode EnteredEditMode 또는 editor.status isPlaying=false)");
        if (serverSeq == null) missing.Add("서버 종료(servers.stopped 또는 harness_status에서 전부 꺼짐)");
        if (dockerSeq == null)
            missing.Add($"Docker 컨테이너 제거(servers_stop 결과 Docker=removed - 지금: {(docker is { ValueKind: JsonValueKind.Object } x ? $"{x.Str("status")} {x.Str("detail")}" : "Docker 단계 없음")})");

        var evidence = new[] { playSeq, serverSeq, dockerSeq }.Where(s => s != null).Select(s => s!.Value).ToArray();
        return missing.Count == 0
            ? TH_Verdict.Pass("Play 멈춤, 서버 종료, Docker 컨테이너 제거 확인", evidence)
            : TH_Verdict.Fail("확인 안 됨: " + string.Join(", ", missing), evidence);
    }

    private static bool NoUnityGameAgents(JsonElement status) =>
        !status.Items("agents").Any(a => a.Str("name") is { } name && a.Bool("connected") &&
                                         (name == Server || name.StartsWith("client", StringComparison.OrdinalIgnoreCase)));

    private static bool AllServersDown(JsonElement status) =>
        status.Get("servers") is { ValueKind: JsonValueKind.Array } servers &&
        servers.EnumerateArray().All(s => s.Get("processAlive") is not { ValueKind: JsonValueKind.True });
}
