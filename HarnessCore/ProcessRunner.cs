using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace TestHarness.Core;

/// <summary>외부 명령 한 번의 결과.</summary>
public sealed record ProcessResult(int ExitCode, bool TimedOut, string Output, TimeSpan Duration)
{
    /// <summary>출력의 마지막 lines줄(실패 원인을 응답에 짧게 붙일 때).</summary>
    public string Tail(int lines)
    {
        var all = Output.TrimEnd().Split('\n');
        return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)).Select(l => l.TrimEnd('\r')));
    }
}

/// <summary>
/// 외부 명령(PowerShell 스크립트, dotnet build)을 실행하고 출력을 파일(outputPath)로 받는다.
///
/// 출력을 파이프가 아니라 파일로 받는 이유: start-servers.ps1이 띄운 서버들과 dotnet build의 MSBuild
/// 노드/컴파일러 서버는 부모(PowerShell, dotnet)가 끝난 뒤에도 살아 있고, Windows에서는 부모의 표준 출력
/// 핸들을 물려받는다. 하네스가 파이프로 받으면 그 손자 프로세스들이 파이프를 계속 쥐고 있어서 "출력 끝"이
/// 오지 않고, 출력 끝까지 기다리는 Process.WaitForExit가 서버를 끌 때까지 멈춘다. 파일이면 누가 쥐고
/// 있든 상관없이 부모 프로세스가 끝나는 순간 끝난 것으로 본다.
///
/// 그래서 셸(Windows cmd.exe, 그 외 sh)의 리다이렉션(&gt; 파일 2&gt;&amp;1)으로 실행한다.
/// </summary>
public static class ProcessRunner
{
    static ProcessRunner()
    {
        // Windows 콘솔 프로그램은 리다이렉트된 출력을 콘솔 코드 페이지(한국어 Windows는 CP949)로 쓴다 - 읽을 때 필요.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        string outputPath,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        if (File.Exists(outputPath)) File.Delete(outputPath);

        var startInfo = BuildShellStartInfo(fileName, arguments, outputPath);
        startInfo.WorkingDirectory = workingDirectory;
        startInfo.UseShellExecute = false;
        // 창은 안 띄우지만 콘솔은 있다(CREATE_NO_WINDOW) - 콘솔 API를 쓰는 스크립트도 정상 동작.
        startInfo.CreateNoWindow = true;
        if (environment != null)
        {
            foreach (var (key, value) in environment) startInfo.Environment[key] = value;
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new HarnessException($"'{startInfo.FileName}' 실행 실패: {ex.Message}", "셸(cmd.exe/sh)을 실행할 수 있는지 확인", ex);
        }

        var timedOut = false;
        using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeoutCts.CancelAfter(timeout);
            try
            {
                // 표준 출력을 리다이렉트하지 않았으므로 프로세스가 끝나면 바로 돌아온다(위 클래스 설명).
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = !ct.IsCancellationRequested;
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                if (!timedOut) throw;
            }
        }

        stopwatch.Stop();

        var output = ReadOutput(outputPath);
        // 명령에 쓴 실행 파일 자체를 못 찾은 경우(셸이 9009/127로 끝남)를 알기 쉽게.
        if (!timedOut && (process.ExitCode == 9009 || process.ExitCode == 127) && output.Length < 400)
        {
            throw new HarnessException($"'{fileName}'을(를) 실행하지 못함(exit={process.ExitCode}): {output.Trim()}",
                "프로그램이 설치돼 있고 PATH에 있는지 확인(PowerShell이면 하네스의 --powershell 옵션으로 경로 지정 가능)");
        }

        if (timedOut) output += $"\n[하네스] {timeout.TotalSeconds:0}초 제한 시간 초과로 강제 종료함\n";
        return new ProcessResult(timedOut ? -1 : process.ExitCode, timedOut, output, stopwatch.Elapsed);
    }

    /// <summary>pid 프로세스가 살아 있는지.</summary>
    public static bool IsAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static ProcessStartInfo BuildShellStartInfo(string fileName, IReadOnlyList<string> arguments, string outputPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // cmd /s /c "<명령> > "파일" 2>&1" - /s는 바깥 따옴표 한 쌍만 벗기고 나머지는 그대로 둔다.
            var command = string.Join(' ', new[] { fileName }.Concat(arguments).Select(QuoteWindows));
            return new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/d /s /c \"{command} > {QuoteWindows(outputPath)} 2>&1\"",
            };
        }

        // sh -c 'out=$1; shift; exec "$@" > "$out" 2>&1' sh <파일> <명령> <인자...> - 인자를 셸이 다시 해석하지 않게.
        var startInfo = new ProcessStartInfo("/bin/sh");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("out=\"$1\"; shift; exec \"$@\" > \"$out\" 2>&1");
        startInfo.ArgumentList.Add("sh");
        startInfo.ArgumentList.Add(outputPath);
        startInfo.ArgumentList.Add(fileName);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    /// <summary>Windows 명령줄 인자 하나를 따옴표로 감싼다(CommandLineToArgvW 규칙: 따옴표 앞 역슬래시는 두 배).</summary>
    private static string QuoteWindows(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"', '&', '|', '<', '>', '^', '(', ')', ',' }) < 0) return argument;

        var builder = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                builder.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(c);
            }

            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
    }

    /// <summary>
    /// 출력 파일을 읽는다(아직 쥐고 있는 프로세스가 있어도 읽히게 공유 모드로). BOM이 있으면 그것대로, 없으면
    /// UTF-8로 읽어 보고 깨지면 Windows 콘솔 코드 페이지(OEM, 한국어 Windows는 949)로 읽는다.
    /// </summary>
    private static string ReadOutput(string path)
    {
        if (!File.Exists(path)) return "";

        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var memory = new MemoryStream())
        {
            stream.CopyTo(memory);
            bytes = memory.ToArray();
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            var codePage = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? CultureInfo.CurrentCulture.TextInfo.OEMCodePage : 28591;
            return Encoding.GetEncoding(codePage).GetString(bytes);
        }
    }
}
