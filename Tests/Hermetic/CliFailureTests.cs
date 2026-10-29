using System.Diagnostics;
using Microsoft.Data.Sqlite;
using QsrPriceBenchmarks.Core.Db;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

/// <summary>
/// A failure the CLI's commands did not expect reaches the command line as a reason and exit code 1, never as an
/// unhandled exception with a stack trace (maintainer, 2026-10-29: "check whether there are any other unhandled
/// exceptions"). The real <c>qsr.exe</c> is run: the boundary is its <c>Main</c>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class CliFailureTests
{
    [Fact(DisplayName = "An export that cannot write its folder fails with a reason and exit code 1, not a crash")]
    public void FailedExport_IsReportedNotThrown()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // qsr.exe drives the SQLite that ships with Windows, the only platform it ships for
        }

        string dir = Path.Combine(Path.GetTempPath(), $"qsr_cli_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string db = Path.Combine(dir, Database.DefaultFileName);
            using (Database.Open(db))
            {
                // A current, empty database: the export itself is what fails.
            }
            SqliteConnection.ClearAllPools();
            File.WriteAllText(Path.Combine(dir, "output"), "a file where the export folder goes");

            (int exitCode, string output) = RunQsr("--db", db, "--chain", "burger-king", "--export");

            Assert.Equal(1, exitCode);
            Assert.Contains("error: ", output, StringComparison.Ordinal); // the reason itself is the OS's wording
            Assert.DoesNotContain("Unhandled exception", output, StringComparison.Ordinal);
            Assert.DoesNotContain("   at ", output, StringComparison.Ordinal); // no stack trace
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    private static (int ExitCode, string Output) RunQsr(params string[] args)
    {
        ProcessStartInfo start = new(Path.Combine(AppContext.BaseDirectory, "qsr.exe"))
        {
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
        if (!qsr.WaitForExit(60_000))
        {
            qsr.Kill(entireProcessTree: true);
            throw new TimeoutException("qsr did not exit within 60 s.");
        }
        return (qsr.ExitCode, stdout.Result + stderr.Result);
    }
}
