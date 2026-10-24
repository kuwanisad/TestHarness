using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TestHarness.Core;

/// <summary>DevTools 스크립트가 남긴 결과 파일(start-result.json / control-result.json)의 한 줄 + 하네스가 덧붙인 것.</summary>
/// <param name="Status">스크립트 값 그대로: ready | failed | skipped | killed | not-running.</param>
/// <param name="AgentAttached">ready인 서버가 하네스 에이전트로도 접속했는지(서버만 뜨고 에이전트가 못 붙으면 false).</param>
/// <param name="LogTail">실패한 단계의 로그 끝부분 + err 로그 앞부분(원인 확인용). 성공이면 null.</param>
public sealed record ServerStep(
    string Name,
    string Status,
    int? Pid,
    string? Detail,
    string? Log,
    string? ErrLog,
    bool? AgentAttached,
    string? LogTail);

/// <summary>DevTools 스크립트 한 번(start/kill/restart/stop)의 결과.</summary>
public sealed record ServerOperationReport(
    bool Ok,
    string Action,
    int ExitCode,
    IReadOnlyList<ServerStep> Steps,
    string Transcript,
    string? ScriptTail,
    string? Hint);

/// <summary>서버 하나의 지금 상태.</summary>
public sealed record ServerStatus(string Name, int? Pid, bool ProcessAlive, bool AgentConnected);

/// <summary>지난 실행에서 정리되지 않고 남은 서버(하네스가 비정상 종료된 경우 등).</summary>
public sealed record OrphanServers(string RunId, IReadOnlyDictionary<string, int> AlivePids);

/// <summary>Docker 컨테이너(Redis/RabbitMQ/Kafka)를 어떻게 할지.</summary>
public enum InfraMode
{
    /// <summary>docker compose up + BrokerInitializer까지 스크립트가 한다(둘 다 이미 돼 있어도 안전).</summary>
    Start,

    /// <summary>건드리지 않는다(-SkipDocker) - 이미 떠 있고 초기화돼 있을 때 몇 초 아끼기.</summary>
    Skip,
}

/// <summary>
/// 서버 기동/종료를 DevTools 스크립트(start-servers.ps1 / control-server.ps1 / stop-servers.ps1)로 한다 -
/// 사람이 띄우는 방식과 AI가 띄우는 방식이 같아야 "AI 환경에서만 재현된다"는 일이 없기 때문.
///
/// 판단은 콘솔 문구가 아니라 스크립트가 남기는 결과 파일과 하네스 에이전트 접속 여부로 한다. 서버가
/// 실행 도중 죽으면 에이전트 연결이 끊기므로 타임라인에 harness.detached가 바로 찍힌다(원인은 err 로그).
///
/// 스크립트 실행은 한 번에 하나만(같은 PID 파일을 고치므로).
/// </summary>
public sealed class ServerManager
{
    private static readonly TimeSpan AgentAttachTimeout = TimeSpan.FromSeconds(10);

    private readonly HarnessPaths _paths;
    private readonly RunManager _runs;
    private readonly Timeline _timeline;
    private readonly HarnessHost.HarnessHost _host;
    private readonly string _powerShell;
    private readonly IReadOnlyDictionary<string, string>? _scriptEnvironment;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ServerManager(
        HarnessPaths paths, RunManager runs, Timeline timeline, HarnessHost.HarnessHost host,
        string? powerShellPath, IReadOnlyDictionary<string, string>? scriptEnvironment)
    {
        _paths = paths;
        _runs = runs;
        _timeline = timeline;
        _host = host;
        _powerShell = powerShellPath ?? (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "powershell.exe" : "pwsh");
        _scriptEnvironment = scriptEnvironment;
        ServerOrder = ReadServerOrder(Path.Combine(paths.DevTools, "server-common.ps1"));
    }

    /// <summary>서버 이름과 기동 순서 - server-common.ps1의 $ServerOrder를 그대로 읽는다(목록을 두 곳에 두지 않게).</summary>
    public IReadOnlyList<string> ServerOrder { get; }

