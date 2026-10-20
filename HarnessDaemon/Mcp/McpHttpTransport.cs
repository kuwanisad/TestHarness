using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace TestHarness.Daemon.Mcp;

/// <summary>
/// MCP Streamable HTTP 전송 중 하네스에 필요한 부분만:
/// - POST /mcp: JSON-RPC 메시지 하나(또는 배열)를 받아 application/json으로 응답. 알림만 왔으면 202.
/// - GET/DELETE /mcp: 405 - 서버가 먼저 보내는 스트림(SSE)과 세션을 쓰지 않는다.
///
/// 세션(Mcp-Session-Id)을 쓰지 않는 이유: 하네스가 재시작돼도 Claude Code가 끊긴 세션 때문에 다시
/// 접속 절차를 밟을 필요가 없게. 상태는 MCP 연결이 아니라 하네스 프로세스가 들고 있다.
///
/// 보안: localhost에서만 열고(Program.cs), 브라우저에서 온 요청(Origin 헤더가 localhost가 아님)은
/// 거부한다 - 웹 페이지가 DNS 리바인딩으로 로컬 하네스를 조작하는 것을 막으라는 MCP 규약 권고.
/// </summary>
public static class McpHttpTransport
{
    public static void Map(WebApplication app, string path, McpServer server)
    {
        app.MapPost(path, async (HttpContext context) =>
        {
            if (!IsAllowedOrigin(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            JsonDocument document;
            try
            {
                document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteJsonAsync(context, StatusCodes.Status400BadRequest, new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = null,
                    ["error"] = new JsonObject { ["code"] = -32700, ["message"] = "Parse error" },
                });
                return;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    var responses = new JsonArray();
                    foreach (var message in root.EnumerateArray())
                    {
                        var response = await server.HandleAsync(message, context.RequestAborted);
                        if (response != null) responses.Add(response);
                    }

                    if (responses.Count == 0) context.Response.StatusCode = StatusCodes.Status202Accepted;
                    else await WriteJsonAsync(context, StatusCodes.Status200OK, responses);
                    return;
                }

                var single = await server.HandleAsync(root, context.RequestAborted);
                if (single == null) context.Response.StatusCode = StatusCodes.Status202Accepted;
                else await WriteJsonAsync(context, StatusCodes.Status200OK, single);
            }
        });

        app.MapMethods(path, new[] { "GET", "DELETE" }, (HttpContext context) =>
        {
            context.Response.Headers.Allow = "POST";
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
        });
    }

    private static bool IsAllowedOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return true; // 브라우저가 아닌 클라(Claude Code)는 Origin을 안 보낸다.
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        return uri.Host is "localhost" or "127.0.0.1" or "[::1]" or "::1";
    }

    private static async Task WriteJsonAsync(HttpContext context, int status, JsonNode body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        // 한글을 \uXXXX로 바꾸지 않는다(응답이 3배쯤 짧아진다 - JSON으로는 둘 다 같은 값).
        await context.Response.WriteAsync(body.ToJsonString(Core.HarnessJson.Options), context.RequestAborted);
    }
}
