namespace TestHarness.Daemon.Mcp;

// =============================================================================================
// MCP 계층 - 공식 SDK로 바꿀 때 손댈 곳
//
// 지금은 MCP 중 "도구" 기능만 직접 구현했다(NuGet이 막힌 작업 환경에서도 끝까지 검증하려고).
// 도구 클래스(Tools/*.cs)는 MCP 전송 방식을 모른다 - 아래 두 가지만 쓴다:
//   - [McpTool("이름")] + System.ComponentModel.[Description]  (메서드와 파라미터에)
//   - 반환값은 평범한 객체(JSON으로 직렬화됨), 실패는 HarnessException(메시지 + 힌트)
// 공식 SDK(ModelContextProtocol.AspNetCore)로 바꿀 때:
//   1. [McpTool("x")] → [McpServerTool(Name = "x")], 도구 클래스에 [McpServerToolType]
//      ([Description]은 SDK도 같은 System.ComponentModel 것을 쓴다 - 그대로 둠)
//   2. Program.cs의 McpHttpTransport.Map(...) → services.AddMcpServer(o => o.ServerInstructions = ...)
//      .WithHttpTransport().WithTools<...>() + app.MapMcp("/mcp")
//   3. 이 폴더(Mcp/)의 나머지 파일(McpServer, McpToolRegistry, McpHttpTransport) 삭제
//   HarnessException → 오류 결과 변환은 SDK의 필터(또는 도구 안 try/catch)로 옮긴다.
// =============================================================================================

/// <summary>이 메서드를 MCP 도구로 노출한다. 설명은 [Description]으로 단다.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class McpToolAttribute : Attribute
{
    public McpToolAttribute(string name) => Name = name;

    /// <summary>도구 이름(snake_case). AI가 부르는 이름이라 바꾸면 사용 안내(McpInstructions.md)도 같이 고칠 것.</summary>
    public string Name { get; }
}
