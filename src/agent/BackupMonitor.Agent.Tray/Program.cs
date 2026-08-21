using System.Threading;
using System.Windows.Forms;

namespace BackupMonitor.Agent.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "Global\\BackupMonitor.Agent.Tray", out var created);
        if (!created)
            return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext(args));
    }
}
