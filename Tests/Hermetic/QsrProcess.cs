using System.Diagnostics;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

/// <summary>
/// Runs the real <c>qsr.exe</c> beside the test assembly, the way a command line does: arguments in, the given text
/// on standard input (then closed, so a prompt reads the end of input), and the exit code and everything it printed
/// out. Bounded at 60 s.
/// </summary>
internal static class QsrProcess
{
    public static (int ExitCode, string Output) Run(string input, params string[] args)
    {
        ProcessStartInfo start = new(Path.Combine(AppContext.BaseDirectory, "qsr.exe"))
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, // the CLI sets UTF-8 output for its glyphs
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using Process qsr = Process.Start(start)!;
        Task<string> stdout = qsr.StandardOutput.ReadToEndAsync();
        Task<string> stderr = qsr.StandardError.ReadToEndAsync();
        qsr.StandardInput.Write(input);
        qsr.StandardInput.Close();
        if (!qsr.WaitForExit(60_000))
        {
            qsr.Kill(entireProcessTree: true);
            throw new TimeoutException("qsr did not exit within 60 s.");
        }
        return (qsr.ExitCode, stdout.Result + stderr.Result);
    }
}
