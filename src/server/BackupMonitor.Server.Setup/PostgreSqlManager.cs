using System.Text;
using System.Text.RegularExpressions;
using System.ServiceProcess;
using Microsoft.Win32;
using BackupMonitor.Shared.Security;
using Npgsql;

namespace BackupMonitor.Server.Setup;

/// <summary>
/// pg_dump / pg_restore 的连接目标。口令单独作为字段而不是拼进连接串，是为了在调用处
/// 一眼看出它只经 PGPASSWORD 传递：子进程的命令行对本机所有用户可见（任务管理器、WMI），
/// 把口令写进 -W/--password 之类的参数等于公开它。
/// </summary>
internal sealed record PostgresEndpoint(
    string Host,
    int Port,
    string Database,
    string Username,
    string Password);

internal sealed class PostgreSqlManager
{
    public const string ServiceName = "BackupMonitor.PostgreSQL";

    public async Task InitializeAsync(
        string postgresRoot,
        string dataDirectory,
        int port,
        string postgresPassword,
        string applicationPassword,
        CancellationToken ct)
    {
        var bin = Path.Combine(postgresRoot, "bin");
        var initdb = Path.Combine(bin, "initdb.exe");
        var pgCtl = Path.Combine(bin, "pg_ctl.exe");
        if (!File.Exists(initdb) || !File.Exists(pgCtl))
            throw new FileNotFoundException(
                $"服务端安装目录缺少 PostgreSQL Windows 运行时：{postgresRoot}");

        // PostgreSQL launches restricted child processes on Windows. An
        // Administrators-group ACE alone is therefore insufficient even when
        // the installer itself is elevated: the restricted token needs the
        // concrete installation-user SID. SYSTEM remains present for the
        // registered Windows service.
        SecureFileSystem.CreateDirectory(
            dataDirectory,
            enforceAcl: true,
            includeCurrentUser: true);
        var initialized = ReadClusterVersion(dataDirectory) is not null;
        if (!initialized)
        {
            ArchiveIncompleteCluster(dataDirectory);
            SecureFileSystem.CreateDirectory(
                dataDirectory,
                enforceAcl: true,
                includeCurrentUser: true);
            var passwordFile = Path.Combine(Path.GetTempPath(), $"BackupMonitor-pg-{Guid.NewGuid():N}.txt");
            try
            {
                using (File.Create(passwordFile))
                {
                }
                // initdb is a child process of the elevated installer. Grant the
                // current token an explicit ACE in addition to SYSTEM and the
                // Administrators group; relying on the Administrators group alone
                // can leave the child token unable to read --pwfile under UAC.
                SecureFileSystem.ApplyFileAcl(
                    passwordFile,
                    enforceAcl: true,
                    includeCurrentUser: true);
                await File.WriteAllTextAsync(passwordFile, postgresPassword + Environment.NewLine, Encoding.UTF8, ct);
                await ProcessRunner.RunAsync(initdb,
                    ["-D", dataDirectory, "-U", "postgres", "--pwfile", passwordFile,
                     "--auth=scram-sha-256", "--encoding", "UTF8", "--locale", "C"],
                    ct);
            }
            finally
            {
                TryDelete(passwordFile);
            }
        }

        Configure(dataDirectory, port);
        await EnsureServiceRegistrationAsync(pgCtl, dataDirectory, port, ct);

        // A prior failed installer may have left PostgreSQL running directly
        // from a temporary payload even though no service owns that process.
        // Stop only that orphaned/manual instance, then start the persistent
        // service so the process and its executable survive logoff/reboot.
        if (await IsServerRunningAsync(pgCtl, dataDirectory, ct)
            && !IsServiceRunning(ServiceName))
        {
            await ProcessRunner.RunAsync(pgCtl,
                ["stop", "-D", dataDirectory, "-m", "fast", "-w"],
                ct);
        }

        if (!IsServiceRunning(ServiceName))
        {
            await ProcessRunner.RunAsync(
                Path.Combine(Environment.SystemDirectory, "sc.exe"),
                ["start", ServiceName],
                ct);
        }

        await WaitForConnectionAsync(port, postgresPassword, ct);
        await EnsureDatabaseAndRoleAsync(port, postgresPassword, applicationPassword, ct);
    }

