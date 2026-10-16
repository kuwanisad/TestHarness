namespace TestHarness.HeadlessClient;

/// <summary>
/// 이벤트 기록 + "조건에 맞는 이벤트가 올 때까지 기다리기". 스트림 수신 루프(백그라운드
/// 스레드)가 기록하고 시나리오(다른 스레드)가 기다리므로 락으로 보호한다.
///
/// 고정 시간 대기(Task.Delay(30초) 후 확인) 대신 조건 대기를 쓰는 게 이 클래스의 목적이다 -
/// 수동 테스트에서 타이밍을 손으로 맞추던 부분(block-port.ps1의 DurationSeconds 조절 등)을
/// "언제 오든 오기만 하면 통과, 제한 시간까지 안 오면 실패"로 바꾼다.
/// </summary>
public sealed class ClientEventLog
{
    private readonly object _lock = new();
    private readonly List<ClientEvent> _events = new();
    private readonly List<Waiter> _waiters = new();
    private readonly string _clientName;
    private readonly Action<ClientEvent>? _sink;
    private long _nextSeq = 1;

    public ClientEventLog(string clientName, Action<ClientEvent>? sink)
    {
        _clientName = clientName;
        _sink = sink;
    }

    /// <summary>지금까지 기록된 마지막 이벤트 번호(아무것도 없으면 0). "이 시점 이후"를
    /// 기다리고 싶으면 행동 직전에 이 값을 읽어두고 WaitForAsync의 afterSeq로 넘긴다.</summary>
    public long LastSeq
    {
        get { lock (_lock) return _nextSeq - 1; }
    }

    public IReadOnlyList<ClientEvent> Snapshot()
    {
        lock (_lock) return _events.ToArray();
    }

    public ClientEvent Record(string? playerId, ClientEventKind kind, string detail = "", object? data = null)
    {
        ClientEvent evt;
        List<Waiter>? satisfied = null;

        lock (_lock)
        {
            evt = new ClientEvent(_nextSeq++, DateTimeOffset.UtcNow, _clientName, playerId, kind, detail, data);
            _events.Add(evt);

            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                var waiter = _waiters[i];
                if (evt.Seq <= waiter.AfterSeq || !waiter.Predicate(evt)) continue;

                _waiters.RemoveAt(i);
                (satisfied ??= new List<Waiter>()).Add(waiter);
            }
        }

        // 출력/대기 해제는 락 밖에서 - 구독자 코드가 락 안에서 돌면 교착 위험이 있다.
        _sink?.Invoke(evt);
        if (satisfied != null)
        {
            foreach (var waiter in satisfied) waiter.Tcs.TrySetResult(evt);
        }

        return evt;
    }

    /// <summary>
    /// afterSeq보다 뒤에 기록된 이벤트 중 predicate를 만족하는 첫 이벤트를 기다린다.
    /// 이미 기록돼 있으면 즉시 반환한다(기다리기 시작하기 전에 와버린 경우도 놓치지 않음).
    /// timeout 안에 안 오면 <see cref="ClientEventTimeoutException"/> - 시나리오에서는
    /// 이게 곧 "기대한 일이 안 일어났다"(FAIL)는 판정이다.
    /// </summary>
    public async Task<ClientEvent> WaitForAsync(
        Func<ClientEvent, bool> predicate, TimeSpan timeout, long afterSeq = 0,
        string? description = null, CancellationToken cancellationToken = default)
    {
        Waiter waiter;

        lock (_lock)
        {
            foreach (var existing in _events)
            {
                if (existing.Seq > afterSeq && predicate(existing)) return existing;
            }

            waiter = new Waiter(predicate, afterSeq);
            _waiters.Add(waiter);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            return await waiter.Tcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            lock (_lock) _waiters.Remove(waiter);
            throw new ClientEventTimeoutException(_clientName, description ?? "조건에 맞는 이벤트", timeout);
        }
    }

    /// <summary>kinds 중 하나가 오기를 기다린다 - WaitForAsync의 가장 흔한 사용 형태.</summary>
    public Task<ClientEvent> WaitForAnyAsync(
        TimeSpan timeout, long afterSeq, params ClientEventKind[] kinds) =>
        WaitForAsync(e => Array.IndexOf(kinds, e.Kind) >= 0, timeout, afterSeq,
            string.Join(" 또는 ", kinds));

    private sealed class Waiter
    {
        public Waiter(Func<ClientEvent, bool> predicate, long afterSeq)
        {
            Predicate = predicate;
            AfterSeq = afterSeq;
        }

        public Func<ClientEvent, bool> Predicate { get; }
        public long AfterSeq { get; }
        public TaskCompletionSource<ClientEvent> Tcs { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>기다리던 이벤트가 제한 시간 안에 안 왔다 - 시나리오 판정에서 FAIL 사유가 된다.</summary>
public sealed class ClientEventTimeoutException : TimeoutException
{
    public ClientEventTimeoutException(string clientName, string description, TimeSpan timeout)
        : base($"[{clientName}] {timeout.TotalSeconds:0.#}초 안에 '{description}'이(가) 오지 않음")
    {
    }
}
