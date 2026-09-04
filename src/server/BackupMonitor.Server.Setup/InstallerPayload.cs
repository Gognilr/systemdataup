using System.IO.Compression;
using System.Reflection;

namespace BackupMonitor.Server.Setup;

/// <summary>
/// 安装器自带 payload 中「数据库迁移脚本」那一部分的读取入口。
///
/// 迁移脚本只存在于 server-payload.zip 里，装完之后不会留在磁盘上——ServerInstaller 是从
/// 本次运行的临时解压目录直接读的。而导入配置备份包时必须在 restore 之后补跑一次迁移
/// （备份包可能来自更旧的服务端），所以这里单独把 database\*.sql 挑出来，
/// 不去碰 payload 里那两百多 MB 的 PostgreSQL 运行时。
/// </summary>
internal static class InstallerPayload
{
    private const string MigrationDirectoryName = "database";

    /// <summary>
    /// payload 里那份 VC++ 运行库安装包的位置。它本来是放给客户端下载用的
    /// （api\wwwroot\downloads），但安装器自己也需要它：内置 PostgreSQL 依赖 MSVC 运行库，
    /// 而缺它的那台机器多半也没有外网——只给一个下载网址等于没给。
    /// </summary>
    private const string VcRedistEntryPath = "api/wwwroot/downloads/VC_redist.x64.exe";

    /// <summary>
    /// 当前安装包携带的最高迁移版本。用于判定备份包是不是「来自更新的服务端」：
    /// 那种包里的库带着本安装器没有的表结构，缺的迁移脚本无处可补，只能拒绝导入。
    /// </summary>
    public static string GetLatestMigrationVersion()
    {
        using var archive = OpenPayload();
        return EnumerateMigrationEntries(archive)
            .Select(entry => Path.GetFileNameWithoutExtension(entry.Name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .LastOrDefault()
            ?? throw new InvalidOperationException("安装包 payload 内没有数据库迁移脚本。");
    }

    /// <summary>把 payload 里的迁移脚本解压到指定目录，供 MigrationRunner 使用。</summary>
    public static async Task ExtractMigrationScriptsAsync(string targetDirectory, CancellationToken ct)
    {
        Directory.CreateDirectory(targetDirectory);
        using var archive = OpenPayload();
        var extracted = 0;
        foreach (var entry in EnumerateMigrationEntries(archive))
        {
            ct.ThrowIfCancellationRequested();
            // 只取文件名再拼路径：条目名来自我们自己的构建脚本，但仍然不给 "..\" 这类
            // 相对段留出目录逃逸的口子。
            var target = Path.Combine(targetDirectory, Path.GetFileName(entry.Name));
            await using var input = entry.Open();
            await using var output = new FileStream(
                target, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 64, useAsync: true);
            await input.CopyToAsync(output, ct);
            extracted++;
        }

        if (extracted == 0)
            throw new InvalidOperationException("安装包 payload 内没有数据库迁移脚本。");
    }

    /// <summary>安装包里带没带 VC++ 运行库。开发机上单独构建的安装器可能没有。</summary>
    public static bool HasVcRedist()
    {
        try
        {
            using var archive = OpenPayload();
            return FindVcRedist(archive) is not null;
        }
        catch (Exception)
        {
            // 这个判断只用来决定「要不要提供一键安装」，取不到就当没带，
            // 回落到原来那条「自己去下载」的路，不该让自检本身炸掉。
            return false;
        }
    }

    /// <summary>把 payload 里的 VC++ 运行库解压到指定文件路径，返回是否解出来了。</summary>
    public static async Task<bool> TryExtractVcRedistAsync(string targetPath, CancellationToken ct)
    {
        using var archive = OpenPayload();
        var entry = FindVcRedist(archive);
        if (entry is null)
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await using var input = entry.Open();
        await using var output = new FileStream(
            targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 64, useAsync: true);
        await input.CopyToAsync(output, ct);
        return true;
    }

    private static ZipArchiveEntry? FindVcRedist(ZipArchive archive) =>
        archive.Entries.FirstOrDefault(entry =>
            // 打包脚本用 Compress-Archive 生成，条目名里是反斜杠；两种分隔符都认，
            // 与 EnumerateMigrationEntries 保持一致。
            string.Equals(
                entry.FullName.Replace('\\', '/'),
                VcRedistEntryPath,
                StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<ZipArchiveEntry> EnumerateMigrationEntries(ZipArchive archive) =>
        archive.Entries.Where(entry =>
            entry.Name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
            // 打包脚本用 Compress-Archive 生成，条目名里是反斜杠；这里两种分隔符都认。
            && entry.FullName.Replace('\\', '/')
                .StartsWith(MigrationDirectoryName + "/", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// payload 优先取内嵌资源；开发机上单独构建的安装器没有内嵌资源，回落到 payload 子目录，
    /// 与 ServerInstaller 的取法保持一致。
    /// </summary>
    private static ZipArchive OpenPayload()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("server-payload.zip", StringComparison.OrdinalIgnoreCase));
        Stream stream;
        if (resource is not null)
        {
            stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException("安装器内嵌 payload 无法读取。");
        }
        else
        {
            var fallback = Path.Combine(AppContext.BaseDirectory, "payload", "server-payload.zip");
            if (!File.Exists(fallback))
                throw new FileNotFoundException(
                    "安装器没有内置 server-payload.zip；请使用发布打包脚本生成完整安装器。", fallback);
            stream = File.OpenRead(fallback);
        }

        return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
    }
}
