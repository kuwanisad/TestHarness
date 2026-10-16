using System.Text.Json;
using Google.Protobuf;
using Harness.Protocol;

namespace TestHarness.HarnessHost;

/// <summary>지금 접속해 있는 테스트 대상 목록 + 접속/끊김/이벤트 기록.</summary>
internal sealed class AgentRegistry
{
    // 하네스가 직접 만드는 이벤트 JSON - 사람이 읽는 출력이라 한글을 \uXXXX로 바꾸지 않는다.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _lock = new();
    private readonly Dictionary<string, AgentSession> _sessions = new(StringComparer.Ordinal);
    private readonly TimeSpan _commandTimeout;

    public AgentRegistry(HarnessEventLog events, TimeSpan commandTimeout)
    {
        Events = events;
        _commandTimeout = commandTimeout;
    }

    public HarnessEventLog Events { get; }

    public IReadOnlyList<AgentSession> Snapshot()
    {
        lock (_lock) return _sessions.Values.ToArray();
    }

    public AgentSession? Find(string name)
    {
        lock (_lock) return _sessions.TryGetValue(name, out var s) ? s : null;
    }

    /// <summary>받아들일 수 없는 Hello면 사유, 괜찮으면 null.</summary>
    public string? CheckHello(Hello hello)
    {
        var expected = (int)ProtocolVersion.Current;
        if (hello.ProtocolVersion != expected)
        {
            return $"프로토콜 버전 불일치(대상={hello.ProtocolVersion}, 하네스={expected}) - " +
                   "harness.proto를 고친 뒤 TestHarness와 대상 프로젝트를 둘 다 다시 빌드했는지 확인";
        }

        if (string.IsNullOrWhiteSpace(hello.Name)) return "이름(HARNESS_NAME)이 비어 있음";

        lock (_lock)
        {
            if (_sessions.TryGetValue(hello.Name, out var existing) && existing.IsConnected)
            {
                return $"같은 이름 '{hello.Name}'이(가) 이미 접속 중(pid={existing.Pid})";
            }
        }

        return null;
    }

    public AgentSession Register(Hello hello)
    {
        var session = new AgentSession(hello, _commandTimeout);
        lock (_lock) _sessions[hello.Name] = session;

        Events.Record(DateTimeOffset.UtcNow, hello.Name, HarnessEventNames.Attached, "",
            JsonSerializer.Serialize(new
            {
                kind = hello.ProcessKind,
                pid = hello.Pid,
                commands = hello.Commands.Select(c => c.Name).Order().ToArray(),
            }, JsonOptions));
        return session;
    }

    public void Unregister(AgentSession session, string reason)
    {
        session.MarkDisconnected(reason);

        lock (_lock)
        {
            // 그 사이 같은 이름으로 새로 접속했으면(재시작) 새 쪽은 건드리지 않는다.
            if (_sessions.TryGetValue(session.Name, out var current) && ReferenceEquals(current, session))
            {
                _sessions.Remove(session.Name);
            }
        }

        Events.Record(DateTimeOffset.UtcNow, session.Name, HarnessEventNames.Detached, "",
            JsonSerializer.Serialize(new { reason }, JsonOptions));
    }

    public void RecordRejected(Hello hello, string reason) =>
        Events.Record(DateTimeOffset.UtcNow, hello.Name, HarnessEventNames.Rejected, "",
            JsonSerializer.Serialize(new { reason, pid = hello.Pid }, JsonOptions));

    public void RecordAgentEvent(AgentSession session, AgentEvent evt)
    {
        var timestamp = evt.TimestampUnixMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(evt.TimestampUnixMs)
            : DateTimeOffset.UtcNow;
        var dataJson = evt.Data == null ? "" : JsonFormatter.Default.Format(evt.Data);
        Events.Record(timestamp, session.Name, evt.Name, evt.PlayerId, dataJson);
    }
}
