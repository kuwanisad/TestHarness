using GameServer.Common;
using Grpc.Core;
using LobbyServer;
using LoginServer;

namespace TestHarness.HeadlessClient;

/// <summary>
/// 이 클라의 접속 단계 - Unity LobbyClient.LobbyState에 로그인/전환 단계를 더한 것.
/// 오토체스 국면(재정비/전투)과는 무관하다 - 그건 인게임(Unity) 쪽 일이고, 하네스에선 Unity
/// 에이전트의 phase.changed 이벤트로 본다. 판정은 이벤트로 하고 이 값은 "지금 어디쯤인가"를
/// 빠르게 확인하는 참고용으로 쓴다.
/// </summary>
public enum ConnectionStage
{
    /// <summary>아무 서버에도 붙어있지 않음(최초, 로그아웃 후, 연결 끊김 후).</summary>
    Disconnected,
    /// <summary>로그인 서버가 로비로 이관까지 끝내고 리다이렉트를 줬음 - 아직 EnterLobby 전.</summary>
    LoggedIn,
    InLobby,
    Matchmaking,
    MatchFound,
    /// <summary>로비를 떠나 인게임 서버로 넘어가는 중(BeginIngameTransfer 이후).</summary>
    TransferringToIngame,
}

/// <summary>
/// 화면 없이 프로토콜만으로 진짜 클라(Unity ClientInstance) 대신 서버들과 대화하는 클라.
///
/// 원칙:
/// - 스스로 판단하지 않는다. 모든 행동은 호출자(시나리오)가 한 단계씩 지시한다 - 같은 시나리오는
///   항상 같은 순서로 돌아야 "고쳤더니 통과했다"를 믿을 수 있으므로. 그래서 진짜 클라처럼
///   매치 확정 즉시 자동으로 인게임에 접속하거나, 끊기면 자동으로 재접속하지 않는다 -
///   그 판단은 시나리오가 BeginIngameTransfer/ReconnectViaLoginServerAsync를 직접 불러서 한다.
/// - 각 행동은 Unity ClientInstance/LobbyClient/GrpcLobbyChannel의 해당 동작을 그대로 따라
///   한다(메서드 주석에 대응하는 원본을 적어둠). 진짜 클라와 어긋나면 "하네스는 통과하는데
///   실제 클라는 실패"하는 사고가 나므로, 원본이 바뀌면 여기도 같이 바꿔야 한다.
/// - 일어난 일은 전부 <see cref="Events"/>에 시각과 함께 기록한다 - 시나리오의 판정과
///   타임라인의 재료.
///
/// 아직 없는 것: 채팅(WebSocket) 연결, 인게임(NGO) 접속 자체 - 후자는 가짜 인게임 서버가
/// <see cref="IngameTransfer.ConnectionPayload"/>를 받아 대신 처리할 예정.
/// </summary>
public sealed class HeadlessClient : IAsyncDisposable
{
    private readonly HeadlessClientOptions _options;
    private LobbyConnection? _lobby;
    private bool _disposed;

    // ClientInstance의 캐시 필드 대응 - 로비 연결이 끊겼다 새로 생겨도 살아남는 값들.
    private string? _cachedPlayerId;   // _cachedPlayerId
    private string? _cachedToken;      // _cachedToken
    private string _nickname = "";     // _nickname - 최초 로그인 때만 채움
    private string _chatAddress = "";

    // 마지막으로 받은 MatchFound. 원본(ClientInstance.HandleMatchFound)은 받는 즉시 그 값으로 인게임
    // 접속을 시작해서 저장할 필요가 없지만, 하네스는 "받기"와 "인게임 이동"을 시나리오가 따로
    // 지시하므로 그 사이를 이어줄 값이 필요하다. 다만 판단 주체(시나리오/AI)가 지난 매치를 참고할 수
    // 있게 값은 지우지 않고, 지금 써도 되는지만 유효 플래그로 따로 관리한다 - 새 매칭을 요청하거나
    // 이미 인게임 이동에 썼거나 매칭을 취소하면 무효가 된다(이전 매치 티켓으로 이동하는 사고 방지).
    private readonly object _matchLock = new();
    private MatchFound? _lastMatchFound;
    private bool _lastMatchFoundValid;
    private string _lastMatchFoundInvalidReason = "MatchFound를 받은 적이 없음";

    public HeadlessClient(HeadlessClientOptions options)
    {
        _options = options;
        Events = new ClientEventLog(options.Name, options.EventSink);
    }

