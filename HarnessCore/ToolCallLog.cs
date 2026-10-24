using System.Text;
using System.Text.Json;

namespace TestHarness.Core;

/// <summary>AI가 부른 도구 한 번(요청 + 응답). 타임라인의 mcp.call 이벤트와 같은 Seq를 가진다.</summary>
/// <param name="Objective">호출 시점에 실행 중이던 시나리오 노드(경로, 없으면 null).</param>
/// <param name="Actor">누가 불렀나: "ai"(MCP로 AI가) | "runner"(시나리오 노드를 실행하는 하네스 자신 - TH_RunContext).</param>
public sealed record ToolCallRecord(
    long Seq,
    DateTimeOffset TimestampUtc,
    string Tool,
    JsonElement? Arguments,
    JsonElement? Result,
    bool IsError,
    double Seconds,
    string? Objective,
    string Actor = ToolCallLog.ActorAi)
{
    /// <summary>agent_command였다면 그 명령 이름(arguments.command).</summary>
    public string? AgentCommand =>
        Tool == "agent_command" && Arguments is { ValueKind: JsonValueKind.Object } a &&
        a.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;
}

/// <summary>
/// AI가 하네스에 한 모든 행동(MCP 도구 호출)을 남긴다 - 규칙 "AI가 출력하면 하네스가 기록한다"의 기록 쪽.
/// 시나리오 노드를 실행하는 하네스 자신(runner)의 도구 호출도 같은 경로로 남긴다(타임라인 출처 "runner") - 노드 판정은
/// runner의 호출만 본다(TH_JudgeContext).
///
/// - 실행 폴더의 mcp-calls.jsonl에 요청/응답 전문(길면 자름)을 한 줄씩.
/// - 타임라인에 source="ai", name="mcp.call" 이벤트(요약)로 - 시나리오 판정과 보고서가 "AI가 언제 무엇을 했는지"를
///   이벤트 번호로 가리킬 수 있게. 출처를 지정하지 않은 조회/대기에는 안 걸린다(EventFilter 참고).
/// 실행이 열려 있지 않을 때의 호출은 runs/mcp-calls-outside-run.jsonl에 쌓는다(호출 때문에 실행을 새로 열지 않는다).
/// </summary>
public sealed class ToolCallLog
{
    public const string Source = "ai";
    public const string RunnerSource = "runner";
    public const string EventName = "mcp.call";
    public const string ActorAi = "ai";
    public const string ActorRunner = "runner";

    /// <summary>파일 한 줄에 남기는 응답의 최대 길이(자).</summary>
    private const int MaxResultChars = 20_000;
    private const int MaxInMemory = 20_000;

    private readonly Timeline _timeline;
    private readonly RunManager _runs;
    private readonly HarnessPaths _paths;
    private readonly object _lock = new();
    private readonly List<ToolCallRecord> _records = new();

    public ToolCallLog(Timeline timeline, RunManager runs, HarnessPaths paths)
    {
        _timeline = timeline;
        _runs = runs;
        _paths = paths;
    }

    /// <summary>호출 시점에 실행 중인 시나리오 노드(경로)를 알려 주는 곳(시나리오 진행기가 채운다).</summary>
    public Func<string?>? CurrentObjective { get; set; }

    /// <summary>도구를 부르기 직전에 - 그 시점의 노드를 잡아 둔다(호출 도중 노드가 바뀌는 도구가 있어서).</summary>
    public string? CaptureObjective() => CurrentObjective?.Invoke();

    public ToolCallRecord Record(string tool, JsonElement? arguments, string resultText, bool isError, TimeSpan elapsed, string? objective,
        string actor = ActorAi)
    {
        var result = HarnessJson.TryParse(resultText);
        var preview = HarnessJson.Truncate(resultText, 300);
        var source = actor == ActorRunner ? RunnerSource : Source;
        var evt = _timeline.Record(DateTimeOffset.UtcNow, source, EventName, null, $"{tool}{(isError ? " (error)" : "")}",
            HarnessJson.ToElement(new
            {
                tool,
                command = CommandOf(tool, arguments),
                arguments = arguments is { } a
                    ? (a.GetRawText().Length <= 2000 ? a : (object)HarnessJson.Truncate(a.GetRawText(), 2000))
                    : null,
                isError,
                seconds = Math.Round(elapsed.TotalSeconds, 2),
                objective,
                resultPreview = preview,
            }));

        var record = new ToolCallRecord(evt.Seq, evt.TimestampUtc, tool, arguments?.Clone(), result, isError,
            Math.Round(elapsed.TotalSeconds, 2), objective, actor);

        lock (_lock)
        {
            _records.Add(record);
            if (_records.Count > MaxInMemory) _records.RemoveRange(0, _records.Count - MaxInMemory);
            WriteLine(record, resultText);
        }

        Recorded?.Invoke(record);
        return record;
    }

    /// <summary>호출이 기록될 때마다(락 밖, 기록한 스레드에서) - 지속 조작 감시(OverrideManager)가 쓴다.</summary>
    public event Action<ToolCallRecord>? Recorded;

    /// <summary>sinceSeq 뒤의 호출(시간순).</summary>
    public IReadOnlyList<ToolCallRecord> Since(long sinceSeq)
    {
        lock (_lock) return _records.Where(r => r.Seq > sinceSeq).ToList();
    }

    private void WriteLine(ToolCallRecord record, string resultText)
    {
        var file = _runs.Current is { } run
            ? Path.Combine(run.Dir, "mcp-calls.jsonl")
            : Path.Combine(_paths.RunsDir, "mcp-calls-outside-run.jsonl");

        var line = HarnessJson.Serialize(new
        {
            seq = record.Seq,
            t = record.TimestampUtc,
            tool = record.Tool,
            arguments = record.Arguments,
            isError = record.IsError,
            seconds = record.Seconds,
            objective = record.Objective,
            actor = record.Actor,
            result = record.Result is { } parsed && resultText.Length <= MaxResultChars ? (object)parsed : HarnessJson.Truncate(resultText, MaxResultChars),
        });

        try
        {
            File.AppendAllText(file, line + "\n", new UTF8Encoding(false));
        }
        catch (IOException)
        {
            // 파일 기록 실패가 도구 응답을 막으면 안 된다(타임라인/메모리에는 남아 있음).
        }
    }

    private static string? CommandOf(string tool, JsonElement? arguments) =>
        tool == "agent_command" && arguments is { ValueKind: JsonValueKind.Object } a &&
        a.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;
}
