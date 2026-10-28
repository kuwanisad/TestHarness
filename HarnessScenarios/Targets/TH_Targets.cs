using System.Text.Json;
using TestHarness.Core.Scenarios;
using TestHarness.HeadlessClient;
using TestHarness.Scenarios.Nodes;

namespace TestHarness.Scenarios.Targets;

// 이 게임의 테스트 대상들(TH_Target 파생). 대상마다 "어떻게 조작하나"(명령/도구 이름)와 "어떻게 판정하나"를 갖고, 노드는 공통 동작만
// 부른다 - Unity 클라든 헤드리스 클라든 같은 노드(Login, EnterGame ...)를 탈 수 있다.
//
// 공통 동작과 생략된 동작:
// - 공통(TH_Client): 계정 준비, 로그인(로비까지), 매칭. 노드 함수가 TH_Client를 받는다.
// - Unity 클라만(TH_UnityClient): 인게임(국면, 상점, cheat 대상), 화면 확인. 노드 함수가 TH_UnityClient를 받는다 - 헤드리스 클라를
//   넘기면 컴파일이 안 된다(생략된 동작을 시나리오를 만들 때 잡는다).
// - 헤드리스 클라는 매칭이 잡혀도 인게임에 접속하지 못한다(HeadlessClient) - 매칭 뒤 상태가 Ingame이 아니라 Matched.

/// <summary>클라 공통(로그인, 로비, 매칭). 상태 칸을 가진다(시작: Offline).</summary>
public abstract class TH_Client : TH_Target
{
    protected TH_Client(string name, string kind) : base(name, kind)
    {
    }

    public override string InitialState => States.Offline;

    /// <summary>로그인 노드를 시작할 수 있는 이 클라의 상태(Unity: Play 뒤 로그인 화면, 헤드리스: 접속 전).</summary>
    public abstract string LoginFrom { get; }

    /// <summary>매칭이 잡힌 뒤 이 클라의 상태(Unity: Ingame - 자동으로 인게임 접속, 헤드리스: Matched - 인게임 접속 못 함).</summary>
    public abstract string AfterMatchState { get; }

    /// <summary>로그인에 쓸 계정을 준비한다(테스트 대상의 데이터 변화).</summary>
    public abstract Task PrepareAccount(TH_RunContext r);

    public abstract TH_Verdict JudgeAccount(TH_JudgeContext c);

    /// <summary>로그인하고 로비까지 들어간다. 끝나면 값 칸에 playerId를 남긴다.</summary>
    public abstract Task Login(TH_RunContext r);

    public abstract TH_Verdict JudgeLogin(TH_JudgeContext c);

    /// <summary>매칭을 요청한다(결과는 기다리지 않음). 요청 직전 이벤트 번호를 돌려준다.</summary>
    public abstract Task<long> JoinMatch(TH_RunContext r);

    /// <summary>매칭 결과를 기다린다(Unity: 인게임 진입과 화면, 방까지).</summary>
    public abstract Task AfterJoin(TH_RunContext r, long since);

    public abstract TH_Verdict JudgeMatch(TH_JudgeContext c);

    /// <summary>로그인 때 남긴 playerId(없으면 null).</summary>
    public string? PlayerId(TH_RunContext r) => r.Values.For(this).Get<string>(ValueKeys.PlayerId);
}

