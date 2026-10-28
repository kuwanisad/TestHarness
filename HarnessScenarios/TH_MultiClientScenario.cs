using TestHarness.Core.Scenarios;
using TestHarness.Scenarios.Nodes;
using TestHarness.Scenarios.Targets;
using static TestHarness.Scenarios.Nodes.TH_Nodes;

namespace TestHarness.Scenarios;

/// <summary>
/// 여러 클라 동시 진행 - Unity 클라 둘(client1, client2)과 헤드리스 클라 하나(hc1). 대상별 노드/상태/값이 섞이지 않는지, 공통 동작
/// (계정, 로그인, 매칭)이 두 종류 클라에서 같은 노드로 도는지 본다. 헤드리스 클라는 로그인까지(매칭하면 인게임에 못 들어가 방이 남는다).
/// </summary>
[TH_Scenario("multi-client", "여러 클라 동시 진행 (Unity 클라 2 + 헤드리스 1)")]
public sealed class TH_MultiClientScenario : TH_Scenario
{
    protected override void Build(TH_ScenarioBuilder s)
    {
        var client1 = s.UnityClient("client1", startScene: Scenes.Login);
        var client2 = s.UnityClient("client2", startScene: Scenes.Login);
        var headless = s.HeadlessClient("hc1");
        var server = s.UnityServer(startScene: Scenes.ServerBoot);
        var editor = s.UnityEditor(bootScene: Scenes.HarnessBoot); // 씬 이름은 HarnessConstants.Scenes - ⚠ Unity 씬 파일 이름과 같아야 함

        s.Context("다중 클라 회귀 확인 - 로그인/로비/매칭/인게임 서버 방 관리 코드를 고친 뒤, 클라 여럿이 동시에 있어도 각자 정상인지 본다.");
        s.Context("기본: ServersUp → AccountReady 셋 → UnityPlay@client1+client2 → Login 셋 → EnterGame@client1+client2 → " +
                  "FinishGameFast@client1, FinishGameFast@client2 → LoadZero@client1+client2. 전부 한 번씩 통과하면 끝내도 된다.");
        s.Context("지금 매칭 인원이 1명이라(MatchingConstants) EnterGame@client1+client2는 둘이 각자 다른 방에 들어간다 - 같은 방이 아니어도 정상. " +
                  "인게임 cheat는 클라마다 자기 방(roomId)으로 보낸다.");
        s.Precondition("Unity는 Play가 꺼진 상태, 서버도 꺼진 상태(harness_status에 orphans 없음)");
        s.Precondition("MPPM 가상 플레이어를 셋 켠다: Server 하나, Client 태그 둘(메인 에디터가 Client1이면 가상 플레이어 하나가 Client2)");
        s.Caution("FinishGameFast의 배속(서버 프로세스 전체)은 다른 방도 잠깐 빨라지게 한다 - 두 클라를 동시에 끝내지 말고 하나씩");
        s.PlayFinishSound = true;
        s.AllowAiJudge = false;

        s.Node(ServersUp);
        s.Node(AccountReady(client1));
        s.Node(AccountReady(client2));
        s.Node(AccountReady(headless));
        s.Node(UnityPlay(editor, server, client1, client2));
        s.Node(Login(client1));
        s.Node(Login(client2));
        s.Node(Login(headless));
        s.Node(EnterGame(client1, client2));
        s.Node(FinishGameFast(client1));
        s.Node(FinishGameFast(client2));
        s.Node(LoadZero(client1, client2));
        s.Cleanup(Cleanup);

        s.AutoPlan(async p =>
        {
            var path = new[]
            {
                ServersUp, AccountReady(client1), AccountReady(client2), AccountReady(headless), UnityPlay(editor, server, client1, client2),
                Login(client1), Login(client2), Login(headless), EnterGame(client1, client2),
                FinishGameFast(client1), FinishGameFast(client2), LoadZero(client1, client2),
            };
            foreach (var node in path)
            {
                if ((await p.Run(node, "자동 실행 기본 경로")).Result != TH_Result.Pass) return;
            }
        });
    }
}