    /// <summary>
    /// 把整个业务库导出成 -Fc 自定义格式，供配置备份包和恢复前的现场快照使用。
    ///
    /// --no-owner / --no-privileges：换机恢复时目标集群的角色是新装出来的，转储里带上源机器的
    /// 角色名只会让 restore 报一片 "role does not exist"；业务库里所有对象都归 backup_monitor_app，
    /// 由谁执行 restore 就归谁，反而正是我们要的。
    /// --no-password：无人值守场景下绝不能弹交互式口令提示——那会让子进程挂在那里等到超时。
    /// </summary>
    public async Task DumpAsync(
        string postgresRoot,
        PostgresEndpoint endpoint,
        string outputPath,
        CancellationToken ct)
    {
        var pgDump = Path.Combine(postgresRoot, "bin", "pg_dump.exe");
        if (!File.Exists(pgDump))
            throw new FileNotFoundException(
                $"服务端安装目录缺少 pg_dump.exe：{postgresRoot}", pgDump);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await ProcessRunner.RunAsync(
            pgDump,
            ["--host", endpoint.Host,
             "--port", endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
             "--username", endpoint.Username,
             "--dbname", endpoint.Database,
             "--format", "custom",
             "--no-owner",
             "--no-privileges",
             "--no-password",
             "--file", outputPath],
            ct,
            BuildPasswordEnvironment(endpoint));
    }

    /// <summary>
    /// 从 -Fc 转储恢复业务库。三个开关都是必需的：
    /// --clean --if-exists 先删掉目标库里新装出来的空表，否则恢复过程全是 "already exists"；
    /// --single-transaction 让恢复要么整体成功、要么一行不改——半个库比不恢复更难收拾，
    /// 有了它，导入失败时只需回滚密钥文件，数据库自身不需要靠 pre-restore 快照兜底。
    /// </summary>
    public async Task RestoreAsync(
        string postgresRoot,
        PostgresEndpoint endpoint,
        string dumpPath,
        CancellationToken ct)
    {
        var pgRestore = Path.Combine(postgresRoot, "bin", "pg_restore.exe");
        if (!File.Exists(pgRestore))
            throw new FileNotFoundException(
                $"服务端安装目录缺少 pg_restore.exe：{postgresRoot}", pgRestore);

        await ProcessRunner.RunAsync(
            pgRestore,
            ["--host", endpoint.Host,
             "--port", endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
             "--username", endpoint.Username,
             "--dbname", endpoint.Database,
             "--clean",
             "--if-exists",
             "--no-owner",
             "--no-privileges",
             "--single-transaction",
             "--no-password",
             dumpPath],
            ct,
            BuildPasswordEnvironment(endpoint));
    }

    /// <summary>
    /// 试探某个超级用户口令能否连上本机集群。
    ///
    /// 导入配置备份包时必须先弄清「现在的口令是哪一个」：备份包里的 secrets 是源机器的，
    /// 本机集群仍然认自己 initdb 时生成的那一套，两者必然不同。认证以外的失败（服务没起来、
    /// 端口不通）照常抛出，不能被当成「口令不对」吞掉。
    /// </summary>
    public async Task<bool> CanAuthenticateSuperuserAsync(int port, string password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(password))
            return false;

