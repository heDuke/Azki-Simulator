using Azki.Core.Diagnostics;
using Azki.Core.Runtime;
using Azki.Core.Session;

namespace Azki.Core.Tests;

public sealed class DiagnosticsTests
{
    [Fact]
    public void TailKeepsTheLastLinesOfAGrowingLog()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "serve.log");
        File.WriteAllLines(path, Enumerable.Range(1, 100).Select(i => $"line {i}"));
        using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var tail = LogTail.Read(path, 40)!.Split('\n');
        Assert.Equal(40, tail.Length);
        Assert.Equal("line 61", tail[0]);
        Assert.Equal("line 100", tail[^1]);
        Assert.Equal("line 99 | line 100", LogTail.Brief(path, 2));
        Assert.Null(LogTail.Read(Path.Combine(directory.Path, "missing.log")));
        Assert.Equal("", LogTail.Brief(Path.Combine(directory.Path, "missing.log")));
    }

    [Fact]
    public void TailDropsALineCutByTheByteLimit()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "qemu.log");
        File.WriteAllText(path, new string('x', 5000) + "\nlast\n");
        Assert.Equal("last", LogTail.Read(path, 40, maximumBytes: 100));
    }

    [Fact]
    public void ReportHasTheErrorFoldersAndLogTails()
    {
        using var directory = new TemporaryDirectory();
        var root = directory.Path;
        var session = Path.Combine(root, "session");
        var run = Path.Combine(session, "m-0123456789ab");
        Directory.CreateDirectory(run);
        File.WriteAllLines(Path.Combine(root, "serve.log"), Enumerable.Range(1, 60).Select(i => $"serve {i}"));
        File.WriteAllLines(Path.Combine(run, "qemu.log"), Enumerable.Range(1, 60).Select(i => $"qemu {i}"));
        File.WriteAllText(Path.Combine(run, "adapter-runner.log"), "Traceback: TimeoutError: timed out\n");
        var status = SessionStatus.From(System.Text.Json.Nodes.JsonNode.Parse("""{"state": "crashed", "error": "host_error"}"""));
        var report = SessionDiagnostics.Build("启动失败（发送 start 命令）：x", "Ready",
            new PreparedSession(root, session, "fake.p67", null), status, null, "D:\\data");
        Assert.Contains("启动失败（发送 start 命令）：x", report);
        Assert.Contains("会话根目录：" + root, report);
        Assert.Contains("会话目录：" + session, report);
        Assert.Contains("最新运行目录：" + run, report);
        Assert.Contains("serve 60", report);
        Assert.Contains("serve 21", report);
        Assert.DoesNotContain("serve 20\n", report.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Contains("qemu 60", report);
        Assert.Contains("TimeoutError: timed out", report);
        Assert.Contains("\"host_error\"", report);
        Assert.Contains("stderr.log", report);  // listed as missing
        Assert.Equal(run, SessionDiagnostics.LatestRun(session));
    }
}
