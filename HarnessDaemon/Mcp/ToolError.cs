using TestHarness.Core;

namespace TestHarness.Daemon.Mcp;

/// <summary>
/// 도구가 "없음/지금은 안 됨"을 알릴 때 돌려주는 값 - 예외를 던지지 않고 반환한다(McpToolRegistry가 오류 결과로 바꾼다).
/// 예외(HarnessException)는 실제 작업이 실패했을 때만 쓴다. 어느 쪽이든 AI가 받는 모양은 같다({error, hint} + isError).
/// </summary>
/// <param name="Error">무엇이 없는지/왜 안 되는지.</param>
/// <param name="Hint">AI가 다음에 할 일.</param>
public sealed record ToolError(string Error, string? Hint = null)
{
    public ToolOutcome ToOutcome() => new(HarnessJson.Serialize(new { error = Error, hint = Hint }), true);
}
