using System.Text;

namespace Azki.Core.Diagnostics;

/// <summary>Last lines of a log file that another process may still be writing.</summary>
public static class LogTail
{
    /// <summary>
    /// Up to <paramref name="lines"/> trailing lines (at most <paramref name="maximumBytes"/> read from the
    /// end), or null when the file does not exist or cannot be read.
    /// </summary>
    public static string? Read(string path, int lines = 40, int maximumBytes = 64 * 1024)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 0);
            var length = stream.Length;
            var count = (int)Math.Min(length, maximumBytes);
            stream.Seek(length - count, SeekOrigin.Begin);
            var buffer = new byte[count];
            stream.ReadExactly(buffer);
            var text = Encoding.UTF8.GetString(buffer).Replace("\r\n", "\n", StringComparison.Ordinal);
            var all = text.TrimEnd('\n').Split('\n');
            // A partial first line (cut by maximumBytes) is dropped.
            var start = count < length ? 1 : 0;
            return string.Join('\n', all.Skip(Math.Max(start, all.Length - lines)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The last non-empty lines joined for a one-line error message ("" when there are none).</summary>
    public static string Brief(string path, int lines = 3)
    {
        var tail = Read(path, lines, 8 * 1024);
        return string.IsNullOrWhiteSpace(tail) ? "" : string.Join(" | ", tail.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));
    }
}
