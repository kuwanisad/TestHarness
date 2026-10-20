using System.ComponentModel;
using GameServer.Common;
using TestHarness.Core;
using TestHarness.Daemon.Mcp;

namespace TestHarness.Daemon.Tools;

/// <summary>
/// 헤드리스 클라(화면 없는 게임 클라 - 로그인/로비/매칭을 프로토콜로 직접 수행). 인게임(NGO) 접속은 못 한다 -
/// 인게임은 Unity 클라로 한다(2주차).
///
/// 모든 행동 도구는 sinceSeq(행동 직전의 마지막 이벤트 번호)를 돌려준다. 결과 이벤트는 그 값으로
/// wait_for(sinceSeq=...)를 걸어 기다린다 - 행동과 대기 사이에 이미 와버린 이벤트도 놓치지 않는다.
/// </summary>
public sealed class ClientTools
{
    private readonly HarnessSession _session;

    public ClientTools(HarnessSession session) => _session = session;

    private ClientPool Clients => _session.Clients;
    private long Seq => _session.Timeline.LastSeq;

    [McpTool("account_create")]
    [Description("계정만 만들어 둔다(계정 생성 → 로비 입장 → 로그아웃으로 세션까지 정리). Unity 클라는 계정을 만들 수 없으니 Unity로 " +
                 "로그인하기 전에 이걸로 계정을 만들고, 돌려받은 accountId/password로 agent_command(client.login)을 보낼 것.")]
    public async Task<object> AccountCreate(
        [Description("계정 ID(비우면 acct-<시각>-<번호>)")] string? accountId = null,
        [Description("비밀번호(비우면 harness-pw)")] string? password = null,
        CancellationToken ct = default)
    {
        var sinceSeq = Seq;
        var (id, pw, login, lobby, loggedOut) = await Clients.CreateAccountAsync(accountId, password, ct);
        return new
        {
            sinceSeq,
            ok = login.Success && loggedOut,
            accountId = id,
            password = pw,
            login,
            lobby,
            loggedOut,
            hint = login.Success && !loggedOut ? "계정은 만들어졌지만 세션 정리(로그아웃)에 실패 - 같은 계정 로그인이 SessionConflict일 수 있음" : null,
        };
    }

    [McpTool("client_create")]
    [Description("헤드리스 클라를 만든다(아직 접속 안 함). 계정 ID를 안 주면 '<alias>-<시각>' 새 계정을 쓴다(client_login이 계정 생성 후 로그인). " +
                 "이미 있는 계정으로 로그인하려면 accountId, password와 accountExists=true.")]
    public object ClientCreate(
        [Description("별칭(p1, p2 ...) - 이후 도구에서 이 이름으로 부르고, 이벤트 출처가 client:<alias>가 된다")] string alias,
        [Description("계정 ID(비우면 자동)")] string? accountId = null,
        [Description("비밀번호(비우면 harness-pw)")] string? password = null,
        [Description("이미 있는 계정인지(true면 생성하지 않고 로그인만)")] bool accountExists = false)
    {
        var state = Clients.Create(alias, accountId, password, accountExists);
        return new { sinceSeq = Seq, state };
    }

    [McpTool("client_login")]
    [Description("로그인 서버에 로그인(새 계정이면 생성 후 로그인)하고, enterLobby면 안내받은 로비에 입장까지 한다. 결과: 로그인/로비 결과" +
                 "(거부 사유는 열거형 이름, 예: SessionConflict)와 클라 상태. 응답이 오면 끝난 것이라 따로 기다릴 필요 없음.")]
    public async Task<object> ClientLogin(
        [Description("별칭")] string alias,
        [Description("로비 입장까지 할지")] bool enterLobby = true,
        CancellationToken ct = default)
    {
        var sinceSeq = Seq;
        var (login, lobby, state) = await Clients.LoginAsync(alias, enterLobby, ct);
        return new { sinceSeq, login, lobby, state };
    }