        try
        {
            await using var connection = new NpgsqlConnection(
                BuildConnectionString(port, "postgres", password, "postgres"));
            await connection.OpenAsync(ct);
            return true;
        }
        catch (PostgresException ex) when (IsAuthenticationFailure(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// 把本机集群的超级用户口令和应用角色口令改成给定的一套。
    ///
    /// 导入配置备份包时不做这一步，等于「装了一把别人家的钥匙」：server-secrets.json 换成了
    /// 源机器的，集群却仍认本机 initdb 生成的旧口令，服务端一启动就连不上库；更麻烦的是下次
    /// 运行安装器会撞上 ExistingClusterPasswordMismatch，现象看起来像备份包坏了。
    /// 角色口令是集群级对象，不在单库转储里，只能这样单独对齐。
    /// </summary>
    public async Task SetRolePasswordsAsync(
        int port,
        string superuserPassword,
        string newSuperuserPassword,
        string newApplicationPassword,
        CancellationToken ct)
    {
        await using var root = new NpgsqlConnection(
            BuildConnectionString(port, "postgres", superuserPassword, "postgres"));
        await OpenSuperuserConnectionAsync(root, ct);

        var superuserLiteral = await QuoteLiteralAsync(root, newSuperuserPassword, ct);
        await using (var superuser = new NpgsqlCommand(
            $"ALTER ROLE postgres WITH PASSWORD {superuserLiteral}", root))
            await superuser.ExecuteNonQueryAsync(ct);

        await EnsureApplicationRoleAsync(root, newApplicationPassword, ct);
    }

    private static IReadOnlyDictionary<string, string?> BuildPasswordEnvironment(PostgresEndpoint endpoint) =>
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PGPASSWORD"] = endpoint.Password
        };

    private static void Configure(string dataDirectory, int port)
    {
        var postgresql = Path.Combine(dataDirectory, "postgresql.conf");
        SetConfig(postgresql, "listen_addresses", "'127.0.0.1'");
        SetConfig(postgresql, "port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        SetConfig(postgresql, "password_encryption", "'scram-sha-256'");

        var hba = Path.Combine(dataDirectory, "pg_hba.conf");
        File.WriteAllText(hba,
            "# BackupMonitor private PostgreSQL instance\n"
            + "local all all scram-sha-256\n"
            + "host all all 127.0.0.1/32 scram-sha-256\n"
            + "host all all ::1/128 scram-sha-256\n",
            new UTF8Encoding(false));
    }

    private static void SetConfig(string path, string key, string value)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var pattern = new Regex($"^\\s*#?\\s*{Regex.Escape(key)}\\s*=", RegexOptions.CultureInvariant);
        var index = lines.FindIndex(line => pattern.IsMatch(line));
        var replacement = $"{key} = {value}";
        if (index >= 0)
            lines[index] = replacement;
        else
            lines.Add(replacement);
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }

    private static string? ReadClusterVersion(string dataDirectory)
    {
        var versionPath = Path.Combine(dataDirectory, "PG_VERSION");
        try
        {
            using var stream = new FileStream(
                versionPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: true);
            var version = reader.ReadToEnd().Trim();
            if (string.IsNullOrWhiteSpace(version))
                throw new InvalidOperationException($"PostgreSQL 集群版本文件为空：{versionPath}");
            return version;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException(
                $"无法读取 PostgreSQL 集群版本文件：{versionPath}。安装器不会把权限错误误判为未初始化。",
                ex);
        }
    }

    private static void ArchiveIncompleteCluster(string dataDirectory)
    {
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(dataDirectory);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException(
                $"无法检查 PostgreSQL 数据目录：{dataDirectory}",
                ex);
        }

        if (entries.Length == 0)
            return;

        var parent = Path.GetDirectoryName(dataDirectory)
            ?? throw new InvalidOperationException($"PostgreSQL 数据目录没有父目录：{dataDirectory}");
        var archive = Path.Combine(
            parent,
            $"data.incomplete-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
        Directory.Move(dataDirectory, archive);
    }

    private static async Task<bool> IsServerRunningAsync(
        string pgCtl,
        string dataDirectory,
        CancellationToken ct)
    {
        var status = await ProcessRunner.RunAsync(
            pgCtl,
            ["status", "-D", dataDirectory],
            ct,
            throwOnError: false);
        return status.ExitCode == 0;
    }

    private static bool IsAuthenticationFailure(PostgresException ex) =>
        string.Equals(ex.SqlState, PostgresErrorCodes.InvalidPassword, StringComparison.Ordinal)
        || string.Equals(ex.SqlState, PostgresErrorCodes.InvalidAuthorizationSpecification, StringComparison.Ordinal);

    private static InvalidOperationException ExistingClusterPasswordMismatch(PostgresException ex) =>
        new(
            "现有 BackupMonitor PostgreSQL 集群正在运行，但 server-secrets.json 中的超级用户口令无法认证。安装器为保护已有数据不会重新执行 initdb；请使用同一安装实例的密钥包恢复口令。",
            ex);

    private static async Task OpenSuperuserConnectionAsync(
        NpgsqlConnection connection,
        CancellationToken ct)
    {
        try
        {
            await connection.OpenAsync(ct);
        }
        catch (PostgresException ex) when (IsAuthenticationFailure(ex))
        {
            throw ExistingClusterPasswordMismatch(ex);
        }
    }

    private static async Task WaitForConnectionAsync(int port, string password, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var connection = new NpgsqlConnection(BuildConnectionString(port, "postgres", password, "postgres"));
                await OpenSuperuserConnectionAsync(connection, ct);
                return;
            }
            catch (NpgsqlException) when (!ct.IsCancellationRequested)
            {
                await Task.Delay(250, ct);
            }
        }

