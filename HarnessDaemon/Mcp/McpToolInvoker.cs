using System.Diagnostics;
using System.Text.Json;
using TestHarness.Core;
using TestHarness.Core.Scenarios;

namespace TestHarness.Daemon.Mcp;

/// <summary>
/// 시나리오 노드 실행(TH_RunContext)이 도구를 부르는 통로 - AI가 MCP로 부르는 것과 같은 도구 목록(McpToolRegistry)을 같은
/// 프로세스 안에서 부른다. HTTP와 응답 시간 한도(50초)는 거치지 않는다(하네스 안이라 끊길 일이 없음). 기록은 같은 곳
/// (ToolCallLog)에 actor="runner"로 남긴다 - 노드 판정은 이 기록만 본다.
/// </summary>
public sealed class McpToolInvoker : ITH_ToolInvoker
{
    private readonly McpToolRegistry _registry;
    private readonly ToolCallLog _calls;
    private readonly Action<string>? _log;

    public McpToolInvoker(McpToolRegistry registry, ToolCallLog calls, Action<string>? log)
    {
        _registry = registry;
        _calls = calls;
        _log = log;
    }

    public async Task<TH_ToolResult> InvokeAsync(string tool, string argumentsJson, string? objective, CancellationToken ct)
    {
        var arguments = HarnessJson.TryParse(argumentsJson);
        var stopwatch = Stopwatch.StartNew();
        string text;
        bool isError;

        if (!_registry.TryGet(tool, out var definition))
        {
            text = HarnessJson.Serialize(new { error = $"모르는 도구: {tool}" });
            isError = true;
        }
        else
        {
            try
            {
                var value = await _registry.InvokeAsync(definition, arguments, ct);
                text = value is ToolOutcome passthrough ? passthrough.Text : value is string s ? s : HarnessJson.Serialize(value);
                isError = value is ToolOutcome { IsError: true };
            }
            catch (HarnessException ex)
            {
                text = HarnessJson.Serialize(new { error = ex.Message, hint = ex.Hint });
                isError = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                text = HarnessJson.Serialize(new { error = $"하네스 내부 오류 {ex.GetType().Name}: {ex.Message}" });
                isError = true;
            }
        }

        _log?.Invoke($"[runner] {objective} {tool} {HarnessJson.Truncate(argumentsJson, 120)} → {(isError ? "ERROR" : "ok")} {stopwatch.Elapsed.TotalSeconds:0.0}s");
        _calls.Record(tool, arguments, text, isError, stopwatch.Elapsed, objective, ToolCallLog.ActorRunner);

        var parsed = HarnessJson.TryParse(text) ?? JsonSerializer.SerializeToElement(text);
        return new TH_ToolResult(parsed, isError, text);
    }
}