    [McpTool("client_enter_lobby")]
    [Description("로비 입장(EnterLobby)만 한다 - client_login(enterLobby=false) 뒤, 또는 게임 종료 후 로비 복귀(transferTicket 필요).")]
    public async Task<object> ClientEnterLobby(
        [Description("별칭")] string alias,
        [Description("로비 주소(비우면 마지막으로 안내받은 로비)")] string? lobbyAddress = null,
        [Description("전입 티켓(로비 복귀 때만)")] string? transferTicket = null,
        CancellationToken ct = default)
    {
        var sinceSeq = Seq;
        var (lobby, state) = await Clients.EnterLobbyAsync(alias, lobbyAddress, transferTicket, ct);
        return new { sinceSeq, lobby, state };
    }

    [McpTool("client_join_match")]
    [Description("매칭을 요청한다. 결과는 기다리지 않는다 - 돌려준 sinceSeq로 wait_for(source=<alias>, names=[MatchFound, MatchFailed, " +
                 "MatchStreamClosed])를 걸 것. 대기열 진입은 MatchQueued 이벤트.")]
    public object ClientJoinMatch(
        [Description("별칭")] string alias,
        [Description("게임 모드")] GameType gameType = GameType.Normal)
    {
        var (sinceSeq, state) = Clients.RequestMatch(alias, gameType);
        return new { sinceSeq, state };
    }

    [McpTool("client_cancel_match")]
    [Description("매칭 취소.")]
    public async Task<object> ClientCancelMatch([Description("별칭")] string alias, CancellationToken ct)
    {
        var sinceSeq = Seq;
        return new { sinceSeq, state = await Clients.CancelMatchAsync(alias, ct) };
    }

    [McpTool("client_logout")]
    [Description("로그아웃(서버가 세션을 지운다). 같은 계정으로 다시 로그인하려면 client_login.")]
    public async Task<object> ClientLogout([Description("별칭")] string alias, CancellationToken ct)
    {
        var sinceSeq = Seq;
        return new { sinceSeq, state = await Clients.LogoutAsync(alias, ct) };
    }

    [McpTool("client_drop")]
    [Description("로그아웃 없이 로비 연결을 끊는다(와이파이 끊김/앱 강제 종료 흉내). 서버는 스트림이 예고 없이 끊긴 것으로 본다. " +
                 "토큰은 남아 있어 client_reconnect로 재진입할 수 있다.")]
    public object ClientDrop([Description("별칭")] string alias)
    {
        var sinceSeq = Seq;
        return new { sinceSeq, state = Clients.Drop(alias) };
    }

    [McpTool("client_reconnect")]
    [Description("캐시된 playerId+토큰으로 로그인 서버에 재진입 안내를 받는다. 로비로 안내되면 EnterLobby까지 한다. 인게임으로 안내되면 " +
                 "state.pendingIngameAddress에 주소만 남는다(헤드리스 클라는 인게임 접속 불가).")]
    public async Task<object> ClientReconnect([Description("별칭")] string alias, CancellationToken ct)
    {
        var sinceSeq = Seq;
        var (login, state) = await Clients.ReconnectAsync(alias, ct);
        return new { sinceSeq, login, state };
    }

    [McpTool("client_state")]
    [Description("클라 상태(참고용 - 판정은 이벤트로): 단계(Disconnected/LoggedIn/InLobby/Matchmaking/MatchFound/TransferringToIngame), " +
                 "playerId, 로비 주소, 마지막 MatchFound와 유효 여부. alias를 비우면 전부.")]
    public object ClientState([Description("별칭(비우면 전부)")] string? alias = null) =>
        alias == null ? new { clients = Clients.List() } : new { clients = new[] { Clients.GetState(alias) } };

    [McpTool("client_close")]
    [Description("클라를 닫고 목록에서 뺀다(연결을 정리함 - 서버 입장에선 끊긴 것과 같다).")]
    public async Task<object> ClientClose([Description("별칭")] string alias)
    {
        var sinceSeq = Seq;
        await Clients.CloseAsync(alias);
        return new { sinceSeq, closed = alias };
    }
}
