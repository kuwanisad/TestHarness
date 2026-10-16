namespace TestHarness.HeadlessClient;

/// <summary>클라 입장에서 일어난 일의 종류. 시나리오의 판정(WaitFor...)과 타임라인의 기준이 된다.</summary>
public enum ClientEventKind
{
    // 로그인 서버
    LoginSucceeded,
    LoginRejected,
    ReconnectRedirected,
    ReconnectRejected,

    // 로비
    LobbyConnected,
    EnterLobbySucceeded,
    EnterLobbyConflict,
    EnterLobbyRateLimited,
    EnterLobbyRejected,
    StayConnectedOpened,
    StayConnectedClosed,
    MailProvided,
    Announcement,
    LoggedOut,
    LobbyDisconnected,

    // 매칭
    MatchRequested,
    MatchQueued,
    MatchStatus,
    MatchFound,
    MatchFailed,
    MatchStreamClosed,
    MatchCancelled,

    // 인게임 전환
    IngameTransferStarted,
    ConnectionWelcomeApplied,

    // 하네스 자체의 오류(RPC 예외 등) - 진짜 클라라면 로그만 찍고 넘어갔을 것들
    Error,
}

/// <summary>
/// 기록된 이벤트 하나. Seq는 이 클라 안에서 단조 증가하는 번호 - "어떤 요청 이후에 온
/// 이벤트만" 기다릴 때 기준점으로 쓴다(이전 매칭의 결과를 이번 결과로 착각하지 않게).
/// TimestampUtc는 여러 프로세스 로그와 합쳐 타임라인을 만들 때의 정렬 기준이다.
///
/// Detail은 사람이 읽는 한 줄(타임라인/콘솔용), Data는 시나리오가 판정에 쓰는 값이다.
/// Data는 값이 필요한 종류에만 붙는다 - 종류별로 어떤 타입인지는 ClientEventData.cs 참고.
/// </summary>
public sealed record ClientEvent(
    long Seq,
    DateTimeOffset TimestampUtc,
    string ClientName,
    string? PlayerId,
    ClientEventKind Kind,
    string Detail,
    object? Data = null)
{
    /// <summary>
    /// Data를 T로 꺼낸다. 이 종류의 이벤트에 T 데이터가 없으면 예외 - 시나리오가 엉뚱한
    /// 이벤트에서 값을 꺼내려는 실수를 바로 드러내기 위해(조용히 기본값을 주지 않음).
    /// </summary>
    public T GetData<T>() where T : class =>
        Data as T ?? throw new InvalidOperationException(
            $"{Kind} 이벤트에 {typeof(T).Name} 데이터가 없음(실제: {Data?.GetType().Name ?? "없음"})");

    public override string ToString() =>
        $"{TimestampUtc:yyyy-MM-ddTHH:mm:ss.fffZ} [{ClientName}{(PlayerId != null ? $"/{PlayerId}" : "")}] {Kind}" +
        (string.IsNullOrEmpty(Detail) ? "" : $" {Detail}");
}
