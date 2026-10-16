namespace TestHarness.HarnessHost;

/// <summary>
/// 하네스가 받은 이벤트 하나. 테스트 대상이 올려 보낸 AgentEvent와, 하네스가 직접 남기는
/// 접속/끊김 기록(<see cref="HarnessEventNames"/>)이 같은 줄에 섞인다.
/// TimestampUtc는 대상 프로세스가 찍은 시각(하네스가 받은 시각이 아님) - 로그와 합쳐
/// 타임라인을 만들 때의 정렬 기준.
/// </summary>
public sealed record HarnessEvent(
    long Seq,
    DateTimeOffset TimestampUtc,
    string AgentName,
    string Name,
    string PlayerId,
    string DataJson)
{
    public override string ToString()
    {
        var player = string.IsNullOrEmpty(PlayerId) ? "" : $" player={PlayerId}";
        var data = DataJson is "" or "{}" ? "" : $" {DataJson}";
        return $"{TimestampUtc:yyyy-MM-ddTHH:mm:ss.fffZ} [{AgentName}] {Name}{player}{data}";
    }
}

/// <summary>하네스가 스스로 기록하는 이벤트 이름.</summary>
public static class HarnessEventNames
{
    /// <summary>테스트 대상이 접속해서 Hello가 받아들여짐. data: kind, pid, commands.</summary>
    public const string Attached = "harness.attached";

    /// <summary>테스트 대상과의 연결이 끊김(프로세스 종료 포함). data: reason.</summary>
    public const string Detached = "harness.detached";

    /// <summary>Hello가 거부됨(버전 불일치, 이름 중복 등). data: reason.</summary>
    public const string Rejected = "harness.rejected";
}

/// <summary>
/// 이벤트 기록 + 조건 대기. HeadlessClient의 ClientEventLog와 같은 방식 - 고정 시간 대기 대신
/// "N초 안에 오면 통과, 안 오면 실패". 여러 접속(스레드)이 동시에 기록하므로 락으로 보호한다.
/// </summary>
public sealed class HarnessEventLog
{
    private readonly object _lock = new();
    private readonly List<HarnessEvent> _events = new();
    private readonly List<Waiter> _waiters = new();
    private readonly Action<HarnessEvent>? _sink;
    private long _nextSeq = 1;

    public HarnessEventLog(Action<HarnessEvent>? sink) => _sink = sink;

    /// <summary>마지막 이벤트 번호(없으면 0). 행동 직전에 읽어두고 WaitForAsync의 afterSeq로 넘긴다.</summary>
    public long LastSeq
    {
        get { lock (_lock) return _nextSeq - 1; }
    }

    public IReadOnlyList<HarnessEvent> Snapshot()
    {
        lock (_lock) return _events.ToArray();
    }

    public HarnessEvent Record(DateTimeOffset timestampUtc, string agentName, string name, string playerId, string dataJson)
    {
        HarnessEvent evt;
        List<Waiter>? satisfied = null;

        lock (_lock)
        {
            evt = new HarnessEvent(_nextSeq++, timestampUtc, agentName, name, playerId, dataJson);
            _events.Add(evt);

            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                var waiter = _waiters[i];
                if (evt.Seq <= waiter.AfterSeq || !waiter.Predicate(evt)) continue;

                _waiters.RemoveAt(i);
                (satisfied ??= new List<Waiter>()).Add(waiter);
            }
        }

        // 출력/대기 해제는 락 밖에서.
        _sink?.Invoke(evt);
        if (satisfied != null)
        {
            foreach (var waiter in satisfied) waiter.Tcs.TrySetResult(evt);
        }

        return evt;
    }

    /// <summary>
    /// afterSeq 뒤에 기록된 이벤트 중 predicate를 만족하는 첫 이벤트를 기다린다. 이미 와 있으면
    /// 즉시 반환. timeout 안에 안 오면 <see cref="HarnessTimeoutException"/>.
    /// </summary>
    public async Task<HarnessEvent> WaitForAsync(
        Func<HarnessEvent, bool> predicate, TimeSpan timeout, long afterSeq = 0,
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
        catch (OperationCanceledException)
        {
            lock (_lock) _waiters.Remove(waiter);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new HarnessTimeoutException($"{timeout.TotalSeconds:0.#}초 안에 '{description ?? "조건에 맞는 이벤트"}'이(가) 오지 않음");
        }
    }

    /// <summary>agentName이 보낸 이름 eventName의 이벤트를 기다린다(agentName이 null이면 누구든).</summary>
    public Task<HarnessEvent> WaitForEventAsync(
        string? agentName, string eventName, TimeSpan timeout, long afterSeq = 0,
        CancellationToken cancellationToken = default) =>
        WaitForAsync(
            e => e.Name == eventName && (agentName == null || e.AgentName == agentName),
            timeout, afterSeq, $"{agentName ?? "*"}/{eventName}", cancellationToken);

    private sealed class Waiter
    {
        public Waiter(Func<HarnessEvent, bool> predicate, long afterSeq)
        {
            Predicate = predicate;
            AfterSeq = afterSeq;
        }

        public Func<HarnessEvent, bool> Predicate { get; }
        public long AfterSeq { get; }
        public TaskCompletionSource<HarnessEvent> Tcs { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>기다리던 것이 제한 시간 안에 안 왔다 - 시나리오 판정에서 FAIL 사유.</summary>
public sealed class HarnessTimeoutException : TimeoutException
{
    public HarnessTimeoutException(string message) : base(message)
    {
    }
}
