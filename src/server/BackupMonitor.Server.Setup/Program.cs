namespace BackupMonitor.Server.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new SetupForm(args));
        return 0;
    }
}
