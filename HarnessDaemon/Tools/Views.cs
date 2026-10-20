using System.Text.Json;
using TestHarness.Core;

namespace TestHarness.Daemon.Tools;

/// <summary>
/// 도구 응답에 싣는 이벤트 모양. 시각은 "HH:mm:ss.fff"(UTC) - 한 실행 안에서는 날짜가 거의 안 바뀌고,
/// logs_query의 시각과 같은 형식이라 둘을 나란히 놓고 맞춰 볼 수 있다.
/// </summary>
public sealed record EventView(long Seq, string T, string Source, string Name, string? PlayerId, string? Detail, JsonElement? Data)
{
    public static EventView From(TimelineEvent e) => new(
        e.Seq,
        Time(e.TimestampUtc),
        e.Source,
        e.Name,
        e.PlayerId,
        string.IsNullOrEmpty(e.Detail) ? null : HarnessJson.Truncate(e.Detail, 300),
        e.Data);

    public static string Time(DateTimeOffset t) => t.UtcDateTime.ToString("HH:mm:ss.fff");
}

/// <summary>logs_query 응답의 한 줄.</summary>
public sealed record LogView(string? T, string Server, string Level, string? Category, string Message, string? Exception)
{
    public static LogView From(LogEntry e) => new(
        e.Time is { } t ? EventView.Time(t) : null, e.Server, e.Level, e.Category, e.Message, e.Exception);
}
