using System.Diagnostics;
using TestHarness.HarnessHost;

// 하네스 제어 서버 동작 확인용 스모크 테스트 - 정식 시나리오 러너는 아니다.
//
// 1. 하네스 제어 서버를 띄운다.
// 2. DevTools\start-servers.ps1을 하네스 모드(-LogDir, -HarnessAddress)로 불러 서버들을 띄운다.
// 3. 서버들이 하네스에 접속하는지, 명령(공통 명령 + GameAgent의 process.status)이 도는지 확인한다.
// 4. DevTools\stop-servers.ps1로 정리한다(실패해도 정리는 함).
//
// 사용법:
//   dotnet run --project HarnessHost.Smoke [-- 옵션]
//   --only <서버,...>     띄울 서버(기본: 전부). GameAgent는 반드시 포함해야 process.status(GameAgent 전용 상태)를 확인한다.
//   --skip-docker         Docker/BrokerInitializer를 건너뜀(이미 떠 있을 때)
//   --port <n>            하네스 포트(기본 9000)
//   --devtools <폴더>     DevTools 위치(기본: TestHarness 옆 ..\DevTools를 찾아감)
//   --powershell <실행파일>  기본: Windows는 powershell, 그 외 pwsh

var port = int.Parse(GetArg("--port") ?? "9000");
var only = GetArg("--only");
var skipDocker = args.Contains("--skip-docker");
var powershell = GetArg("--powershell") ?? (OperatingSystem.IsWindows() ? "powershell" : "pwsh");
var devTools = GetArg("--devtools") ?? FindDevTools();

var runDir = Path.Combine(FindTestHarnessRoot(), "runs", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
var logDir = Path.Combine(runDir, "logs");
Directory.CreateDirectory(logDir);

var failures = new List<string>();
void Check(bool ok, string what)
{
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}");
    if (!ok) failures.Add(what);
}

var eventFile = Path.Combine(runDir, "harness-events.log");
await using var eventWriter = new StreamWriter(eventFile) { AutoFlush = true };

await using var host = await HarnessHost.StartAsync(new HarnessHostOptions
{
    Port = port,
    EventSink = e =>
    {
        Console.WriteLine($"      {e}");
        lock (eventWriter) eventWriter.WriteLine(e.ToString());
    },
});
Console.WriteLine($"하네스 제어 서버: {host.Address}");
Console.WriteLine($"실행 기록 폴더: {runDir}");

var servers = only?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
              ?? new[] { "MasterServer", "MatchingServer", "AccountServer", "LoginServer",
                         "DummySubServer", "LobbyServer", "GameAgent", "ChatServer" };

try
{
    Console.WriteLine("[1] DevTools로 서버 실행(하네스 모드)");
    var startArgs = new List<string> { "-LogDir", logDir, "-HarnessAddress", host.Address, "-Only", string.Join(",", servers) };
    if (skipDocker) startArgs.Add("-SkipDocker");
    var startExit = await RunScriptAsync("start-servers.ps1", startArgs);
    Check(startExit == 0, $"start-servers.ps1 종료 코드={startExit}");
    if (startExit != 0) return 1;

    Console.WriteLine("[2] 모든 서버가 하네스에 접속");
    foreach (var name in servers)
    {
        try
        {
            var agent = await host.WaitForAgentAsync(name, TimeSpan.FromSeconds(15));
            Check(true, $"{name} 접속 (pid={agent.Pid}, 명령 {agent.Commands.Count}개)");
        }
        catch (HarnessTimeoutException ex)
        {
            Check(false, $"{name}: {ex.Message}");
        }
    }

    Console.WriteLine("[3] 공통 명령");
    var first = host.Agents.FirstOrDefault(a => a.Name == servers[0]);
    if (first != null)
    {
        var info = await first.InvokeAsync("process.info");
        Check(info.Ok && info.Value.GetProperty("name").GetString() == first.Name, $"process.info → {info}");

        var mark = await first.InvokeAsync("log.mark", new { text = "스모크 테스트 표시" });
        Check(mark.Ok, $"log.mark → {mark}");

        var unknown = await first.InvokeAsync("no.such.command");
        Check(!unknown.Ok && unknown.Error.Contains("모르는 명령"), $"모르는 명령은 실패 → {unknown.Error}");

        var missing = await first.InvokeAsync("log.mark");
        Check(!missing.Ok && missing.Error.Contains("누락"), $"인자 누락은 실패 → {missing.Error}");

        var extra = await first.InvokeAsync("log.mark", new { text = "x", txet = "오타" });
        Check(!extra.Ok && extra.Error.Contains("모르는 인자"), $"모르는 인자는 실패 → {extra.Error}");
    }

    Console.WriteLine("[4] GameAgent 상태 명령 process.status (Unity 인게임 서버 없음 → 미연결)");
    var gameAgent = host.Agents.FirstOrDefault(a => a.Name == "GameAgent");
    if (gameAgent == null)
    {
        Console.WriteLine("  (GameAgent를 띄우지 않아 건너뜀)");
    }
    else
    {
        var status = await gameAgent.InvokeAsync("process.status");
        Check(status.Ok && status.Value.GetProperty("gameServerConnected").GetBoolean() == false,
            $"process.status → {status}");
    }
}
finally
{
    Console.WriteLine("[정리] DevTools로 서버 종료");
    var stopArgs = new List<string> { "-LogDir", logDir };
    if (skipDocker) stopArgs.Add("-SkipDocker");
    await RunScriptAsync("stop-servers.ps1", stopArgs);
}

Console.WriteLine();
Console.WriteLine($"실행 기록: {runDir}");
Console.WriteLine(failures.Count == 0 ? "결과: 전부 PASS" : $"결과: FAIL {failures.Count}건");
return failures.Count == 0 ? 0 : 1;

async Task<int> RunScriptAsync(string script, List<string> scriptArgs)
{
    var psi = new ProcessStartInfo(powershell) { UseShellExecute = false };
    foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(devTools, script) })
        psi.ArgumentList.Add(a);
    foreach (var a in scriptArgs) psi.ArgumentList.Add(a);

    using var process = Process.Start(psi) ?? throw new InvalidOperationException($"{powershell} 실행 실패");
    await process.WaitForExitAsync();
    return process.ExitCode;
}

string FindTestHarnessRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "TestHarness.slnx"))) return dir.FullName;
    }
    return Directory.GetCurrentDirectory();
}

string FindDevTools()
{
    var candidate = Path.GetFullPath(Path.Combine(FindTestHarnessRoot(), "..", "DevTools"));
    if (!File.Exists(Path.Combine(candidate, "start-servers.ps1")))
    {
        throw new DirectoryNotFoundException($"DevTools를 못 찾음({candidate}) - --devtools로 지정할 것");
    }
    return candidate;
}

string? GetArg(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
