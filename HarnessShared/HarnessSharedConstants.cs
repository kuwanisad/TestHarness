// 테스트 하네스와 에이전트(서버 Harness.Agent, Unity 테스트 코드)가 같아야 하는 값 - 원본은 이 파일 하나다.
//
// ※ 원본: GameProject1/TestHarness/HarnessShared/HarnessSharedConstants.cs. harness.proto와 같은 길로 퍼진다 -
//    TestHarness 빌드(HarnessHost)가 Server/GameServer/Shared/Test/Harness.Contracts/Shared/에 복사하고, Harness.Contracts 빌드가
//    Unity/Assets/Scripts/Test/Harness/Generated/에 복사한다. 사본에서 고치지 말 것 - 다음 빌드에 덮어써진다.
// ※ Unity(.NET Standard 2.1, C# 9)에서도 컴파일된다 - using 없이, 최신 문법 없이 둔다. Unity에서는 테스트 코드라 개발 빌드에만 들어간다.
#if !UNITY_5_3_OR_NEWER || UNITY_EDITOR || DEVELOPMENT_BUILD
namespace Harness.Shared
{
    public static class HarnessSharedConstants
    {
        /// <summary>대상이 하네스 주소/이름을 받는 환경 변수(서버는 DevTools가 정함, Unity는 실행 인자가 먼저).</summary>
        public static class EnvironmentVariables
        {
            public const string Address = "HARNESS_ADDRESS";
            public const string Name = "HARNESS_NAME";
        }

        /// <summary>에이전트 연결 규칙(서버 HarnessAgentService, Unity HarnessConnection).</summary>
        public static class Connection
        {
            /// <summary>하네스가 없거나 끊기면 다시 접속하는 간격(초).</summary>
            public const double RetryDelaySeconds = 1;

            /// <summary>명령 결과를 이 시간 안에 못 보내면(연결 없음) AfterReply로 등록한 일을 그냥 실행한다(초).</summary>
            public const double AfterReplyTimeoutSeconds = 2;

            /// <summary>메인 에디터 브리지의 에이전트 이름(Unity HarnessEditorBridge).</summary>
            public const string EditorAgentName = "UnityEditor";

            /// <summary>하네스의 에이전트 접속 포트(하네스 HarnessConstants.Defaults.AgentPort).</summary>
            public const int AgentPort = 9000;

            /// <summary>Unity 에디터 Play가 실행 인자 없이 붙을 때의 주소(Unity HarnessSettings).</summary>
            public const string DefaultEditorAddress = "http://localhost:9000";
        }

        /// <summary>명령 실패 문구 중 하네스가 구분해야 하는 것.</summary>
        public static class Errors
        {
            /// <summary>그 에이전트에 없는 명령(앞부분 - 뒤에 명령 이름이 붙는다).</summary>
            public const string UnknownCommand = "모르는 명령";

            /// <summary>노드가 쓰는 status 조회가 대상에 없음 - {0} 명령, {1} 에이전트.</summary>
            public const string ProbeMissingFormat = "구현부가 없음: {0} ({1}) - 테스트 실행 전에 Unity/서버 테스트 코드에 구현했어야 함";
        }

        /// <summary>명령 영역 중 규칙이 따로 있는 것.</summary>
        public static class Areas
        {
            /// <summary>게임 상태 조회(status.*) - 테스트 실행 전에 정의해야 하는 조회. 없으면 Errors.ProbeMissingFormat.</summary>
            public const string Status = "status";
        }

        /// <summary>log.filter 인자 형식.</summary>
        public static class LogFilter
        {
            public const string CollectArg = "collect";
            public const string ConsoleArg = "console";

            /// <summary>목록 구분자.</summary>
            public const string Separator = ",";

            /// <summary>빈 목록(하나도 없음) - 빈 문자열은 전부(기본값).</summary>
            public const string None = "none";
        }

        /// <summary>Unity GameLogType 이름(시나리오의 s.Logs) - ⚠ Unity Test/DebugManager.cs의 열거형 멤버 이름과 같아야 한다.</summary>
        public static class GameLogTypes
        {
            public const string LobbyClientDisconnected = "LobbyClientDisconnected";
            public const string LobbyStreamCancelled = "LobbyStreamCancelled";
        }

        /// <summary>명령 인자/이벤트 data에 오가는 값 중 하네스가 비교하는 것(Unity 열거형 이름이나 에이전트가 정한 문자열).</summary>
        public static class Values
        {
            // 오토체스 국면(AutoChessPhase)
            public const string PhasePlanning = "Planning";
            public const string PhasePvP = "PvP";

            // 로비 단계(LobbyState)
            public const string LobbyInLobby = "InLobby";

            // network.role의 role(HarnessWatchers.GetNetworkRole)
            public const string RoleNone = "none";
            public const string RoleServer = "server";
            public const string RoleHost = "host";
            public const string RoleClientConnecting = "client-connecting";
            public const string RoleClient = "client";

