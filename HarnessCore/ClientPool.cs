using GameServer.Common;
using LobbyServer;
using TestHarness.HeadlessClient;
using Client = TestHarness.HeadlessClient.HeadlessClient;

namespace TestHarness.Core;

/// <summary>로그인/재진입 결과 요약(열거형은 이름으로 나간다).</summary>
public sealed record LoginSummary(
    bool Success,
    string? RejectReason,
    string? ClientFailure,
    string? Detail,
    string? PlayerId,
    string? Destination,
    ServerType? DestinationType);

/// <summary>로비 입장 결과 요약.</summary>
public sealed record LobbySummary(bool Success, EnterLobbyResult Result, string? ClientFailure, string? Detail, string? ConflictingServer);

/// <summary>클라 하나의 지금 상태(참고용 - 판정은 이벤트로).</summary>
public sealed record ClientState(
    string Alias,
    string AccountId,
    bool AccountCreated,
    string? PlayerId,
    string? Nickname,
    ConnectionStage Stage,
    string? LobbyAddress,
    MatchFoundData? LastMatchFound,
    bool LastMatchFoundValid,
    string? PendingIngameAddress,
    long LastSeq);

/// <summary>
/// 하네스가 들고 있는 헤드리스 클라들. 별칭(p1, p2...)으로 부르고, 계정 ID는 따로 안 주면 하네스가
/// "별칭-시각"으로 만든다(매번 새 계정 - 앞 실행의 세션/상태가 섞이지 않게). 닉네임은 계정 ID와 같게
/// 둔다(닉네임은 중복이 안 되므로).
///
/// 클라 이벤트는 전부 타임라인에 "client:별칭"으로 들어간다.
///
/// 같은 클라에 대한 호출은 한 줄로 세운다(클라마다 SemaphoreSlim) - MCP 호출은 서로 순서가 없어서
/// AI가 같은 클라에 두 행동을 동시에 보내면(병렬 도구 호출) HeadlessClient가 동시에 두 작업을 하게 된다.
/// 도착한 순서대로 하나씩 실행하므로 상태가 꼬이지 않고, 순서가 뒤집히면 뒤 행동이 "로비에 접속돼 있지
/// 않다" 같은 오류로 끝난다. 서로 다른 클라끼리는 동시에 돈다.
/// </summary>
public sealed class ClientPool : IAsyncDisposable
{
    private readonly Timeline _timeline;
    private readonly string _loginAddress;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _clients = new(StringComparer.OrdinalIgnoreCase);
    private int _accountCounter;

    public ClientPool(Timeline timeline, string loginAddress)
    {
        _timeline = timeline;
        _loginAddress = loginAddress;
    }

    public ClientState Create(string alias, string? accountId, string? password, bool accountExists)
    {
        if (string.IsNullOrWhiteSpace(alias) || alias.Any(char.IsWhiteSpace))
        {
            throw new HarnessException($"별칭 '{alias}'을(를) 쓸 수 없음", "공백 없는 짧은 이름(p1, p2 ...)을 쓸 것");
        }

        lock (_lock)
        {
            if (_clients.ContainsKey(alias))
            {
                throw new HarnessException($"이미 있는 별칭 '{alias}'", "다른 별칭을 쓰거나 client_close로 먼저 닫을 것");
            }
        }

        var id = string.IsNullOrWhiteSpace(accountId) ? $"{alias}-{DateTime.Now:MMddHHmmss}" : accountId.Trim();
        var client = new Client(new HeadlessClientOptions
        {
            Name = alias,
            LoginAddress = _loginAddress,
            EventSink = _timeline.RecordClientEvent,
        });

        var entry = new Entry(alias, client, id, string.IsNullOrEmpty(password) ? "harness-pw" : password)
        {
            AccountCreated = accountExists,
        };
        lock (_lock) _clients[alias] = entry;

        _timeline.RecordHarness("client.created", $"{alias} 생성(계정 {id})", new { alias, accountId = id });
        return Snapshot(entry);
    }

