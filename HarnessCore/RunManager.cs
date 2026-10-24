using System.Text;

namespace TestHarness.Core;

/// <summary>
/// 실행 하나 = 폴더 하나(runs/&lt;시각&gt;[-label]/). 그 안에 서버 로그(logs/), 이벤트(events.jsonl),
/// DevTools 스크립트 출력(devtools/), 빌드 출력(build/)이 모인다. 한 실행 = 한 번의 조사/재현 단위라서,
/// 나중에 "그때 무슨 일이 있었나"를 폴더 하나로 볼 수 있다.
/// </summary>
public sealed class HarnessRun
{
    internal HarnessRun(string id, string label, string dir)
    {
        Id = id;
        Label = label;
        Dir = dir;
        StartedUtc = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(LogsDir);
    }

    public string Id { get; }
    public string Label { get; }
    public string Dir { get; }
    public DateTimeOffset StartedUtc { get; }

    /// <summary>DevTools -LogDir로 넘기는 폴더(서버 로그 + server-pids.json + *-result.json).</summary>
    public string LogsDir => Path.Combine(Dir, "logs");
    public string EventsFile => Path.Combine(Dir, "events.jsonl");

    /// <summary>run 폴더 아래 하위 폴더를 만들고 그 안의 새 파일 경로를 준다(번호를 붙여 덮어쓰지 않음).</summary>
    public string NewFile(string subDir, string name)
    {
        var dir = Path.Combine(Dir, subDir);
        Directory.CreateDirectory(dir);
        var index = Directory.GetFiles(dir).Length + 1;
        return Path.Combine(dir, $"{index:D2}-{name}");
    }
}

public sealed class RunManager
{
    private readonly HarnessPaths _paths;
    private readonly Timeline _timeline;
    private readonly object _lock = new();
    private HarnessRun? _current;

    public RunManager(HarnessPaths paths, Timeline timeline)
    {
        _paths = paths;
        _timeline = timeline;
    }

    public HarnessRun? Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>새 실행을 연다. label은 폴더 이름에 붙는다(영문/숫자/-/_ 외 문자는 _로).</summary>
    public HarnessRun Begin(string? label)
    {
        var safeLabel = Sanitize(label);
        var id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + (safeLabel.Length > 0 ? "-" + safeLabel : "");
        var dir = Path.Combine(_paths.RunsDir, id);

        // 같은 초에 두 번 열면 뒤에 번호를 붙인다.
        for (var n = 2; Directory.Exists(dir); n++) dir = Path.Combine(_paths.RunsDir, $"{id}-{n}");

        var run = new HarnessRun(Path.GetFileName(dir), safeLabel, dir);
        lock (_lock) _current = run;

        _timeline.SetEventsFile(run.EventsFile);
        _timeline.RecordHarness("run.begin", $"실행 시작: {run.Id}", new { runId = run.Id, dir = run.Dir });
        return run;
    }

    /// <summary>열린 실행이 없으면 자동으로 하나 연다 - run_begin을 깜빡해도 기록이 흩어지지 않게.</summary>
    public HarnessRun EnsureCurrent() => Current ?? Begin("auto");

    public void End()
    {
        HarnessRun? run;
        lock (_lock)
        {
            run = _current;
            _current = null;
        }

        if (run == null) return;
        _timeline.RecordHarness("run.end", $"실행 종료: {run.Id}", new { runId = run.Id });
        _timeline.SetEventsFile(null);
    }

    /// <summary>runs 폴더에서 id로 실행 폴더를 찾는다(지난 실행의 서버를 정리할 때).</summary>
    public string? FindRunDir(string runId)
    {
        var dir = Path.Combine(_paths.RunsDir, runId);
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>최근 실행 폴더들(새것부터).</summary>
    public IEnumerable<string> RecentRunDirs(int count) =>
        Directory.Exists(_paths.RunsDir)
            ? Directory.GetDirectories(_paths.RunsDir).OrderByDescending(d => d, StringComparer.Ordinal).Take(count)
            : Enumerable.Empty<string>();

    private static string Sanitize(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "";
        var chars = label.Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        var text = new string(chars);
        return text.Length > 40 ? text[..40] : text;
    }
}
