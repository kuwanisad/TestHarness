using System.Text.Json;
using AutoBattler.Shared;
using TestHarness.Core;
using TestHarness.Core.Scenarios;

namespace TestHarness.Scenarios;

/// <summary>이 어셈블리를 가리키는 표지(하네스 데몬이 typeof(ScenarioAssembly).Assembly로 시나리오를 등록한다).</summary>
public static class ScenarioAssembly
{
}

/// <summary>
/// 여러 시나리오가 같이 쓰는 판정 조각. 출처 이름은 Unity Multiplayer Play Mode 태그 기준(메인 = client, 가상 플레이어 =
/// server)이고, 에디터 브리지는 UnityEditor다.
/// </summary>
internal static class TH_CommonJudges
{
    public const string Client = "client";
    public const string Server = "server";
    public const string Editor = "UnityEditor";

    // 단계/씬 역할 값은 Unity와 같은 파일(Unity/Assets/Scripts/Shared/ClientScreenState.cs, 링크 컴파일)에서 온다 - 씬 이름에
    // 의존하지 않는다(씬은 Unity 인스펙터에서 에셋으로 지정되고, 클라가 그 씬인지를 sceneRole로 알려준다).
    private static readonly string StageLobby = nameof(ClientStage.Lobby);
    private static readonly string StageIngame = nameof(ClientStage.Ingame);
    private static readonly string SceneLogin = nameof(ClientSceneRole.Login);
    private static readonly string SceneIngame = nameof(ClientSceneRole.Ingame);

    /// <summary>화면 기대값의 설명(통과 기준 문구와 판정 이유에 같이 쓴다).</summary>
    public static readonly string LobbyScreenText =
        $"client process.status: stage={StageLobby}, localPawn=LobbyPawn, uiManager=LobbyUIManager, LoginPawn 0개, UIManager 1개";
    public static readonly string IngameScreenText =
        $"client process.status: sceneRole={SceneIngame}, stage={StageIngame}, autoChessView=true, uiManager=AutoChessUIManager, LobbyPawn/LoginPawn 0개, UIManager 1개";
    public static readonly string BackInLobbyScreenText =
        $"client process.status: sceneRole={SceneLogin}, stage={StageLobby}, localPawn=LobbyPawn, uiManager=LobbyUIManager, autoChessPawn=false, UIManager 1개";

    private static bool OneUiManager(JsonElement v) => v.Num("pawnCounts.uiManagers") == 1;

    private static bool LobbyScreen(JsonElement v) =>
        v.Str("stage") == StageLobby && v.Str("localPawn") == "LobbyPawn" && v.Str("uiManager") == "LobbyUIManager" &&
        v.Num("pawnCounts.login") == 0 && OneUiManager(v);

    private static bool IngameScreen(JsonElement v) =>
        v.Str("sceneRole") == SceneIngame && v.Str("stage") == StageIngame && v.Bool("autoChessView") && v.Str("uiManager") == "AutoChessUIManager" &&
        v.Num("pawnCounts.lobby") == 0 && v.Num("pawnCounts.login") == 0 && OneUiManager(v);

    private static bool BackInLobbyScreen(JsonElement v) =>
        v.Str("sceneRole") == SceneLogin && LobbyScreen(v) && !v.Bool("autoChessPawn");

    /// <summary>
    /// 기본 판정(이벤트)이 통과면, 그 뒤에 AI가 본 client process.status(마지막 것)로 화면까지 확인한다 - 이벤트만 보면
    /// "로비 UI가 인게임에 남거나 인게임 화면이 안 생기는" 버그를 못 잡는다(실제로 그렇게 놓친 적이 있음).
    /// </summary>
    private static TH_Verdict WithScreen(TH_JudgeContext c, TH_Verdict basis, long afterSeq, string expectText, Func<JsonElement, bool> expected)
    {
        if (!basis.Passed) return basis;

        var seqs = basis.EvidenceSeqs.ToList();
        var screen = c.LastCommandValue("process.status", agent: Client);
        if (screen is not { } s || s.Seq <= afterSeq)
            return TH_Verdict.Fail($"{basis.Reason} - 하지만 그 뒤 화면 확인이 없음: agent_command(client, process.status)로 볼 것(기대: {expectText})", seqs.ToArray());

        seqs.Add(s.Seq);
        return expected(s.Value)
            ? TH_Verdict.Pass($"{basis.Reason}, 화면 확인({expectText})", seqs.ToArray())
            : TH_Verdict.Fail($"{basis.Reason} - 하지만 화면이 기대와 다름. 기대: {expectText} / 실제: {DescribeScreen(s.Value)}", seqs.ToArray());
    }

    private static string DescribeScreen(JsonElement v) =>
        $"scene={v.Str("scene")}, sceneRole={v.Str("sceneRole")}, stage={v.Str("stage")}, localPawn={v.Str("localPawn")}, uiManager={v.Str("uiManager")}, " +
        $"autoChessPawn={v.Bool("autoChessPawn")}, autoChessView={v.Bool("autoChessView")}, " +
        $"pawnCounts(login={v.Num("pawnCounts.login")}, lobby={v.Num("pawnCounts.lobby")}, uiManagers={v.Num("pawnCounts.uiManagers")})";