/// <summary>
/// Unity 클라 - Multiplayer Play Mode 태그 Client1, Client2 ...를 가진 플레이어(메인 에디터 또는 가상 플레이어). 하네스 이름은 태그를
/// 소문자로(client1). 조작은 agent_command(client.*, shop.* ...), 이벤트 출처는 agent:client1.
/// </summary>
public sealed class TH_UnityClient : TH_Client
{
    public TH_UnityClient(string name, string? startScene = null) : base(name, "UnityClient")
    {
        if (!name.StartsWith(TargetNames.UnityClientNamePrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Unity 클라 이름 '{name}': MPPM 태그가 {TargetNames.UnityClientTagPrefix}로 시작해야 한다" +
                                        $"(태그 {TargetNames.UnityClientTagPrefix}1 → 이름 {TargetNames.UnityClientNamePrefix}1)");
        Tag = TargetNames.UnityClientTagPrefix + name[TargetNames.UnityClientNamePrefix.Length..];
        StartScene = string.IsNullOrWhiteSpace(startScene) ? null : startScene;
    }

    /// <summary>MPPM 태그(이름의 첫 글자만 대문자 - client1 → Client1).</summary>
    public string Tag { get; }

    /// <summary>
    /// Play 뒤 이 플레이어가 HarnessBoot에서 넘어갈 시작 씬(UnityPlay가 scene.load로 시킴 - HarnessConstants.Scenes). 시나리오가 선언한다
    /// (s.UnityClient(name, startScene)). ⚠ Unity 씬 파일 이름과 같고 Scene List에 있어야 한다.
    /// </summary>
    public string? StartScene { get; }

    public override string? Precondition => $"Multiplayer Play Mode: 태그 {Tag}인 플레이어(메인 에디터 또는 가상 플레이어)가 Play에 참가 (하네스 이름 {Name})";

    public override string LoginFrom => States.Login;

    public override string AfterMatchState => States.Ingame;

    public override async Task PrepareAccount(TH_RunContext r)
    {
        var result = await r.Call(HarnessConstants.Tools.AccountCreate);
        if (result.IsError || !result.Value.Bool("ok")) return; // 판정이 이유를 쓴다
        var values = r.Values.For(this);
        values.Set(ValueKeys.AccountId, result.Value.Str("accountId"));
        values.Set(ValueKeys.Password, result.Value.Str("password"));
        r.Note($"{Name} 계정 {result.Value.Str("accountId")}");
    }

    public override TH_Verdict JudgeAccount(TH_JudgeContext c) => TH_CommonJudges.AccountCreated(c);

    public override async Task Login(TH_RunContext r)
    {
        var values = r.Values.For(this);
        var accountId = values.Get<string>(ValueKeys.AccountId) ?? throw TH_RunContext.Fail($"{Name}의 계정이 없음 - AccountReady@{Name}을 먼저 돌릴 것");
        var (since, _) = await r.Agent(this, Commands.ClientLogin, new { accountId, password = values.Get<string>(ValueKeys.Password) });
        var result = await r.Expect(this, new[] { Events.ClientLoginResult }, since, Waits.LoginResultSeconds);
        if (result.Get("data") is { } data && !data.Bool("success")) return; // 판정이 실패 이유를 쓴다
        await r.PollAgent(this, Commands.StatusScreen, TH_CommonJudges.LobbyScreen, Waits.ScreenSeconds, TH_CommonJudges.ScreenArgs);

        var (_, state) = await r.Agent(this, Commands.ClientState);
        if (state?.Str("playerId") is { Length: > 0 } playerId)
        {
            values.Set(ValueKeys.PlayerId, playerId);
            r.Note($"{Name} playerId {playerId}");
        }
    }

    public override TH_Verdict JudgeLogin(TH_JudgeContext c) => TH_CommonJudges.LoggedIn(c, this);

    public override async Task<long> JoinMatch(TH_RunContext r)
    {
        r.Values.For(this).Remove(ValueKeys.RoomId); // 새 매치 - 지난 방은 잊는다
        var (since, _) = await r.Agent(this, Commands.ClientJoinMatch);
        return since;
    }

    public override async Task AfterJoin(TH_RunContext r, long since)
    {
        var found = await r.Expect(this, new[] { Events.ClientMatchFound, Events.ClientMatchFailed }, since, Waits.MatchSeconds);
        if (found.Str("name") == Events.ClientMatchFailed) return; // 판정이 실패 이유를 쓴다
        await r.Expect(this, new[] { Events.PhaseChanged }, since, Waits.FirstPlanningSeconds, Values.PhasePlanning);
        await r.PollAgent(this, Commands.StatusScreen, TH_CommonJudges.IngameScreen, Waits.ScreenSeconds, TH_CommonJudges.ScreenArgs);
        await TH_Rooms.Resolve(r, this); // 이 클라의 방(cheat 명령에 roomId로 넘긴다 - 방이 여럿일 수 있다)
    }

    public override TH_Verdict JudgeMatch(TH_JudgeContext c) =>
        TH_Verdict.All(TH_CommonJudges.MatchFound(c, this), TH_CommonJudges.InPlanning(c, this));
}

/// <summary>
/// 헤드리스 클라(HeadlessClient - 화면 없이 로그인/로비/매칭 서버와 진짜 클라처럼 통신). 조작은 client_* 도구(alias = 이름), 이벤트 출처는
/// client:별칭. 인게임 접속은 못 한다. 이름은 client로 시작하면 안 된다(Unity 클라 에이전트 이름과 이벤트 출처가 섞이지 않게 - 예: hc1).
/// </summary>
public sealed class TH_HeadlessClient : TH_Client
{
    public TH_HeadlessClient(string alias) : base(alias, "HeadlessClient")
    {
        if (alias.StartsWith(TargetNames.UnityClientNamePrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"헤드리스 클라 별칭 '{alias}': client로 시작하면 Unity 클라와 이벤트 출처가 섞인다 - hc1처럼 지을 것");
    }

    public override string LoginFrom => States.Offline;

    public override string AfterMatchState => States.Matched;

    public override async Task PrepareAccount(TH_RunContext r)
    {
        // 새 계정 - client_login이 계정을 만든 뒤 로그인한다(client_create 설명).
        var result = await r.Call(HarnessConstants.Tools.ClientCreate, new { alias = Name });
        if (result.IsError) return; // 판정이 이유를 쓴다
        r.Values.For(this).Set(ValueKeys.Created, true);
        r.Note($"{Name} 헤드리스 클라 생성");
    }

    public override TH_Verdict JudgeAccount(TH_JudgeContext c) =>
        c.LastCallWith(HarnessConstants.Tools.ClientCreate, "alias", Name) is { } call
            ? TH_Verdict.PassIf(!call.IsError, $"헤드리스 클라 {Name} 생성", $"client_create 실패: {call.Result?.Str("error")}", call.Seq)
            : TH_Verdict.Fail($"이 노드 구간에 client_create(alias={Name}) 호출이 없음");

    public override async Task Login(TH_RunContext r)
    {
        if (!r.Values.For(this).Has(ValueKeys.Created)) throw TH_RunContext.Fail($"{Name}이 없음 - AccountReady@{Name}을 먼저 돌릴 것");
        var result = await r.Call(HarnessConstants.Tools.ClientLogin, new { alias = Name, enterLobby = true });
        if (result.IsError) return; // 판정이 이유를 쓴다
        if (result.Value.Str("login.playerId") is { Length: > 0 } playerId)
        {
            r.Values.For(this).Set(ValueKeys.PlayerId, playerId);
            r.Note($"{Name} playerId {playerId}");
        }
    }

    public override TH_Verdict JudgeLogin(TH_JudgeContext c)
    {
        if (c.LastCallWith(HarnessConstants.Tools.ClientLogin, "alias", Name) is not { } call) return TH_Verdict.Fail($"이 노드 구간에 client_login(alias={Name}) 호출이 없음");
        if (call.IsError || call.Result is not { } result) return TH_Verdict.Fail($"client_login 실패: {call.Result?.Str("error")}", call.Seq);
        if (!result.Bool("login.success"))
            return TH_Verdict.Fail($"로그인 거부: {result.Str("login.rejectReason") ?? result.Str("login.clientFailure")} {result.Str("login.detail")}", call.Seq);
        return TH_Verdict.PassIf(result.Bool("lobby.success"), $"{Name} 로그인 + 로비 입장",
            $"로비 입장 실패: {result.Str("lobby.result")} {result.Str("lobby.detail")}", call.Seq);
    }

    public override async Task<long> JoinMatch(TH_RunContext r)
    {
        var result = await r.CallOk(HarnessConstants.Tools.ClientJoinMatch, new { alias = Name });
        return (long)(result.Num("sinceSeq") ?? r.LastSeq);
    }

    public override async Task AfterJoin(TH_RunContext r, long since) =>
        await r.Expect(this, new[] { nameof(ClientEventKind.MatchFound), nameof(ClientEventKind.MatchFailed), nameof(ClientEventKind.MatchStreamClosed) }, since,
            Waits.MatchSeconds);

    public override TH_Verdict JudgeMatch(TH_JudgeContext c)
    {
        if (c.Last(this, nameof(ClientEventKind.MatchFound)) is { } found) return TH_Verdict.Pass($"{Name} MatchFound", found.Seq);
        var failed = c.Last(this, nameof(ClientEventKind.MatchFailed)) ?? c.Last(this, nameof(ClientEventKind.MatchStreamClosed));
        return failed != null
            ? TH_Verdict.Fail($"{Name} 매칭 실패: {failed.Name} {failed.Detail}", failed.Seq)
            : TH_Verdict.Fail($"{Name}의 MatchFound가 없음");
    }
}

/// <summary>Unity 인게임 서버(MPPM 태그 Server인 가상 플레이어, 하네스 이름 server). 상태 칸 없음.</summary>
public sealed class TH_UnityServer : TH_Target
{
    public TH_UnityServer(string name = TargetNames.UnityServerName, string? startScene = null) : base(name, "UnityServer")
    {
        StartScene = string.IsNullOrWhiteSpace(startScene) ? null : startScene;
    }

