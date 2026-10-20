using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TestHarness.Core;

/// <summary>로그 한 줄(조회 응답용으로 다듬은 것).</summary>
/// <param name="Level">Trace..Critical. JSON이 아닌 줄은 Raw(.log) / Error(.err.log).</param>
/// <param name="Category">로거 이름의 마지막 부분(예: LobbyServer.Services.LobbyControlService → LobbyControlService).</param>
public sealed record LogEntry(
    DateTimeOffset? Time,
    string Server,
    string Level,
    string? Category,
    string Message,
    string? Exception);

public sealed record LogQueryResult(
    int Matched,
    int Returned,
    bool Truncated,
    IReadOnlyList<LogEntry> Entries,
    IReadOnlyList<string> Files,
    string? Hint);

/// <summary>로그 조회 조건.</summary>
/// <param name="Server">서버 이름(비면 전부). 재시작으로 생긴 Name.2.log 등도 같은 서버로 본다.</param>
/// <param name="MinLevel">이 수준 이상만(Raw 줄은 Information으로 친다).</param>
/// <param name="Contains">메시지/예외에 들어 있는 문자열(대소문자 무시) - playerId, roomId를 넣으면 그 사람/방만.</param>
/// <param name="FromEnd">true면 조건에 맞는 마지막 Limit개, false면 처음 Limit개.</param>
public sealed record LogQuery(
    string? Server,
    LogLevel MinLevel,
    string? Contains,
    DateTimeOffset? SinceUtc,
    DateTimeOffset? UntilUtc,
    int Limit,
    bool FromEnd);

/// <summary>
/// 실행 폴더의 서버 로그(DevTools 하네스 모드가 남긴 JSON 한 줄 로그) 조회. 파일을 통째로 돌려주지 않고
/// 걸러서 개수 제한을 건다 - AI 컨텍스트가 로그로 차면 그때부터 판단력이 떨어지므로.
///
/// 로그 형식(ASP.NET Core JSON 콘솔 포매터): {"Timestamp":"...","EventId":0,"LogLevel":"Information",
/// "Category":"...","Message":"...","Exception":"...","State":{...}}. 빌드 출력처럼 JSON이 아닌 줄도
/// 버리지 않고 Raw로 싣는다(바로 앞 줄의 시각을 물려받음).
/// </summary>
public sealed class LogStore
{
    public const int MaxMessageLength = 400;

    private readonly RunManager _runs;
    private readonly HarnessPaths _paths;

    public LogStore(RunManager runs, HarnessPaths paths)
    {
        _runs = runs;
        _paths = paths;
    }

