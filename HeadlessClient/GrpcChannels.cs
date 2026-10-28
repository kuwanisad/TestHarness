using System.Net.Security;
using Grpc.Net.Client;

namespace TestHarness.HeadlessClient;

/// <summary>
/// gRPC 채널 생성. Unity 클라는 YetAnotherHttpHandler(Http2Only, SkipCertificateVerification)를
/// 쓰는데, 일반 .NET에선 SocketsHttpHandler로 같은 동작을 낸다. KeepAlive 값은 서버끼리 쓰는
/// Shared.Common.GrpcChannelFactory와 맞췄다 - 끊김 감지 시간이 들쭉날쭉하면(KNOWN_ISSUES #30)
/// 시나리오 판정 시간도 들쭉날쭉해지므로.
/// </summary>
internal static class GrpcChannels
{
    public static GrpcChannel Create(string address, bool skipCertificateVerification)
    {
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay = TimeSpan.FromSeconds(HarnessConstants.Limits.HeadlessKeepAlivePingSeconds),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(HarnessConstants.Limits.HeadlessKeepAliveTimeoutSeconds),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
        };

        if (skipCertificateVerification)
        {
            // [로컬 개발 전용] Unity 클라와 같은 이유 - 로컬 자체 서명 개발 인증서.
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            };
        }

        return GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true,
        });
    }
}
