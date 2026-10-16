using GameServer.Common;
using LoginServer;
using TestHarness.HeadlessClient;

// 헤드리스 클라이언트 동작 확인용 스모크 테스트 - 아직 시나리오 러너가 아니다(정식 러너는 다음 단계).
// 서버(Master/Matching/Account/Login/Lobby)가 떠 있는 상태에서 실행한다. GameAgent는 띄우지
// 않는다 - 인게임 서버가 하나도 없으면 매칭이 성사되지 않고 매칭 서버의 타임아웃(기본 30초)으로
// MatchFailed가 와야 정상이므로, 그것까지 확인한다.
//
// 사용법: dotnet run --project HeadlessClient.Smoke [-- --login https://localhost:5400]

var loginAddress = GetArg(args, "--login") ?? "https://localhost:5400";
var runId = DateTime.UtcNow.ToString("HHmmss");
var failures = new List<string>();

void Check(bool ok, string what)
{
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}");
    if (!ok) failures.Add(what);
}

HeadlessClientOptions Options(string name) => new()
{
    Name = name,
    LoginAddress = loginAddress,
    EventSink = e => Console.WriteLine($"      {e}"),
};

var accountId = $"smoke-{runId}";
const string password = "smoke-pw";

await using var a = new HeadlessClient(Options("bot-a"));
await using var b = new HeadlessClient(Options("bot-b"));

Console.WriteLine("[1] 계정 생성 + 로그인 → 로비 입장");
var created = await a.CreateAccountAsync(accountId, password, $"Smoke{runId}");
Check(created.Success, $"계정 생성 성공 (사유={(created.Success ? "-" : $"{created.FailureName} {created.Detail}")})");
if (created.Success)
{
    var enter = await a.EnterLobbyAsync();
    Check(enter.Success, "EnterLobby 성공");
    await a.Events.WaitForAnyAsync(TimeSpan.FromSeconds(5), 0, ClientEventKind.StayConnectedOpened);
    Check(a.Stage == ConnectionStage.InLobby, "상태가 InLobby");
}

Console.WriteLine("[2] 틀린 비밀번호 → INVALID_CREDENTIALS");
var wrong = await b.LoginAsync(accountId, "wrong-pw");
Check(!wrong.Success && wrong.RejectReason == LoginRejectReason.InvalidCredentials, $"거부 사유={wrong.FailureName}");

Console.WriteLine("[3] 같은 계정이 로비에 있는 동안 다른 클라가 로그인 → SESSION_CONFLICT");
var dup = await b.LoginAsync(accountId, password);
Check(!dup.Success && dup.RejectReason == LoginRejectReason.SessionConflict, $"거부 사유={dup.FailureName}");

Console.WriteLine("[4] 매칭 요청 → 인게임 서버가 없으므로 매칭 타임아웃으로 MatchFailed (최대 45초 대기)");
if (a.Stage == ConnectionStage.InLobby)
{
    var seq = a.RequestMatch(GameType.Normal);
    var queued = await a.Events.WaitForAnyAsync(TimeSpan.FromSeconds(10), seq, ClientEventKind.MatchQueued);
    Check(queued.Kind == ClientEventKind.MatchQueued, "MatchQueued 수신");

    try
    {
        var result = await a.WaitForMatchResultAsync(seq, TimeSpan.FromSeconds(45));
        Check(result.Kind == ClientEventKind.MatchFailed, $"매칭 결과={result.Kind} {result.Detail}");
        if (result.Kind == ClientEventKind.MatchFailed)
        {
            var failed = result.GetData<MatchFailedData>();
            Check(failed.Reason == MatchFailReason.Timeout, $"MatchFailed 사유={failed.Reason} ({failed.Detail})");
        }
        Check(a.Stage == ConnectionStage.InLobby, $"매칭 실패 후 단계={a.Stage}");

        // 쓸 수 있는 MatchFound가 없으면 인게임 이동은 이유와 함께 거부돼야 한다(이전 매치 티켓 재사용 방지).
        try
        {
            a.BeginIngameTransfer();
            Check(false, "MatchFound 없이 BeginIngameTransfer가 통과함");
        }
        catch (InvalidOperationException ex)
        {
            Check(!a.IsLastMatchFoundValid && ex.Message.Contains("MatchFound"), $"MatchFound 없이 인게임 이동 거부: {ex.Message}");
        }
    }
    catch (ClientEventTimeoutException ex)
    {
        Check(false, ex.Message);
    }
}

Console.WriteLine("[5] 로그아웃 → 같은 계정으로 다시 로그인 가능");
if (a.Stage != ConnectionStage.Disconnected)
{
    await a.LogoutAsync();
    var again = await b.LoginAsync(accountId, password);
    Check(again.Success, $"재로그인 성공 (사유={(again.Success ? "-" : $"{again.FailureName} {again.Detail}")})");
    if (again.Success) await b.EnterLobbyAsync();
}

Console.WriteLine("[6] 강제 연결 끊기 → LobbyDisconnected(Local) + 단계 Disconnected");
if (b.Stage == ConnectionStage.InLobby)
{
    var seq = b.Events.LastSeq;
    b.DropLobbyConnection();
    var dropped = await b.Events.WaitForAnyAsync(TimeSpan.FromSeconds(5), seq, ClientEventKind.LobbyDisconnected);
    Check(dropped.GetData<ClosedData>().ClosedBy == ClosedBy.Local, $"끊은 쪽={dropped.GetData<ClosedData>().ClosedBy}");
    Check(b.Stage == ConnectionStage.Disconnected, $"단계={b.Stage}");
}

Console.WriteLine();
Console.WriteLine(failures.Count == 0 ? "결과: 전부 PASS" : $"결과: FAIL {failures.Count}건");
return failures.Count == 0 ? 0 : 1;

static string? GetArg(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
