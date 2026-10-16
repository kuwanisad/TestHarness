using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Harness.Protocol;

namespace TestHarness.HarnessHost;

/// <summary>
/// 명령 하나의 결과. Ok는 "명령 함수가 예외 없이 끝났다"는 뜻이다 - 클라 명령이라면
/// "요청을 보냈다"까지만 의미하고, 실제로 일어났는지는 이벤트로 판정한다.
/// </summary>
public sealed record CommandOutcome(bool Ok, string ValueJson, string Error)
{
    /// <summary>반환값을 T로 꺼낸다(반환값이 없거나 null이면 default).</summary>
    public T? GetValue<T>() =>
        string.IsNullOrEmpty(ValueJson) ? default : JsonSerializer.Deserialize<T>(ValueJson, AgentSession.JsonOptions);

    /// <summary>반환값을 JSON 요소로 꺼낸다 - 필드 몇 개만 볼 때 편하다.</summary>
    public JsonElement Value =>
        string.IsNullOrEmpty(ValueJson) ? default : JsonDocument.Parse(ValueJson).RootElement.Clone();

    public override string ToString() => Ok ? $"ok {ValueJson}" : $"FAILED {Error}";
}

/// <summary>
/// 접속해 있는 테스트 대상 하나(서버 프로세스, Unity 클라 등). 시나리오는 이걸로 명령을 보낸다.
/// 연결이 끊기면 IsConnected=false가 되고 대기 중이던 명령은 전부 실패로 끝난다. 같은 이름으로
/// 다시 접속하면(서버 재시작 등) 새 AgentSession이 생긴다 - 옛 객체를 계속 들고 있지 말고
/// <see cref="HarnessHost.WaitForAgentAsync"/>로 다시 받을 것.
/// </summary>
public sealed class AgentSession
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ConcurrentDictionary<string, TaskCompletionSource<CommandResult>> _pending = new();
    private readonly TimeSpan _defaultTimeout;
    private long _nextCommandId;
    private volatile bool _connected = true;

    internal AgentSession(Hello hello, TimeSpan defaultTimeout)
    {
        Name = hello.Name;
        ProcessKind = hello.ProcessKind;
        Pid = hello.Pid;
        Commands = hello.Commands.ToDictionary(c => c.Name, StringComparer.Ordinal);
        _defaultTimeout = defaultTimeout;
    }

    public string Name { get; }
    public string ProcessKind { get; }
    public int Pid { get; }

    /// <summary>이 대상이 받을 수 있는 명령(Hello에서 받은 목록).</summary>
    public IReadOnlyDictionary<string, CommandInfo> Commands { get; }

    public bool IsConnected => _connected;

    /// <summary>하네스 → 대상 송신 큐. HarnessControlService의 송신 루프가 단독 소비한다
    /// (gRPC 응답 스트림은 동시에 두 곳에서 쓸 수 없어서).</summary>
    internal Channel<HarnessMessage> Outbound { get; } = Channel.CreateUnbounded<HarnessMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    /// <summary>
    /// 명령을 보내고 결과를 기다린다. args는 익명 객체나 딕셔너리(예: <c>new { slotIndex = 1 }</c>) -
    /// 속성 이름이 그대로 명령 함수의 파라미터 이름에 대응한다.
    /// 모르는 명령, 연결 끊김, 시간 초과도 예외가 아니라 Ok=false로 돌려준다 - 시나리오에서
    /// "명령이 실패해야 정상"인 경우도 판정할 수 있게.
    /// </summary>
    public async Task<CommandOutcome> InvokeAsync(
        string commandName, object? args = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (!_connected) return new CommandOutcome(false, "", $"[{Name}] 연결이 끊긴 대상");

        if (!Commands.ContainsKey(commandName))
        {
            return new CommandOutcome(false, "",
                $"[{Name}] 모르는 명령 '{commandName}' (가능: {string.Join(", ", Commands.Keys.Order())})");
        }

        var id = Interlocked.Increment(ref _nextCommandId).ToString();
        var tcs = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var command = new Command { Id = id, Name = commandName, Args = ToStruct(args) };
        await Outbound.Writer.WriteAsync(new HarnessMessage { Command = command }, cancellationToken);

        var limit = timeout ?? _defaultTimeout;
        try
        {
            var result = await tcs.Task.WaitAsync(limit, cancellationToken);
            var valueJson = result.Value == null ? "" : JsonFormatter.Default.Format(result.Value);
            return new CommandOutcome(result.Ok, valueJson, result.Error);
        }
        catch (TimeoutException)
        {
            return new CommandOutcome(false, "", $"[{Name}] '{commandName}' 결과가 {limit.TotalSeconds:0.#}초 안에 안 옴");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>InvokeAsync + 실패면 예외. 반드시 성공해야 하는 준비 단계(상태 조회 등)에 쓴다.</summary>
    public async Task<CommandOutcome> InvokeOrThrowAsync(
        string commandName, object? args = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var outcome = await InvokeAsync(commandName, args, timeout, cancellationToken);
        if (!outcome.Ok) throw new InvalidOperationException($"[{Name}] {commandName} 실패: {outcome.Error}");
        return outcome;
    }

    internal void CompleteCommand(CommandResult result)
    {
        if (_pending.TryRemove(result.Id, out var tcs)) tcs.TrySetResult(result);
    }

    internal void MarkDisconnected(string reason)
    {
        _connected = false;
        Outbound.Writer.TryComplete();

        foreach (var (id, tcs) in _pending)
        {
            if (_pending.TryRemove(id, out _))
            {
                tcs.TrySetResult(new CommandResult { Id = id, Ok = false, Error = $"[{Name}] 결과를 받기 전에 연결 끊김: {reason}" });
            }
        }
    }

    private static Struct ToStruct(object? args)
    {
        if (args == null) return new Struct();
        if (args is Struct s) return s;

        // 익명 객체/딕셔너리 → JSON → protobuf Struct. 이름은 쓴 그대로 둔다(camelCase 변환 안 함) -
        // 명령 쪽이 파라미터 이름을 대소문자 무시로 맞춘다.
        var json = JsonSerializer.Serialize(args);
        return JsonParser.Default.Parse<Struct>(json);
    }
}