            // editor.play_mode의 state(PlayModeStateChange)
            public const string PlayModeEnteredEditMode = "EnteredEditMode";

            // editor.ready의 reason(HarnessEditorBridge)
            public const string ReadyStartup = "startup";
            public const string ReadyReloadCompile = "reload_compile";
            public const string ReadyReloadPlay = "reload_play";
            public const string ReadyNoChanges = "no-changes";

            // net.link_changed의 cause(NetHarnessCommands)
            public const string NetCauseLink = "link";
            public const string NetCauseLagSpikeStart = "lag_spike_start";
            public const string NetCauseLagSpikeEnd = "lag_spike_end";
            public const string NetCauseTransportRecreated = "transport_recreated";
            public const string NetCauseReapplied = "reapplied";

            // cheat.planning_duration의 mode(AutoChessGameMode.PlanningDurationChange)
            public const string PlanningCurrentOnly = "CurrentOnly";
            public const string PlanningRescale = "Rescale";
            public const string PlanningSettingOnly = "SettingOnly";

            // 기물 종류(AutoChessPieceType) - 시나리오가 사는 기물
            public const string PieceWarrior = "Warrior";
        }

        /// <summary>에이전트 명령 이름(&lt;영역&gt;.&lt;동작&gt;).</summary>
        public static class Commands
        {
            public const string CheatBuy = "cheat.buy";
            public const string CheatDamage = "cheat.damage";
            public const string CheatPlanningDuration = "cheat.planning_duration";
            public const string CheatPlayers = "cheat.players";
            public const string CheatSkipPlanning = "cheat.skip_planning";
            public const string CheatTimeScale = "cheat.time_scale";
            public const string ClientCancelMatch = "client.cancel_match";
            public const string ClientJoinMatch = "client.join_match";
            public const string ClientLogin = "client.login";
            public const string ClientState = "client.state";
            public const string EditorClearPlayStart = "editor.clear_play_start";
            public const string EditorCompileErrors = "editor.compile_errors";
            public const string EditorConsole = "editor.console";
            public const string EditorPlay = "editor.play";
            public const string EditorRefresh = "editor.refresh";
            public const string EditorScenes = "editor.scenes";
            public const string EditorSetPlayStart = "editor.set_play_start";
            public const string EditorStatus = "editor.status";
            public const string EditorStop = "editor.stop";
            public const string FaultAdd = "fault.add";
            public const string FaultList = "fault.list";
            public const string FaultMethods = "fault.methods";
            public const string FaultRemove = "fault.remove";
            public const string IngameRooms = "ingame.rooms";
            public const string LogFilter = "log.filter";
            public const string LogMark = "log.mark";
            public const string LogRecent = "log.recent";
            public const string LogState = "log.state";
            public const string NetCondition = "net.condition";
            public const string NetLagSpike = "net.lag_spike";
            public const string NetLink = "net.link";
            public const string NetState = "net.state";
            public const string ProcessInfo = "process.info";
            public const string ProcessStatus = "process.status";
            public const string SceneLoad = "scene.load";
            public const string ShopBuy = "shop.buy";
            public const string ShopReroll = "shop.reroll";
            public const string ShopSell = "shop.sell";
            public const string ShopState = "shop.state";
            public const string ShopUnits = "shop.units";
            public const string StatusReplication = "status.replication";
            public const string StatusScreen = "status.screen";
        }

        /// <summary>에이전트 이벤트 이름(&lt;영역&gt;.&lt;일&gt;). harness.*는 하네스가 접속/끊김/거부 때 남긴다.</summary>
        public static class Events
        {
            public const string ClientError = "client.error";
            public const string ClientIngameDisconnected = "client.ingame_disconnected";
            public const string ClientLobbyState = "client.lobby_state";
            public const string ClientLoginResult = "client.login_result";
            public const string ClientMatchFailed = "client.match_failed";
            public const string ClientMatchFound = "client.match_found";
            public const string EditorCompileFailed = "editor.compile_failed";
            public const string EditorPlayMode = "editor.play_mode";
            public const string EditorReady = "editor.ready";
            public const string FaultEnded = "fault.ended";
            public const string FaultHit = "fault.hit";
            public const string GoldChanged = "gold.changed";
            public const string HarnessAttached = "harness.attached";
            public const string HarnessDetached = "harness.detached";
            public const string HarnessRejected = "harness.rejected";
            public const string NetLinkChanged = "net.link_changed";
            public const string NetworkRole = "network.role";
            public const string PhaseChanged = "phase.changed";
            public const string PlayerHealthChanged = "player.health_changed";
            public const string RoomClosed = "room.closed";
            public const string RoomDrawingOpened = "room.drawing_opened";
            public const string RoomPromoted = "room.promoted";
            public const string SceneLoaded = "scene.loaded";
            public const string ServerLoad = "server.load";
            public const string ShopChanged = "shop.changed";
            public const string TestRole = "test.role";
        }
    }
}
#endif
