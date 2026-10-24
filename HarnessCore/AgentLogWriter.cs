using System.Text;
using System.Text.Json;
using TestHarness.HarnessHost;

namespace TestHarness.Core;

/// <summary>
/// 대상이 보낸 로그(AgentLog - 자기 로그 파일이 없는 Unity)를 지금 실행의 logs/&lt;에이전트 이름&gt;.log에 쓴다.
/// 형식은 .NET 서버 로그(DevTools가 받은 JSON 콘솔 출력)와 같다 - {"Timestamp","LogLevel","Category","Message","Exception"} +
/// "Pid"(같은 프로세스의 두 에이전트가 같은 줄을 보냈는지 알아보는 데 씀). 그래서 LogStore(logs_query), RunTrace(보고서)가
/// 서버 로그와 같은 경로로 읽는다.
///
/// - 열린 실행이 없으면 버린다(서버 로그도 실행 폴더에만 남는 것과 같다).
/// - 줄마다 열고 닫는다(HarnessFiles.AppendLine).
/// - 메인 에디터는 Play 중에 UnityEditor와 client가 같은 로그를 각자 보낸다 - 일부러 둘 다 남긴다(에이전트마다 자기 로그가
///   온전해야 해서). 같은 줄 합치기는 읽는 쪽(RunTrace)이 Pid로 한다.
/// </summary>
public sealed class AgentLogWriter
{
    // 한글을 \uXXXX로 바꾸지 않는다(파일을 grep으로 바로 찾을 수 있게) - 하네스의 다른 JSON(HarnessJson)과 같은 인코더.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = HarnessJson.Options.Encoder };

    private readonly RunManager _runs;
    private readonly object _lock = new();

    // 지금 실행에서 이 클래스가 쓴 로그 파일(노드 경계 줄을 넣을 곳) - 실행이 바뀌면 비운다.
    private string? _runId;
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);

    public AgentLogWriter(RunManager runs) => _runs = runs;

    /// <summary>HarnessHost의 LogSink - gRPC 수신 스레드에서 불린다(여러 대상이 동시에).</summary>
    public void Write(AgentLogRecord record)
    {
        var run = _runs.Current;
        if (run == null) return;

        var line = Serialize(record);
        var path = Path.Combine(run.LogsDir, FileNameOf(record.AgentName));

        lock (_lock)
        {
            if (_runId != run.Id)
            {
                _runId = run.Id;
                _files.Clear();
            }
            _files.Add(path);
            HarnessFiles.AppendLine(path, line);
        }
    }

    /// <summary>
    /// 지금 실행에서 쓴 에이전트 로그 파일 전부에 한 줄을 넣는다(노드 경계 - HarnessConstants.NodeMarker). 아직 로그가 없는 에이전트 파일은
    /// 만들지 않는다. 열린 실행이 없거나 아직 이 실행에서 쓴 파일이 없으면 아무 일도 안 한다.
    /// </summary>
    public void WriteMarker(string message)
    {
        var run = _runs.Current;
        if (run == null) return;
        var line = Serialize(new AgentLogRecord("harness", 0, DateTimeOffset.UtcNow, "Information", HarnessConstants.NodeMarker.Category, message, ""));
        lock (_lock)
        {
            if (_runId != run.Id) return;
            foreach (var path in _files) HarnessFiles.AppendLine(path, line);
        }
    }

    /// <summary>에이전트 이름 → 파일 이름. LogStore는 첫 '.' 앞을 서버(에이전트) 이름으로 보므로 '.'과 경로 문자를 바꾼다.</summary>
    public static string FileNameOf(string agentName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(agentName.Select(c => c == '.' || invalid.Contains(c) ? '_' : c).ToArray());
        return (safe.Length == 0 ? "agent" : safe) + ".log";
    }

    private static string Serialize(AgentLogRecord r)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            json.WriteStartObject();
            json.WriteString("Timestamp", r.TimestampUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
            json.WriteString("LogLevel", r.Level);
            json.WriteString("Category", string.IsNullOrEmpty(r.Category) ? "Unity" : r.Category);
            json.WriteString("Message", r.Message ?? "");
            if (!string.IsNullOrEmpty(r.Exception)) json.WriteString("Exception", r.Exception);
            json.WriteNumber("Pid", r.Pid);
            json.WriteString("Agent", r.AgentName);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
