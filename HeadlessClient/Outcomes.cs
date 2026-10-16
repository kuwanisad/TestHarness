using GameServer.Common;
using LobbyServer;
using LoginServer;

namespace TestHarness.HeadlessClient;

/// <summary>
/// 서버 응답을 받기도 전에 클라 쪽에서 실패한 이유 - 서버 열거형(LoginRejectReason 등)에는 없는 값들.
/// Unity의 ClientFailure와 이름을 맞췄다(Unity에만 있는 LobbyEntryFailed는 빼고, 하네스 전용인
/// Timeout/NoCachedCredentials를 더했다). None이면 서버가 응답했다는 뜻 - 서버 열거형 쪽을 볼 것.
/// </summary>
public enum ClientFailure
{
    None = 0,
    NoResponse,          // 스트림이 응답 없이 끝남
    UnknownResponse,     // 모르는 응답 형식
    RpcError,            // gRPC 예외(서버 다운, 연결 실패 등) - Detail에 상태 코드/설명
    Timeout,             // 하네스가 정한 RPC 제한 시간 초과
    NoCachedCredentials, // 재진입하려는데 캐시된 playerId/토큰이 없음
}

/// <summary>
/// 로그인 서버 왕복 한 번의 결과 - Unity의 LoginResult와 같은 내용.
/// 실패 시 서버가 거부했으면 RejectReason, 클라에서 실패했으면 ClientFailure가 채워진다.
/// Detail은 로그/디버그용 설명(판정에 쓰지 말 것 - 판정은 열거형으로).
/// </summary>
public sealed record LoginOutcome(
    bool Success,
    LoginRejectReason RejectReason,
    ClientFailure ClientFailure,
    string Detail,
    string PlayerId,
    string Token,
    string DestinationAddress,
    ServerType DestinationServerType,
    string ChatAddress,
    string Nickname)
{
    public static LoginOutcome Rejected(LoginRejectReason reason, string detail) =>
        new(false, reason, ClientFailure.None, detail, "", "", "", ServerType.Unspecified, "", "");

    public static LoginOutcome Failed(ClientFailure failure, string detail) =>
        new(false, LoginRejectReason.Unspecified, failure, detail, "", "", "", ServerType.Unspecified, "", "");

    /// <summary>로그/이벤트 표시용 - "INVALID_CREDENTIALS" 같은 식이 아니라 C# 이름(InvalidCredentials).</summary>
    public string FailureName => ClientFailure != ClientFailure.None ? $"Client.{ClientFailure}" : RejectReason.ToString();
}

/// <summary>
/// EnterLobby 한 번의 결과. 서버 응답(EnterLobbyResponse.result)을 그대로 담는다 - RATE_LIMITED,
/// SESSION_CONFLICT도 Result 값으로 구분한다. 클라에서 실패했으면(RPC 예외) ClientFailure가 채워지고
/// Result는 Unspecified.
/// </summary>
public sealed record EnterLobbyOutcome(
    EnterLobbyResult Result,
    ClientFailure ClientFailure,
    string Detail,
    string ConflictingServerAddress)
{
    public bool Success => Result == EnterLobbyResult.Ok;
    public bool RateLimited => Result == EnterLobbyResult.RateLimited;
    public bool HasConflictingSession => Result == EnterLobbyResult.SessionConflict;

    public string FailureName => ClientFailure != ClientFailure.None ? $"Client.{ClientFailure}" : Result.ToString();
}

/// <summary>
/// 매치 확정 후 인게임 서버로 넘어갈 때 필요한 정보 - Unity ClientInstance.ConnectToIngameServerAsync가
/// NGO에 넣던 값들. ConnectionPayload는 NGO ConnectionData와 정확히 같은 형식
/// ("playerId|token|ticket")이라, 가짜 인게임 서버가 이 문자열을 그대로 ApprovalRequest로
/// 바꿔 게임 에이전트에 보내면 된다.
/// </summary>
public sealed record IngameTransfer(
    string GameServerAddress,
    string IngameServerId,
    string PlayerId,
    string Token,
    string TransferTicket)
{
    public string ConnectionPayload => $"{PlayerId}|{Token}|{TransferTicket}";
}