    public string Name => _options.Name;
    public ClientEventLog Events { get; }
    // 시나리오 스레드와 스트림 수신 루프(백그라운드)가 둘 다 바꾸므로 락으로 보호한다.
    private readonly object _stageLock = new();
    private ConnectionStage _stage = ConnectionStage.Disconnected;

    /// <summary>지금 접속 단계(참고용 - 판정은 이벤트로).</summary>
    public ConnectionStage Stage
    {
        get { lock (_stageLock) return _stage; }
        private set { lock (_stageLock) _stage = value; }
    }

    /// <summary>현재 단계가 expected 중 하나일 때만 next로 바꾼다 - 백그라운드 루프가 늦게 도착해서
    /// 이미 다른 단계로 넘어간 상태를 덮어쓰지 않게.</summary>
    private bool TrySetStage(ConnectionStage next, params ConnectionStage[] expected)
    {
        lock (_stageLock)
        {
            if (Array.IndexOf(expected, _stage) < 0) return false;
            _stage = next;
            return true;
        }
    }

    public string? PlayerId => _cachedPlayerId;
    public string? Token => _cachedToken;
    public string Nickname => _nickname;
    public string ChatAddress => _chatAddress;

    /// <summary>지금(또는 마지막으로) 붙어있던 로비 주소.</summary>
    public string? LobbyAddress { get; private set; }

    /// <summary>마지막으로 받은 MatchFound(무효가 된 뒤에도 남아 있음 - 참고용). 없으면 null.</summary>
    public MatchFoundData? LastMatchFound
    {
        get
        {
            lock (_matchLock)
            {
                var found = _lastMatchFound;
                return found == null ? null : new MatchFoundData(found.GameServerAddress, found.IngameServerId, found.TransferTicket);
            }
        }
    }

    /// <summary>LastMatchFound를 지금 인게임 이동에 써도 되는지.</summary>
    public bool IsLastMatchFoundValid
    {
        get { lock (_matchLock) return _lastMatchFoundValid; }
    }

    private void InvalidateLastMatchFound(string reason)
    {
        lock (_matchLock)
        {
            if (!_lastMatchFoundValid) return;
            _lastMatchFoundValid = false;
            _lastMatchFoundInvalidReason = reason;
        }
    }

    // ================================ 로그인 서버 ================================

    /// <summary>기존 계정으로 로그인한다 - ClientInstance.LoginAsync의 1단계(GrpcLoginChannel.LoginAsync).
    /// 성공하면 playerId/토큰/닉네임/채팅 주소를 캐시하고 로비 주소를 기억해둔다. 로비 입장은
    /// 하지 않는다(EnterLobbyAsync를 따로 부르거나, 한 번에 하려면 LoginAndEnterLobbyAsync).</summary>
    public Task<LoginOutcome> LoginAsync(string accountId, string password, CancellationToken ct = default) =>
        SendLoginAsync(new ClientToLoginMessage
        {
            Credentials = new Credentials { AccountId = accountId, Password = password },
        }, isReconnect: false, ct);

    /// <summary>새 계정을 만들고 그 즉시 로그인한다(CreateAccountRequest - 성공하면 서버가
    /// Credentials 로그인과 같은 절차를 탄다).</summary>
    public Task<LoginOutcome> CreateAccountAsync(
        string accountId, string password, string nickname, CancellationToken ct = default) =>
        SendLoginAsync(new ClientToLoginMessage
        {
            CreateAccount = new CreateAccountRequest { AccountId = accountId, Password = password, Nickname = nickname },
        }, isReconnect: false, ct);

