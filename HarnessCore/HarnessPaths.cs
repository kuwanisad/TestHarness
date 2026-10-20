namespace TestHarness.Core;

/// <summary>
/// 하네스가 쓰는 폴더 위치. GameProject1/{DevTools, Server, Unity, TestHarness} 배치를 전제로 한다
/// (Directory.Build.props와 같은 전제).
/// </summary>
public sealed class HarnessPaths
{
    private HarnessPaths(string root, string runsDir)
    {
        Root = root;
        RunsDir = runsDir;
    }

    /// <summary>GameProject1 폴더.</summary>
    public string Root { get; }

    public string DevTools => Path.Combine(Root, "DevTools");
    public string GameServer => Path.Combine(Root, "Server", "GameServer");
    public string TestHarness => Path.Combine(Root, "TestHarness");

    /// <summary>실행 기록 폴더(기본 TestHarness/runs, git 제외).</summary>
    public string RunsDir { get; }

    /// <summary>
    /// root를 안 주면 실행 파일 위치와 현재 폴더에서 위로 올라가며 DevTools와 TestHarness가 같이 있는
    /// 폴더를 찾는다 - dotnet run으로 띄우든 빌드된 exe를 직접 띄우든 같은 결과가 나오게.
    /// </summary>
    public static HarnessPaths Detect(string? root = null, string? runsDir = null)
    {
        var resolvedRoot = root != null
            ? Path.GetFullPath(root)
            : FindRoot(AppContext.BaseDirectory) ?? FindRoot(Environment.CurrentDirectory)
              ?? throw new HarnessException(
                  "GameProject1 폴더를 찾지 못함(DevTools와 TestHarness가 같이 있는 폴더)",
                  "--root <GameProject1 경로>로 직접 지정할 것");

        if (!Directory.Exists(Path.Combine(resolvedRoot, "DevTools")))
        {
            throw new HarnessException($"{resolvedRoot}에 DevTools 폴더가 없음", "--root가 GameProject1을 가리키는지 확인");
        }

        var resolvedRuns = Path.GetFullPath(runsDir ?? Path.Combine(resolvedRoot, "TestHarness", "runs"));
        Directory.CreateDirectory(resolvedRuns);
        return new HarnessPaths(resolvedRoot, resolvedRuns);
    }

    /// <summary>Root 기준 상대 경로(응답을 짧게 하려고). Root 밖이면 그대로.</summary>
    public string Relative(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var full = Path.GetFullPath(path);
        return full.StartsWith(Root, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(Root, full)
            : full;
    }

    private static string? FindRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "DevTools")) &&
                Directory.Exists(Path.Combine(dir.FullName, "TestHarness")))
            {
                return dir.FullName;
            }
        }

        return null;
    }
}
