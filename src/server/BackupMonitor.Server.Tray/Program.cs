namespace BackupMonitor.Server.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 自启对所有登录用户生效（HKLM\...\Run，见 R13），因此同一台机器上可能有
        // 多个用户会话各自起一份。互斥量用 Local\ 而不是 Global\：
        // 每个会话有自己的托盘区，Global 会让第二个登录的人干脆看不到图标——
        // 那正是「关掉窗口托盘仍在」想避免的那种消失。
        using var mutex = new Mutex(true, @"Local\BackupMonitor.Server.Tray", out var created);
        if (!created)
            return;

        ApplicationConfiguration.Initialize();
        Application.Run(new ServerTrayContext(args));
    }
}
