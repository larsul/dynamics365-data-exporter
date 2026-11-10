using System.Runtime.InteropServices;
using DataverseToSql.Sync;

namespace DataverseToSql;

/// <summary>Application entry point: WinForms UI, or headless sync with <c>--run [settingsPath]</c>.</summary>
internal static class Program
{
    private const string SecretEnvVar = "DATAVERSE_CLIENT_SECRET";

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    /// <summary>The main entry point for the application.</summary>
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "--run", StringComparison.OrdinalIgnoreCase))
        {
            return RunHeadless(args.Length > 1 ? args[1] : null);
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    /// <summary>Runs one sync without UI (for Task Scheduler etc.). Returns 0 on success, 1 on failure.</summary>
    private static int RunHeadless(string? settingsPath)
    {
        AttachConsole(-1); // ATTACH_PARENT_PROCESS: show output when started from a console.
        AppLog.Message += line =>
        {
            try { Console.WriteLine(line); } catch { /* console may be unavailable */ }
        };

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            AppLog.Info("Cancellation requested (Ctrl+C).");
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        };

        try
        {
            var path = string.IsNullOrWhiteSpace(settingsPath) ? null : settingsPath;
            AppLog.Info($"Headless run. Settings: {path ?? AppSettings.DefaultPath}");
            var settings = AppSettings.Load(path);

            // STD-INT-006: the secret only comes from the environment, never from settings or args.
            var secret = Environment.GetEnvironmentVariable(SecretEnvVar);

            var result = new SyncEngine { NonInteractive = true }.RunAsync(settings, secret, progress: null, cts.Token).GetAwaiter().GetResult();
            AppLog.Info($"Headless run finished: {result.RowsWritten:N0} rows written, {result.RowsDeleted:N0} deleted, " +
                        $"elapsed {result.Elapsed:hh\\:mm\\:ss}, correlation id {result.CorrelationId}.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            AppLog.Error("Headless run cancelled.");
            return 1;
        }
        catch (Exception ex)
        {
            AppLog.Error("Headless run failed.", ex);
            return 1;
        }
    }
}
