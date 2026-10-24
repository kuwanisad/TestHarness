using System.Text.RegularExpressions;

namespace TestHarness.Core;

/// <summary>프로젝트 하나의 빌드 결과.</summary>
public sealed record ProjectBuildResult(string Project, bool Ok, int ExitCode, double Seconds, int Errors, int Warnings);

/// <summary>빌드 한 번의 결과. Errors는 중복을 뺀 오류 줄(경로는 GameProject1 기준 상대 경로).
/// OutputDir에 프로젝트별 전체 출력(&lt;번호&gt;-&lt;프로젝트&gt;.txt)이 있다.</summary>
public sealed record BuildReport(
    bool Ok,
    IReadOnlyList<ProjectBuildResult> Projects,
    IReadOnlyList<string> Errors,
    int ErrorsOmitted,
    int WarningCount,
    string OutputDir);

/// <summary>
/// 서버 코드 빌드(dotnet build). 서버를 고친 AI가 컴파일 오류 원문을 직접 보게 하려는 것.
///
/// 솔루션(AutoBattlerServer.slnx) 대신 프로젝트를 하나씩 빌드한다 - .slnx는 .NET SDK 9.0.200 이상의
/// dotnet CLI만 읽을 수 있어서, 설치된 SDK가 무엇이든 되게. 공용 프로젝트는 첫 서버를 빌드할 때 같이
/// 빌드되고 이후엔 최신이라 건너뛰므로 크게 느리지 않다. 실패한 프로젝트가 있어도 나머지를 계속 빌드한다
/// (오류를 한 번에 다 보여주려고 - 공용 프로젝트 오류는 중복 제거됨).
///
/// 서버를 하나라도 빌드하면 하네스 쪽 프로젝트(Shared/Test의 Harness.Agent와 &lt;서버&gt;.Harness 전부)도 이어서 빌드한다 -
/// 서버가 하네스를 참조하지 않으므로(하네스 모드에서 DevTools가 주입) 서버 빌드가 하네스를 같이 빌드해 주지 않는다.
/// 최신이면 금방 건너뛴다.
/// </summary>
public sealed class BuildRunner
{
    public const int MaxErrors = 30;

    private static readonly Regex DiagnosticLine = new(@":\s(?<kind>error|warning)\s[A-Z]+\d+:", RegexOptions.Compiled);

    // 컴파일러/MSBuild 메시지를 영어로 - 한국어 SDK면 메시지가 콘솔 코드 페이지로 나와 섞이고, 오류 줄 형식도
    // 영어 기준으로 거르기 때문.
    private static readonly IReadOnlyDictionary<string, string> BuildEnvironment = new Dictionary<string, string>
    {
        ["DOTNET_CLI_UI_LANGUAGE"] = "en",
        ["DOTNET_NOLOGO"] = "1",
    };

    private readonly HarnessPaths _paths;
    private readonly RunManager _runs;
    private readonly ServerManager _servers;
    private readonly Timeline _timeline;

    public BuildRunner(HarnessPaths paths, RunManager runs, ServerManager servers, Timeline timeline)
    {
        _paths = paths;
        _runs = runs;
        _servers = servers;
        _timeline = timeline;
    }

    /// <summary>빌드할 수 있는 이름: 서버 이름들 + BrokerInitializer.</summary>
    public IReadOnlyList<string> Targets => _servers.ServerOrder.Append("BrokerInitializer").ToList();

