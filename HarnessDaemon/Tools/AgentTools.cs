using System.ComponentModel;
using System.Text.Json;
using TestHarness.Core;
using TestHarness.Daemon.Mcp;

namespace TestHarness.Daemon.Tools;

/// <summary>테스트 대상(서버/Unity) 에이전트에 디버그 명령 보내기.</summary>
public sealed class AgentTools
{
    private readonly HarnessSession _session;

    public AgentTools(HarnessSession session) => _session = session;

    [McpTool("agent_list")]
    [Description("하네스에 접속한 에이전트(서버 프로세스, Unity)와 그 디버그 명령을 찾는다. query 없이 부르면 에이전트 목록과 명령 영역 요약" +
                 "(영역, 명령 수, 설명)만 - 명령 전체는 query=<영역>(예: shop, cheat, editor)으로 그 영역만 본다. query는 영역 이름 또는 " +
                 "명령 이름(예: shop.buy)만 받는다(모르면 영역 목록을 hint로 돌려줌). agent_command를 쓰기 전에 그 영역을 한 번 볼 것.")]
    public object AgentList(
        [Description("영역 이름(예: shop) 또는 명령 이름(예: shop.buy). 비우면 영역 요약")] string? query = null,
        [Description("이 에이전트만(예: client, server, LobbyServer)")] string? agent = null)
    {
        var agents = _session.Host.Agents
            .Where(a => agent == null || string.Equals(a.Name, agent, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .ToList();

        var agentViews = agents.Select(a => new { a.Name, kind = a.ProcessKind, a.Pid, connected = a.IsConnected, commandCount = a.Commands.Count }).ToList();
        if (agents.Count == 0)
        {
            return new
            {
                agents = agentViews,
                hint = agent == null ? "접속한 에이전트가 없음 - servers_start로 서버를 띄우거나 Unity를 Play할 것" : $"'{agent}' 에이전트가 없음",
            };
        }

        // (에이전트, 명령) 전부 - 영역별로 묶어 쓴다.
        var all = agents
            .SelectMany(a => a.Commands.Values.Select(c => (Agent: a.Name, Command: c, Area: CommandAreas.AreaOf(c.Name))))
            .ToList();

        var unknownAreas = all
            .Where(x => !CommandAreas.TryGet(x.Area, out _))
            .GroupBy(x => x.Area, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { area = g.Key, agents = g.Select(x => x.Agent).Distinct().ToArray(), commands = g.Select(x => x.Command.Name).Distinct().ToArray() })
            .ToList();

        if (string.IsNullOrWhiteSpace(query))
        {
            var areas = CommandAreas.All
                .Select(area =>
                {
                    var inArea = all.Where(x => string.Equals(x.Area, area.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                    return new
                    {
                        area = area.Name,
                        commandCount = inArea.Select(x => x.Command.Name).Distinct().Count(),
                        agents = inArea.Select(x => x.Agent).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray(),
                        area.Description,
                        area.WhenToUse,
                    };
                })
                .Where(a => a.commandCount > 0)
                .ToList();

            return new
            {
                agents = agentViews,
                areas,
                unknownAreas = unknownAreas.Count == 0 ? null : unknownAreas,
                hint = "명령은 agent_list(query=<영역>)으로 볼 것" +
                       (unknownAreas.Count > 0 ? " - unknownAreas는 하네스에 등록 안 된 영역(HarnessCore/CommandAreas.cs에 등록 필요, 사람에게 알릴 것)" : ""),
            };
        }

        var key = query.Trim();
        var isCommand = key.Contains('.');
        var area = isCommand ? CommandAreas.AreaOf(key) : key;
        var registered = CommandAreas.TryGet(area, out _);
        var matched = all
            .Where(x => isCommand
                ? string.Equals(x.Command.Name, key, StringComparison.OrdinalIgnoreCase)
                : string.Equals(x.Area, area, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Command.Name, StringComparer.Ordinal).ThenBy(x => x.Agent, StringComparer.Ordinal)
            .ToList();

        if (matched.Count == 0)
        {
            if (!registered)
                throw new HarnessException($"모르는 영역 '{area}'", $"query에 쓸 수 있는 영역: {CommandAreas.Names} (또는 명령 이름 예: shop.buy)");

            throw new HarnessException(
                isCommand ? $"명령 '{key}'를 가진 에이전트가 없음" : $"'{area}' 영역 명령을 가진 에이전트가 지금 없음",
                $"영역 '{area}'의 대상: {(CommandAreas.TryGet(area, out var info) ? info.Agents : "?")} - 그 에이전트가 붙었는지 agent_list()로 확인" +
                " (Unity 쪽이면 Play 중이어야 함)");
        }

        return new
        {
            query = key,
            registered,
            commands = matched.Select(x => new
            {
                agent = x.Agent,
                x.Command.Name,
                x.Command.Description,
                parameters = x.Command.Parameters.Select(p => $"{p.Name}:{p.Type}{(p.Optional ? "?" : "")}"),
            }),
            hint = registered ? null : $"'{area}'는 하네스에 등록 안 된 영역 - HarnessCore/CommandAreas.cs에 등록 필요(사람에게 알릴 것)",
        };
    }

    [McpTool("agent_command")]
    [Description("에이전트에 디버그 명령 하나를 보내고 결과를 받는다(예: agent=GameAgent, command=process.status). ok는 '명령 함수가 예외 없이 " +
                 "끝났다'는 뜻일 뿐 - 그 결과로 일어나야 할 일은 이벤트(wait_for)로 확인할 것.")]
    public async Task<object> AgentCommand(
        [Description("에이전트 이름(agent_list의 name)")] string agent,
        [Description("명령 이름(예: process.status) - agent_list(query=<영역>)으로 찾는다")] string command,
        [Description("명령 인자 객체(예: {\"slotIndex\":1}). 없으면 생략")] JsonElement? args = null,
        [Description("결과 대기 시간(초)")] int timeoutSeconds = 15,
        CancellationToken ct = default)
    {
        var session = _session.Host.Agents.FirstOrDefault(a => string.Equals(a.Name, agent, StringComparison.OrdinalIgnoreCase))
                      ?? throw new HarnessException($"'{agent}' 에이전트가 없음",
                          $"접속 중: {string.Join(", ", _session.Host.Agents.Select(a => a.Name).DefaultIfEmpty("없음"))}");

        var sinceSeq = _session.Timeline.LastSeq;
        // 인자는 그대로 JSON으로 넘긴다(AgentSession이 익명 객체/딕셔너리를 JSON → protobuf Struct로 바꾼다).
        object? payload = args is { ValueKind: JsonValueKind.Object } a
            ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(a.GetRawText())
            : null;

        var outcome = await session.InvokeAsync(command, payload, TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 300)), ct);
        return new
        {
            sinceSeq,
            ok = outcome.Ok,
            value = string.IsNullOrEmpty(outcome.ValueJson) ? (JsonElement?)null : HarnessJson.TryParse(outcome.ValueJson),
            error = string.IsNullOrEmpty(outcome.Error) ? null : outcome.Error,
        };
    }
}
