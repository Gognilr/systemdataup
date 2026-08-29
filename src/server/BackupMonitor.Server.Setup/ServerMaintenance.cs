using System.Net.Http;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.ServiceProcess;
using System.Text.Json;
using BackupMonitor.Api.Bootstrap;
using BackupMonitor.Infrastructure.Common;
using BackupMonitor.Shared.Security;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace BackupMonitor.Server.Setup;

internal sealed class ServerMaintenance
{
    public const string ServerServiceName = "BackupMonitor.Server";
    public const string PostgreSqlServiceName = "BackupMonitor.PostgreSQL";

    public static string DefaultInstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "BackupMonitor",
        "Server");

    public static string DefaultDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "BackupMonitor",
        "Server");

    public static string DefaultPostgreSqlDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "BackupMonitor",
        "PostgreSQL");

    /// <summary>
    /// 配置备份包 (.bmbp) 的内部布局。这些名字发布之后就是对外契约：包是跨机器、跨版本传递的，
    /// 改名等于让旧包再也导不进来。版本号单独放在 manifest 里，好让不认识新格式的安装器
    /// 给出「格式版本不匹配」这种能看懂的话，而不是一串「缺少某某文件」。
    /// </summary>
    private const int BackupPackageFormatVersion = 1;
    private const string BackupPackageManifestEntry = "manifest.json";
    private const string BackupPackageKeyDirectoryName = "keys";
    private const string BackupPackageKeyPrefix = BackupPackageKeyDirectoryName + "/";
    private const string BackupPackageDumpDirectoryName = "db";
    private const string BackupPackageDumpFileName = "backupmonitor.dump";
    private const string BackupPackageDumpEntry =
        BackupPackageDumpDirectoryName + "/" + BackupPackageDumpFileName;

    /// <summary>
    /// 迁移脚本落地的子目录名。它只存在于导入时的临时目录里，不是包内条目——
    /// 脚本来自本机安装包的 payload，不是备份包带来的，只要不和 keys/db 撞名即可。
    /// </summary>
    private const string BackupPackageMigrationDirectoryName = "migrations";

    /// <summary>恢复前自动快照的存放目录，放在数据目录下而不是 %TEMP%：那里已经是收紧过的 ACL。</summary>
    private const string PreRestoreDumpDirectoryName = "PreRestore";

    /// <summary>
    /// manifest 缩进输出，是为了让管理员能直接用记事本打开包里的 manifest.json 核对来源；
    /// 大小写不敏感则是给「包由更旧/更新版本写出」留的余地。
    /// </summary>
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public async Task ResetAdminPasswordAsync(
        string connectionString,
        string newPassword,
        CancellationToken ct)
    {
        // 走与自助改密同一套 PasswordPolicy。原来这里只判长度 ≥ 12，"aaaaaaaaaaaa"、
        // "123456789012" 这类口令在 Web 端会被拒、在安装器里却能设进去——而 PasswordPolicy
        // 的注释本身就写着「两端必须共用同一套规则」。
        var strengthError = PasswordPolicy.Validate(newPassword, "admin");
        if (strengthError is not null)
            throw new InvalidOperationException(strengthError);

        var hash = PasswordPolicy.Hash(newPassword);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var update = new NpgsqlCommand(
            "UPDATE users SET password_hash=@hash, password_changed_at=now(), must_change_password=false, updated_at=now() WHERE username='admin'",
            connection,
            transaction))
        {
            update.Parameters.AddWithValue("hash", hash);
            if (await update.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("找不到 admin 账户，请先完成服务端初始化。");
        }

        await using (var revoke = new NpgsqlCommand(
            "UPDATE refresh_tokens SET revoked_at=now(), revoke_reason='maintenance_password_reset' WHERE revoked_at IS NULL",
            connection,
            transaction))
            await revoke.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<string> ReissueServerCertificateAsync(
        string installDirectory,
        string dataDirectory,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var configPath = Path.Combine(installDirectory, "appsettings.Turnkey.json");
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(configPath, optional: false, reloadOnChange: false)
            .Build();
        var fingerprint = LocalServerBootstrap.ReissueServerCertificate(
            configuration,
            new LocalServerBootstrapOptions
            {
                Enabled = true,
                DataDirectory = dataDirectory,
                SecretsPath = Path.Combine(dataDirectory, "server-secrets.json"),
                DiscoveryEnabled = configuration.GetValue("LanMode:DiscoveryEnabled", true),
                DiscoveryPort = configuration.GetValue("LanMode:DiscoveryPort", 45808)
            });
        await RestartServicesAsync(ct);
        return fingerprint;
    }

    /// <summary>
    /// 读取当前服务端 TLS 证书指纹，供操作员带外核对——客户端安装器会显示同一个值，
    /// 由人比对两边是否一致，这是识破中间人的唯一手段（自签名证书没有公共 CA 背书）。
    ///
    /// 复用现有证书，不会重新签发：Initialize 在证书已存在时只读取，不生成新的。
    /// </summary>
    public string GetServerCertificateFingerprint(string installDirectory, string dataDirectory)
    {
        var configPath = Path.Combine(installDirectory, "appsettings.Turnkey.json");
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(configPath, optional: false, reloadOnChange: false)
            .Build();
        var bootstrap = LocalServerBootstrap.Initialize(
            configuration,
            new LocalServerBootstrapOptions
            {
                Enabled = true,
                DataDirectory = dataDirectory,
                SecretsPath = Path.Combine(dataDirectory, "server-secrets.json"),
                DiscoveryEnabled = configuration.GetValue("LanMode:DiscoveryEnabled", true),
                DiscoveryPort = configuration.GetValue("LanMode:DiscoveryPort", 45808)
            });

        return bootstrap.Values["Security:ServerCertificate:Fingerprint"]
            ?? throw new InvalidOperationException("未能读取服务端 TLS 证书指纹，请检查服务端证书配置。");
    }

    public string RepairPermissions(string dataDirectory)
    {
        var postgresDataDirectory = DefaultPostgreSqlDataDirectory;
        foreach (var directory in new[]
        {
            dataDirectory,
            Path.Combine(dataDirectory, "Repository"),
            Path.Combine(dataDirectory, "Staging"),
            postgresDataDirectory
        })
            SecureFileSystem.CreateDirectory(directory);

        foreach (var file in new[]
        {
            Path.Combine(dataDirectory, "server-secrets.json"),
            Path.Combine(dataDirectory, "client-ca.pfx"),
            Path.Combine(dataDirectory, "server-certificate.pfx")
        })
            SecureFileSystem.ApplyFileAcl(file);

        return "服务端数据目录、Repository、Staging、PostgreSQL data 以及现有密钥文件的 ACL 已重新收紧为 SYSTEM + Administrators。";
    }

    public async Task ExportKeyPackageAsync(
        string dataDirectory,
        string packagePath,
        CancellationToken ct)
    {
        var files = GetKeyPackageFiles(dataDirectory);
        EnsureKeyPackageFilesExist(files);

        var targetPath = Path.GetFullPath(packagePath);
        if (GetKeyPackageFiles(dataDirectory).Any(file =>
                string.Equals(file.Path, targetPath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("密钥包不能覆盖当前 server-secrets.json、client-ca.pfx 或 server-certificate.pfx。");
        var parent = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("密钥包目标路径无效。");
        Directory.CreateDirectory(parent);
        var tempPath = targetPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (File.Create(tempPath))
            {
            }
            SecureFileSystem.ApplyFileAcl(tempPath);
            await using (var output = new FileStream(
                tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1024 * 64, useAsync: true))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var (entryName, sourcePath) in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
                    await using var input = new FileStream(
                        sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 64, useAsync: true);
                    await using var entryStream = entry.Open();
                    await input.CopyToAsync(entryStream, ct);
                }
            }

            File.Move(tempPath, targetPath, true);
            SecureFileSystem.ApplyFileAcl(targetPath);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    public async Task ImportKeyPackageAsync(
        string dataDirectory,
        string packagePath,
        CancellationToken ct)
    {
        var sourcePath = Path.GetFullPath(packagePath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("找不到服务端密钥包。", sourcePath);

        var importDirectory = Path.Combine(dataDirectory, $".key-package-import-{Guid.NewGuid():N}");
        SecureFileSystem.CreateDirectory(importDirectory);
        try
        {
            await ExtractAndValidateKeyPackageAsync(sourcePath, importDirectory, ct);
            var files = GetKeyPackageFiles(importDirectory);
            var targets = GetKeyPackageFiles(dataDirectory);
            var backups = new List<(string Target, string Backup, bool Existed)>();
            try
            {
                foreach (var (_, target) in targets)
                {
                    var backup = target + $".{Environment.ProcessId}.{Guid.NewGuid():N}.import-backup";
                    var existed = File.Exists(target);
                    if (existed)
                    {
                        File.Copy(target, backup, true);
                        SecureFileSystem.ApplyFileAcl(backup);
                    }
                    backups.Add((target, backup, existed));
                }

                foreach (var (entryName, source) in files)
                {
                    var target = targets.First(item => item.EntryName == entryName).Path;
                    ReplaceProtectedFile(source, target);
                }
            }
            catch
            {
                foreach (var backup in backups)
                {
                    if (backup.Existed && File.Exists(backup.Backup))
                        ReplaceProtectedFile(backup.Backup, backup.Target);
                    else if (!backup.Existed && File.Exists(backup.Target))
                        TryDelete(backup.Target);
                }
                throw;
            }
            finally
            {
                foreach (var (_, backup, _) in backups)
                    TryDelete(backup);
            }
        }
        finally
        {
            TryDeleteDirectory(importDirectory);
        }

        await RestartServicesAsync(ct);
    }

    /// <summary>
    /// 导出「配置备份包」(.bmbp)：三个密钥文件 + pg_dump 的整库转储 + manifest。
    ///
    /// 与密钥包的差别只有一处，但很致命：客户端 mTLS 是拿证书指纹去 client_certificates 表
    /// 查客户端的。只还原密钥、不还原库，新装出来的空库里查不到记录，客户端每次心跳都是 401，
    /// 而且永远不会自愈——所以换机恢复必须密钥和库一起走。
    ///
    /// 包里不含 Repository / Staging 里的备份文件本体：那是几百 GB 的量级，不该塞进配置包。
    /// 恢复后 backup_files 会指向这些文件，仓库目录需要管理员另行复制。
    /// </summary>
    public async Task ExportBackupPackageAsync(
        string installDirectory,
        string dataDirectory,
        string packagePath,
        CancellationToken ct)
    {
        var keyFiles = GetKeyPackageFiles(dataDirectory);
        EnsureKeyPackageFilesExist(keyFiles);

        var targetPath = Path.GetFullPath(packagePath);
        if (keyFiles.Any(file =>
                string.Equals(file.Path, targetPath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("配置备份包不能覆盖当前 server-secrets.json、client-ca.pfx 或 server-certificate.pfx。");
        var parent = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("配置备份包目标路径无效。");
        Directory.CreateDirectory(parent);

        var secrets = await ReadSecretsAsync(Path.Combine(dataDirectory, "server-secrets.json"), ct);
        var endpoint = ResolveEndpoint(secrets);
        var schemaVersion = await GetAppliedSchemaVersionAsync(endpoint, ct);

        // 转储先落到数据目录下的临时目录：那里已经是 SYSTEM + Administrators 的收紧 ACL，
        // 不会在 %TEMP% 里留下一份任何人可读的全库明文。
        var workDirectory = Path.Combine(dataDirectory, $".backup-package-export-{Guid.NewGuid():N}");
        SecureFileSystem.CreateDirectory(workDirectory, enforceAcl: true, includeCurrentUser: true);
        var tempPath = targetPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            var dumpPath = Path.Combine(workDirectory, BackupPackageDumpFileName);
            await new PostgreSqlManager().DumpAsync(
                Path.Combine(installDirectory, "PostgreSQL"),
                endpoint,
                dumpPath,
                ct);
            SecureFileSystem.ApplyFileAcl(dumpPath);

            var manifestPath = Path.Combine(workDirectory, BackupPackageManifestEntry);
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(
                    new BackupPackageManifest
                    {
                        FormatVersion = BackupPackageFormatVersion,
                        ExportedAtUtc = DateTime.UtcNow,
                        InstanceId = secrets.InstanceId,
                        ProductVersion = GetProductVersion(),
                        DatabaseSchemaVersion = schemaVersion,
                        DatabaseName = endpoint.Database,
                        IncludesRepositoryFiles = false
                    },
                    ManifestJsonOptions),
                ct);

            using (File.Create(tempPath))
            {
            }
            SecureFileSystem.ApplyFileAcl(tempPath);
            await using (var output = new FileStream(
                tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1024 * 64, useAsync: true))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                await AddPackageEntryAsync(archive, BackupPackageManifestEntry, manifestPath, ct);
                foreach (var (entryName, sourcePath) in keyFiles)
                    await AddPackageEntryAsync(archive, BackupPackageKeyPrefix + entryName, sourcePath, ct);
                await AddPackageEntryAsync(archive, BackupPackageDumpEntry, dumpPath, ct);
            }

            File.Move(tempPath, targetPath, true);
            SecureFileSystem.ApplyFileAcl(targetPath);
        }
        finally
        {
            TryDelete(tempPath);
            TryDeleteDirectory(workDirectory);
        }
    }

    /// <summary>
    /// 导入配置备份包，返回恢复前自动导出的快照路径。
    ///
    /// 顺序不能改：先校验（含 schema 版本闸门）→ 停 API 但保留 PostgreSQL（restore 要用它）
    /// → 导出恢复前快照 → 换密钥 → 对齐集群角色口令 → restore → 补迁移 → 起 API。
    /// 任何一步失败都把密钥和角色口令还原回去；数据库那一侧由 pg_restore 的单事务保证不留半成品。
    /// </summary>
    public async Task<string> ImportBackupPackageAsync(
        string installDirectory,
        string dataDirectory,
        string packagePath,
        CancellationToken ct)
    {
        var sourcePath = Path.GetFullPath(packagePath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("找不到服务端配置备份包。", sourcePath);

        var postgresRoot = Path.Combine(installDirectory, "PostgreSQL");
        var postgres = new PostgreSqlManager();
        var currentSecrets = await ReadSecretsAsync(Path.Combine(dataDirectory, "server-secrets.json"), ct);
        var currentEndpoint = ResolveEndpoint(currentSecrets);

        var importDirectory = Path.Combine(dataDirectory, $".backup-package-import-{Guid.NewGuid():N}");
        SecureFileSystem.CreateDirectory(importDirectory, enforceAcl: true, includeCurrentUser: true);
        string preRestoreDump;
        try
        {
            var manifest = await ExtractAndValidateBackupPackageAsync(sourcePath, importDirectory, ct);
            var keyDirectory = Path.Combine(importDirectory, BackupPackageKeyDirectoryName);
            var importedSecrets = await ReadSecretsAsync(
                Path.Combine(keyDirectory, "server-secrets.json"), ct);
            var importedEndpoint = ResolveEndpoint(importedSecrets);

            // schema 版本闸门：低于本机可以导，restore 之后补迁移就是了；高于本机必须拒绝——
            // 缺的是「本安装包里还不存在的迁移脚本」，恢复出来的库会缺表缺列，无处可补。
            var installerVersion = InstallerPayload.GetLatestMigrationVersion();
            if (CompareMigrationVersions(manifest.DatabaseSchemaVersion, installerVersion) > 0)
                throw new InvalidOperationException(
                    $"备份包的数据库 schema 版本为 {manifest.DatabaseSchemaVersion}，高于当前安装包的 {installerVersion}。"
                    + "该包来自更新版本的服务端，本安装包没有它需要的迁移脚本。"
                    + "请先用同版本或更新版本的服务端安装包升级本机，再导入。");

            // 端口取自 secrets，服务端启动后就照它连库；本机集群只监听自己 postgresql.conf 里的
            // 那个端口。两边不一致时导入完成后必然连不上库，而现象是「客户端一直 401」，很难查。
            if (importedEndpoint.Port != currentEndpoint.Port)
                throw new InvalidOperationException(
                    $"备份包记录的 PostgreSQL 端口为 {importedEndpoint.Port}，本机集群监听 {currentEndpoint.Port}。"
                    + "请先在本机以相同端口安装服务端，再导入该备份包。");

            await StopServerKeepPostgresAsync(ct);
            preRestoreDump = await CreatePreRestoreDumpAsync(
                postgresRoot, dataDirectory, currentEndpoint, importedEndpoint, ct);

            var targets = GetKeyPackageFiles(dataDirectory);
            var files = GetKeyPackageFiles(keyDirectory);
            var backups = new List<(string Target, string Backup, bool Existed)>();
            var rolePasswordsChanged = false;
            try
            {
                foreach (var (_, target) in targets)
                {
                    var backup = target + $".{Environment.ProcessId}.{Guid.NewGuid():N}.import-backup";
                    var existed = File.Exists(target);
                    if (existed)
                    {
                        File.Copy(target, backup, true);
                        SecureFileSystem.ApplyFileAcl(backup);
                    }
                    backups.Add((target, backup, existed));
                }

                foreach (var (entryName, source) in files)
                {
                    var target = targets.First(item => item.EntryName == entryName).Path;
                    ReplaceProtectedFile(source, target);
                }

                await AlignRolePasswordsAsync(postgres, currentSecrets, importedSecrets, importedEndpoint, ct);
                rolePasswordsChanged = true;

                await postgres.RestoreAsync(
                    postgresRoot,
                    importedEndpoint,
                    Path.Combine(importDirectory, BackupPackageDumpDirectoryName, BackupPackageDumpFileName),
                    ct);

                // 备份包可能来自更旧的服务端：恢复出来的库停在它自己的 schema 版本上，
                // 差的那几条迁移在这里补齐。管理员口令传 null——V005 早已随备份一起恢复，
                // 恢复出来的本就该是源机器的管理员口令，这里不该、也不能重设它。
                var migrationDirectory = Path.Combine(importDirectory, BackupPackageMigrationDirectoryName);
                await InstallerPayload.ExtractMigrationScriptsAsync(migrationDirectory, ct);
                await new MigrationRunner().ApplyAsync(
                    BuildConnectionString(importedEndpoint),
                    migrationDirectory,
                    null,
                    null,
                    ct);
            }
            catch (Exception ex)
            {
                foreach (var backup in backups)
                {
                    if (backup.Existed && File.Exists(backup.Backup))
                        ReplaceProtectedFile(backup.Backup, backup.Target);
                    else if (!backup.Existed && File.Exists(backup.Target))
                        TryDelete(backup.Target);
                }

                // 口令还原本身也可能失败（比如集群此刻已经认新口令、而新口令又没写成）。
                // 这种中间状态必须如实报出来：告诉管理员「都已还原」而实际没还原，
                // 他会照着这句话去排查别的方向，而真正卡住服务的是登不上库。
                var rolePasswordsRestored = !rolePasswordsChanged
                    || await TryRestoreRolePasswordsAsync(postgres, currentSecrets, importedSecrets, currentEndpoint);

                // 失败路径必须把 API 服务重新拉起来。数据库恢复是单事务的，失败时库没被改动，
                // 密钥也已回滚——整机状态等价于导入之前，没有任何理由让服务停着。
                // 不这么做的话，一次导入失败就等于一次计划外停机，而提示里还不会提这件事。
                var serviceRestarted = await TryStartServerServiceAsync();

                throw new InvalidOperationException(
                    ex.Message
                    + Environment.NewLine + Environment.NewLine
                    + (rolePasswordsRestored
                        ? "导入已回滚：密钥文件和数据库角色口令都已还原为导入前的状态。"
                        : "导入已回滚密钥文件，但数据库角色口令还原失败——集群此刻可能仍认备份包里的口令。"
                          + "可重新导入同一个备份包（口令对齐逻辑认得这个中间状态），"
                          + "或用导入前的密钥包把 server-secrets.json 还原成集群实际认的那套。")
                    + "数据库恢复是单事务执行的，失败时内容不会被改动；"
                    + $"确需回退时可使用恢复前自动导出的 {preRestoreDump}。"
                    + (serviceRestarted
                        ? string.Empty
                        : Environment.NewLine + "注意：API 服务未能自动启动，请在维护面板点「启动服务」。"),
                    ex);
            }
            finally
            {
                foreach (var (_, backup, _) in backups)
                    TryDelete(backup);
            }
        }
        finally
        {
            TryDeleteDirectory(importDirectory);
        }

        // 只起 API：PostgreSQL 全程没停过，对着运行中的服务再 sc start 会返回 1056。
        await StartServerServiceAsync(ct);
        return preRestoreDump;
    }

    public string GetInstalledConnectionString(string installDirectory, string dataDirectory)
    {
        var configPath = Path.Combine(installDirectory, "appsettings.Turnkey.json");
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(configPath, optional: true, reloadOnChange: false)
            .Build();
        var bootstrap = LocalServerBootstrap.Initialize(
            configuration,
            new LocalServerBootstrapOptions
            {
                Enabled = true,
                DataDirectory = dataDirectory,
                SecretsPath = Path.Combine(dataDirectory, "server-secrets.json"),
                DiscoveryEnabled = configuration.GetValue("LanMode:DiscoveryEnabled", true),
                DiscoveryPort = configuration.GetValue("LanMode:DiscoveryPort", 45808)
            });

        return bootstrap.Values["ConnectionStrings:Default"]
            ?? throw new InvalidOperationException("找不到已安装服务端的数据库连接配置。");
    }

    public int GetInstalledApiPort(string installDirectory)
    {
        var configPath = Path.Combine(installDirectory, "appsettings.Turnkey.json");
        if (!File.Exists(configPath))
            return 5080;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            var url = document.RootElement
                .GetProperty("Kestrel")
                .GetProperty("Endpoints")
                .GetProperty("Https")
                .GetProperty("Url")
                .GetString();
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Port : 5080;
        }
        catch (Exception)
        {
            return 5080;
        }
    }

    public async Task RestartServicesAsync(CancellationToken ct)
    {
        await StopServicesAsync(ct);
        await StartServicesAsync(ct);
    }

    /// <summary>
    /// 停止服务端。顺序是先 API 后 PostgreSQL——反过来会让 API 在数据库消失后
    /// 继续跑一段时间，日志里刷一片连接失败，看起来像是出了故障。
    ///
    /// 每一步都等到真的 Stopped 再进行下一步：sc stop 只是把停止控制码投递给服务，
    /// 立刻返回，此时进程往往还活着。这条教训是从「卸载时 icudt67.dll 被占用」来的。
    /// </summary>
    public async Task StopServicesAsync(CancellationToken ct)
    {
        var sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
        await ProcessRunner.RunAsync(sc, ["stop", ServerServiceName], ct, throwOnError: false);
        await WaitForServiceStatusAsync(ServerServiceName, ServiceControllerStatus.Stopped, ct, required: false);
        await ProcessRunner.RunAsync(sc, ["stop", PostgreSqlServiceName], ct, throwOnError: false);
        await WaitForServiceStatusAsync(PostgreSqlServiceName, ServiceControllerStatus.Stopped, ct, required: false);
    }

    /// <summary>启动服务端。顺序与停止相反：数据库先起来，API 才有得连。</summary>
    public async Task StartServicesAsync(CancellationToken ct)
    {
        var sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
        await ProcessRunner.RunAsync(sc, ["start", PostgreSqlServiceName], ct);
        await WaitForServiceStatusAsync(PostgreSqlServiceName, ServiceControllerStatus.Running, ct);
        await ProcessRunner.RunAsync(sc, ["start", ServerServiceName], ct);
        await WaitForServiceStatusAsync(ServerServiceName, ServiceControllerStatus.Running, ct);
    }

    /// <summary>
    /// 两个服务的当前状态，给维护界面显示。
    /// 有了它，「停止服务」和「启动服务」两个按钮才不是让人盲按——
    /// 人需要先知道现在是什么状态，才知道该按哪个。
    /// </summary>
    public (string Server, string Postgres) GetServiceStatuses() =>
        (TranslateStatus(GetServiceStatus(ServerServiceName)),
         TranslateStatus(GetServiceStatus(PostgreSqlServiceName)));

    private static string TranslateStatus(string status) => status switch
    {
        "Running" => "运行中",
        "Stopped" => "已停止",
        "StartPending" => "正在启动",
        "StopPending" => "正在停止",
        "Paused" => "已暂停",
        _ => status
    };

    public async Task<string> GetDiagnosticsAsync(
        string installDirectory,
        string dataDirectory,
        CancellationToken ct)
    {
        var apiPort = GetInstalledApiPort(installDirectory);
        var serverStatus = GetServiceStatus(ServerServiceName);
        var postgresStatus = GetServiceStatus(PostgreSqlServiceName);
        var secretPath = Path.Combine(dataDirectory, "server-secrets.json");
        var apiStatus = "unreachable";
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(installDirectory, "appsettings.Turnkey.json"), optional: false, reloadOnChange: false)
                .Build();
            var bootstrap = LocalServerBootstrap.Initialize(
                configuration,
                new LocalServerBootstrapOptions
                {
                    Enabled = true,
                    DataDirectory = dataDirectory,
                    SecretsPath = Path.Combine(dataDirectory, "server-secrets.json"),
                    DiscoveryEnabled = configuration.GetValue("LanMode:DiscoveryEnabled", true),
                    DiscoveryPort = configuration.GetValue("LanMode:DiscoveryPort", 45808)
                });
            var expectedFingerprint = bootstrap.Values["Security:ServerCertificate:Fingerprint"];
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                    certificate is not null
                    && string.Equals(
                        ServerCertificateFactory.GetFingerprint(new X509Certificate2(certificate)),
                        expectedFingerprint,
                        StringComparison.OrdinalIgnoreCase)
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await http.GetAsync($"https://127.0.0.1:{apiPort}/health", ct);
            apiStatus = $"HTTP {(int)response.StatusCode}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or CryptographicException or InvalidOperationException)
        {
            apiStatus = "unreachable";
        }

        return string.Join(Environment.NewLine,
            $"服务端服务：{serverStatus}",
            $"PostgreSQL 服务：{postgresStatus}",
            $"API /health：{apiStatus}",
            $"内部密钥文件：{(File.Exists(secretPath) ? "存在" : "缺失")}",
            $"API 端口：{apiPort}",
            $"检查时间（UTC）：{DateTime.UtcNow:O}");
    }

    public async Task UninstallAsync(
        string installDirectory,
        string dataDirectory,
        bool removeData,
        CancellationToken ct)
    {
        EnsureSafePath(installDirectory, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BackupMonitor"));
        EnsureSafePath(dataDirectory, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackupMonitor"));
        var postgresDataDirectory = DefaultPostgreSqlDataDirectory;
        EnsureSafePath(postgresDataDirectory, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackupMonitor"));

        var sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
        await ProcessRunner.RunAsync(sc, ["stop", ServerServiceName], ct, throwOnError: false);
        await ProcessRunner.RunAsync(sc, ["stop", PostgreSqlServiceName], ct, throwOnError: false);
        // sc stop 只是把 STOP 控制码投递给服务就立刻返回，PostgreSQL 还要做关机 checkpoint，
        // 期间 postgres.exe 仍然占着 bin 目录里的 icudt*.dll 等文件。不等它真正停下就删目录，
        // 必然报 "Access to the path 'icudt67.dll' is denied."
        await WaitForServiceStatusAsync(ServerServiceName, ServiceControllerStatus.Stopped, ct, required: false);
        await WaitForServiceStatusAsync(PostgreSqlServiceName, ServiceControllerStatus.Stopped, ct, required: false);

        // 集群未必由服务托管：安装中途失败、或上一次卸载只删了服务没杀进程，都会留下一个没有
        // 服务归属的 postmaster。它会一直占着 PGDATA 和端口活到下次重启，而下一次安装看到
        // PG_VERSION 存在就跳过 initdb，直接沿用这个旧集群——于是「重装完还是昨天的数据」。
        // 所以删服务之前，先照着 PGDATA 直接把 postmaster 停掉。
        await StopPostgresClusterDirectlyAsync(installDirectory, postgresDataDirectory, ct);

        await ProcessRunner.RunAsync(sc, ["delete", ServerServiceName], ct, throwOnError: false);
        await ProcessRunner.RunAsync(sc, ["delete", PostgreSqlServiceName], ct, throwOnError: false);

        // 服务停了之后仍可能残留从安装目录启动的子进程（postgres 的后台进程、托盘等），
        // 给它们几秒自行退出的时间，再强杀，最后才删目录。
        await WaitForProcessesToExitAsync(installDirectory, ct);

        var netsh = Path.Combine(
            Environment.GetEnvironmentVariable("SystemRoot") ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "netsh.exe");
        await ProcessRunner.RunAsync(netsh,
            ["advfirewall", "firewall", "delete", "rule", "name=BackupMonitor Server TCP"],
            ct,
            throwOnError: false);
        await ProcessRunner.RunAsync(netsh,
            ["advfirewall", "firewall", "delete", "rule", "name=BackupMonitor Discovery UDP"],
            ct,
            throwOnError: false);

        // 每个目录独立删除、各自收集错误：安装目录删不掉时若直接抛出，数据库目录就永远留在盘上，
        // 重装后被原样沿用。数据的清除比安装目录的清除更要紧，不能被前者的失败带走。
        var failures = new List<Exception>();
        await TryDeleteAsync(installDirectory);
        if (removeData)
        {
            await TryDeleteAsync(dataDirectory);
            await TryDeleteAsync(postgresDataDirectory);
        }

        if (failures.Count > 0)
            throw new AggregateException(
                "卸载未能删除全部目录，残留内容会在下次安装时被沿用，请重启计算机后再次运行卸载。",
                failures);

        async Task TryDeleteAsync(string directory)
        {
            try
            {
                await DeleteDirectoryWithRetryAsync(directory, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(ex);
            }
        }
    }

    /// <summary>
    /// 用安装目录里的 pg_ctl 直接停掉 PGDATA 上的集群，不依赖 Windows 服务是否还在。
    /// 用 immediate 模式：卸载紧接着就要删掉整个数据目录，没必要为一个即将消失的集群等
    /// checkpoint 写完。
    /// </summary>
    private static async Task StopPostgresClusterDirectlyAsync(
        string installDirectory,
        string postgresDataDirectory,
        CancellationToken ct)
    {
        var pgCtl = Path.Combine(installDirectory, "PostgreSQL", "bin", "pg_ctl.exe");
        if (!File.Exists(pgCtl))
            return;

        // 实际 PGDATA 是 <postgresDataDirectory>\data，老版本直接用父目录，两个都试一遍。
        string[] candidates = [Path.Combine(postgresDataDirectory, "data"), postgresDataDirectory];
        foreach (var candidate in candidates)
        {
            if (!File.Exists(Path.Combine(candidate, "postmaster.pid")))
                continue;

            await ProcessRunner.RunAsync(
                pgCtl,
                ["stop", "-D", candidate, "-m", "immediate", "-w", "-t", "30"],
                ct,
                throwOnError: false);
        }
    }

    /// <summary>
    /// 等待所有可执行文件位于 <paramref name="directory"/> 下的进程退出，超时后强制结束。
    /// </summary>
    private static async Task WaitForProcessesToExitAsync(string directory, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var running = GetProcessesUnder(directory);
            if (running.Count == 0)
                return;

            if (DateTime.UtcNow >= deadline)
            {
                foreach (var process in running)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5000);
                    }
                    catch
                    {
                        // 进程可能刚好自己退出了，或者没有权限；后面删目录的重试会给出准确报错。
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }

                return;
            }

            foreach (var process in running)
                process.Dispose();

            await Task.Delay(500, ct);
        }
    }

    private static List<System.Diagnostics.Process> GetProcessesUnder(string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var matches = new List<System.Diagnostics.Process>();
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            string? path = null;
            try
            {
                path = process.MainModule?.FileName;
            }
            catch
            {
                // 系统进程和其他会话的进程读不到主模块，直接跳过。
            }

            if (path is not null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                matches.Add(process);
            else
                process.Dispose();
        }

        return matches;
    }

    /// <summary>
    /// 带重试的目录删除：文件句柄的释放是异步的，服务刚停时删除仍可能撞上 ACCESS_DENIED。
    /// 全部重试用尽后抛出可读的错误，告诉操作员是哪个文件被占用。
    /// </summary>
    private static async Task DeleteDirectoryWithRetryAsync(string directory, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (!Directory.Exists(directory))
                return;

            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                if (attempt >= 9)
                    throw new IOException(
                        $"删除 {directory} 失败：{ex.Message}。该文件仍被其他进程占用，请重启计算机后再次运行卸载。",
                        ex);

                await Task.Delay(1000, ct);
            }
        }
    }

    private sealed record KeyPackageFile(string EntryName, string Path);

    private static KeyPackageFile[] GetKeyPackageFiles(string dataDirectory) =>
    [
        new("server-secrets.json", Path.Combine(dataDirectory, "server-secrets.json")),
        new("client-ca.pfx", Path.Combine(dataDirectory, "client-ca.pfx")),
        new("server-certificate.pfx", Path.Combine(dataDirectory, "server-certificate.pfx"))
    ];

    private static void EnsureKeyPackageFilesExist(IEnumerable<KeyPackageFile> files)
    {
        foreach (var file in files)
        {
            if (!File.Exists(file.Path))
                throw new InvalidOperationException($"密钥包缺少服务端文件：{file.Path}");
        }
    }

    private static async Task ExtractAndValidateKeyPackageAsync(
        string packagePath,
        string importDirectory,
        CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var expectedNames = new[] { "server-secrets.json", "client-ca.pfx", "server-certificate.pfx" };
        if (archive.Entries.Count != expectedNames.Length
            || archive.Entries.Any(entry => !expectedNames.Contains(entry.FullName, StringComparer.Ordinal)))
            throw new InvalidOperationException("密钥包内容不完整或包含未识别文件；必须同时包含 server-secrets.json、client-ca.pfx、server-certificate.pfx。");

        foreach (var name in expectedNames)
        {
            ct.ThrowIfCancellationRequested();
            var entry = archive.GetEntry(name)
                ?? throw new InvalidOperationException($"密钥包缺少 {name}。");
            var target = Path.Combine(importDirectory, name);
            await using var input = entry.Open();
            await using var output = new FileStream(
                target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 64, useAsync: true);
            await input.CopyToAsync(output, ct);
            SecureFileSystem.ApplyFileAcl(target);
        }

        await ValidateKeyFilesAsync(importDirectory, ct);
    }

    /// <summary>
    /// 校验一个目录里的三个密钥文件互相自洽：口令齐全、CA 带私钥、服务端证书能被真正加载。
    /// 密钥包和配置备份包共用同一套校验——两者放的是同样的三个文件，
    /// 坏包必须在覆盖现有密钥之前就被拦下。
    /// </summary>
    private static async Task ValidateKeyFilesAsync(string directory, CancellationToken ct)
    {
        var secretPath = Path.Combine(directory, "server-secrets.json");
        var secrets = JsonSerializer.Deserialize<LocalServerSecrets>(
            await File.ReadAllTextAsync(secretPath, ct),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("包中的 server-secrets.json 无法读取。");
        if (string.IsNullOrWhiteSpace(secrets.ClientCaPassword)
            || string.IsNullOrWhiteSpace(secrets.ServerCertificatePassword))
            throw new InvalidOperationException("包中的服务端密钥口令不完整。");

        using (var ca = new X509Certificate2(
                   Path.Combine(directory, "client-ca.pfx"),
                   secrets.ClientCaPassword,
                   X509KeyStorageFlags.EphemeralKeySet))
        {
            if (!ca.HasPrivateKey)
                throw new InvalidOperationException("包中的 client-ca.pfx 不包含私钥。");
        }

        using var serverCertificate = ServerCertificateFactory.Load(
            Path.Combine(directory, "server-certificate.pfx"),
            secrets.ServerCertificatePassword);
    }

    /// <summary>
    /// 配置备份包的 manifest。它不是给人读的说明文件，而是导入侧的两道闸门：
    /// FormatVersion 决定这个包本安装器还认不认得，DatabaseSchemaVersion 决定恢复出来的库
    /// 还能不能靠本机 payload 里的迁移脚本补齐。其余字段只用于出问题时人工核对来源。
    /// </summary>
    private sealed class BackupPackageManifest
    {
        public int FormatVersion { get; set; }
        public DateTime ExportedAtUtc { get; set; }
        public string InstanceId { get; set; } = string.Empty;
        public string ProductVersion { get; set; } = string.Empty;
        public string DatabaseSchemaVersion { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;

        /// <summary>
        /// 恒为 false，但仍然写进包里：它是「本包不含 Repository/Staging 实际备份文件」这条
        /// 前提的书面记录。将来真做了含数据的包，旧安装器也能凭它拒绝而不是默默少还原一半。
        /// </summary>
        public bool IncludesRepositoryFiles { get; set; }
    }

    private static async Task<LocalServerSecrets> ReadSecretsAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("找不到服务端内部密钥文件 server-secrets.json。", path);

        return JsonSerializer.Deserialize<LocalServerSecrets>(
                await File.ReadAllTextAsync(path, ct),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"服务端内部密钥文件无法解析：{path}");
    }

    /// <summary>
    /// 由 secrets 推出 pg_dump / pg_restore 的连接目标。
    ///
    /// 用应用角色而不是超级用户，是因为 pg_restore 带 --no-owner：恢复出来的对象归执行者所有。
    /// 用 postgres 执行，全库对象就都归了 postgres，而服务端是以 backup_monitor_app 连库的，
    /// 结果是恢复「成功」、API 却对每张表都没有权限。归属正确比权限宽松重要。
    ///
    /// 口令口径与 LocalServerBootstrap 的默认连接串保持一致，否则安装器和服务端会连到两处去。
    /// </summary>
    private static PostgresEndpoint ResolveEndpoint(LocalServerSecrets secrets)
    {
        // 外部数据库不在安装器的管辖范围内：它的备份策略、权限和停机窗口都由 DBA 决定，
        // 安装器既不该 --clean 别人的库，也没有把握能连上它。
        if (!string.IsNullOrWhiteSpace(secrets.ExplicitConnectionString))
            throw new InvalidOperationException(
                "当前服务端配置为使用外部 PostgreSQL 连接串，配置备份包只适用于安装器自带的本机集群。"
                + "请由数据库管理员用 pg_dump / pg_restore 单独处理该数据库。");

        if (string.IsNullOrEmpty(secrets.PostgresPassword))
            throw new InvalidOperationException(
                "server-secrets.json 中没有 PostgreSQL 应用角色口令，无法执行数据库备份或恢复。");

        return new PostgresEndpoint(
            "127.0.0.1",
            secrets.PostgresPort > 0 ? secrets.PostgresPort : 55432,
            "backup_monitor",
            "backup_monitor_app",
            secrets.PostgresPassword);
    }

    /// <summary>
    /// 超级用户口令：SchemaVersion 1 的旧密钥文件里没有独立字段，那时两个角色共用同一个口令，
    /// 回落逻辑与 LocalServerBootstrap 相同，避免旧机器导出的包在新机器上对不齐角色口令。
    /// </summary>
    private static string ResolveSuperuserPassword(LocalServerSecrets secrets)
    {
        var password = !string.IsNullOrEmpty(secrets.PostgresSuperuserPassword)
            ? secrets.PostgresSuperuserPassword
            : secrets.PostgresPassword;
        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException(
                "server-secrets.json 中没有 PostgreSQL 超级用户口令，无法对齐集群角色口令。");
        return password;
    }

    private static string BuildConnectionString(PostgresEndpoint endpoint) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = endpoint.Host,
            Port = endpoint.Port,
            Username = endpoint.Username,
            Password = endpoint.Password,
            Database = endpoint.Database,
            Timeout = 10,
            // 补迁移可能要重建索引，180 秒的语句超时是 MigrationRunner 自己的口径，这里给够余量。
            CommandTimeout = 300,
            // 维护动作里连库是一次性的，而且中途会改角色口令；连接池留着旧口令的连接只会添乱。
            Pooling = false
        }.ConnectionString;

    private static string GetProductVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // InformationalVersion 常带 "+<commit>" 后缀，对人工核对没有意义，去掉。
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }

    /// <summary>
    /// 读取库里已应用的最高迁移版本。先用 to_regclass 判表在不在，而不是靠捕获 42P01：
    /// 「库里还没跑过任何迁移」是合法状态（比如刚 initdb 完就导出），不该走异常路径。
    /// </summary>
    private static async Task<string> GetAppliedSchemaVersionAsync(
        PostgresEndpoint endpoint,
        CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(BuildConnectionString(endpoint));
        await connection.OpenAsync(ct);

        // ::text 不能省。to_regclass 返回的是 regclass，而 ExecuteScalar 按 System.Object 读，
        // Npgsql 8 对这种没有默认 CLR 映射的类型是直接抛错的：
        // "Reading as 'System.Object' is not supported for fields having DataTypeName 'regclass'"。
        // 转成 text 之后，表不存在仍然是 NULL，判空逻辑不变。
        await using (var exists = new NpgsqlCommand(
            "SELECT to_regclass('public.schema_migrations')::text", connection))
        {
            var table = await exists.ExecuteScalarAsync(ct);
            if (table is null or DBNull)
                return string.Empty;
        }

        await using var latest = new NpgsqlCommand(
            "SELECT max(version) FROM public.schema_migrations", connection);
        return await latest.ExecuteScalarAsync(ct) as string ?? string.Empty;
    }

    /// <summary>
    /// 比较两个迁移版本号（形如 V029__xxx）。只比 V 后面的序号，不比脚本描述：
    /// 同一个版本号在不同分支上出现过不同的描述后缀，整串字典序会把它们排出假的先后。
    /// 空串表示「库里还没有任何迁移」，永远最小。
    /// </summary>
    private static int CompareMigrationVersions(string? left, string? right) =>
        ParseMigrationOrdinal(left).CompareTo(ParseMigrationOrdinal(right));

    private static int ParseMigrationOrdinal(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return -1;

        var text = version.Trim();
        if (text[0] is not ('V' or 'v'))
            throw new InvalidOperationException($"无法识别的数据库迁移版本号：{version}");

        var digits = 0;
        while (digits + 1 < text.Length && char.IsAsciiDigit(text[digits + 1]))
            digits++;
        if (digits == 0 || !int.TryParse(
                text.AsSpan(1, digits),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var ordinal))
            throw new InvalidOperationException($"无法识别的数据库迁移版本号：{version}");

        return ordinal;
    }

    /// <summary>
    /// 把一个文件按给定条目名写进包。统一 NoCompression：三个密钥文件本来就是高熵内容，
    /// pg_dump 的 -Fc 转储自带 zlib 压缩，再压一遍只是白烧 CPU 和导出时间。
    /// </summary>
    private static async Task AddPackageEntryAsync(
        ZipArchive archive,
        string entryName,
        string sourcePath,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
        await using var input = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 64, useAsync: true);
        await using var entryStream = entry.Open();
        await input.CopyToAsync(entryStream, ct);
    }

    /// <summary>
    /// 解包并校验配置备份包，返回 manifest。
    ///
    /// 这一步跑在停服务之前：坏包、错包（比如选成了 .bmkp 密钥包）、版本不匹配的包，
    /// 都必须在动现有密钥和数据库之前被拦下来，否则一次误操作就要人工收拾两处现场。
    /// </summary>
    private static async Task<BackupPackageManifest> ExtractAndValidateBackupPackageAsync(
        string packagePath,
        string importDirectory,
        CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var manifestEntry = FindPackageEntry(archive, BackupPackageManifestEntry)
            ?? throw new InvalidOperationException(
                "这不是 BackupMonitor 配置备份包：包内没有 manifest.json。"
                + "如果选的是密钥包 (.bmkp)，请改用「导入密钥包」。");

        BackupPackageManifest manifest;
        await using (var manifestStream = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<BackupPackageManifest>(
                    manifestStream, ManifestJsonOptions, ct)
                ?? throw new InvalidOperationException("配置备份包的 manifest.json 无法解析。");
        }

        if (manifest.FormatVersion != BackupPackageFormatVersion)
            throw new InvalidOperationException(
                $"配置备份包的格式版本为 {manifest.FormatVersion}，本安装器只识别 {BackupPackageFormatVersion}。"
                + "请使用与该备份包配套的服务端安装器。");

        var keyDirectory = Path.Combine(importDirectory, BackupPackageKeyDirectoryName);
        SecureFileSystem.CreateDirectory(keyDirectory, enforceAcl: true, includeCurrentUser: true);
        foreach (var (entryName, target) in GetKeyPackageFiles(keyDirectory))
        {
            ct.ThrowIfCancellationRequested();
            var entry = FindPackageEntry(archive, BackupPackageKeyPrefix + entryName)
                ?? throw new InvalidOperationException(
                    $"配置备份包缺少 {BackupPackageKeyPrefix}{entryName}。");
            await ExtractPackageEntryAsync(entry, target, ct);
        }

        var dumpDirectory = Path.Combine(importDirectory, BackupPackageDumpDirectoryName);
        SecureFileSystem.CreateDirectory(dumpDirectory, enforceAcl: true, includeCurrentUser: true);
        var dumpEntry = FindPackageEntry(archive, BackupPackageDumpEntry)
            ?? throw new InvalidOperationException(
                $"配置备份包缺少数据库转储 {BackupPackageDumpEntry}。"
                + "只恢复密钥而不恢复数据库，客户端会因为查不到证书记录而永远 401。");
        await ExtractPackageEntryAsync(
            dumpEntry, Path.Combine(dumpDirectory, BackupPackageDumpFileName), ct);

        // 密钥自洽性与密钥包共用同一套校验：包里放的是同样三个文件，坏包的判据也该是同一个。
        await ValidateKeyFilesAsync(keyDirectory, ct);
        return manifest;
    }

    /// <summary>
    /// 按条目名查找。不同打包工具写出的分隔符不一样（Compress-Archive 写反斜杠），
    /// 两种都认，与 InstallerPayload 的取法保持一致。
    /// </summary>
    private static ZipArchiveEntry? FindPackageEntry(ZipArchive archive, string entryName) =>
        archive.Entries.FirstOrDefault(entry => string.Equals(
            entry.FullName.Replace('\\', '/'), entryName, StringComparison.OrdinalIgnoreCase));

    private static async Task ExtractPackageEntryAsync(
        ZipArchiveEntry entry,
        string targetPath,
        CancellationToken ct)
    {
        await using (var input = entry.Open())
        await using (var output = new FileStream(
            targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 64, useAsync: true))
            await input.CopyToAsync(output, ct);

        // 带上当前用户：pg_restore 是本进程的子进程，落在临时目录里的转储必须能被它读到。
        SecureFileSystem.ApplyFileAcl(targetPath, enforceAcl: true, includeCurrentUser: true);
    }

    /// <summary>
    /// 只停 API，保留 PostgreSQL——恢复过程正要用它执行 pg_restore。
    /// 必须等到真的 Stopped：服务还活着就一直持着到 backup_monitor 的连接，
    /// pg_restore 的 DROP 会卡在锁上直到超时。
    /// </summary>
    private static async Task StopServerKeepPostgresAsync(CancellationToken ct)
    {
        var sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
        await ProcessRunner.RunAsync(sc, ["stop", ServerServiceName], ct, throwOnError: false);
        await WaitForServiceStatusAsync(ServerServiceName, ServiceControllerStatus.Stopped, ct, required: false);
    }

    /// <summary>只启动 API。PostgreSQL 全程没停过，对运行中的服务再 sc start 会返回 1056。</summary>
    private static async Task StartServerServiceAsync(CancellationToken ct)
    {
        var sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
        await ProcessRunner.RunAsync(sc, ["start", ServerServiceName], ct);
        await WaitForServiceStatusAsync(ServerServiceName, ServiceControllerStatus.Running, ct);
    }

    /// <summary>
    /// 恢复之前先把现场导出一份，返回快照文件的完整路径。
    ///
    /// pg_restore 是单事务的，失败不会留下半个库；这份快照防的是另一件事——恢复本身成功了，
    /// 但导入的根本不是管理员想要的那个包。那时旧库已经被 --clean 掉，没有它就真的回不去。
    ///
    /// 两个 endpoint 都要试：正常情况下集群认的是本机 secrets 里的口令；但若上一次导入在
    /// 对齐角色口令之后失败、回滚口令又没成功，集群此刻认的是备份包里的那一套。
    /// 分不清就直接失败，等于把人卡在一个自己修不出来的状态里。
    /// </summary>
    private static async Task<string> CreatePreRestoreDumpAsync(
        string postgresRoot,
        string dataDirectory,
        PostgresEndpoint currentEndpoint,
        PostgresEndpoint importedEndpoint,
        CancellationToken ct)
    {
        var endpoint = await ResolveAuthenticatedEndpointAsync(currentEndpoint, importedEndpoint, ct);
        var directory = Path.Combine(dataDirectory, PreRestoreDumpDirectoryName);
        SecureFileSystem.CreateDirectory(directory, enforceAcl: true, includeCurrentUser: true);
        // 时间戳用本地时间：这个文件名是给现场的人读的，UTC 只会让人多算一次时差。
        var path = Path.Combine(
            directory,
            $"pre-restore-{DateTime.Now:yyyyMMdd-HHmmss}.dump");
        await new PostgreSqlManager().DumpAsync(postgresRoot, endpoint, path, ct);
        SecureFileSystem.ApplyFileAcl(path);
        return path;
    }

    /// <summary>
    /// 判断本机集群此刻认的是哪一套应用角色口令，返回能连上的那个 endpoint。
    /// 只把认证失败当作「口令不对」，连不上、库不存在这类问题必须带着原始报错抛出去——
    /// 把它们一并吞掉，现场看到的就只剩一句笼统的「口令都不对」。
    /// </summary>
    private static async Task<PostgresEndpoint> ResolveAuthenticatedEndpointAsync(
        PostgresEndpoint primary,
        PostgresEndpoint fallback,
        CancellationToken ct)
    {
        if (await CanAuthenticateAsync(primary, ct))
            return primary;

        if (!string.Equals(primary.Password, fallback.Password, StringComparison.Ordinal)
            && await CanAuthenticateAsync(fallback, ct))
            return fallback;

        throw new InvalidOperationException(
            "无法用 server-secrets.json 或备份包中的口令连接本机 PostgreSQL 数据库。"
            + "该集群可能来自另一次安装；安装器不会强行改写它，以免破坏其中的数据。");
    }

    private static async Task<bool> CanAuthenticateAsync(PostgresEndpoint endpoint, CancellationToken ct)
    {
        try
        {
            await using var connection = new NpgsqlConnection(BuildConnectionString(endpoint));
            await connection.OpenAsync(ct);
            return true;
        }
        catch (PostgresException ex) when (
            ex.SqlState == PostgresErrorCodes.InvalidPassword
            || ex.SqlState == PostgresErrorCodes.InvalidAuthorizationSpecification)
        {
            return false;
        }
    }

    /// <summary>
    /// 把集群的 postgres 与 backup_monitor_app 两个角色的口令改成备份包里的那一套。
    ///
    /// 角色口令是集群级对象，不在单库转储里。少了这一步，密钥文件换成源机器的之后，
    /// 服务端就拿着源机器的口令去连本机集群，启动即认证失败——而现场看到的现象是
    /// 「客户端一直 401」，几乎查不到这里。
    ///
    /// 允许集群已经认备份包口令的情况直接连过去：上一次导入中途失败、口令回滚也没成功时，
    /// 重试导入不该被卡死在这一步。
    /// </summary>
    private static async Task AlignRolePasswordsAsync(
        PostgreSqlManager postgres,
        LocalServerSecrets currentSecrets,
        LocalServerSecrets importedSecrets,
        PostgresEndpoint importedEndpoint,
        CancellationToken ct)
    {
        var currentSuperuser = ResolveSuperuserPassword(currentSecrets);
        var importedSuperuser = ResolveSuperuserPassword(importedSecrets);
        var connectPassword = await ResolveSuperuserPasswordAsync(
            postgres, importedEndpoint.Port, currentSuperuser, importedSuperuser, ct);

        await postgres.SetRolePasswordsAsync(
            importedEndpoint.Port,
            connectPassword,
            importedSuperuser,
            importedEndpoint.Password,
            ct);
    }

    /// <summary>
    /// 回滚角色口令：用备份包的超级用户口令连上去（对齐已经生效），改回本机 secrets 的那一套。
    ///
    /// 失败只能吞掉——它跑在异常处理路径上，抛出去会把真正的失败原因盖掉。真出现这种情况，
    /// 管理员的出路是重新导入同一个包（对齐逻辑认得这个中间状态），或者用密钥包把
    /// server-secrets.json 还原成集群实际认的那一套。
    /// </summary>
    /// <returns>true 表示口令确已还原；false 表示还原失败，调用方必须把这件事告诉管理员。</returns>
    private static async Task<bool> TryRestoreRolePasswordsAsync(
        PostgreSqlManager postgres,
        LocalServerSecrets currentSecrets,
        LocalServerSecrets importedSecrets,
        PostgresEndpoint currentEndpoint)
    {
        try
        {
            var currentSuperuser = ResolveSuperuserPassword(currentSecrets);
            var importedSuperuser = ResolveSuperuserPassword(importedSecrets);
            var connectPassword = await ResolveSuperuserPasswordAsync(
                postgres, currentEndpoint.Port, importedSuperuser, currentSuperuser, CancellationToken.None);

            await postgres.SetRolePasswordsAsync(
                currentEndpoint.Port,
                connectPassword,
                currentSuperuser,
                currentEndpoint.Password,
                CancellationToken.None);
            return true;
        }
        catch
        {
            // 见方法注释：这里的失败不能遮蔽导入本身的报错，但也不能被当作成功——
            // 调用方拿 false 去改写给管理员看的那句结论。
            return false;
        }
    }

    /// <summary>
    /// 尽力启动 API 服务，启动失败不抛。用在导入失败的回滚路径上：
    /// 那里正在抛出导入本身的错误，再叠一个「服务起不来」会把真正的原因埋掉。
    /// </summary>
    private async Task<bool> TryStartServerServiceAsync()
    {
        try
        {
            await StartServerServiceAsync(CancellationToken.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> ResolveSuperuserPasswordAsync(
        PostgreSqlManager postgres,
        int port,
        string primary,
        string fallback,
        CancellationToken ct)
    {
        if (await postgres.CanAuthenticateSuperuserAsync(port, primary, ct))
            return primary;

        if (!string.Equals(primary, fallback, StringComparison.Ordinal)
            && await postgres.CanAuthenticateSuperuserAsync(port, fallback, ct))
            return fallback;

        throw new InvalidOperationException(
            "无法用 server-secrets.json 或备份包中的超级用户口令连接本机 PostgreSQL 集群，"
            + "无法对齐数据库角色口令。请确认该集群由本安装器创建。");
    }

    private static void ReplaceProtectedFile(string source, string target)
    {
        var temporaryPath = target + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (File.Create(temporaryPath))
            {
            }
            SecureFileSystem.ApplyFileAcl(temporaryPath);
            File.Copy(source, temporaryPath, true);
            File.Move(temporaryPath, target, true);
            SecureFileSystem.ApplyFileAcl(target);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 导入临时目录清理失败不覆盖主流程；下次维护操作会再次清理。
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 不在日志中输出密钥文件路径或内容。
        }
    }

    private static string GetServiceStatus(string name)
    {
        try
        {
            using var service = new ServiceController(name);
            return service.Status.ToString();
        }
        catch (InvalidOperationException)
        {
            return "未安装";
        }
    }

    private static async Task WaitForServiceStatusAsync(
        string name,
        ServiceControllerStatus desired,
        CancellationToken ct,
        bool required = true)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var service = new ServiceController(name);
                service.Refresh();
                if (service.Status == desired)
                    return;
            }
            catch (InvalidOperationException) when (!required)
            {
                return;
            }

            await Task.Delay(250, ct);
        }

        if (required)
            throw new System.TimeoutException($"Windows 服务 {name} 未能在 30 秒内进入 {desired} 状态。");
    }

    private static void EnsureSafePath(string path, string allowedRoot)
    {
        var root = Path.GetFullPath(allowedRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target, root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("维护操作的目录不在 BackupMonitor 专用目录内。");
    }
}
