using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace WorkDot;

public partial class App : System.Windows.Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        TerminateOtherWorkDotInstances();
        base.OnStartup(e);
    }

    private static void TerminateOtherWorkDotInstances()
    {
        int currentPid = Environment.ProcessId;
        string currentName = Process.GetCurrentProcess().ProcessName;

        foreach (Process proc in Process.GetProcessesByName(currentName))
        {
            if (proc.Id == currentPid)
                continue;

            try
            {
                proc.Kill();
                proc.WaitForExit(1500);
            }
            catch
            {
                // Ignore already-exited or inaccessible processes
            }
            finally
            {
                proc.Dispose();
            }
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            string logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WorkDot");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(
                Path.Combine(logDir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\n\n");
        }
        catch
        {
            // Ignore logging error
        }

        e.Handled = true;
    }
}
