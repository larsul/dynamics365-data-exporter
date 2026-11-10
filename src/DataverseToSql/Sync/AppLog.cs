using System.Globalization;
using System.Text;

namespace DataverseToSql.Sync;

/// <summary>Thread-safe file logger with a live message event. Never logs secrets (STD-INT-006).</summary>
public static class AppLog
{
    private static readonly object Gate = new();

    /// <summary>Log folder: %LOCALAPPDATA%\DataverseToSql\logs.</summary>
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DataverseToSql", "logs");

    /// <summary>Raised for every formatted log line (may be on any thread).</summary>
    public static event Action<string>? Message;

    /// <summary>Writes an informational line.</summary>
    public static void Info(string msg) => Write("INFO ", msg, null);

    /// <summary>Writes an error line, optionally with exception details.</summary>
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", msg, ex);

    private static void Write(string level, string? msg, Exception? ex)
    {
        string line;
        try
        {
            var now = DateTime.Now;
            var sb = new StringBuilder();
            sb.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
              .Append(' ').Append(level).Append(' ').Append(msg ?? "");
            if (ex is not null)
                sb.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(ex.Message)
                  .AppendLine().Append(ex);
            line = sb.ToString();

            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                var file = Path.Combine(LogDirectory,
                    "sync-" + now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
                File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            line = $"{level} {msg}";
        }

        try
        {
            Message?.Invoke(line);
        }
        catch
        {
            // Subscriber failures must never break the caller.
        }
    }
}