    /// <summary>
    /// Play 뒤 인게임 서버가 HarnessBoot에서 넘어갈 시작 씬(UnityPlay가 scene.load로 시킴 - 보통 Scenes.ServerBoot). 시나리오가 선언한다
    /// (s.UnityServer(startScene)). ⚠ Unity 씬 파일 이름과 같고 Scene List에 있어야 한다.
    /// </summary>
    public string? StartScene { get; }

    public override string? Precondition => $"Multiplayer Play Mode: 태그 Server인 가상 플레이어(인게임 서버, 하네스 이름 {Name})";
}

/// <summary>
/// Unity 에디터 자체(에디터 브리지, 하네스 이름 UnityEditor) - Play/Stop/재컴파일. 상태 칸 없음.
///
/// <see cref="BootScene"/>: Play 출발 씬(보통 빈 씬 HarnessBoot). 시나리오 시작 때 하네스가 준비 단계(<see cref="PrepareAsync"/>)로 Play 시작 씬을
/// 이 씬으로 정한다(editor.set_play_start - 메인 에디터와 MPPM 가상 플레이어 모두, 에디터에 열린 씬은 그대로). Play 뒤 플레이어마다 어느 씬으로
/// 갈지는 대상의 StartScene(UnityPlay가 scene.load로 시킴). 시작 씬은 시나리오 끝에 되돌린다(지속 조작 play_start).
/// 왜 빈 씬에서 출발하나, 왜 열린 씬을 바꾸지 않나: Unity/ARCHITECTURE.md "HarnessBoot 씬".
///
/// ⚠ 씬 이름은 Unity 쪽 씬 파일 이름(확장자 뺀 것)과 정확히 같아야 한다(대소문자 포함) - 에디터 브리지/에이전트가 이름으로 찾는다. Unity에서 씬
/// 이름을 바꾸면 HarnessConstants.Scenes도 같이 고칠 것. 확인은 agent_command(UnityEditor, editor.scenes).
/// </summary>
public sealed class TH_UnityEditor : TH_Target
{
    public TH_UnityEditor(string name = HarnessSharedConstants.Connection.EditorAgentName, string? bootScene = null) : base(name, "UnityEditor")
    {
        BootScene = string.IsNullOrWhiteSpace(bootScene) ? null : bootScene;
    }