    public LogQueryResult Query(LogQuery query, string? runId = null)
    {
        var logsDir = ResolveLogsDir(runId);
        if (!Directory.Exists(logsDir))
        {
            return new LogQueryResult(0, 0, false, Array.Empty<LogEntry>(), Array.Empty<string>(), "로그 폴더가 없음 - 서버를 띄운 적이 없는 실행");
        }

        var files = Directory.GetFiles(logsDir, "*.log")
            .Where(f => query.Server == null || string.Equals(ServerNameOf(f), query.Server, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var matched = new List<LogEntry>();
        foreach (var file in files)
        {
            var server = ServerNameOf(file);
            var isErr = file.EndsWith(".err.log", StringComparison.OrdinalIgnoreCase);
            DateTimeOffset? lastTime = null;

            foreach (var line in ServerManager.ReadSharedLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var entry = Parse(line, server, isErr, lastTime);
                lastTime = entry.Time ?? lastTime;
                if (Accept(entry, query)) matched.Add(entry);
            }
        }

        // 여러 파일을 시각순으로 합친다(시각 없는 줄은 맨 앞 - 파일 첫 줄이 JSON이 아닌 경우뿐).
        var ordered = matched.OrderBy(e => e.Time ?? DateTimeOffset.MinValue).ToList();
        var limit = Math.Clamp(query.Limit, 1, 500);
        var page = query.FromEnd ? ordered.Skip(Math.Max(0, ordered.Count - limit)).ToList() : ordered.Take(limit).ToList();
        var truncated = ordered.Count > page.Count;

        string? hint = null;
        if (truncated)
        {
            hint = $"조건에 맞는 {ordered.Count}줄 중 {(query.FromEnd ? "마지막" : "처음")} {page.Count}줄만 보냄 - " +
                   "contains/server/minLevel/since로 좁히거나 fromEnd를 바꿔 볼 것";
        }
        else if (files.Count == 0)
        {
            hint = query.Server == null ? "로그 파일이 없음" : $"'{query.Server}' 로그 파일이 없음 - 서버 이름 확인(harness_status)";
        }

        return new LogQueryResult(ordered.Count, page.Count, truncated, page, files.Select(_paths.Relative).ToList(), hint);
    }

    private string ResolveLogsDir(string? runId)
    {
        if (runId != null)
        {
            var dir = _runs.FindRunDir(runId) ?? throw new HarnessException($"실행 '{runId}'를 찾지 못함");
            return Path.Combine(dir, "logs");
        }

        return (_runs.Current ?? throw new HarnessException("열린 실행이 없음", "runId로 지난 실행을 지정하거나 run_begin/servers_start 먼저")).LogsDir;
    }

    /// <summary>LobbyServer.log / LobbyServer.2.log / LobbyServer.err.log → LobbyServer.</summary>
    private static string ServerNameOf(string path)
    {
        var name = Path.GetFileName(path);
        var dot = name.IndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    private static bool Accept(LogEntry entry, LogQuery query)
    {
        if (LevelOf(entry.Level) < query.MinLevel) return false;
        if (query.SinceUtc is { } since && entry.Time is { } t1 && t1 < since) return false;
        if (query.UntilUtc is { } until && entry.Time is { } t2 && t2 > until) return false;

        if (!string.IsNullOrEmpty(query.Contains))
        {
            var inMessage = entry.Message.Contains(query.Contains, StringComparison.OrdinalIgnoreCase);
            var inException = entry.Exception?.Contains(query.Contains, StringComparison.OrdinalIgnoreCase) == true;
            if (!inMessage && !inException) return false;
        }

        return true;
    }

    private static LogLevel LevelOf(string level) => level switch
    {
        "Raw" => LogLevel.Information,
        _ => Enum.TryParse<LogLevel>(level, ignoreCase: true, out var parsed) ? parsed : LogLevel.Information,
    };

    /// <summary>로그 한 줄을 사람이 읽는 한 줄로(실패 원인 요약용): "HH:mm:ss.fff Level Category: 메시지 | 예외".</summary>
    internal static string Render(string line)
    {
        var entry = Parse(line, "", isErr: false, lastTime: null);
        if (entry.Level == "Raw") return entry.Message;

        var time = entry.Time is { } t ? t.UtcDateTime.ToString("HH:mm:ss.fff") + " " : "";
        var exception = entry.Exception != null ? " | " + entry.Exception : "";
        return $"{time}{entry.Level} {entry.Category}: {entry.Message}{exception}";
    }

    internal static LogEntry Parse(string line, string server, bool isErr, DateTimeOffset? lastTime)
    {
        if (line.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var time = root.TryGetProperty("Timestamp", out var ts) && ts.ValueKind == JsonValueKind.String &&
                           DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture,
                               DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                    ? parsed
                    : lastTime;
                var level = root.TryGetProperty("LogLevel", out var lv) ? lv.GetString() ?? "Information" : "Information";
                var category = root.TryGetProperty("Category", out var cat) ? ShortCategory(cat.GetString()) : null;
                var message = root.TryGetProperty("Message", out var msg) ? msg.GetString() ?? "" : "";
                var exception = root.TryGetProperty("Exception", out var ex) && ex.ValueKind == JsonValueKind.String
                    ? FirstLines(ex.GetString(), 3)
                    : null;

                return new LogEntry(time, server, level, category, HarnessJson.Truncate(message, MaxMessageLength), exception);
            }
            catch (JsonException)
            {
                // JSON처럼 생겼지만 아니면 아래 Raw로.
            }
        }

        return new LogEntry(lastTime, server, isErr ? "Error" : "Raw", null, HarnessJson.Truncate(line, MaxMessageLength), null);
    }

    private static string? ShortCategory(string? category)
    {
        if (string.IsNullOrEmpty(category)) return null;
        var dot = category.LastIndexOf('.');
        return dot >= 0 ? category[(dot + 1)..] : category;
    }

    private static string? FirstLines(string? text, int count)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var lines = text.Split('\n').Take(count).Select(l => l.TrimEnd('\r'));
        return HarnessJson.Truncate(string.Join(" | ", lines), MaxMessageLength);
    }
}
