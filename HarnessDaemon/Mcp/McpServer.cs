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

    /// <param name="calls">모든 tools/call(요청+응답)을 남길 곳 - 규칙 "AI가 출력하면 하네스가 기록한다".</param>
    public McpServer(McpToolRegistry registry, string instructions, string version, Action<string>? log, ToolCallLog? calls = null)
    {
        _registry = registry;
        _instructions = instructions;
        _version = version;
        _log = log;
        _calls = calls;
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
        string text;
        bool isError;

        try
        {
            var value = await _registry.InvokeAsync(tool, arguments, ct);
            text = value is string s ? s : HarnessJson.Serialize(value);
            isError = false;
        }
        catch (HarnessException ex)
        {
            text = HarnessJson.Serialize(new { error = ex.Message, hint = ex.Hint });
            isError = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 하네스 버그일 가능성이 높다 - 숨기지 않고 종류와 위치를 그대로 보여준다.
            text = HarnessJson.Serialize(new
            {
                error = $"하네스 내부 오류 {ex.GetType().Name}: {ex.Message}",
                hint = "하네스 쪽 버그일 수 있음 - 같은 호출을 반복하지 말고 사람에게 알릴 것",
                stack = HarnessJson.Truncate(ex.StackTrace, 1500),
            });
            isError = true;
        }

        _log?.Invoke($"[mcp] {name} {Brief(arguments)} → {(isError ? "ERROR" : "ok")} {stopwatch.Elapsed.TotalSeconds:0.0}s");
        _calls?.Record(name, arguments, text, isError, stopwatch.Elapsed, objective);

        return new JsonObject
        {
            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
            ["isError"] = isError,
        };
    }

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