    /// <summary>servers_start 결과가 report.ok=true.</summary>
    public static TH_Verdict ServersStarted(TH_JudgeContext c) =>
        c.LastToolResult("servers_start", r => r.Bool("report.ok")) is { } ok
            ? TH_Verdict.Pass("servers_start report.ok=true", ok.Seq)
            : c.LastToolResult("servers_start") is { } any
                ? TH_Verdict.Fail("servers_start가 실패(report.ok=false) - 실패 단계의 logTail 확인", any.Seq)
                : TH_Verdict.Fail("이 목표 구간에 servers_start 호출이 없음");

    /// <summary>account_create 결과가 ok=true.</summary>
    public static TH_Verdict AccountCreated(TH_JudgeContext c) =>
        c.LastToolResult("account_create", r => r.Bool("ok")) is { } ok
            ? TH_Verdict.Pass($"계정 {ok.Result.Str("accountId")} 생성", ok.Seq)
            : c.Calls.LastOrDefault(x => x.Tool == "account_create") is { } call
                ? TH_Verdict.Fail($"account_create가 실패: login={call.Result?.Get("login")?.GetRawText()}", call.Seq)
                : TH_Verdict.Fail("이 목표 구간에 account_create 호출이 없음");

    /// <summary>Unity 클라와 인게임 서버가 둘 다 하네스에 붙음(Play 시작).</summary>
    public static TH_Verdict UnityAttached(TH_JudgeContext c) => c.RequireAll(
        ("client 접속(harness.attached)", c.Last(Client, "harness.attached")),
        ("server 접속(harness.attached)", c.Last(Server, "harness.attached")));

    /// <summary>Unity 클라 로그인 성공 + 로비 화면(LobbyPawn).</summary>
    public static TH_Verdict LoggedIn(TH_JudgeContext c)
    {
        if (c.Last(Client, "client.login_result", d => d.Bool("success")) is { } ok)
            return WithScreen(c, TH_Verdict.Pass("client.login_result success=true", ok.Seq), ok.Seq, LobbyScreenText, LobbyScreen);
        return c.Last(Client, "client.login_result") is { } failed
            ? TH_Verdict.Fail($"로그인 실패: {failed.Data?.Str("failure")}", failed.Seq)
            : TH_Verdict.Fail("client.login_result가 없음");
    }

    /// <summary>매치 확정.</summary>
    public static TH_Verdict MatchFound(TH_JudgeContext c)
    {
        if (c.Last(Client, "client.match_found") is { } found) return TH_Verdict.Pass("client.match_found", found.Seq);
        return c.Last(Client, "client.match_failed") is { } failed
            ? TH_Verdict.Fail($"매칭 실패: {failed.Data?.Str("reason")}", failed.Seq)
            : TH_Verdict.Fail("client.match_found가 없음");
    }

    /// <summary>클라가 재정비 국면에 들어감(인게임 진입) + 인게임 화면(AutoChessView, 로비 UI 없음).</summary>
    public static TH_Verdict InPlanning(TH_JudgeContext c) =>
        c.Last(Client, "phase.changed", d => d.Str("phase") == "Planning") is { } e
            ? WithScreen(c, TH_Verdict.Pass("client phase.changed(Planning)", e.Seq), e.Seq, IngameScreenText, IngameScreen)
            : TH_Verdict.Fail("client의 phase.changed(Planning)가 없음 - 인게임 접속/방 승격을 server 쪽 room.*와 log.recent로 볼 것");

    /// <summary>게임이 끝나 로비로 돌아옴 + 로비 화면(로그인 씬, LobbyPawn).</summary>
    public static TH_Verdict BackInLobby(TH_JudgeContext c) =>
        c.Last(Client, "client.lobby_state", d => d.Str("state") == "InLobby") is { } e
            ? WithScreen(c, TH_Verdict.Pass("client.lobby_state(InLobby)", e.Seq), e.Seq, BackInLobbyScreenText, BackInLobbyScreen)
            : TH_Verdict.Fail("client.lobby_state(InLobby)가 없음");

    /// <summary>
    /// 정리: Play가 멈췄고 서버가 꺼짐. 시나리오가 Play 전에 멈췄을 수도 있으니 이벤트가 없으면 마지막 상태 조회로도 인정한다
    /// (editor.status의 isPlaying=false, harness_status의 servers 전부 processAlive=false).
    /// </summary>
    public static TH_Verdict CleanedUp(TH_JudgeContext c)
    {
        long? playSeq = c.Last(Editor, "editor.play_mode", d => d.Str("state") == "EnteredEditMode")?.Seq
                        ?? c.LastCommandValue("editor.status", v => v.Get("isPlaying") is { ValueKind: JsonValueKind.False })?.Seq;
        long? serverSeq = c.Last("harness", "servers.stopped")?.Seq
                          ?? c.LastToolResult("harness_status", AllServersDown)?.Seq;

        var missing = new List<string>();
        if (playSeq == null) missing.Add("Play 멈춤(editor.play_mode EnteredEditMode 또는 editor.status isPlaying=false)");
        if (serverSeq == null) missing.Add("서버 종료(servers.stopped 또는 harness_status에서 전부 꺼짐)");

        var evidence = new[] { playSeq, serverSeq }.Where(s => s != null).Select(s => s!.Value).ToArray();
        return missing.Count == 0
            ? TH_Verdict.Pass("Play 멈춤, 서버 종료 확인", evidence)
            : TH_Verdict.Fail("확인 안 됨: " + string.Join(", ", missing), evidence);
    }

    private static bool AllServersDown(JsonElement status) =>
        status.Get("servers") is { ValueKind: JsonValueKind.Array } servers &&
        servers.EnumerateArray().All(s => s.Get("processAlive") is not { ValueKind: JsonValueKind.True });
}
