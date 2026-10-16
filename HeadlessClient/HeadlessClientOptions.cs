namespace TestHarness.HeadlessClient;

/// <summary>헤드리스 클라이언트 하나의 설정.</summary>
public sealed class HeadlessClientOptions
{
    /// <summary>로그/타임라인에 찍힐 이 클라의 이름(예: "bot-1"). 여러 클라를 동시에 돌릴 때
    /// 누가 누군지 구분하는 용도라 playerId와 별개다 - playerId는 로그인 전엔 모르므로.</summary>
    public string Name { get; init; } = "client";

    /// <summary>로그인 서버 주소 - Unity ClientInstance._loginAddress 기본값과 같다.</summary>
    public string LoginAddress { get; init; } = "https://localhost:5400";

    /// <summary>
    /// [로컬 개발 전용] 서버 인증서 검증을 건너뛸지. Unity 클라(YetAnotherHttpHandlerFactory.
    /// SkipCertificateVerification)와 같은 이유 - 서버들이 로컬 자체 서명 개발 인증서를 쓴다.
    /// 실배포용 정식 인증서로 전환하면 false로.
    /// </summary>
    public bool SkipCertificateVerification { get; init; } = true;

    /// <summary>단항 RPC 하나의 기본 제한 시간 - 서버가 응답 없이 멈춰도 테스트가 영원히
    /// 안 끝나는 일이 없게 한다(진짜 클라에는 없는 장치 - 테스트 하네스 전용).</summary>
    public TimeSpan RpcTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>이벤트가 기록될 때마다 한 줄씩 받는다(콘솔/파일 출력용). null이면 출력 안 함 -
    /// 이벤트 자체는 항상 <see cref="HeadlessClient.Events"/>에 남는다.</summary>
    public Action<ClientEvent>? EventSink { get; init; }
}