    /// <summary>start-servers.ps1. only가 비면 전부.</summary>
    public async Task<ServerOperationReport> StartAsync(
        IReadOnlyList<string>? only, InfraMode infra, int readyTimeoutSeconds, CancellationToken ct)
    {
        var names = ValidateNames(only);
        await _gate.WaitAsync(ct);
        try
        {
            var run = _runs.EnsureCurrent();
            var args = new List<string> { "-LogDir", run.LogsDir, "-HarnessAddress", _host.Address, "-ReadyTimeoutSeconds", readyTimeoutSeconds.ToString() };
            if (infra == InfraMode.Skip) args.Add("-SkipDocker");
            if (names.Count > 0) args.AddRange(new[] { "-Only", string.Join(',', names) });

            // 스크립트 제한 시간: 서버마다 준비 대기 + Docker/브로커 여유.
            var serverCount = names.Count > 0 ? names.Count : ServerOrder.Count;
            var timeout = TimeSpan.FromSeconds(readyTimeoutSeconds * serverCount + 120);

            var report = await RunScriptAsync(run, "start", "start-servers.ps1", args, "start-result.json", timeout, ct);
            _timeline.RecordHarness(report.Ok ? "servers.started" : "servers.start_failed", Summarize(report), report.Steps);
            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>control-server.ps1 - 서버 하나를 kill / start / restart.</summary>
    public async Task<ServerOperationReport> ControlAsync(string name, string action, int readyTimeoutSeconds, CancellationToken ct)
    {
        var server = ValidateNames(new[] { name }).Single();
        var scriptAction = action.ToLowerInvariant() switch
        {
            "kill" => "Kill",
            "start" => "Start",
            "restart" => "Restart",
            _ => throw new HarnessException($"모르는 action '{action}'", "kill | start | restart 중 하나"),
        };

        await _gate.WaitAsync(ct);
        try
        {
            var run = _runs.Current ?? throw new HarnessException("열린 실행이 없음", "servers_start로 서버를 먼저 띄울 것");
            var args = new List<string>
            {
                "-ServerName", server, "-Action", scriptAction, "-LogDir", run.LogsDir,
                "-HarnessAddress", _host.Address, "-ReadyTimeoutSeconds", readyTimeoutSeconds.ToString(),
            };

            var report = await RunScriptAsync(run, scriptAction.ToLowerInvariant(), "control-server.ps1", args,
                "control-result.json", TimeSpan.FromSeconds(readyTimeoutSeconds + 60), ct);
            _timeline.RecordHarness($"server.{scriptAction.ToLowerInvariant()}", Summarize(report), report.Steps);
            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>stop-servers.ps1. runId를 주면 그 실행의 서버를(지난 실행에 남은 서버 정리), 안 주면 지금 실행의 서버를.</summary>
    public async Task<ServerOperationReport> StopAsync(bool stopInfra, string? runId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            string logsDir;
            HarnessRun? transcriptRun = _runs.Current;
            if (runId != null)
            {
                var dir = _runs.FindRunDir(runId) ?? throw new HarnessException($"실행 '{runId}'를 찾지 못함", "harness_status의 orphans에 나온 runId를 쓸 것");
                logsDir = Path.Combine(dir, "logs");
            }
            else
            {
                logsDir = (transcriptRun ?? throw new HarnessException("열린 실행이 없음", "지난 실행의 서버를 끄려면 runId를 줄 것")).LogsDir;
            }

            var before = ReadPidFile(logsDir);
            var args = new List<string> { "-LogDir", logsDir };
            if (!stopInfra) args.Add("-SkipDocker");

            var transcript = transcriptRun?.NewFile("devtools", "stop.txt") ?? Path.GetFullPath(Path.Combine(logsDir, "..", "stop-transcript.txt"));
            var result = await ProcessRunner.RunAsync(_powerShell, ScriptArguments("stop-servers.ps1", args), _paths.DevTools,
                TimeSpan.FromSeconds(120), transcript, _scriptEnvironment, ct);

            // stop-servers.ps1은 결과 파일을 안 쓴다 - PID 파일에 있던 프로세스가 실제로 죽었는지로 판단.
            var steps = before.Select(kv => new ServerStep(
                    kv.Key, ProcessRunner.IsAlive(kv.Value) ? "failed" : "killed", kv.Value,
                    ProcessRunner.IsAlive(kv.Value) ? "still-alive" : null, null, null, null, null))
                .ToList();

            // Docker를 내렸으면(docker-compose down - 컨테이너 제거) 인프라 포트가 닫혔는지로 확인한다.
            if (stopInfra)
            {
                var stillUp = (await InfraProbe.CheckAsync(ct)).Where(i => i.Up).Select(i => i.Name).ToList();
                steps.Add(new ServerStep("Docker", stillUp.Count == 0 ? "removed" : "failed", null,
                    stillUp.Count == 0 ? null : $"still-up: {string.Join(",", stillUp)}", null, null, null, null));
            }
            var ok = result.ExitCode == 0 && steps.All(s => s.Status is "killed" or "removed");

            var report = new ServerOperationReport(ok, "stop", result.ExitCode, steps, _paths.Relative(transcript),
                ok ? null : result.Tail(15), ok ? null : "남은 프로세스가 있으면 server_control(kill)로 하나씩 끄거나 transcript를 볼 것");
            _timeline.RecordHarness("servers.stopped", Summarize(report), steps);
            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>지금 실행의 서버 상태(PID 파일 + 에이전트 접속). PID 파일에 없어도 에이전트로 붙어 있으면 포함.</summary>
    public IReadOnlyList<ServerStatus> GetStatus()
    {
        // 지금 실행의 PID 파일이 우선이고, 거기 없는 서버는 지난 실행에 남은 것(orphans)에서 찾는다 - 하네스를
        // 재시작하면 남은 서버들이 새 하네스에 다시 붙는데, 그때도 프로세스가 살아 있다고 보여야 하므로.
        var pids = _runs.Current is { } run ? ReadPidFile(run.LogsDir) : new Dictionary<string, int>();
        foreach (var orphan in FindOrphans())
        {
            foreach (var (name, pid) in orphan.AlivePids) pids.TryAdd(name, pid);
        }

        var agents = _host.Agents.Where(a => a.IsConnected).Select(a => a.Name).ToHashSet(StringComparer.Ordinal);

        return ServerOrder
            .Where(name => pids.ContainsKey(name) || agents.Contains(name))
            .Select(name =>
            {
                var hasPid = pids.TryGetValue(name, out var pid);
                return new ServerStatus(name, hasPid ? pid : null, hasPid && ProcessRunner.IsAlive(pid), agents.Contains(name));
            })
            .ToList();
    }

    /// <summary>지금 실행이 아닌 최근 실행들 중 서버가 아직 살아 있는 것.</summary>
    public IReadOnlyList<OrphanServers> FindOrphans()
    {
        var currentDir = _runs.Current?.Dir;
        var result = new List<OrphanServers>();
        foreach (var dir in _runs.RecentRunDirs(10))
        {
            if (currentDir != null && string.Equals(Path.GetFullPath(dir), Path.GetFullPath(currentDir), StringComparison.OrdinalIgnoreCase)) continue;

            var alive = ReadPidFile(Path.Combine(dir, "logs"))
                .Where(kv => ProcessRunner.IsAlive(kv.Value))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            if (alive.Count > 0) result.Add(new OrphanServers(Path.GetFileName(dir), alive));
        }

        return result;
    }

    /// <summary>지금 실행에서 살아 있는 서버가 하나라도 있는지(빌드 전 확인용 - 실행 중인 DLL은 덮어쓸 수 없다).</summary>
    public bool AnyAlive() => GetStatus().Any(s => s.ProcessAlive);

    private async Task<ServerOperationReport> RunScriptAsync(
        HarnessRun run, string action, string script, List<string> args, string resultFileName, TimeSpan timeout, CancellationToken ct)
    {
        var resultFile = Path.Combine(run.LogsDir, resultFileName);
        var transcript = run.NewFile("devtools", $"{action}.txt");
        var result = await ProcessRunner.RunAsync(_powerShell, ScriptArguments(script, args), _paths.DevTools,
            timeout, transcript, _scriptEnvironment, ct);

        if (!File.Exists(resultFile))
        {
            // 결과 파일이 없다 = 스크립트가 결과를 쓰기 전에 죽음(문법 오류, 실행 정책 등) 또는 시간 초과.
            return new ServerOperationReport(false, action, result.ExitCode, Array.Empty<ServerStep>(),
                _paths.Relative(transcript), result.Tail(25),
                result.TimedOut
                    ? "스크립트가 제한 시간 안에 안 끝남 - transcript와 logs 폴더를 볼 것"
                    : "스크립트가 결과 파일을 남기지 못함 - scriptTail의 오류를 볼 것(DevTools 스크립트가 최신인지 확인)");
        }

        var steps = new List<ServerStep>();
        bool scriptOk;
        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(resultFile, Encoding.UTF8, ct)))
        {
            scriptOk = document.RootElement.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
            foreach (var item in EnumerateResults(document.RootElement))
            {
                var status = GetString(item, "status") ?? "unknown";
                var pid = item.TryGetProperty("pid", out var pidElement) && pidElement.TryGetInt32(out var p) && p > 0 ? p : (int?)null;
                var log = GetString(item, "log");
                var errLog = GetString(item, "errLog");
                steps.Add(new ServerStep(
                    GetString(item, "name") ?? "?", status, pid, NullIfEmpty(GetString(item, "detail")),
                    string.IsNullOrEmpty(log) ? null : _paths.Relative(log),
                    string.IsNullOrEmpty(errLog) ? null : _paths.Relative(errLog),
                    null,
                    status == "failed" ? NullIfEmpty(ReadFailureTail(log, errLog)) : null));
            }
        }

        // ready인 서버는 하네스 에이전트 접속까지 확인한다 - 프로세스만 뜨고 에이전트가 못 붙으면(HARNESS_ADDRESS
        // 누락, 프로토콜 버전 불일치) 이후 명령/이벤트가 전부 안 되므로 여기서 드러내야 한다.
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Status != "ready" || !ServerOrder.Contains(steps[i].Name)) continue;

            bool attached;
            try
            {
                var session = await _host.WaitForAgentAsync(steps[i].Name, AgentAttachTimeout, ct);
                attached = session.IsConnected;
            }
            catch (TimeoutException)
            {
                attached = false;
            }

            steps[i] = steps[i] with { AgentAttached = attached };
        }

        var ok = scriptOk && result.ExitCode == 0 && steps.All(s => s.AgentAttached != false);
        string? hint = null;
        if (steps.Any(s => s.Detail == "missing-dll")) hint = "빌드된 DLL이 없음 - servers_stop 후 server_build를 하고 다시 servers_start";
        else if (steps.Any(s => s.Detail == "missing-harness"))
            hint = "하네스 에이전트 DLL(Server/GameServer/Shared/Test/bin)이 없음 - servers_stop 후 server_build(서버를 빌드하면 하네스 쪽도 같이 빌드)를 하고 다시 servers_start";
        else if (steps.Any(s => s.Detail == "already-running")) hint = "이미 떠 있는 서버가 있음 - servers_stop(지난 실행이면 runId)으로 끈 뒤 다시 시작";
        else if (!scriptOk) hint = "실패한 단계의 logTail을 볼 것. 이미 뜬 서버는 그대로 있으니 servers_stop으로 정리한 뒤 다시 시작";
        else if (steps.Any(s => s.AgentAttached == false))
            hint = "서버는 떴지만 하네스 에이전트가 안 붙음 - 그 서버 로그에서 Harness.Agent 줄(접속 실패 사유)을 찾아볼 것. " +
                   "'하네스 에이전트 시작' 줄이 아예 없으면 주입이 안 된 것" +
                   "(하네스 DLL이 서버보다 오래됐거나 어셈블리를 못 찾음 - server_build로 다시 빌드)";

        // 스크립트 출력 끝부분은 실패 단계의 logTail이 없을 때만 싣는다(있으면 같은 내용이 두 번 실려 길어지기만 한다).
        var scriptTail = scriptOk || steps.Any(s => s.LogTail != null) ? null : result.Tail(15);
        return new ServerOperationReport(ok, action, result.ExitCode, steps, _paths.Relative(transcript), scriptTail, hint);
    }

    private List<string> ScriptArguments(string script, IEnumerable<string> args)
    {
        var list = new List<string> { "-NoProfile" };
        // Windows PowerShell은 실행 정책 때문에 서명 안 된 스크립트를 막을 수 있다 - 이 실행에만 풀어준다.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) list.AddRange(new[] { "-ExecutionPolicy", "Bypass" });
        list.AddRange(new[] { "-File", Path.Combine(_paths.DevTools, script) });
        list.AddRange(args);
        return list;
    }

    private List<string> ValidateNames(IReadOnlyList<string>? names)
    {
        if (names == null || names.Count == 0) return new List<string>();

        var result = new List<string>();
        foreach (var raw in names.SelectMany(n => n.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            var match = ServerOrder.FirstOrDefault(s => string.Equals(s, raw, StringComparison.OrdinalIgnoreCase))
                        ?? throw new HarnessException($"모르는 서버 이름 '{raw}'", $"가능: {string.Join(", ", ServerOrder)}");
            if (!result.Contains(match)) result.Add(match);
        }

        return result;
    }

    private string ReadFailureTail(string? log, string? errLog)
    {
        var builder = new StringBuilder();
        var logLines = ReadSharedLines(log);
        if (logLines.Count > 0)
        {
            builder.AppendLine($"--- {Path.GetFileName(log)} (마지막 {Math.Min(12, logLines.Count)}줄) ---");
            foreach (var line in logLines.Skip(Math.Max(0, logLines.Count - 12))) builder.AppendLine(LogStore.Render(line));
        }

        // err 로그는 앞부분 - 처리 안 된 예외는 맨 위에 종류와 메시지가 온다(스크립트의 Write-LogHead와 같은 이유).
        var errLines = ReadSharedLines(errLog);
        if (errLines.Count > 0)
        {
            builder.AppendLine($"--- {Path.GetFileName(errLog)} (처음 {Math.Min(12, errLines.Count)}줄) ---");
            foreach (var line in errLines.Take(12)) builder.AppendLine(HarnessJson.Truncate(line, 300));
        }

        return builder.ToString().TrimEnd();
    }

    internal static List<string> ReadSharedLines(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new List<string>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    internal static Dictionary<string, int> ReadPidFile(string logsDir)
    {
        var path = Path.Combine(logsDir, "server-pids.json");
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!File.Exists(path)) return map;

        try
        {
            // Save-PidFile(ConvertTo-Json)은 Windows PowerShell에서 ANSI로 쓰지만 내용이 영문/숫자뿐이라 괜찮다.
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return map;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.TryGetInt32(out var pid)) map[property.Name] = pid;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // 스크립트가 쓰는 도중에 읽었으면 다음 조회 때 다시 읽힌다.
        }

        return map;
    }

    private static IEnumerable<JsonElement> EnumerateResults(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results)) yield break;

