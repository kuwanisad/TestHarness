# TestHarness 작업 규칙

이 폴더는 오토체스 서버/Unity를 테스트하는 하네스다. 하네스를 **쓰는** 법은 MCP 접속 때 하네스가 직접 알려준다
(`HarnessDaemon/McpInstructions.md`). 이 파일은 하네스 **자체를 고칠 때**의 규칙이다.

## 구조

- `HarnessCore/` - 기능은 전부 여기. 실행 단위(RunManager), 타임라인(Timeline), 서버 제어(ServerManager, DevTools 스크립트 호출),
  빌드(BuildRunner), 헤드리스 클라 풀(ClientPool), 로그 조회(LogStore). `HarnessSession`이 전부 묶는다.
- `HarnessDaemon/` - AI용 입구. `Tools/*.cs`는 인자를 받아 HarnessCore에 넘기고 결과를 돌려주는 얇은 층으로 유지한다.
  기능 로직을 도구 클래스에 넣지 않는다(3주차 C# 시나리오도 HarnessCore를 그대로 쓰므로).
- `HarnessCore/Scenarios/` - 시나리오 틀(`TH_*`): 목표 그래프(TH_Objective, TH_Scenario, TH_ScenarioBuilder), 판정 재료
  (TH_JudgeContext), 진행 스테이트머신 + 보고서(TH_ScenarioRunner). `HarnessCore/ToolCallLog.cs` - AI의 모든 도구 호출 기록.
- `HarnessScenarios/` - 시나리오 정의(`[TH_Scenario]` 클래스 하나 = 목표 그래프 하나). 데몬이 시작할 때 리플렉션으로 모은다.
- `HarnessDaemon/Mcp/` - MCP(도구 기능만) 직접 구현. 공식 SDK로 바꿀 때 손댈 곳은 `McpToolAttribute.cs` 맨 위 주석.
  도구 클래스는 `[McpTool]`, `[Description]`, `HarnessException`만 안다 - 전송 방식에 의존하는 코드를 도구 쪽에 넣지 않는다.
- `HarnessHost/`, `HeadlessClient/` - 에이전트 접속 서버, 화면 없는 클라. 스모크 테스트(`*.Smoke`)가 따로 있다.

## 도구를 추가/변경할 때

- 이름은 snake_case, `묶음_동작`(예: `client_join_match`). 바꾸면 `McpInstructions.md`와 README 도구 표도 같이 고친다.
- 설명(`[Description]`)에는 "언제 쓰는지 / 무엇을 돌려주는지 / 실패하면 무엇을 하면 되는지"를 쓴다 - AI가 읽는 유일한 사용법이다.
- 행동 도구는 `sinceSeq`(행동 직전 `Timeline.LastSeq`)를 돌려준다.
- 출력에는 한도를 둔다(목록은 limit + matched/truncated/hint, 긴 문자열은 `HarnessJson.Truncate`). 전문은 실행 폴더 파일로 남긴다.
- AI가 고칠 수 있는 실패는 `HarnessException(메시지, 힌트)`로 던진다. 힌트는 "그럼 무엇을 하면 되는지" 한 줄.
- 열거형은 이름으로 내보낸다(`HarnessJson.Options`). 거부 사유를 문장으로 바꾸지 않는다.

## 명령 영역 (에이전트 명령 이름 `<영역>.<동작>`)

- 영역 목록의 원본은 `HarnessCore/CommandAreas.cs` 하나다. AI는 `agent_list(query=<영역>)`으로 명령을 찾으므로 영역 이름이 곧 검색 키워드다.
- 영역은 "무엇을 다루나"(대상)로 정한다. 동작 단어는 영역이 아니다(`buy.piece`가 아니라 `shop.buy`).
- 조회 명령은 `state`(한 대상의 현재 상태) 또는 `list`(여러 개 목록)로 이름을 맞춘다.
- 기존 영역에 안 맞을 때만 새 영역을 만들고, 만들면 `CommandAreas.cs`에 먼저 등록한 뒤 `McpInstructions.md`의 영역 줄과 README도 고친다.
  등록 안 된 영역의 명령은 `agent_list`가 `unknownAreas`로 드러낸다.

## 시나리오

- 시나리오 틀의 타입은 `TH_` 접두어(TestHarness)를 붙인다(일반 단어 `Objective`, `Scenario`와 겹치지 않게). 시나리오 클래스도
  `TH_<무엇>Scenario`.
- 모든 상호작용은 두 형태뿐이다: (가) 하네스가 질문 → AI가 대답 → 하네스가 기록, (나) AI가 출력 → 하네스가 기록.
  하네스 응답의 `expect`에는 시나리오 진행 행동과 목표 ID만 넣는다 - 기능 도구 목록을 넣지 않는다(기능 도구는 언제든 허용, 기록만).
- 판정은 가능하면 judge(코드)로 하네스가 한다. judge는 기록(TH_JudgeContext)만 보고 판정한다 - 판정 중에 에이전트에 새로 묻지 않는다.
- 분기는 가능하면 결과 분기(`Then(select)`), AI가 골라야 할 때만 `ThenChoice(guide, ...)`.
- 뒤 목표가 앞 결과에 의존하면 `TH_FailPolicy.Stop`. 정리 목표(`Cleanup`)는 반드시 하나.
- 보고서는 하네스가 기록으로 쓴다(AI가 쓰지 않는다).

## 지키는 원칙

- 판정은 바깥에서 관찰한 사실(이벤트, 응답 열거형, 결과 파일)로 한다. 서버 로그는 원인 찾기용.
- 고정 시간 대기 대신 조건 대기(`Timeline.WaitForAsync`).
- 서버 기동/종료는 DevTools 스크립트로만 한다(사람과 같은 방식). 판단은 스크립트 결과 파일(`*-result.json`) + 에이전트 접속.
- 외부 명령은 `ProcessRunner`로 실행한다(출력을 파일로 받는다 - 손자 프로세스가 파이프를 쥐어 멈추는 문제 때문. 클래스 주석 참고).

## 확인

- 빌드: `dotnet build TestHarness.slnx`
- MCP 수동 확인: 하네스를 띄우고 `curl -X POST http://localhost:9100/mcp -H "Content-Type: application/json" -d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}"`
- 기존 스모크: `dotnet run --project HarnessHost.Smoke`(HarnessDaemon과 같은 9000 포트를 쓰니 동시에 띄우지 말 것), `dotnet run --project HeadlessClient.Smoke`