    /// <summary>Play 출발 씬 이름(또는 Assets/...unity 경로). null이면 에디터에 열린 씬 그대로(준비 없음).</summary>
    public string? BootScene { get; }

    /// <summary>출발 씬의 이름 부분(test.role의 startScene과 비교).</summary>
    public string? BootSceneName => BootScene == null ? null : System.IO.Path.GetFileNameWithoutExtension(BootScene);

    public override string? Precondition =>
        "Unity 에디터가 열려 있고 에디터 브리지가 켜져 있음(agent_list에 UnityEditor), Play는 꺼진 상태" +
        (BootScene == null ? "" : $", 열린 씬에 저장 안 한 변경이 없음(하네스가 Play 시작 씬을 {BootScene}으로 정해 두므로 저장 안 한 변경이 있으면 Play 때 저장 창이 뜸 - editor.play가 거부)");

    public override string? Preparation => BootScene == null ? null : $"Play 시작 씬을 출발 씬 {BootScene}으로 정함(메인 에디터와 가상 플레이어 모두)";

    public override async Task PrepareAsync(TH_RunContext r)
    {
        if (BootScene == null) return;
        var (_, ok, value, error) = await r.TryAgent(this, Commands.EditorSetPlayStart, new { scene = BootScene });
        if (!ok)
            throw TH_RunContext.Fail($"Play 시작 씬을 {BootScene}으로 정하지 못함: {error} - 씬 이름이 Unity 씬 파일 이름과 같은지(editor.scenes) 확인");
        var previous = value?.Get("previous") is { ValueKind: JsonValueKind.String } p ? p.GetString() : null; // JSON null = 정한 적 없음
        r.Note($"시작 씬 {value?.Str("playStart")}, 그 전 시작 씬 {previous ?? "없음(열린 씬에서 시작)"}");
    }
}

/// <summary>
/// .NET 서버 프로세스 하나(하네스 이름 = 서버 이름, 예: LobbyServer) - 경계 장애 규칙(fault.*)을 거는 곳(at). 상태 칸 없음.
/// 노드는 TH_Servers의 인스턴스를 쓴다.
/// </summary>
public sealed class TH_ServerProcess : TH_Target
{
    public TH_ServerProcess(string name) : base(name, "ServerProcess")
    {
    }
}

/// <summary>.NET 서버 8개(DevTools server-common.ps1의 순서와 같은 이름). 상태 칸이 없어 시나리오끼리 같이 써도 된다.</summary>
public static class TH_Servers
{
    public static readonly TH_ServerProcess Master = new(HarnessConstants.Servers.Master);
    public static readonly TH_ServerProcess Matching = new(HarnessConstants.Servers.Matching);
    public static readonly TH_ServerProcess Account = new(HarnessConstants.Servers.Account);
    public static readonly TH_ServerProcess Login = new(HarnessConstants.Servers.Login);
    public static readonly TH_ServerProcess DummySub = new(HarnessConstants.Servers.DummySub);
    public static readonly TH_ServerProcess Lobby = new(HarnessConstants.Servers.Lobby);
    public static readonly TH_ServerProcess GameAgent = new(HarnessConstants.Servers.GameAgent);
    public static readonly TH_ServerProcess Chat = new(HarnessConstants.Servers.Chat);
}

/// <summary>
/// 노드가 쓰는 Unity 서버/에디터 - 한 PC에 하나씩이라 노드 함수 인자로 받지 않고 이것을 쓴다. 상태 칸이 없어 노드 캐시 키가 되지 않으므로
/// (TH_Node.For는 클라를 첫 대상으로) 시나리오끼리 같이 써도 된다(이름만 쓴다). 시나리오는 s.UnityServer(startScene)/s.UnityEditor(bootScene)로
/// 자기 인스턴스를 선언하고 UnityPlay에 넘긴다(시작 씬, 출발 씬은 시나리오마다).
/// </summary>
public static class TH_Unity
{
    public static readonly TH_UnityServer Server = new();
    public static readonly TH_UnityEditor Editor = new();
}

/// <summary>클라가 들어간 인게임 방 찾기 - 인게임 서버 하나가 방을 여럿 가질 수 있어 cheat 명령에 roomId를 넘긴다.</summary>
public static class TH_Rooms
{
    /// <summary>ingame.rooms에서 이 클라의 playerId가 들어 있는 방을 찾아 값 칸에 남긴다(못 찾으면 실패).</summary>
    public static async Task<string> Resolve(TH_RunContext r, TH_UnityClient client)
    {
        var playerId = client.PlayerId(r) ?? throw TH_RunContext.Fail($"{client.Name}의 playerId를 모름 - Login@{client.Name}을 먼저 돌릴 것");
        var rooms = await r.PollAgent(TH_Unity.Server, Commands.IngameRooms, v => FindRoom(v, playerId) != null, Waits.RoomLookupSeconds);
        var roomId = rooms is { } v ? FindRoom(v, playerId) : null;
        if (roomId == null) throw TH_RunContext.Fail($"인게임 서버에 {client.Name}(playerId {playerId})이 든 방이 없음(ingame.rooms)");
        r.Values.For(client).Set(ValueKeys.RoomId, roomId);
        r.Note($"{client.Name} 방 {roomId}");
        return roomId;
    }