    /// <summary>
    /// 로그인. 계정을 아직 안 만들었으면 계정 생성(= 생성 후 바로 로그인)을 한다. enterLobby면 안내받은
    /// 곳이 로비일 때 EnterLobby까지 한다.
    /// </summary>
    public async Task<(LoginSummary Login, LobbySummary? Lobby, ClientState State)> LoginAsync(
        string alias, bool enterLobby, CancellationToken ct)
    {
        var entry = Get(alias);
        await entry.Gate.WaitAsync(ct);
        try
        {
            return await LoginCoreAsync(entry, enterLobby, ct);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private async Task<(LoginSummary Login, LobbySummary? Lobby, ClientState State)> LoginCoreAsync(
        Entry entry, bool enterLobby, CancellationToken ct)
    {
        LoginOutcome outcome;
        if (!entry.AccountCreated)
        {
            outcome = await entry.Client.CreateAccountAsync(entry.AccountId, entry.Password, entry.AccountId, ct);
            if (outcome.Success) entry.AccountCreated = true;
        }
        else
        {
            outcome = await entry.Client.LoginAsync(entry.AccountId, entry.Password, ct);
        }

        LobbySummary? lobby = null;
        if (outcome.Success && enterLobby && outcome.DestinationServerType != ServerType.Ingame)
        {
            lobby = ToSummary(await entry.Client.EnterLobbyAsync(ct: ct));
        }

        return (ToSummary(outcome), lobby, Snapshot(entry));
    }

    /// <summary>EnterLobby(로그인 후 따로, 또는 끊긴 뒤 다시). lobbyAddress가 비면 마지막으로 안내받은 로비.</summary>
    public async Task<(LobbySummary Lobby, ClientState State)> EnterLobbyAsync(string alias, string? lobbyAddress, string? transferTicket, CancellationToken ct)
    {
        var entry = Get(alias);
        var outcome = await Serialized(entry, ct, () => Guard(() => entry.Client.EnterLobbyAsync(
            string.IsNullOrWhiteSpace(lobbyAddress) ? null : lobbyAddress, transferTicket ?? "", ct)));
        return (ToSummary(outcome), Snapshot(entry));
    }

    /// <summary>매칭 요청. 결과는 기다리지 않는다 - 반환한 sinceSeq로 wait_for(MatchFound/MatchFailed)를 걸 것.</summary>
    public (long SinceSeq, ClientState State) RequestMatch(string alias, GameType gameType)
    {
        var entry = Get(alias);
        entry.Gate.Wait();
        try
        {
            var sinceSeq = _timeline.LastSeq;
            Guard(() => entry.Client.RequestMatch(gameType));
            return (sinceSeq, Snapshot(entry));
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public async Task<ClientState> CancelMatchAsync(string alias, CancellationToken ct)
    {
        var entry = Get(alias);
        await Serialized(entry, ct, () => Guard(() => entry.Client.CancelMatchAsync(ct)));
        return Snapshot(entry);
    }

    public async Task<ClientState> LogoutAsync(string alias, CancellationToken ct)
    {
        var entry = Get(alias);
        await Serialized(entry, ct, () => Guard(() => entry.Client.LogoutAsync(ct)));
        return Snapshot(entry);
    }

    /// <summary>로그아웃 없이 로비 연결을 끊는다(네트워크 끊김/앱 강제 종료 흉내). 토큰은 남는다.</summary>
    public ClientState Drop(string alias)
    {
        var entry = Get(alias);
        entry.Gate.Wait();
        try
        {
            entry.Client.DropLobbyConnection();
            return Snapshot(entry);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>캐시된 토큰으로 로그인 서버 재진입. 로비로 안내되면 EnterLobby까지 한다(원본 클라와 같음).</summary>
    public async Task<(LoginSummary Login, ClientState State)> ReconnectAsync(string alias, CancellationToken ct)
    {
        var entry = Get(alias);
        var outcome = await Serialized(entry, ct, () => entry.Client.ReconnectViaLoginServerAsync(ct));
        return (ToSummary(outcome), Snapshot(entry));
    }

    public ClientState GetState(string alias) => Snapshot(Get(alias));

    public IReadOnlyList<ClientState> List()
    {
        lock (_lock) return _clients.Values.Select(Snapshot).ToList();
    }

    public async Task CloseAsync(string alias)
    {
        Entry entry;
        lock (_lock)
        {
            if (!_clients.Remove(alias, out entry!))
            {
                throw new HarnessException($"없는 별칭 '{alias}'", "client_list로 있는 클라를 확인할 것");
            }
        }

        // 진행 중인 호출이 끝난 뒤에 닫는다.
        await entry.Gate.WaitAsync();
        try
        {
            await entry.Client.DisposeAsync();
        }
        finally
        {
            entry.Gate.Release();
        }

        _timeline.RecordHarness("client.closed", $"{alias} 닫음", new { alias });
    }

    /// <summary>
    /// 계정만 만들어 두고 접속은 정리한다(계정 생성 → 로비 입장 → 로그아웃). Unity 클라는 계정을 만들 수 없어서
    /// (로그인 화면에 계정 생성이 없음) Unity로 로그인할 계정을 이걸로 준비한다. 로그아웃까지 해야 세션이 지워져
    /// Unity가 같은 계정으로 바로 로그인할 수 있다(남아 있으면 SessionConflict).
    /// </summary>
    public async Task<(string AccountId, string Password, LoginSummary Login, LobbySummary? Lobby, bool LoggedOut)> CreateAccountAsync(
        string? accountId, string? password, CancellationToken ct)
    {
        var id = string.IsNullOrWhiteSpace(accountId)
            ? $"acct-{DateTime.Now:MMddHHmmss}-{Interlocked.Increment(ref _accountCounter)}"
            : accountId.Trim();
        var pw = string.IsNullOrEmpty(password) ? "harness-pw" : password;

        await using var client = new Client(new HeadlessClientOptions
        {
            Name = $"account-{id}",
            LoginAddress = _loginAddress,
            EventSink = _timeline.RecordClientEvent,
        });

        var login = await client.CreateAccountAsync(id, pw, id, ct);
        if (!login.Success) return (id, pw, ToSummary(login), null, false);

        var lobby = await client.EnterLobbyAsync(ct: ct);
        var loggedOut = false;
        if (lobby.Success)
        {
            await client.LogoutAsync(ct);
            loggedOut = client.Events.Snapshot().Any(e => e.Kind == ClientEventKind.LoggedOut);
        }

        _timeline.RecordHarness("account.created", $"계정 {id} 생성(로그아웃={loggedOut})", new { accountId = id, loggedOut });
        return (id, pw, ToSummary(login), ToSummary(lobby), loggedOut);
    }

    private static async Task<T> Serialized<T>(Entry entry, CancellationToken ct, Func<Task<T>> action)
    {
        await entry.Gate.WaitAsync(ct);
        try { return await action(); }
        finally { entry.Gate.Release(); }
    }

    private static async Task Serialized(Entry entry, CancellationToken ct, Func<Task> action)
    {
        await entry.Gate.WaitAsync(ct);
        try { await action(); }
        finally { entry.Gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        List<Entry> all;
        lock (_lock)
        {
            all = _clients.Values.ToList();
            _clients.Clear();
        }

        foreach (var entry in all) await entry.Client.DisposeAsync();
    }

    private Entry Get(string alias)
    {
        lock (_lock)
        {
            if (_clients.TryGetValue(alias, out var entry)) return entry;
            var known = _clients.Count == 0 ? "없음" : string.Join(", ", _clients.Keys);
            throw new HarnessException($"없는 별칭 '{alias}' (지금 있는 클라: {known})", "client_create로 먼저 만들 것");
        }
    }

    private ClientState Snapshot(Entry entry)
    {
        var client = entry.Client;
        return new ClientState(entry.Alias, entry.AccountId, entry.AccountCreated, client.PlayerId,
            string.IsNullOrEmpty(client.Nickname) ? null : client.Nickname, client.Stage, client.LobbyAddress,
            client.LastMatchFound, client.IsLastMatchFoundValid, client.PendingIngameTransfer?.GameServerAddress,
            _timeline.LastSeq);
    }

    // HeadlessClient는 순서 위반(로비 연결 없이 매칭 등)을 InvalidOperationException으로 알린다 -
    // AI가 다음 행동을 고를 수 있게 하네스 오류로 바꾼다.
    private static void Guard(Action action)
    {
        try { action(); }
        catch (InvalidOperationException ex) { throw new HarnessException(ex.Message, "client_state로 지금 단계를 확인할 것"); }
    }

    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (InvalidOperationException ex) { throw new HarnessException(ex.Message, "client_state로 지금 단계를 확인할 것"); }
    }

    private static async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidOperationException ex) { throw new HarnessException(ex.Message, "client_state로 지금 단계를 확인할 것"); }
    }

    private static LoginSummary ToSummary(LoginOutcome o) => new(
        o.Success,
        o.Success || o.RejectReason == LoginServer.LoginRejectReason.Unspecified ? null : o.RejectReason.ToString(),
        o.ClientFailure == HeadlessClient.ClientFailure.None ? null : o.ClientFailure.ToString(),
        string.IsNullOrEmpty(o.Detail) ? null : o.Detail,
        string.IsNullOrEmpty(o.PlayerId) ? null : o.PlayerId,
        string.IsNullOrEmpty(o.DestinationAddress) ? null : o.DestinationAddress,
        o.Success ? o.DestinationServerType : null);

    private static LobbySummary ToSummary(EnterLobbyOutcome o) => new(
        o.Success,
        o.Result,
        o.ClientFailure == HeadlessClient.ClientFailure.None ? null : o.ClientFailure.ToString(),
        string.IsNullOrEmpty(o.Detail) ? null : o.Detail,
        string.IsNullOrEmpty(o.ConflictingServerAddress) ? null : o.ConflictingServerAddress);

    private sealed class Entry
    {
        public Entry(string alias, Client client, string accountId, string password)
        {
            Alias = alias;
            Client = client;
            AccountId = accountId;
            Password = password;
        }

        public string Alias { get; }
        public Client Client { get; }
        public string AccountId { get; }
        public string Password { get; }
        public bool AccountCreated { get; set; }

        /// <summary>이 클라에 대한 호출을 한 번에 하나씩(클래스 주석).</summary>
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}
