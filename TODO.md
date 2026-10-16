# TODO (TestHarness)

남은 작업만 간결하게 모아둔다. 세팅/실행 절차는 `README.md`, 서버 쪽 남은 작업은
`Server/TODO.md`, Unity 쪽은 `Unity/TODO.md`에 있다.

---

## 하네스 기능

- **가짜 인게임 서버** - Unity 없이 게임 에이전트의 `IngameControl.Connect` 스트림에 붙어
  Unity 역할(Ready, OpenRoom → RoomOpened, ApprovalRequest, PlayerJoined/Left/Abandoned,
  GameServerStatus, RoomTimeout)을 흉내 낸다. HeadlessClient의 `IngameTransfer.ConnectionPayload`가
  이미 NGO 페이로드와 같은 형식(`playerId|token|ticket`)이라 그대로 `ApprovalRequest`로 바꿔 보내면
  된다. 이게 있어야 아래 "런타임 검증 대기" 항목 대부분을 자동화할 수 있다.
- **시나리오 러너** - 시나리오 정의 → 실행 → 판정 → `report.md` 작성. 지금은 스모크 테스트만 있다.
- **타임라인** - 서버 로그(JSON) + 클라 이벤트 + 에이전트 이벤트를 시간순으로 합친 한 파일.
- **Unity용 스모크** - 하네스만 띄워두고 Unity 접속을 기다렸다가 명령을 보내는 모드.
- **Unity가 하네스에 평문 HTTP/2로 붙는지 미확인** - YetAnotherHttpHandler가 TLS 없는 h2c에
  접속되는지 PC에서 확인 전(`README.md` "문제 해결" 참고).

## HeadlessClient

- **채팅(WebSocket) 미지원.**
- **전입 티켓 재사용이 Unity와 다름** - Unity 클라는 MatchFound로 받은 티켓을 기억했다가 로그인
  서버 재진입이 같은 주소로 안내하면 다시 낸다(`ClientInstance._ingameTransfer`). HeadlessClient의
  `ReconnectViaLoginServerAsync`는 인게임 안내를 받으면 티켓 없이 `PendingIngameTransfer`를 만든다 -
  가짜 인게임 서버로 "이동 중 첫 접속 실패" 시나리오를 돌리려면 같은 동작으로 맞출 것.
- (완료) 코드 리뷰 수정 3건 - 이전 매치 정보 재사용(`LastMatchFound` + 유효 플래그), 서버 쪽 끊김
  시 단계 미갱신(`ConnectionStage`로 이름 변경 + 서버가 닫으면 갱신), 로비 연결 교체 경합
  (`DetachLobby`의 원자적 교체).

## 런타임 검증 대기 (코드로만 확인한 수정)

서버/Unity에서 코드로 원인을 확정하고 고쳤지만 실제 실행으로는 아직 확인 못 한 것들
(`Server/TODO.md` "코드 리뷰로 발견해 수정한 항목"). 가짜 인게임 서버가 생기면 시나리오로 만들 것.

- 정상 종료를 여러 판 반복해도 게임 에이전트 부하가 쌓이지 않는지(`GameServerStatus` 값 추적),
  수락~응접실 생성 사이에 정원을 넘겨 수락하지 않는지.
- PrepareMatch가 계속 거부될 때 매칭 타임아웃이 처음 대기열에 들어온 시각 기준으로 걸리는지.
- 재접속 가능 방: 유예 안 재접속 → 복귀, 유예 밖 → `PlayerAbandoned` 후 세션 삭제, 이후 재접속은
  `PLAYER_ABANDONED`로 거부. 재접속 불가 방: 끊기면 즉시 소유권 해제.
- 게임 에이전트 승인 조건: `Moving`인데 티켓 없음/틀림 → `INVALID_TRANSFER_TICKET`, 틀린 티켓
  뒤에도 진짜 티켓으로 입장 가능, `Reconnecting`은 티켓 없이 승인, 소유자가 다른 서버면 거부.
- 이동 중 첫 접속 실패 → 로그인 서버가 `NextServer`로 안내 → 받아둔 티켓으로 입장. TTL이 지나면
  세션 삭제 → 로그인 서버가 `NO_ACTIVE_SESSION`.
- 응접실 승격 중(닉네임 조회 대기 중) 접속 완료/끊김/방 종료가 반영되는지, 닉네임 조회가 5초 안에
  안 오거나 계정이 없으면 방이 깨지는지.

## 서버 쪽 보완 (하네스 판정에 필요)

- **`INVALID_CREDENTIALS` 분기 일부에 로그가 없음** - 로그인 서버가 거부 사유를 클라에는 보내지만
  서버 로그에 구체적인 값(어느 계정, 어느 단계)을 남기지 않는 분기가 있다. 하네스는 로그로 판정하므로
  "무엇이 일어났는지"를 값으로 남기도록 보완할 것.