    /// <summary>값 칸의 방(없으면 찾는다).</summary>
    public static async Task<string> Of(TH_RunContext r, TH_UnityClient client) =>
        r.Values.For(client).Get<string>(ValueKeys.RoomId) ?? await Resolve(r, client);

    private static string? FindRoom(JsonElement rooms, string playerId) =>
        rooms.Items("rooms").FirstOrDefault(room => room.Items("expected").Any(p => p.ValueKind == JsonValueKind.String && p.GetString() == playerId))
            is { ValueKind: JsonValueKind.Object } found ? found.Str("roomId") : null;
}

/// <summary>시나리오에서 대상을 선언하는 줄임말.</summary>
public static class TH_TargetDeclarations
{
    /// <summary>Unity 클라 대상. startScene: Play 뒤 HarnessBoot에서 넘어갈 시작 씬(HarnessConstants.Scenes - 보통 Login).</summary>
    public static TH_UnityClient UnityClient(this TH_ScenarioBuilder s, string name, string startScene) => s.Target(new TH_UnityClient(name, startScene));

    public static TH_HeadlessClient HeadlessClient(this TH_ScenarioBuilder s, string alias) => s.Target(new TH_HeadlessClient(alias));

    /// <summary>Unity 인게임 서버 대상. startScene: Play 뒤 HarnessBoot에서 넘어갈 시작 씬(HarnessConstants.Scenes - 보통 ServerBoot).</summary>
    public static TH_UnityServer UnityServer(this TH_ScenarioBuilder s, string startScene) => s.Target(new TH_UnityServer(TH_Unity.Server.Name, startScene));

    /// <summary>Unity 에디터 대상. bootScene: Play 출발 씬(Scenes.HarnessBoot) - 시나리오 시작 때 하네스가 미리 연다.</summary>
    public static TH_UnityEditor UnityEditor(this TH_ScenarioBuilder s, string? bootScene = null) =>
        s.Target(new TH_UnityEditor(TH_Unity.Editor.Name, bootScene));
}
