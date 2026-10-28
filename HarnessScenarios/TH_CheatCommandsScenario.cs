using TestHarness.Core.Scenarios;
using TestHarness.Scenarios.Nodes;
using TestHarness.Scenarios.Targets;
using static TestHarness.Scenarios.Nodes.TH_Nodes;

namespace TestHarness.Scenarios;

/// <summary>
/// 테스트 조작 명령(cheat.*)과 클라 판매(shop.sell) 검증(Unity 클라 하나): 인게임 준비(묶음) → 지정 구매+배치 → (합성 확인) → 판매 →
/// 재정비 시간 세 모드 → 데미지 → 빨리 끝내고 로비 복귀 → 정리.
/// </summary>
[TH_Scenario("cheat-commands", "테스트 조작 명령(cheat.*)과 판매 검증")]
public sealed class TH_CheatCommandsScenario : TH_Scenario
{
    protected override void Build(TH_ScenarioBuilder s)
    {
        var client = s.UnityClient("client1", startScene: Scenes.Login);
        var server = s.UnityServer(startScene: Scenes.ServerBoot);
        var editor = s.UnityEditor(bootScene: Scenes.HarnessBoot); // 씬 이름은 HarnessConstants.Scenes - ⚠ Unity 씬 파일 이름과 같아야 함

        s.Context("테스트 조작 명령(cheat.*)과 클라 판매(shop.sell)가 제대로 동작하는지 본다 - cheat 코드나 국면/상점 코드를 고친 뒤에 돌린다.");
        s.Context("ReadyIngame@client1(서버 기동부터 인게임까지 묶음)으로 들어간 뒤 BuyPlace, (BuyPlace가 merged=true면 MergeCheck), Sell, " +
                  "PlanningCurrentOnly, PlanningSettingOnly, PlanningRescale, Damage를 한 번씩(전부 @client1), 마지막에 FinishGameFast@client1로 " +
                  "로비 복귀까지 보면 충분하다.");
        s.Context("테스트 대상이 바뀌면(environment.changes) 바뀐 쪽에 영향 받는 노드를 다시 본다(Unity 재컴파일이면 Play부터 다시 해야 하므로 끝내고 다시 시작하는 편이 낫다).");
        s.Precondition("Unity는 Play가 꺼진 상태, 서버도 꺼진 상태(harness_status에 orphans 없음)");
        s.Caution("재정비 시간을 재접속 유예(20초)보다 짧게 줄이는 건 이 시나리오 안에서만 - 끝나면 Play를 멈추므로 남지 않는다");
        s.PlayFinishSound = true; // 끝나면 하네스 PC에서 완료음/경고음
        s.AllowAiJudge = true; // MergeCheck는 AI 판정 노드

        s.Node(ReadyIngame(editor, server, client));
        s.Node(BuyPlace(client));
        s.Node(MergeCheck(client));
        s.Node(Sell(client));
        s.Node(PlanningCurrentOnly(client));
        s.Node(PlanningSettingOnly(client));
        s.Node(PlanningRescale(client));
        s.Node(Damage(client));
        s.Node(FinishGameFast(client));
        s.Cleanup(Cleanup);

        // 자동 실행: 뒤가 앞 결과에 의존하는 곳(준비, 구매, 재정비 단축/설정)은 실패하면 멈추고, 독립인 곳은 계속한다.
        s.AutoPlan(async p =>
        {
            const string why = "자동 실행 기본 경로";
            if ((await p.Run(ReadyIngame(editor, server, client), why)).Result != TH_Result.Pass) return;
            if ((await p.Run(BuyPlace(client), why)).Result != TH_Result.Pass) return;
            if (p.Values.For(client).Get<bool>(ValueKeys.Merged)) await p.Run(MergeCheck(client), "BuyPlace에서 합성됨"); // 자동 실행에서는 AI 판정 노드라 미확인으로 남는다
            await p.Run(Sell(client), why);
            if ((await p.Run(PlanningCurrentOnly(client), why)).Result != TH_Result.Pass) return;
            if ((await p.Run(PlanningSettingOnly(client), why)).Result != TH_Result.Pass) return;
            await p.Run(PlanningRescale(client), why);
            await p.Run(Damage(client), why);
            await p.Run(FinishGameFast(client), why);
        });
    }
}