    /// <summary>names가 비면 서버 전부 + BrokerInitializer.</summary>
    public async Task<BuildReport> BuildAsync(IReadOnlyList<string>? names, CancellationToken ct)
    {
        if (_servers.AnyAlive())
        {
            throw new HarnessException("실행 중인 서버가 있어 빌드할 수 없음(실행 중인 DLL은 덮어쓸 수 없다)",
                "servers_stop으로 먼저 끌 것(지난 실행에 남은 서버면 harness_status의 orphans 참고)");
        }

        var targets = ResolveTargets(names);
        var run = _runs.EnsureCurrent();

        var projectResults = new List<ProjectBuildResult>();
        var errors = new List<string>();
        var seenErrors = new HashSet<string>(StringComparer.Ordinal);
        var warningCount = 0;
        string? outputDir = null;

        foreach (var (name, projectPath) in targets)
        {
            var outputPath = run.NewFile("build", $"{name}.txt");
            outputDir ??= Path.GetDirectoryName(outputPath);
            var result = await ProcessRunner.RunAsync("dotnet",
                new[] { "build", projectPath, "-c", "Debug", "-nologo", "-v", "q", "-clp:NoSummary" },
                Path.GetDirectoryName(projectPath)!, TimeSpan.FromMinutes(5), outputPath, BuildEnvironment, ct);

            var projectErrors = 0;
            var projectWarnings = 0;
            foreach (var rawLine in result.Output.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                var match = DiagnosticLine.Match(line);
                if (!match.Success) continue;

                if (match.Groups["kind"].Value == "warning")
                {
                    projectWarnings++;
                    continue;
                }

                projectErrors++;
                var shortLine = ShortenPaths(line);
                if (seenErrors.Add(shortLine)) errors.Add(shortLine);
            }

            // 진단 줄 없이 실패(복원 실패, SDK 없음 등)하면 출력 끝부분을 오류로 싣는다.
            if (result.ExitCode != 0 && projectErrors == 0)
            {
                var tail = $"[{name}] exit={result.ExitCode}: {ShortenPaths(result.Tail(5))}";
                if (seenErrors.Add(tail)) errors.Add(tail);
            }

            warningCount += projectWarnings;
            projectResults.Add(new ProjectBuildResult(name, result.ExitCode == 0, result.ExitCode,
                Math.Round(result.Duration.TotalSeconds, 1), projectErrors, projectWarnings));
        }

        var ok = projectResults.All(p => p.Ok);
        var report = new BuildReport(ok, projectResults, errors.Take(MaxErrors).ToList(),
            Math.Max(0, errors.Count - MaxErrors), warningCount, _paths.Relative(outputDir ?? run.Dir));
        _timeline.RecordHarness(ok ? "build.succeeded" : "build.failed",
            $"빌드 {(ok ? "성공" : "실패")}: {string.Join(", ", projectResults.Select(p => $"{p.Project}={(p.Ok ? "ok" : "FAILED")}"))}",
            new { errors = errors.Count, warnings = warningCount });
        return report;
    }

    private List<(string Name, string Path)> ResolveTargets(IReadOnlyList<string>? names)
    {
        var all = Targets;
        var selected = names is { Count: > 0 }
            ? names.SelectMany(n => n.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Select(n => all.FirstOrDefault(t => string.Equals(t, n, StringComparison.OrdinalIgnoreCase))
                             ?? throw new HarnessException($"모르는 빌드 대상 '{n}'", $"가능: {string.Join(", ", all)}"))
                .Distinct()
                .ToList()
            : all.ToList();

        var targets = selected.Select(name => (name, name == "BrokerInitializer"
                ? Path.Combine(_paths.DevTools, "BrokerInitializer", "BrokerInitializer.csproj")
                : Path.Combine(_paths.GameServer, name, $"{name}.csproj")))
            .ToList();

        if (selected.Any(name => name != "BrokerInitializer")) targets.AddRange(HarnessProjects());
        return targets;
    }

    /// <summary>하네스 쪽 프로젝트: Harness.Agent + Shared/Test/&lt;서버&gt;.Harness 전부(서버별 명령).</summary>
    private IEnumerable<(string Name, string Path)> HarnessProjects()
    {
        var sharedTest = Path.Combine(_paths.GameServer, "Shared", "Test");
        yield return ("Harness.Agent", Path.Combine(sharedTest, "Harness.Agent", "Harness.Agent.csproj"));

        if (!Directory.Exists(sharedTest)) yield break;
        foreach (var dir in Directory.GetDirectories(sharedTest, "*.Harness").OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir);
            var project = Path.Combine(dir, name + ".csproj");
            if (File.Exists(project)) yield return (name, project);
        }
    }

    private string ShortenPaths(string text) =>
        text.Replace(_paths.Root + Path.DirectorySeparatorChar, "", StringComparison.OrdinalIgnoreCase)
            .Replace(_paths.Root, "", StringComparison.OrdinalIgnoreCase);
}