    /// <summary>
    /// 갈 곳을 잃었을 때 캐시된 playerId+토큰으로 로그인 서버에 재진입 안내를 받는다 -
    /// ClientInstance.TryReconnectViaLoginServerAsync. 원본은 안내받은 즉시 로비면 EnterLobby,
    /// 인게임이면 NGO 접속까지 하는데, 여기선 로비 쪽만 그대로 따라 하고(EnterLobby까지 수행)
    /// 인게임 쪽은 <see cref="PendingIngameTransfer"/>만 채워두고 멈춘다(실제 접속은 시나리오가
    /// 가짜 인게임 서버로 진행).
    /// </summary>
    public async Task<LoginOutcome> ReconnectViaLoginServerAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_cachedPlayerId) || string.IsNullOrEmpty(_cachedToken))
        {
            const string detail = "캐시된 인증 정보 없음 - 처음부터 로그인해야 함";
            Record(ClientEventKind.ReconnectRejected, $"Client.{ClientFailure.NoCachedCredentials}: {detail}",
                new LoginRejectedData(LoginRejectReason.Unspecified, ClientFailure.NoCachedCredentials, detail));
            return LoginOutcome.Failed(ClientFailure.NoCachedCredentials, detail);
        }

        var outcome = await SendLoginAsync(new ClientToLoginMessage
        {
            Reconnect = new ReconnectRequest { PlayerId = _cachedPlayerId, Token = _cachedToken },
        }, isReconnect: true, ct);

        if (!outcome.Success) return outcome;

        if (outcome.DestinationServerType == ServerType.Ingame)
        {
            // 재진입은 이미 소유 중인 서버로 다시 붙는 것이라 전입 티켓이 없다(원본과 동일).
            PendingIngameTransfer = new IngameTransfer(
                outcome.DestinationAddress, "", outcome.PlayerId, outcome.Token, TransferTicket: "");
        }
        else
        {
            await EnterLobbyAsync(outcome.DestinationAddress, transferTicket: "", ct);
        }

        return outcome;
    }

    /// <summary>로그인 → 로비 입장까지 한 번에 - ClientInstance.LoginAsync에서 채팅 접속만 뺀 것.</summary>
    public async Task<(LoginOutcome Login, EnterLobbyOutcome? Lobby)> LoginAndEnterLobbyAsync(
        string accountId, string password, CancellationToken ct = default)
    {
        var login = await LoginAsync(accountId, password, ct);
        if (!login.Success) return (login, null);

        var lobby = await EnterLobbyAsync(login.DestinationAddress, transferTicket: "", ct);
        return (login, lobby);
    }

    private async Task<LoginOutcome> SendLoginAsync(ClientToLoginMessage message, bool isReconnect, CancellationToken ct)
    {
        ThrowIfDisposed();

        // GrpcLoginChannel과 같은 모양 - 접속할 때마다 채널을 새로 만들고, 메시지 하나 보내고
        // 응답 하나 받고 끝(로그인 서버는 재접속 개념이 없다).
        using var channel = GrpcChannels.Create(_options.LoginAddress, _options.SkipCertificateVerification);
        var client = new LoginControl.LoginControlClient(channel);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.RpcTimeout);

        LoginOutcome outcome;
        try
        {
            using var call = client.Connect(cancellationToken: timeoutCts.Token);
            await call.RequestStream.WriteAsync(message, timeoutCts.Token);
            await call.RequestStream.CompleteAsync();

            if (!await call.ResponseStream.MoveNext(timeoutCts.Token))
            {
                outcome = LoginOutcome.Failed(ClientFailure.NoResponse, "로그인 서버가 응답 없이 스트림을 닫음");
            }
            else
            {
                var response = call.ResponseStream.Current;
                outcome = response.PayloadCase switch
                {
                    LoginToClientMessage.PayloadOneofCase.Redirect => new LoginOutcome(
                        true, LoginRejectReason.Unspecified, ClientFailure.None, "", response.Redirect.PlayerId, response.Redirect.Token,
                        response.Redirect.LobbyAddress, response.Redirect.DestinationServerType,
                        response.Redirect.ChatAddress, response.Redirect.Nickname),
                    LoginToClientMessage.PayloadOneofCase.Rejected => LoginOutcome.Rejected(response.Rejected.Reason, response.Rejected.Detail),
                    _ => LoginOutcome.Failed(ClientFailure.UnknownResponse, $"payload={response.PayloadCase}"),
                };
            }
        }
        catch (RpcException ex)
        {
            outcome = LoginOutcome.Failed(ClientFailure.RpcError, $"{ex.StatusCode}: {ex.Status.Detail}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            outcome = LoginOutcome.Failed(ClientFailure.Timeout, $"하네스 RPC 제한 시간 {_options.RpcTimeout.TotalSeconds}초 초과");
        }

        if (outcome.Success)
        {
            _cachedPlayerId = outcome.PlayerId;
            _cachedToken = outcome.Token;
            _chatAddress = outcome.ChatAddress;

            // 닉네임은 최초 로그인 응답에만 실린다 - 재진입으로는 절대 덮어쓰지 않는다(원본과 동일).
            if (!isReconnect) _nickname = outcome.Nickname;

            if (outcome.DestinationServerType != ServerType.Ingame)
            {
                Stage = ConnectionStage.LoggedIn;
                LobbyAddress = outcome.DestinationAddress;
            }

            Record(isReconnect ? ClientEventKind.ReconnectRedirected : ClientEventKind.LoginSucceeded,
                $"→ {outcome.DestinationAddress} ({outcome.DestinationServerType})",
                new LoginRedirectData(outcome.PlayerId, outcome.DestinationAddress, outcome.DestinationServerType));
        }
        else
        {
            Record(isReconnect ? ClientEventKind.ReconnectRejected : ClientEventKind.LoginRejected,
                string.IsNullOrEmpty(outcome.Detail) ? outcome.FailureName : $"{outcome.FailureName}: {outcome.Detail}",
                new LoginRejectedData(outcome.RejectReason, outcome.ClientFailure, outcome.Detail));
        }

        return outcome;
    }

    // ================================ 로비 ================================

    /// <summary>
    /// 로비에 접속해 EnterLobby를 부른다 - LobbyClient.ConnectAsync + SetToken + EnterLobbyAsync.
    /// lobbyAddress가 null이면 마지막 로그인/재진입이 알려준 주소를 쓴다. 성공하면 원본처럼
    /// 새 토큰을 캐시하고 StayConnected 스트림을 연다(로비는 이 스트림이 살아있는 것으로 클라
    /// 생존을 판단한다).
    ///
    /// 이미 다른 로비 연결이 있으면 먼저 닫는다 - 원본은 로비 이동 때마다 LobbyClient를 새로
    /// 만든다(InitializeLobbyClient).
    /// </summary>
    public async Task<EnterLobbyOutcome> EnterLobbyAsync(
        string? lobbyAddress = null, string transferTicket = "", CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var address = lobbyAddress ?? LobbyAddress ?? throw new InvalidOperationException(
            "로비 주소를 모른다 - 먼저 로그인하거나 주소를 직접 넘길 것.");

        if (string.IsNullOrEmpty(_cachedPlayerId) || string.IsNullOrEmpty(_cachedToken))
        {
            throw new InvalidOperationException("캐시된 토큰이 없다 - 먼저 로그인할 것.");
        }

        // _lobby는 백그라운드 루프가 떼어낼 수 있으므로(DetachLobby) 한 번만 읽어 지역 변수로 쓴다.
        var lobby = Volatile.Read(ref _lobby);
        if (lobby == null || lobby.Address != address)
        {
            CloseLobby(reason: "다른 로비로 이동");
            lobby = new LobbyConnection(address, _options.SkipCertificateVerification, _cachedToken);
            Volatile.Write(ref _lobby, lobby);
            LobbyAddress = address;
            Record(ClientEventKind.LobbyConnected, address);
        }
        else
        {
            lobby.Token = _cachedToken; // LobbyClient.SetToken(_cachedToken, _cachedPlayerId)
        }

        EnterLobbyResponse response;
        try
        {
            response = await lobby.Client.EnterLobbyAsync(
                new EnterLobbyRequest { TransferTicket = transferTicket },
                headers: lobby.AuthHeaders(),
                deadline: Deadline(),
                cancellationToken: ct);
        }
        catch (RpcException ex)
        {
            var rpcDetail = $"{ex.StatusCode}: {ex.Status.Detail}";
            Record(ClientEventKind.EnterLobbyRejected, $"Client.{ClientFailure.RpcError}: {rpcDetail}",
                new EnterLobbyRejectedData(EnterLobbyResult.Unspecified, ClientFailure.RpcError, rpcDetail));
            return new EnterLobbyOutcome(EnterLobbyResult.Unspecified, ClientFailure.RpcError, rpcDetail, "");
        }

        var outcome = new EnterLobbyOutcome(response.Result, ClientFailure.None, response.Detail,
            response.ConflictingServerAddress);

        if (outcome.Success)
        {
            // GrpcLobbyChannel: 새 토큰으로 갱신 / ClientInstance.OnEnterLobbySuccess: 캐시.
            lobby.Token = response.Token;
            _cachedToken = response.Token;
            Stage = ConnectionStage.InLobby;
            Record(ClientEventKind.EnterLobbySucceeded, address);

            lobby.StayConnectedTask = StayConnectedLoopAsync(lobby);
            return outcome;
        }

        if (outcome.RateLimited)
        {
            Record(ClientEventKind.EnterLobbyRateLimited, response.Detail,
                new EnterLobbyRejectedData(response.Result, ClientFailure.None, response.Detail));
            return outcome;
        }

        if (outcome.HasConflictingSession)
        {
            Record(ClientEventKind.EnterLobbyConflict, response.ConflictingServerAddress,
                new ConflictData(response.ConflictingServerAddress));
            return outcome;
        }

        Record(ClientEventKind.EnterLobbyRejected,
            string.IsNullOrEmpty(response.Detail) ? outcome.FailureName : $"{outcome.FailureName}: {response.Detail}",
            new EnterLobbyRejectedData(response.Result, ClientFailure.None, response.Detail));
        return outcome;
    }

    /// <summary>GrpcLobbyChannel.StayConnectedAsync - 스트림이 살아있는 것 자체가 목적이고,
    /// 우편함/공지 push만 기록한다.</summary>
    private async Task StayConnectedLoopAsync(LobbyConnection lobby)
    {
        var closedBy = ClosedBy.Server;
        var reason = "서버가 스트림을 끝냄";
        try
        {
            using var call = lobby.Client.StayConnected(
                new StayConnectedRequest(), headers: lobby.AuthHeaders(), cancellationToken: lobby.Cts.Token);

            Record(ClientEventKind.StayConnectedOpened);

            await foreach (var evt in call.ResponseStream.ReadAllAsync(lobby.Cts.Token))
            {
                switch (evt.PayloadCase)
                {
                    case StayConnectedEvent.PayloadOneofCase.MailProvided:
                        Record(ClientEventKind.MailProvided, $"{evt.MailProvided.Mails.Count}통",
                            new MailData(evt.MailProvided.Mails.Count));
                        break;
                    case StayConnectedEvent.PayloadOneofCase.Announcement:
                        Record(ClientEventKind.Announcement, evt.Announcement.AnnouncementType.ToString(),
                            new AnnouncementData(evt.Announcement.AnnouncementType.ToString()));
                        break;
                }
            }
        }
        catch (OperationCanceledException) { (closedBy, reason) = (ClosedBy.Local, "로컬에서 연결 닫음"); }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && lobby.Cts.IsCancellationRequested)
        {
            (closedBy, reason) = (ClosedBy.Local, "로컬에서 연결 닫음");
        }
        catch (RpcException ex) { (closedBy, reason) = (ClosedBy.Error, $"RPC_ERROR:{ex.StatusCode}:{ex.Status.Detail}"); }
        catch (Exception ex) { (closedBy, reason) = (ClosedBy.Error, $"{ex.GetType().Name}: {ex.Message}"); }

        Record(ClientEventKind.StayConnectedClosed, reason, new ClosedData(closedBy, reason));

        // 로비는 이 스트림이 살아있는 것으로 클라 생존을 판단한다 - 서버 쪽에서 끊겼다면 이 로비
        // 연결은 끝난 것이다. 진짜 클라라면 여기서 로그인 서버를 거쳐 자동 재접속하지만, 하네스는
        // 스스로 판단하지 않으므로 끊긴 상태로만 만들어 둔다(재접속은 시나리오가
        // ReconnectViaLoginServerAsync로 직접 지시).
        if (closedBy != ClosedBy.Local)
        {
            DetachLobby(lobby, closedBy, $"StayConnected 종료: {reason}");
        }
    }

    /// <summary>LobbyClient.LogoutAsync. 성공하면 서버가 세션을 지우고 채팅 서버에도
    /// SessionInvalidated를 발행한다.</summary>
    public async Task LogoutAsync(CancellationToken ct = default)
    {
        var lobby = RequireLobby();
        try
        {
            await lobby.Client.LogoutAsync(new LogoutRequest(), headers: lobby.AuthHeaders(),
                deadline: Deadline(), cancellationToken: ct);
            Record(ClientEventKind.LoggedOut);
        }
        catch (RpcException ex)
        {
            Record(ClientEventKind.Error, $"Logout RPC_ERROR:{ex.StatusCode}");
        }

        CloseLobby(reason: "로그아웃");
        _cachedToken = null;
    }

    /// <summary>
    /// [테스트 전용 - 원본에 없음] 로그아웃 없이 로비 연결을 그냥 끊는다 - 와이파이가 끊기거나
    /// 앱이 죽은 상황의 흉내. 서버 입장에선 StayConnected/RequestMatch 스트림이 예고 없이
    /// 끊긴 것으로 보인다. 캐시된 토큰/playerId는 남겨둔다(진짜 클라도 메모리엔 남아있으므로 -
    /// 이후 ReconnectViaLoginServerAsync로 재진입 시도 가능).
    /// </summary>
    public void DropLobbyConnection()
    {
        CloseLobby(reason: "연결 끊김(테스트에서 강제)");
    }

    // ================================ 매칭 ================================

    /// <summary>
    /// 매칭을 요청한다 - ClientInstance.RequestMatchAsync → GrpcLobbyChannel.RequestMatchAsync.
    /// 원본처럼 스트림 수신은 백그라운드로 돌리고 바로 돌아온다. 반환값은 요청 직전의 이벤트
    /// 번호 - 결과를 기다릴 때 <see cref="WaitForMatchResultAsync"/>에 넘기면 이전 매칭의
    /// 결과를 이번 것으로 착각하지 않는다.
    /// </summary>
    public long RequestMatch(GameType gameType = GameType.Normal)
    {
        var lobby = RequireLobby();
        var startSeq = Events.LastSeq;

        lobby.MatchCts?.Cancel();
        lobby.MatchCts = CancellationTokenSource.CreateLinkedTokenSource(lobby.Cts.Token);

        InvalidateLastMatchFound("이후 새 매칭을 요청함(RequestMatch)");
        Stage = ConnectionStage.Matchmaking;
        Record(ClientEventKind.MatchRequested, gameType.ToString());
        _ = MatchReceiveLoopAsync(lobby, gameType, lobby.MatchCts.Token);

        return startSeq;
    }

    /// <summary>afterSeq 이후의 매칭 결과(MatchFound/MatchFailed, 또는 결과 없이 스트림이 닫힘)를
    /// 기다린다. timeout 안에 아무것도 안 오면 <see cref="ClientEventTimeoutException"/> -
    /// "매칭 결과가 안 온다"는 버그(TODO의 무한 대기)는 정확히 이 예외로 드러난다.</summary>
    public Task<ClientEvent> WaitForMatchResultAsync(long afterSeq, TimeSpan timeout) =>
        Events.WaitForAnyAsync(timeout, afterSeq,
            ClientEventKind.MatchFound, ClientEventKind.MatchFailed, ClientEventKind.MatchStreamClosed);

    private async Task MatchReceiveLoopAsync(LobbyConnection lobby, GameType gameType, CancellationToken ct)
    {
        var closedBy = ClosedBy.Server;
        var reason = "서버가 스트림을 닫음";
        try
        {
            using var call = lobby.Client.RequestMatch(
                new MatchRequest { GameType = gameType }, headers: lobby.AuthHeaders(), cancellationToken: ct);

            await foreach (var evt in call.ResponseStream.ReadAllAsync(ct))
            {
                switch (evt.PayloadCase)
                {
                    case MatchEvent.PayloadOneofCase.Queued:
                        Record(ClientEventKind.MatchQueued, $"position={evt.Queued.QueuePosition}",
                            new QueuePositionData(evt.Queued.QueuePosition));
                        break;
                    case MatchEvent.PayloadOneofCase.Status:
                        Record(ClientEventKind.MatchStatus, $"position={evt.Status.QueuePosition}",
                            new QueuePositionData(evt.Status.QueuePosition));
                        break;
                    case MatchEvent.PayloadOneofCase.MatchFound:
                        lock (_matchLock)
                        {
                            _lastMatchFound = evt.MatchFound;
                            _lastMatchFoundValid = true;
                        }
                        Stage = ConnectionStage.MatchFound;
                        Record(ClientEventKind.MatchFound,
                            $"→ {evt.MatchFound.GameServerAddress} (ingameServerId={evt.MatchFound.IngameServerId})",
                            new MatchFoundData(evt.MatchFound.GameServerAddress, evt.MatchFound.IngameServerId,
                                evt.MatchFound.TransferTicket));
                        break;
                    case MatchEvent.PayloadOneofCase.MatchFailed:
                        Stage = ConnectionStage.InLobby;
                        Record(ClientEventKind.MatchFailed,
                            string.IsNullOrEmpty(evt.MatchFailed.Detail)
                                ? evt.MatchFailed.Reason.ToString()
                                : $"{evt.MatchFailed.Reason}: {evt.MatchFailed.Detail}",
                            new MatchFailedData(evt.MatchFailed.Reason, evt.MatchFailed.Detail));
                        break;
                }
            }
        }
        catch (OperationCanceledException) { (closedBy, reason) = (ClosedBy.Local, "로컬에서 취소"); }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested)
        {
            (closedBy, reason) = (ClosedBy.Local, "로컬에서 취소");
        }
        catch (RpcException ex) { (closedBy, reason) = (ClosedBy.Error, $"RPC_ERROR:{ex.StatusCode}:{ex.Status.Detail}"); }
        catch (Exception ex) { (closedBy, reason) = (ClosedBy.Error, $"{ex.GetType().Name}: {ex.Message}"); }

        Record(ClientEventKind.MatchStreamClosed, reason, new ClosedData(closedBy, reason));

        // 결과(MatchFound/MatchFailed) 없이 서버 쪽에서 매칭 스트림이 끝났으면 매칭은 끝난 것 -
        // 로비 연결이 아직 이 연결이면 로비 대기 상태로 되돌린다(로비 자체가 끊긴 경우는
        // StayConnectedLoopAsync가 Disconnected로 만든다).
        if (closedBy != ClosedBy.Local && ReferenceEquals(Volatile.Read(ref _lobby), lobby))
        {
            TrySetStage(ConnectionStage.InLobby, ConnectionStage.Matchmaking);
        }
    }

    /// <summary>LobbyClient.CancelMatchAsync - 수신 루프를 먼저 끊고 CancelMatch를 보낸다(원본 순서).</summary>
    public async Task CancelMatchAsync(CancellationToken ct = default)
    {
        var lobby = RequireLobby();
        lobby.MatchCts?.Cancel();
        InvalidateLastMatchFound("이후 매칭을 취소함(CancelMatchAsync)");

        try
        {
            await lobby.Client.CancelMatchAsync(new CancelMatchRequest(), headers: lobby.AuthHeaders(),
                deadline: Deadline(), cancellationToken: ct);
            Stage = ConnectionStage.InLobby;
            Record(ClientEventKind.MatchCancelled);
        }
        catch (RpcException ex)
        {
            Record(ClientEventKind.Error, $"CancelMatch RPC_ERROR:{ex.StatusCode}");
        }
    }

    // ================================ 인게임 전환 ================================

    /// <summary>재진입 안내가 인게임 서버였을 때 채워진다(ReconnectViaLoginServerAsync).</summary>
    public IngameTransfer? PendingIngameTransfer { get; private set; }

    /// <summary>
    /// 매치 확정(MatchFound) 후 인게임 서버로 넘어간다 - ClientInstance.HandleMatchFound →
    /// ConnectToIngameServerAsync의 "클라 쪽 상태 변화"만 따라 한다: 접속 정보(주소, 페이로드)를
    /// 확정하고 로비 연결을 닫는다(원본은 StartClient 직후 lobbyClientToDispose.Dispose()).
    /// 실제 NGO 접속은 반환값을 받은 가짜 인게임 서버가 대신한다.
    ///
    /// 주의: 전입 티켓은 발급 후 10초(MainServerIntermediary.TicketTtl) 안에 써야 한다 -
    /// 원본 클라는 MatchFound를 받자마자 접속하므로, 시나리오도 특별한 의도가 없으면 바로 부를 것.
    /// </summary>
    public IngameTransfer BeginIngameTransfer()
    {
        if (string.IsNullOrEmpty(_cachedPlayerId) || string.IsNullOrEmpty(_cachedToken))
        {
            throw new InvalidOperationException("캐시된 토큰이 없다.");
        }

        MatchFound found;
        lock (_matchLock)
        {
            if (!_lastMatchFoundValid || _lastMatchFound == null)
            {
                throw new InvalidOperationException(
                    $"쓸 수 있는 MatchFound가 없다 - {_lastMatchFoundInvalidReason}" +
                    (_lastMatchFound != null ? $" (마지막 MatchFound: {_lastMatchFound.GameServerAddress})" : ""));
            }

            found = _lastMatchFound;
            // 전입 티켓은 한 번만 쓸 수 있다 - 같은 MatchFound로 두 번 이동하지 않게 바로 무효화.
            _lastMatchFoundValid = false;
            _lastMatchFoundInvalidReason = "이미 인게임 이동에 사용함(BeginIngameTransfer)";
        }

        var transfer = new IngameTransfer(
            found.GameServerAddress, found.IngameServerId, _cachedPlayerId, _cachedToken, found.TransferTicket);

        Stage = ConnectionStage.TransferringToIngame;
        Record(ClientEventKind.IngameTransferStarted, $"→ {transfer.GameServerAddress}");
        CloseLobby(reason: "인게임으로 이동");

        return transfer;
    }

    /// <summary>인게임 서버가 접속 완료 시 보내는 ConnectionWelcome의 새 토큰을 반영한다 -
    /// ClientInstance.OnConnectionWelcomeMessageReceived. 안 하면 게임 종료 후 로비 복귀 때
    /// 한 세대 지난 토큰을 내게 된다.</summary>
    public void ApplyConnectionWelcome(string newToken)
    {
        if (!string.IsNullOrEmpty(newToken)) _cachedToken = newToken;
        Record(ClientEventKind.ConnectionWelcomeApplied, string.IsNullOrEmpty(newToken) ? "새 토큰 없음" : "새 토큰 반영");
    }

    /// <summary>게임 종료(RoomClosed) 후 안내받은 로비로 돌아간다 - ClientInstance.ReconnectToLobbyAsync
    /// (전입 티켓을 같이 제출).</summary>
    public Task<EnterLobbyOutcome> ReturnToLobbyAsync(string lobbyAddress, string transferTicket, CancellationToken ct = default) =>
        EnterLobbyAsync(lobbyAddress, transferTicket, ct);

    // ================================ 내부 ================================

    private LobbyConnection RequireLobby()
    {
        ThrowIfDisposed();
        return Volatile.Read(ref _lobby) ?? throw new InvalidOperationException("로비에 접속돼 있지 않다.");
    }

    /// <summary>이 클라가 스스로 로비 연결을 닫는다(로그아웃, 로비 이동, 인게임 이동, DropLobbyConnection).</summary>
    private void CloseLobby(string reason)
    {
        var lobby = Volatile.Read(ref _lobby);
        if (lobby != null) DetachLobby(lobby, ClosedBy.Local, reason);
    }

    /// <summary>
    /// lobby가 아직 현재 연결이면 떼어내고 정리한다. 백그라운드 루프가 늦게 도착해서 이미 새로
    /// 만든 로비 연결을 닫아버리지 않게, 현재 연결과 같을 때만 처리한다.
    /// 인게임으로 넘어가는 중(TransferringToIngame)이면 단계는 그대로 둔다.
    /// </summary>
    private void DetachLobby(LobbyConnection lobby, ClosedBy closedBy, string reason)
    {
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _lobby, null, lobby), lobby)) return;

        lobby.Dispose();
        TrySetStage(ConnectionStage.Disconnected,
            ConnectionStage.LoggedIn, ConnectionStage.InLobby, ConnectionStage.Matchmaking, ConnectionStage.MatchFound);

        Record(ClientEventKind.LobbyDisconnected, reason, new ClosedData(closedBy, reason));
    }

    private DateTime Deadline() => DateTime.UtcNow + _options.RpcTimeout;

    private void Record(ClientEventKind kind, string detail = "", object? data = null) =>
        Events.Record(_cachedPlayerId, kind, detail, data);

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(Name);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        var lobby = Interlocked.Exchange(ref _lobby, null);
        if (lobby == null) return;

        lobby.Dispose();
        if (lobby.StayConnectedTask != null)
        {
            try { await lobby.StayConnectedTask; } catch { /* 이미 기록됨 */ }
        }
    }

    /// <summary>로비 하나와의 연결 - GrpcLobbyChannel 하나에 대응. 로비를 옮기거나 끊을 때마다
    /// 통째로 버리고 새로 만든다.</summary>
    private sealed class LobbyConnection : IDisposable
    {
        private readonly Grpc.Net.Client.GrpcChannel _channel;

        public LobbyConnection(string address, bool skipCertificateVerification, string token)
        {
            Address = address;
            Token = token;
            _channel = GrpcChannels.Create(address, skipCertificateVerification);
            Client = new LobbyControl.LobbyControlClient(_channel);
        }

        public string Address { get; }
        public string Token { get; set; }
        public LobbyControl.LobbyControlClient Client { get; }
        public CancellationTokenSource Cts { get; } = new();
        public CancellationTokenSource? MatchCts { get; set; }
        public Task? StayConnectedTask { get; set; }

        /// <summary>GrpcLobbyChannel.MakeAuthHeader - 모든 요청에 Bearer 토큰.</summary>
        public Metadata AuthHeaders() => new() { { "authorization", $"Bearer {Token}" } };

        public void Dispose()
        {
            MatchCts?.Cancel();
            Cts.Cancel();
            _channel.Dispose();
        }
    }
}
