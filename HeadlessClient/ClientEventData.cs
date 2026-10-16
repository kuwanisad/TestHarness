using GameServer.Common;
using LobbyServer;
using LoginServer;

namespace TestHarness.HeadlessClient;

// ClientEvent.Data에 붙는 값들. 이벤트 종류별로 어떤 타입이 붙는지:
//
//   LoginSucceeded, ReconnectRedirected                      → LoginRedirectData
//   LoginRejected, ReconnectRejected                          → LoginRejectedData
//   EnterLobbyRejected, EnterLobbyRateLimited                 → EnterLobbyRejectedData
//   MatchFailed                                              → MatchFailedData
//   EnterLobbyConflict                                       → ConflictData
//   MatchQueued, MatchStatus                                 → QueuePositionData
//   MatchFound                                               → MatchFoundData
//   StayConnectedClosed, MatchStreamClosed, LobbyDisconnected → ClosedData
//   MailProvided                                             → MailData
//   Announcement                                             → AnnouncementData
//   그 외(LobbyConnected, LoggedOut 등)                      → 없음(null)
//
// 이 이벤트들은 하네스 프로세스 안에서만 오가므로(네트워크를 안 탐) protobuf가 아니라 C# 타입으로
// 둔다 - 종류마다 다른 데이터를 하나의 필드로 담는 건 proto의 oneof와 같은 발상.

/// <summary>로그인/재진입 성공 시 안내받은 목적지.</summary>
public sealed record LoginRedirectData(string PlayerId, string DestinationAddress, ServerType DestinationServerType);

/// <summary>로그인/재진입 거부. 서버가 거부했으면 Reason, 클라에서 실패했으면 ClientFailure(Reason은 Unspecified).</summary>
public sealed record LoginRejectedData(LoginRejectReason Reason, ClientFailure ClientFailure, string Detail);

/// <summary>로비 입장 거부. 서버가 거부했으면 Result, 클라에서 실패했으면 ClientFailure(Result는 Unspecified).</summary>
public sealed record EnterLobbyRejectedData(EnterLobbyResult Result, ClientFailure ClientFailure, string Detail);

/// <summary>매칭 실패 - 서버가 보낸 사유 그대로.</summary>
public sealed record MatchFailedData(MatchFailReason Reason, string Detail);

/// <summary>다른 서버에 세션이 남아 있어 로비 입장이 거부됨.</summary>
public sealed record ConflictData(string ConflictingServerAddress);

/// <summary>매칭 대기 순번.</summary>
public sealed record QueuePositionData(int Position);

/// <summary>매치 확정 - 인게임 서버로 넘어갈 때 필요한 값.</summary>
public sealed record MatchFoundData(string GameServerAddress, string IngameServerId, string TransferTicket);

/// <summary>누가 연결(스트림)을 끊었는지.</summary>
public enum ClosedBy
{
    /// <summary>이 클라가 닫음(로그아웃, 매칭 취소, DropLobbyConnection, 인게임 이동 등).</summary>
    Local,

    /// <summary>서버가 정상적으로 스트림을 끝냄.</summary>
    Server,

    /// <summary>오류로 끊김(서버 종료, 네트워크 끊김, RPC 예외 등).</summary>
    Error,
}

/// <summary>스트림/연결이 닫힌 이유.</summary>
public sealed record ClosedData(ClosedBy ClosedBy, string Reason);

/// <summary>우편 push.</summary>
public sealed record MailData(int Count);

/// <summary>공지 push.</summary>
public sealed record AnnouncementData(string AnnouncementType);
