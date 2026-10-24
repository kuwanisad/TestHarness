using TestHarness.Core.Scenarios;
using TestHarness.Scenarios.Nodes;
using TestHarness.Scenarios.Targets;
using static TestHarness.Scenarios.Nodes.TH_Nodes;

namespace TestHarness.Scenarios;

/// <summary>
/// 한 판 전체(Unity 클라 하나): 서버 기동 → 계정 → Unity Play → 로그인 → 매칭(인게임) → 게임 종료 → 로비 복귀 → 부하 복귀 → 정리.
/// 노드를 하나씩 드러내 AI가 고르게 한다(환경이 바뀌면 영향 받는 노드만 다시 돌 수 있게).
/// </summary>
[TH_Scenario("full-match", "한 판 전체 (로그인부터 로비 복귀까지)")]
public sealed class TH_FullMatchScenario : TH_Scenario
{
    protected override void Build(TH_ScenarioBuilder s)
    {
        var client = s.UnityClient("client1", startScene: TH_Scenes.Login);
        var server = s.UnityServer(startScene: TH_Scenes.ServerBoot);
        var editor = s.UnityEditor(bootScene: TH_Scenes.HarnessBoot); // 씬 이름은 TH_Scenes - ⚠ Unity 씬 파일 이름과 같아야 함

        s.Context("정상 한 판 회귀 확인 - 서버나 클라 코드를 고친 뒤, 로그인 → 매칭 → 인게임 → 게임 종료 → 로비 복귀 → 인게임 서버 부하 복귀가 " +
                  "정상인지 본다. 기본은 ServersUp → AccountReady@client1 → UnityPlay@client1 → Login@client1 → EnterGame@client1 → " +
                  "(WaitGameEnd@client1 또는 FinishGameFast@client1) → LoadZero@client1을 한 번씩.");
        s.Context("테스트 대상이 바뀌면(environment.changes) 바뀐 쪽에 영향 받는 노드를 다시 본다(예: 로비 서버를 다시 빌드했으면 Login부터).");
        s.Context("한 판을 끝까지 기다리는 게 기본(WaitGameEnd). 사람이 '빨리'라고 했을 때만 FinishGameFast. 전 노드를 한 번씩 통과했으면 끝내도 된다.");
        s.Precondition("Unity는 Play가 꺼진 상태, 서버도 꺼진 상태(harness_status에 orphans 없음)");
        s.Caution("Unity 명령의 ok는 '요청을 보냈다'까지 - 노드 판정은 이벤트로 한다(하네스가 함)");
        s.PlayFinishSound = true; // 끝나면 하네스 PC에서 완료음/경고음
        s.AllowAiJudge = false; // 전부 기계적으로 판정 가능 - AI 판정 노드를 넣지 않는다

        s.Node(ServersUp);
        s.Node(AccountReady(client));
        s.Node(UnityPlay(editor, server, client));
        s.Node(Login(client));
        s.Node(EnterGame(client));
        s.Node(WaitGameEnd(client));
        s.Node(FinishGameFast(client));
        s.Node(LoadZero(client));
        s.Cleanup(Cleanup);

        // 자동 실행: 한 번씩 차례로. 앞이 통과 못 하면 뒤는 의미가 없으니 멈춘다(정리는 하네스가 한다).
        s.AutoPlan(async p =>
        {
            foreach (var node in new[] { ServersUp, AccountReady(client), UnityPlay(editor, server, client), Login(client), EnterGame(client), WaitGameEnd(client), LoadZero(client) })
            {
                if ((await p.Run(node, "자동 실행 기본 경로")).Result != TH_Result.Pass) return;
            }
        });
    }
}