        // ConvertTo-Json은 원소가 하나뿐인 배열을 객체 하나로 풀어 쓸 때가 있다 - 둘 다 받는다.
        if (results.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in results.EnumerateArray()) yield return item;
        }
        else if (results.ValueKind == JsonValueKind.Object)
        {
            yield return results;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string Summarize(ServerOperationReport report)
    {
        var parts = report.Steps.Select(s => $"{s.Name}={s.Status}{(s.Detail != null ? $"({s.Detail})" : "")}");
        return $"{report.Action} {(report.Ok ? "ok" : "FAILED")}: {string.Join(", ", parts)}";
    }

    private static IReadOnlyList<string> ReadServerOrder(string serverCommonPath)
    {
        if (!File.Exists(serverCommonPath))
        {
            throw new HarnessException($"{serverCommonPath}가 없음", "--root가 GameProject1을 가리키는지 확인");
        }

        // $ServerOrder = @( "MasterServer", "MatchingServer", # 주석 ... ) 에서 문자열만 뽑는다.
        var text = File.ReadAllText(serverCommonPath);
        var block = Regex.Match(text, @"\$ServerOrder\s*=\s*@\((?<body>.*?)\)", RegexOptions.Singleline);
        if (!block.Success) throw new HarnessException("server-common.ps1에서 $ServerOrder를 찾지 못함");

        var body = string.Join('\n', block.Groups["body"].Value.Split('\n').Select(line => line.Split('#')[0]));
        var names = Regex.Matches(body, "\"(?<name>[A-Za-z0-9_]+)\"").Select(m => m.Groups["name"].Value).ToList();
        if (names.Count == 0) throw new HarnessException("server-common.ps1의 $ServerOrder가 비어 있음");
        return names;
    }
}
