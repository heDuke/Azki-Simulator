using System.Globalization;
using System.Text;
using Azki.Core.Runtime;
using Azki.Core.Session;

namespace Azki.Core.Diagnostics;

/// <summary>
/// Plain-text report for "复制诊断信息": the error, the session folders and the tails of the logs
/// (serve.log, and for the newest run m-*: adapter-runner.log, stderr.log, qemu.log, result.json).
/// No firmware bytes: only text logs the engine itself wrote.
/// </summary>
public static class SessionDiagnostics
{
    public const int TailLines = 40;

    /// <summary>The newest run directory (<c>m-*</c>) of a session directory, or null.</summary>
    public static string? LatestRun(string? sessionDirectory)
    {
        if (sessionDirectory is null || !Directory.Exists(sessionDirectory))
        {
            return null;
        }
        try
        {
            return new DirectoryInfo(sessionDirectory).GetDirectories("m-*")
                .OrderByDescending(directory => directory.CreationTimeUtc).FirstOrDefault()?.FullName;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string Build(string? error, string? phase, PreparedSession? session, SessionStatus? status,
        EngineBundle? bundle, string? dataRoot)
    {
        var report = new StringBuilder();
        var now = DateTimeOffset.Now;
        report.AppendLine("Azki Simulator 诊断信息");
        report.AppendLine(CultureInfo.InvariantCulture, $"时间：{now:yyyy-MM-dd HH:mm:ss} (UTC{now:zzz})");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"系统：{Environment.OSVersion.VersionString}；{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}；进程 {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"App：{typeof(SessionDiagnostics).Assembly.GetName().Version}；目录 {AppContext.BaseDirectory}");
        report.AppendLine("引擎：" + (bundle?.Root ?? "未找到"));
        report.AppendLine("数据：" + (dataRoot ?? "-"));
        report.AppendLine("阶段：" + (phase ?? "-"));
        report.AppendLine();
        report.AppendLine("错误：");
        report.AppendLine(string.IsNullOrEmpty(error) ? "（无）" : error);
        report.AppendLine();
        report.AppendLine("会话根目录：" + (session?.Root ?? "-"));
        report.AppendLine("会话目录：" + (session?.SessionDirectory ?? "-"));
        report.AppendLine("机型：" + (session?.ProfileId ?? "-"));
        if (status?.Raw is { } raw)
        {
            report.AppendLine();
            report.AppendLine("最后一次状态（status）：");
            report.AppendLine(raw.ToJsonString());
        }
        if (session is not null)
        {
            Section(report, Path.Combine(session.Root, "serve.log"));
            var run = LatestRun(session.SessionDirectory);
            report.AppendLine();
            report.AppendLine("最新运行目录：" + (run ?? "（还没有运行目录 m-*）"));
            if (run is not null)
            {
                foreach (var name in new[] { "adapter-runner.log", "stderr.log", "qemu.log" })
                {
                    Section(report, Path.Combine(run, name));
                }
                Section(report, Path.Combine(run, "result.json"), lines: 60);
                report.AppendLine();
                report.AppendLine("运行目录文件：" + Listing(run));
            }
        }
        return report.ToString();
    }

    private static void Section(StringBuilder report, string path, int lines = TailLines)
    {
        report.AppendLine();
        var tail = LogTail.Read(path, lines);
        report.AppendLine(CultureInfo.InvariantCulture, $"===== {path}（最后 {lines} 行）=====");
        report.AppendLine(tail ?? "（文件不存在或无法读取）");
    }

    private static string Listing(string directory)
    {
        try
        {
            return string.Join("，", new DirectoryInfo(directory).GetFileSystemInfos().OrderBy(item => item.Name, StringComparer.Ordinal)
                .Select(item => item is FileInfo file ? $"{file.Name} {file.Length} B" : item.Name + "\\"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "（无法列出：" + error.Message + "）";
        }
    }
}
