using System.Runtime.InteropServices;

namespace TestHarness.Core;

/// <summary>
/// 시나리오가 끝났을 때 하네스 PC에서 소리를 낸다(TH_ScenarioRunner가 부른다 - AI 지시가 아니라 하네스가 끝을 판단).
/// 사람이 다른 일을 하다가도 테스트가 끝난 걸 알게 하려고.
///
/// - 성공(전부 통과): Windows 기본 완료음(C:\Windows\Media\tada.wav), 그 외(실패/미확인/불일치/중단): 경고음
///   (Windows Exclamation.wav). 파일이 없으면 시스템 알림음(MessageBeep).
/// - 비동기로 재생하고 바로 돌아온다(진행을 막지 않음). Windows가 아니거나 재생에 실패하면 조용히 넘어간다 - 소리는 부가 기능이라
///   테스트 결과에 영향을 주면 안 된다.
/// - 추가 패키지 없이 winmm/user32를 직접 부른다(System.Media.SoundPlayer는 별도 NuGet 패키지가 필요).
/// </summary>
public static class FinishSound
{
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndFilename = 0x00020000;
    private const uint MbIconAsterisk = 0x00000040;
    private const uint MbIconExclamation = 0x00000030;

    public static void Play(bool success)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            var media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
            var file = Path.Combine(media, success ? "tada.wav" : "Windows Exclamation.wav");
            if (File.Exists(file) && PlaySound(file, IntPtr.Zero, SndFilename | SndAsync | SndNoDefault)) return;

            MessageBeep(success ? MbIconAsterisk : MbIconExclamation);
        }
        catch (Exception)
        {
            // DLL이 없거나(서버 코어 등) 재생 실패 - 소리 없이 넘어간다.
        }
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern bool PlaySound(string pszSound, IntPtr hmod, uint fdwSound);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern bool MessageBeep(uint uType);
}
