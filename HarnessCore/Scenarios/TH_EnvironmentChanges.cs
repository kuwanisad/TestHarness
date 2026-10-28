using System.Text.Json;

namespace TestHarness.Core.Scenarios;

/// <summary>환경 변화 하나(보고서와 AI 알림용).</summary>
/// <param name="Environment">이 변화로 시작된(또는 합쳐진) 환경 번호.</param>
/// <param name="Seq">원인 이벤트 번호(하네스 밖 빌드처럼 이벤트가 없으면 null).</param>
public sealed record TH_EnvChange(int Environment, string Cause, long? Seq, DateTimeOffset At);

/// <summary>
/// "환경 변화" = 테스트 대상의 변화. 하네스가 남기는 이벤트(서버 기동, 빌드 ...)는 여기, 에이전트가 알리는 변화(Unity 재컴파일 등)와 데이터를
/// 바꾸는 에이전트 명령은 시나리오 어셈블리가 등록한다(ITH_GameKnowledge - HarnessScenarios/Knowledge).
///
/// 쓰임: 시나리오 진행 중 노드와 노드 사이에 테스트 대상이 바뀌면, 그 전에 돈 노드의 결과는 "이전 환경"이 된다(방문 기록이
/// 초기화된 것과 같음 - 결과는 보고서에 남는다). AI는 알림을 보고 맥락상 다시 볼 노드를 고른다. 노드 실행 중에 생긴 변화는
/// 그 노드의 의도된 행동(서버 기동 노드 등)이라 세지 않는다(TH_ScenarioRunner).
///
/// 테스트 조작(cheat.time_scale 등 AI가 테스트를 위해 바꾸는 설정)은 테스트 대상의 변화가 아니므로 넣지 않는다.
///
/// 두 경로로 안다:
/// - 사건: 아래 하네스 이벤트, 등록된 에이전트 이벤트, 등록된 데이터 변경 명령.
/// - 확인: 노드 실행 직전마다 서버 빌드 결과(DLL) 수정 시각을 비교 - 사람이 VS에서 직접 빌드한 것처럼 하네스를 거치지 않은 변화
///   (<see cref="TH_BuildProbe"/>).
/// </summary>
public static class TH_EnvironmentChanges
{
    /// <summary>하네스가 남기는 이벤트 중 테스트 대상을 바꾸는 것(ServerManager, BuildRunner, ClientPool).</summary>
    private static readonly Dictionary<string, string> HarnessEvents = new(StringComparer.Ordinal)
    {
        [HarnessConstants.HarnessEvents.ServersStarted] = "서버 기동",
        [HarnessConstants.HarnessEvents.ServersStartFailed] = "서버 기동(일부 실패)",
        [HarnessConstants.HarnessEvents.ServersStopped] = "서버 종료",
        [HarnessConstants.HarnessEvents.ServerKill] = "서버 강제 종료",
        [HarnessConstants.HarnessEvents.ServerStart] = "서버 기동(개별)",
        [HarnessConstants.HarnessEvents.ServerRestart] = "서버 재기동(개별)",
        [HarnessConstants.HarnessEvents.BuildSucceeded] = "서버 빌드",
        [HarnessConstants.HarnessEvents.AccountCreated] = "계정 생성",
        [HarnessConstants.HarnessEvents.ClientCreated] = "헤드리스 클라 생성(계정 생성 포함)",
    };

    private static readonly object Lock = new();

    // 테스트 대상의 데이터를 바꾸는 에이전트 명령(agent_command로 보낸 것 - AI든 노드 실행이든)과 에이전트 이벤트 중 테스트 대상의 변화 - 등록된 것.
    private static IReadOnlyCollection<string> _dataCommands = Array.Empty<string>();
    private static IReadOnlyList<Func<TimelineEvent, string?>> _agentEvents = Array.Empty<Func<TimelineEvent, string?>>();

    /// <summary>데이터를 바꾸는 에이전트 명령을 등록한다(계정 삭제, 친구 추가 같은 명령이 생기면).</summary>
    public static void RegisterDataCommand(string command)
    {
        lock (Lock) _dataCommands = _dataCommands.Append(command).Distinct().ToArray();
    }

    /// <summary>에이전트 이벤트 중 테스트 대상의 변화를 알아보는 함수를 등록한다(변화면 설명, 아니면 null).</summary>
    public static void RegisterAgentEvent(Func<TimelineEvent, string?> describe)
    {
        lock (Lock) _agentEvents = _agentEvents.Append(describe).ToArray();
    }

    /// <summary>이 이벤트가 환경 변화면 설명, 아니면 null.</summary>
    public static string? Describe(TimelineEvent e)
    {
        if (e.Source == HarnessConstants.Sources.Harness && HarnessEvents.TryGetValue(e.Name, out var harness))
            return $"{harness}({e.Name}{(string.IsNullOrEmpty(e.Detail) ? "" : ": " + HarnessJson.Truncate(e.Detail, HarnessConstants.Limits.EnvironmentDetailMaxLength))})";

        IReadOnlyList<Func<TimelineEvent, string?>> agentEvents;
        IReadOnlyCollection<string> dataCommands;
        lock (Lock)
        {
            agentEvents = _agentEvents;
            dataCommands = _dataCommands;
        }

        if (e.Source.StartsWith(HarnessConstants.Sources.AgentPrefix, StringComparison.Ordinal))
        {
            foreach (var describe in agentEvents)
                if (describe(e) is { } change) return change;
        }

        if ((e.Source == HarnessConstants.Sources.Ai || e.Source == HarnessConstants.Sources.Runner) && e.Name == HarnessConstants.HarnessEvents.McpCall &&
            e.Data is { } call && call.Str("command") is { } command && dataCommands.Contains(command) &&
            call.Get("isError") is not { ValueKind: JsonValueKind.True })
            return $"데이터 변경 명령({command})";

        return null;
    }
}

/// <summary>
/// 하네스 밖의 빌드를 알아채는 확인 - 서버마다 빌드 결과 DLL(Server/GameServer/&lt;이름&gt;/bin/Debug/net8.0/&lt;이름&gt;.dll,
/// DevTools server-common.ps1과 같은 경로)의 수정 시각을 비교한다. 노드 실행 직전과 직후에 부른다.
/// </summary>
public sealed class TH_BuildProbe
{
    private readonly HarnessPaths _paths;
    private readonly IReadOnlyList<string> _servers;

    public TH_BuildProbe(HarnessPaths paths, IReadOnlyList<string> servers)
    {
        _paths = paths;
        _servers = servers;
    }

    public IReadOnlyDictionary<string, DateTime?> Snapshot()
    {
        var snapshot = new Dictionary<string, DateTime?>(StringComparer.Ordinal);
        foreach (var server in _servers)
        {
            var dll = Path.Combine(_paths.GameServer, server, HarnessConstants.ServerBuild.OutputDir, server + HarnessConstants.ServerBuild.AssemblyExtension);
            snapshot[server] = File.Exists(dll) ? File.GetLastWriteTimeUtc(dll) : null;
        }
        return snapshot;
    }

    /// <summary>두 스냅숏 사이에 빌드 결과가 바뀐 서버들.</summary>
    public static IReadOnlyList<string> Changed(IReadOnlyDictionary<string, DateTime?> before, IReadOnlyDictionary<string, DateTime?> after) =>
        after.Where(kv => !before.TryGetValue(kv.Key, out var old) || old != kv.Value).Select(kv => kv.Key).ToList();
}
