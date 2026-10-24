using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using TestHarness.Core;

namespace TestHarness.Daemon.Mcp;

/// <summary>
/// MCP 메시지(JSON-RPC 2.0) 처리 - 전송 방식(HTTP)과 분리돼 있다. 지원 범위는 하네스에 필요한 만큼:
///   initialize, notifications/initialized, ping, tools/list, tools/call
/// 그 외 메서드는 "Method not found". 서버가 먼저 보내는 알림(진행률, 목록 변경)은 없다.
///
/// 도구 결과는 text 콘텐츠 하나(JSON 문자열)로 보낸다. 도구가 HarnessException을 던지면 프로토콜 오류가
/// 아니라 isError=true인 정상 결과로 보낸다 - MCP 규약상 "도구가 실패했다"는 AI가 읽고 대응할 내용이라서.
///
/// 응답 시간 한도: 모든 도구는 "실행"과 "응답 기다리기"로 나뉜다. 실행은 하네스 수명 동안 계속되고(요청이 끊겨도 안 멈춤),
/// 응답은 최대 McpPendingCalls.ResponseBudget(50초)만 기다린다. 넘으면 pending(callId)으로 먼저 응답하고, AI가
/// call_continue로 이어서 기다리면 끝나는 순간 원래 결과로 응답한다(McpPendingCalls 주석). 끝난 결과는 원래 도구 이름으로도
/// 기록한다 - 판정 코드와 보고서가 원래 도구 기록을 보므로.
/// </summary>
public sealed class McpServer
{
    /// <summary>알고 있는 프로토콜 버전(새것부터). 클라가 이 중 하나를 요청하면 그대로, 아니면 첫 번째를 쓴다.</summary>
    public static readonly string[] SupportedProtocolVersions = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };

    private readonly McpToolRegistry _registry;
    private readonly string _instructions;
    private readonly string _version;
    private readonly Action<string>? _log;
    private readonly ToolCallLog? _calls;
    private readonly McpPendingCalls _pending;
    private readonly CancellationToken _lifetime;

    /// <param name="calls">모든 tools/call(요청+응답)을 남길 곳 - 규칙 "AI가 출력하면 하네스가 기록한다".</param>
    /// <param name="pending">응답 시간 한도를 넘긴 호출 목록(call_continue 도구와 같은 것을 넘긴다).</param>
    /// <param name="lifetime">하네스 종료 신호 - 도구 실행은 요청이 아니라 이것으로만 취소된다.</param>
    public McpServer(McpToolRegistry registry, string instructions, string version, Action<string>? log, ToolCallLog? calls,
        McpPendingCalls pending, CancellationToken lifetime)
    {
        _registry = registry;
        _instructions = instructions;
        _version = version;
        _log = log;
        _calls = calls;
        _pending = pending;
        _lifetime = lifetime;
    }

    /// <summary>메시지 하나를 처리한다. 알림(id 없음)이면 null(응답 없음).</summary>
    public async Task<JsonObject?> HandleAsync(JsonElement message, CancellationToken ct)
    {
        if (message.ValueKind != JsonValueKind.Object) return Error(null, -32600, "Invalid Request");

        JsonNode? id = message.TryGetProperty("id", out var idElement) ? JsonNode.Parse(idElement.GetRawText()) : null;
        var isNotification = !message.TryGetProperty("id", out _);
        var method = message.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        JsonElement? parameters = message.TryGetProperty("params", out var p) ? p : null;

        if (method == null)
        {
            // 응답(result/error) 메시지 - 서버가 요청을 보내지 않으니 받을 일이 없다. 무시.
            return isNotification ? null : Error(id, -32600, "Invalid Request");
        }

        if (isNotification) return null; // notifications/initialized, notifications/cancelled 등

        try
        {
            return method switch
            {
                "initialize" => Result(id, Initialize(parameters)),
                "ping" => Result(id, new JsonObject()),
                "tools/list" => Result(id, ListTools()),
                "tools/call" => Result(id, await CallToolAsync(parameters, ct)),
                _ => Error(id, -32601, $"Method not found: {method}"),
            };
        }
        catch (McpProtocolException ex)
        {
            return Error(id, ex.Code, ex.Message);
        }
    }

    private JsonObject Initialize(JsonElement? parameters)
    {
        var requested = parameters is { } p && p.TryGetProperty("protocolVersion", out var v) ? v.GetString() : null;
        var version = requested != null && SupportedProtocolVersions.Contains(requested) ? requested : SupportedProtocolVersions[0];

        var clientName = parameters is { } q && q.TryGetProperty("clientInfo", out var info) && info.TryGetProperty("name", out var n)
            ? n.GetString()
            : "?";
        _log?.Invoke($"[mcp] 접속: {clientName} (protocol {version})");

        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "testharness", ["version"] = _version },
            ["instructions"] = _instructions,
        };
    }

    private JsonObject ListTools()
    {
        var tools = new JsonArray();
        foreach (var tool in _registry.Tools.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone(),
            });
        }

        return new JsonObject { ["tools"] = tools };
    }

    private async Task<JsonObject> CallToolAsync(JsonElement? parameters, CancellationToken ct)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } p || !p.TryGetProperty("name", out var nameElement))
        {
            throw new McpProtocolException(-32602, "tools/call: name이 없음");
        }

        var name = nameElement.GetString() ?? "";
        JsonElement? arguments = p.TryGetProperty("arguments", out var a) ? a : null;
        var objective = _calls?.CaptureObjective();

        if (!_registry.TryGet(name, out var tool))
        {
            _calls?.Record(name, arguments, HarnessJson.Serialize(new { error = $"Unknown tool: {name}" }), true, TimeSpan.Zero, objective);
            throw new McpProtocolException(-32602, $"Unknown tool: {name}");
        }

        var stopwatch = Stopwatch.StartNew();
        // 요청 메시지(JsonDocument)는 응답과 함께 사라진다 - 계속 도는 작업이 쓸 수 있게 복사해 둔다.
        var args = arguments?.Clone();

        // 실행: 하네스 수명 동안(요청이 끊겨도 계속). Task.Run - 동기로 오래 막는 도구도 응답 한도를 지키게.
        var work = Task.Run(() => RunAsync(tool, args));
        await Task.WhenAny(work, Task.Delay(_pending.ResponseBudget, ct));

        ToolOutcome outcome;
        if (work.IsCompleted)
        {
            outcome = await work;
        }
        else
        {
            var pending = _pending.Add(name, work);
            _ = work.ContinueWith(done => RecordCompletion(pending, args, stopwatch, done.Result), TaskScheduler.Default);

            if (ct.IsCancellationRequested)
            {
                // 클라(Claude Code)가 한도 전에 먼저 끊음 - 응답은 못 보내도 "모든 호출 기록"은 지킨다(작업은 계속 돌고, 끝나면 기록됨).
                var cancelled = HarnessJson.Serialize(new
                {
                    error = $"응답 전에 호출이 끊김({stopwatch.Elapsed.TotalSeconds:0.0}초) - 클라가 기다리기를 그만둠. 작업은 계속되고 끝나면 기록된다(callId={pending.Id})",
                });
                _log?.Invoke($"[mcp] {name} {Brief(args)} → CANCELLED {stopwatch.Elapsed.TotalSeconds:0.0}s ({pending.Id})");
                _calls?.Record(name, args, cancelled, true, stopwatch.Elapsed, objective);
                throw new OperationCanceledException(ct);
            }

            outcome = new ToolOutcome(_pending.PendingText(pending), false);
            _log?.Invoke($"[mcp] {name} {Brief(args)} → PENDING {pending.Id} {stopwatch.Elapsed.TotalSeconds:0.0}s");
            _calls?.Record(name, args, outcome.Text, false, stopwatch.Elapsed, objective);
            return Content(outcome);
        }

        _log?.Invoke($"[mcp] {name} {Brief(args)} → {(outcome.IsError ? "ERROR" : "ok")} {stopwatch.Elapsed.TotalSeconds:0.0}s");
        _calls?.Record(name, args, outcome.Text, outcome.IsError, stopwatch.Elapsed, objective);
        return Content(outcome);
    }

    /// <summary>도구 하나를 실행해 결과 글로 바꾼다. 어떤 실패든 예외 대신 isError 결과로.</summary>
    private async Task<ToolOutcome> RunAsync(McpToolDefinition tool, JsonElement? arguments)
    {
        try
        {
            var value = await _registry.InvokeAsync(tool, arguments, _lifetime);
            // call_continue처럼 다른 호출의 결과를 그대로 돌려주는 도구는 ToolOutcome(오류 여부 포함)을 돌려준다.
            if (value is ToolOutcome passthrough) return passthrough;
            return new ToolOutcome(value is string s ? s : HarnessJson.Serialize(value), false);
        }
        catch (HarnessException ex)
        {
            return new ToolOutcome(HarnessJson.Serialize(new { error = ex.Message, hint = ex.Hint }), true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return new ToolOutcome(HarnessJson.Serialize(new { error = "하네스가 종료되는 중 - 작업을 멈춤" }), true);
        }
        catch (Exception ex)
        {
            // 하네스 버그일 가능성이 높다 - 숨기지 않고 종류와 위치를 그대로 보여준다.
            return new ToolOutcome(HarnessJson.Serialize(new
            {
                error = $"하네스 내부 오류 {ex.GetType().Name}: {ex.Message}",
                hint = "하네스 쪽 버그일 수 있음 - 같은 호출을 반복하지 말고 사람에게 알릴 것",
                stack = HarnessJson.Truncate(ex.StackTrace, 1500),
            }), true);
        }
    }

    /// <summary>pending이었던 작업이 끝남 - 원래 도구 이름/인자로 결과를 기록한다(그 시점의 시나리오 노드에 묶임).</summary>
    private void RecordCompletion(PendingCall pending, JsonElement? args, Stopwatch stopwatch, ToolOutcome outcome)
    {
        pending.CompletedAt = DateTimeOffset.UtcNow;
        _log?.Invoke($"[mcp] {pending.Tool} {Brief(args)} → {(outcome.IsError ? "ERROR" : "ok")} {stopwatch.Elapsed.TotalSeconds:0.0}s (끝남, {pending.Id})");
        _calls?.Record(pending.Tool, args, outcome.Text, outcome.IsError, stopwatch.Elapsed, _calls.CaptureObjective());
    }

    private static JsonObject Content(ToolOutcome outcome) => new()
    {
        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = outcome.Text } },
        ["isError"] = outcome.IsError,
    };

    private static string Brief(JsonElement? arguments)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } args) return "";
        return HarnessJson.Truncate(args.GetRawText(), 160);
    }

    private static JsonObject Result(JsonNode? id, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private sealed class McpProtocolException : Exception
    {
        public McpProtocolException(int code, string message) : base(message) => Code = code;
        public int Code { get; }
    }
}
