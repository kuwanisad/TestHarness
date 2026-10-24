using System.Text;

namespace TestHarness.Core;

/// <summary>
/// 실행 폴더에 기록하는 파일 쓰기 공통. 줄마다 열고 닫는다 - 실행이 열려 있어도 하네스가 붙잡고 있는 파일이 없게(Windows에서 실행 폴더를
/// 지우거나 옮길 수 있게). 읽는 쪽(LogStore 등)과 동시에 열 수 있도록 공유 모드로 연다.
/// </summary>
public static class HarnessFiles
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>한 줄을 덧붙인다. 실패하면 false - 기록 실패가 부른 쪽(수신 루프, 판정)까지 막으면 안 되므로 던지지 않는다.</summary>
    public static bool AppendLine(string path, string line)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            using var writer = new StreamWriter(stream, Utf8NoBom);
            writer.WriteLine(line);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
