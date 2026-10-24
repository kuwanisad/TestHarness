using TestHarness.Core;
using TestHarness.Core.Scenarios;
using TestHarness.Scenarios.Targets;
using static TestHarness.Scenarios.Nodes.TH_Nodes;

namespace TestHarness.Scenarios;

/// <summary>
/// 재접속 / 탈주(Unity 클라 하나): 인게임에서 NGO 연결을 끊어(net.* - Network Simulator) 짧은 끊김, 끊김 감지 뒤 스스로 재진입, 끊어 둔 뒤
/// 다시 잇기, 끊긴 채 탈락을 본다. 끊긴 상태는 Disconnected(Cut)로 드러나 그 상태에서 다시 잇기/유예 만료/끊긴 동안의 게임 일을 고른다.
/// 재접속하면 게임이 그대로 이어져야 한다(인게임 화면, 국면 진행 수신). 탈주 뒤 클라 처리는 아직 정하지 않아 서버 탈락까지만 판정한다.
/// </summary>
[TH_Scenario("reconnect", "재접속 / 탈주 (짧은 끊김, 재진입, 다시 잇기, 탈락)")]
public sealed class TH_ReconnectScenario : TH_Scenario
{
    protected override void Build(TH_ScenarioBuilder s)
    {
        var client = s.UnityClient("client1", startScene: TH_Scenes.Login);
        var server = s.UnityServer(startScene: TH_Scenes.ServerBoot);
        var editor = s.UnityEditor(bootScene: TH_Scenes.HarnessBoot); // 씬 이름은 TH_Scenes - ⚠ Unity 씬 파일 이름과 같아야 함

        s.Context("인게임 중 클라↔인게임 서버 연결이 끊겼을 때를 본다 - 서버/클라의 재접속, 탈주 처리를 고친 뒤 회귀 확인. 기본은 ServersUp → " +
                  "AccountReady@client1 → UnityPlay@client1 → Login@client1 → EnterGame@client1 → BriefDrop@client1 → Reconnect@client1 → " +
                  "ReconnectPlanning@client1 ×3 → Cut@client1 → Restore@client1 → Cut@client1 → GraceExpire@client1.");
        s.Context("ReconnectPlanning은 반복 재접속 점검이다(재정비 중, 오브젝트 수가 서버와 맞는지, 트리 장부가 새지 않는지) - 여러 번 연달아 고른다.");
        s.Context("측정값(2026-10-23): 끊김 감지 약 30초, 클라 재진입 1회·10초, 서버 재접속 유예 약 20초. 재접속이 되면 게임이 그대로 이어져야 한다.");
        s.Context("Cut 뒤 client1은 Disconnected(서버 유예 약 20초, 클라 재진입 시도 약 10초) - Restore는 Cut 바로 다음에 골라야 재진입 시도 안에 이어진다. " +
                  "GraceExpire 뒤 클라가 어디로 가야 하는지는 아직 정하지 않았다 - 서버 탈락까지만 판정하고 클라 상태는 메모로 남는다. GraceExpire 뒤에는 끝낸다.");
        s.Precondition("Unity는 Play가 꺼진 상태, 서버도 꺼진 상태(harness_status에 orphans 없음)");
        s.Precondition("MPPM: server 가상 플레이어 창을 최소화하지 말 것(백그라운드에서 멈추면 하트비트가 끊겨 서버가 스스로 종료함 - 10-23 PC 실행)");
        s.Caution("노드 사이에 net.*로 직접 끊으면 그 결과는 판정에 안 들어간다(보고서의 '노드 밖에서 일어난 일'에 남음)");
        s.PlayFinishSound = true;
        // 로비 정리 로그(인게임 이동/재진입 때마다 남)는 하네스로만 모으고 콘솔에는 안 띄운다.
        s.Logs(collect: [HarnessConstants.GameLogTypes.LobbyClientDisconnected, HarnessConstants.GameLogTypes.LobbyStreamCancelled], console: []);
        s.AllowAiJudge = false;

        s.Node(ServersUp);
        s.Node(AccountReady(client));
        s.Node(UnityPlay(editor, server, client));
        s.Node(Login(client));
        s.Node(EnterGame(client));
        s.Node(BriefDrop(client));
        s.Node(Reconnect(client));
        s.Node(ReconnectPlanning(client));
        s.Node(Cut(client));
        s.Node(Restore(client));
        s.Node(GraceExpire(client));
        s.Cleanup(Cleanup);

        // 자동 실행: 준비가 안 되면 멈춘다. 짧은 끊김과 스스로 재진입은 서로 독립이라 앞이 실패해도 다음을 보고, 끊어 둔 뒤 다시 잇기가 실패하면
        // 클라 상태를 알 수 없어(재진입 포기 뒤 갇힘 등) 유예 만료는 건너뛴다.
        s.AutoPlan(async p =>
        {
            foreach (var node in new[] { ServersUp, AccountReady(client), UnityPlay(editor, server, client), Login(client), EnterGame(client) })
            {
                if ((await p.Run(node, "자동 실행 - 인게임까지")).Result != TH_Result.Pass) return;
            }

            await p.Run(BriefDrop(client), "자동 실행 - 짧은 끊김");
            await p.Run(Reconnect(client), "자동 실행 - 끊김 감지 뒤 스스로 재진입");
            for (var i = 1; i <= 3; i++)
            {
                if ((await p.Run(ReconnectPlanning(client), $"자동 실행 - 재정비 중 반복 재접속 {i}/3")).Result != TH_Result.Pass) break;
            }
            if ((await p.Run(Cut(client), "자동 실행 - 끊어 두기")).Result != TH_Result.Pass) return;
            if ((await p.Run(Restore(client), "자동 실행 - 다시 잇기(재진입 시도 안에)")).Result != TH_Result.Pass) return;
            if ((await p.Run(Cut(client), "자동 실행 - 다시 끊어 두기")).Result != TH_Result.Pass) return;
            await p.Run(GraceExpire(client), "자동 실행 - 유예 만료(탈주)");
        });
    }
}
