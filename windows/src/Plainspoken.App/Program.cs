using Plainspoken.App.Platform;
using Plainspoken.Core;
using Plainspoken.Core.Logging;

namespace Plainspoken.App;

internal static class Program
{
    // Per-user names so two Windows users on one PC can each run their own copy.
    private static readonly string MutexName = $@"Local\{AppInfo.Name}-SingleInstance-5f3c2b1e";
    private static readonly string ShowSettingsEventName = $@"Local\{AppInfo.Name}-ShowSettings-5f3c2b1e";

    private static FileLog? _log;
    private static TrayContext? _context;
    private static DateTime _lastErrorShown = DateTime.MinValue;

    [STAThread]
    private static int Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var firstInstance);
        if (!firstInstance)
        {
            // Already running: ask that copy to open its Settings window, then quit.
            try
            {
                NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
                using var signal = EventWaitHandle.OpenExisting(ShowSettingsEventName);
                signal.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // The other copy is still starting up; nothing to do.
            }

            return 0;
        }

        _log = new FileLog(AppPaths.LogsDir);
        _log.Info($"{AppInfo.Name} {AppInfo.Version} starting (Windows {Environment.OSVersion.Version}, .NET {Environment.Version})");

        // Global safety nets: log, show a friendly message, keep the tray app alive where possible.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => OnUnhandled("UI thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => _log.Error("fatal unhandled exception (background thread)", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _log.Error("unobserved task exception", e.Exception);
            e.SetObserved();
        };

        ApplicationConfiguration.Initialize();

        using var showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEventName);
        try
        {
            _context = new TrayContext(_log);
        }
        catch (Exception ex)
        {
            _log.Error("startup failed", ex);
            MessageBox.Show($"{AppInfo.Name} could not start. Details are in the log folder:\n{AppPaths.LogsDir}",
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        var sync = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        var wait = ThreadPool.RegisterWaitForSingleObject(showSettings,
            (_, _) => sync.Post(_ => _context?.ShowSettings(), null), null, Timeout.Infinite, executeOnlyOnce: false);
        try
        {
            Application.Run(_context);
        }
        finally
        {
            wait.Unregister(null);
            _context.Dispose();
            _log.Info("stopped");
        }

        GC.KeepAlive(mutex);
        return 0;
    }

    private static void OnUnhandled(string where, Exception ex)
    {
        _log?.Error($"unhandled exception on {where}", ex);
        if (DateTime.UtcNow - _lastErrorShown < TimeSpan.FromSeconds(10))
        {
            return;
        }

        _lastErrorShown = DateTime.UtcNow;
        try
        {
            _context?.ShowUnexpectedError();
        }
        catch (Exception inner)
        {
            _log?.Error("could not show error", inner);
        }
    }
}