        throw new System.TimeoutException("BackupMonitor 专用 PostgreSQL 未能在 30 秒内就绪。");
    }

    private static async Task EnsureDatabaseAndRoleAsync(
        int port,
        string postgresPassword,
        string applicationPassword,
        CancellationToken ct)
    {
        await using var root = new NpgsqlConnection(BuildConnectionString(port, "postgres", postgresPassword, "postgres"));
        await OpenSuperuserConnectionAsync(root, ct);

        await using (var extension = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS pgcrypto", root))
            await extension.ExecuteNonQueryAsync(ct);

        await EnsureApplicationRoleAsync(root, applicationPassword, ct);

        await using (var databaseExists = new NpgsqlCommand(
            "SELECT 1 FROM pg_database WHERE datname = 'backup_monitor'", root))
        {
            if (await databaseExists.ExecuteScalarAsync(ct) is null)
            {
                await using var create = new NpgsqlCommand(
                    "CREATE DATABASE backup_monitor OWNER backup_monitor_app", root);
                await create.ExecuteNonQueryAsync(ct);
            }
        }

        await using var database = new NpgsqlConnection(BuildConnectionString(port, "backup_monitor_app", applicationPassword, "backup_monitor"));
        await database.OpenAsync(ct);
        await using var schema = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS pgcrypto", database);
        await schema.ExecuteNonQueryAsync(ct);

        await using var revoke = new NpgsqlCommand(
            "REVOKE ALL ON DATABASE backup_monitor FROM PUBLIC; GRANT CONNECT ON DATABASE backup_monitor TO backup_monitor_app",
            root);
        await revoke.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 保证 backup_monitor_app 角色存在，并把它的口令设成给定的一套。
    ///
    /// 这段逻辑首装（EnsureDatabaseAndRoleAsync）和导入配置备份包对齐口令（SetRolePasswordsAsync）
    /// 都要跑，只能有一份：两处各写一遍，漏改其中一处的后果不是编译错误而是**静默的口令不一致**——
    /// 集群里的角色口令和 server-secrets.json 对不上，要等服务端下次启动连库才暴露。
    ///
    /// 为什么不用 <c>DO $$ … $$</c> 包一个 IF NOT EXISTS：PostgreSQL 没有 CREATE ROLE IF NOT EXISTS，
    /// DO 块原本就是为补这个缺口而写的，但它和口令字面量是致命组合。quote_literal() 只负责转义单引号，
    /// 返回的是 <c>'ab$$cd'</c> 这样的普通字面量；把它塞进美元引用块里，口令中只要含 <c>$$</c>
    /// 就会**提前闭合美元引用块**，轻则语法错误，重则从块外注入 SQL——而这条命令是以 postgres
    /// 超级用户身份执行的。自动生成的口令是 Base64（字母表里没有 <c>$</c>）所以正常路径安全，
    /// 但这里的入参也可能来自管理员导入的 .bmbp 备份包 secrets，或 BACKUPMONITOR_POSTGRES_PASSWORD
    /// 环境变量，内容完全不受控。改成「先查存在、再发一条 CREATE 或 ALTER」，全程不出现美元引用。
    ///
    /// 存在性判断走查询参数而不是拼字符串：角色名眼下是常量，但留一个「这里可以拼」的样板，
    /// 下一个人就会照着把变量拼进去。
    ///
    /// 口令这一处只能用 quote_literal()：CREATE/ALTER ROLE 的 PASSWORD 子句是工具命令语法，
    /// 不接受参数占位符（@p / $1），传参数会直接报语法错误——不是没想到参数化，是用不了。
    /// </summary>
    private static async Task EnsureApplicationRoleAsync(
        NpgsqlConnection root,
        string applicationPassword,
        CancellationToken ct)
    {
        bool exists;
        await using (var probe = new NpgsqlCommand("SELECT 1 FROM pg_roles WHERE rolname = @name", root))
        {
            probe.Parameters.AddWithValue("name", "backup_monitor_app");
            exists = await probe.ExecuteScalarAsync(ct) is not null;
        }

        var passwordLiteral = await QuoteLiteralAsync(root, applicationPassword, ct);
        var sql = exists
            ? $"ALTER ROLE backup_monitor_app WITH LOGIN PASSWORD {passwordLiteral}"
            : $"CREATE ROLE backup_monitor_app LOGIN PASSWORD {passwordLiteral}";

        await using var command = new NpgsqlCommand(sql, root);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string> QuoteLiteralAsync(NpgsqlConnection connection, string value, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT quote_literal(@value)", connection);
        command.Parameters.AddWithValue("value", value);
        return (string)(await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("PostgreSQL 无法生成安全的角色口令字面量。"));
    }

    private static async Task EnsureServiceRegistrationAsync(
        string pgCtl,
        string dataDirectory,
        int port,
        CancellationToken ct)
    {
        if (ServiceExists(ServiceName) && !ServiceRegistrationMatches(pgCtl))
        {
            await ProcessRunner.RunAsync(
                Path.Combine(Environment.SystemDirectory, "sc.exe"),
                ["stop", ServiceName],
                ct,
                throwOnError: false);
            await WaitForServiceStoppedAsync(ct);
            await ProcessRunner.RunAsync(
                pgCtl,
                ["unregister", "-N", ServiceName],
                ct);
            await WaitForServiceDeletedAsync(ct);
        }

        if (!ServiceExists(ServiceName))
        {
            await ProcessRunner.RunAsync(pgCtl,
                ["register", "-N", ServiceName, "-D", dataDirectory, "-S", "auto",
                 "-w", "-o", $"-p {port} -h 127.0.0.1"],
                ct);
        }
    }

    private static bool ServiceRegistrationMatches(string pgCtl)
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
        var imagePath = key?.GetValue("ImagePath") as string;
        if (string.IsNullOrWhiteSpace(imagePath))
            return false;

        return imagePath.Contains(
            Path.GetFullPath(pgCtl),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsServiceRunning(string name)
    {
        try
        {
            using var service = new ServiceController(name);
            return service.Status is ServiceControllerStatus.Running
                or ServiceControllerStatus.StartPending;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task WaitForServiceStoppedAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (!ServiceExists(ServiceName) || !IsServiceRunning(ServiceName))
                return;
            await Task.Delay(250, ct);
        }

        throw new System.TimeoutException($"Windows 服务 {ServiceName} 未能在 30 秒内停止。");
    }

    private static async Task WaitForServiceDeletedAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (!ServiceExists(ServiceName))
                return;
            await Task.Delay(200, ct);
        }

        throw new System.TimeoutException($"Windows 服务 {ServiceName} 未能在 15 秒内删除旧注册。");
    }

    internal static bool ServiceExists(string name)
    {
        try
        {
            using var service = new ServiceController(name);
            _ = service.Status;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string BuildConnectionString(int port, string user, string password, string database) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = port,
            Username = user,
            Password = password,
            Database = database,
            Timeout = 10,
            CommandTimeout = 120,
            Pooling = false
        }.ConnectionString;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 安装失败时不覆盖主异常；临时文件由系统清理。
        }
    }
}
