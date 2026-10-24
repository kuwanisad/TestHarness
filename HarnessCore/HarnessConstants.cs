namespace TestHarness.Core;

/// <summary>
/// 하네스의 상수를 한곳에 모은다 - 문구 형식, 명령/인자 이름처럼 여러 곳이 같은 값을 써야 하는 것. 새 상수는 여기에 넣는다(코드에 직접
/// 적지 않는다). 기존 코드에 흩어진 상수는 코드 정리 때 옮긴다(TODO.md "진행 중").
/// </summary>
public static class HarnessConstants
{
    /// <summary>
    /// 노드 경계 줄 - 하네스가 노드(최상위 방문과 정리 노드)를 시작하고 끝낼 때 Unity 에이전트 로그 파일(logs/&lt;에이전트&gt;.log -
    /// AgentLogWriter가 쓰는 파일)에 넣는다. 서버 로그 파일은 DevTools가 서버 출력을 받아 쓰고 있어 넣지 않는다(파일 잠금 충돌).
    /// </summary>
    public static class NodeMarker
    {
        /// <summary>경계 줄의 분류(Category).</summary>
        public const string Category = "Harness";

        /// <summary>{0} 노드 경로, {1} 방문 번호(정리 노드는 "정리").</summary>
        public const string StartFormat = "===== 노드 시작: {0} (방문 {1}) =====";

        /// <summary>{0} 노드 경로, {1} 방문 번호, {2} 판정.</summary>
        public const string EndFormat = "===== 노드 끝: {0} (방문 {1}) {2} =====";

        /// <summary>정리 노드의 방문 번호 자리.</summary>
        public const string CleanupVisit = "정리";
    }

    /// <summary>
    /// 게임 코드 상황 로그 목록(Unity DebugManager) - 시나리오의 s.Logs를 러너가 Unity 에이전트에 보낸다(시작, 대상 접속, 끝).
    /// 명령/인자 이름은 Unity 쪽 BuiltInCommands의 log.filter와 같아야 한다.
    /// </summary>
    public static class LogFilter
    {
        public const string Command = "log.filter";
        public const string CollectArg = "collect";
        public const string ConsoleArg = "console";

        /// <summary>목록 구분자(Unity log.filter가 쉼표로 나눈다).</summary>
        public const string Separator = ",";

        /// <summary>빈 목록(하나도 없음) - 빈 문자열은 전부(기본값)라서 따로 둔다. Unity BuiltInCommands의 NoLogTypes와 같아야 한다.</summary>
        public const string None = "none";
    }

    /// <summary>Unity GameLogType 이름(시나리오의 s.Logs에 쓴다) - ⚠ Unity Test/DebugManager.cs의 열거형 멤버 이름과 같아야 한다.</summary>
    public static class GameLogTypes
    {
        public const string LobbyClientDisconnected = "LobbyClientDisconnected";
        public const string LobbyStreamCancelled = "LobbyStreamCancelled";
    }

    /// <summary>에이전트 이벤트 이름 중 하네스 코어가 직접 보는 것.</summary>
    public static class AgentEvents
    {
        public const string Attached = "harness.attached";

        /// <summary>타임라인에서 에이전트가 낸 이벤트의 출처 접두어(출처 = 접두어 + 에이전트 이름).</summary>
        public const string SourcePrefix = "agent:";
    }

    /// <summary>에이전트 명령 실패 문구 중 하네스 코어가 구분해야 하는 것 - Unity HarnessCommandRegistry / HarnessHost AgentSession과 같아야 한다.</summary>
    public static class AgentErrors
    {
        /// <summary>그 에이전트에 없는 명령(예: 서버/헤드리스에 log.filter).</summary>
        public const string UnknownCommand = "모르는 명령";
    }
}
