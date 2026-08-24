using System.Text.RegularExpressions;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 迁移脚本不得把某台机器的绝对路径带进产品。
///
/// V001 曾把 repository_path / staging_path 直接种成开发机上的
/// 'E:\BackupRepository' 与 'D:\BackupStaging'。目标机上没有这两个盘时，
/// 解析仓库根的代码会对该路径执行 CreateDirectory 并抛异常——概览页的容量统计
/// 首当其冲，整页 500，而且报错内容完全看不出跟盘符有关。
/// 这类缺陷一路活到生产环境安装，就是因为这条路径此前零覆盖。
///
/// 这里只做静态扫描，不去查库断言取值：postgres 集合共用同一个数据库，
/// 别的用例会把 repository_path 指向自己的临时仓库根，查库断言必然随执行顺序漂移。
/// 静态扫描既确定，覆盖面也更大——它拦的是「任何迁移写死盘符」这一整类问题。
/// </summary>
[Collection("postgres")]
public sealed class StoragePathSeedTests
{
    /// <summary>
    /// V001 是首犯，但它已被 checksum 冻结：改动它会让所有已存在的库拒绝迁移。
    /// 只能由 V014 在运行期把那两个种子值清成 null 来消解，脚本本身留在原地。
    /// </summary>
    private const string FrozenOffender = "V001__initial_schema.sql";

    private readonly PostgresDatabaseFixture _fixture;

    public StoragePathSeedTests(PostgresDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void 迁移脚本里不得出现写死的盘符路径()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(_fixture.MigrationDirectory, "V*.sql"))
        {
            var name = Path.GetFileName(file);
            if (name == FrozenOffender)
                continue;

            foreach (var (line, index) in File.ReadLines(file).Select((line, index) => (line, index)))
            {
                // 清理历史遗留值的迁移必须引用那个旧值才能定位它，属正当用法。
                // 要求显式写出 legacy-path-cleanup 标记，让作者声明意图，
                // 而不是让正则去猜 WHERE 和 SET 的语法差别。
                if (line.Contains("legacy-path-cleanup", StringComparison.Ordinal))
                    continue;

                // 注释是在解释这段历史，不算违规。
                var code = line.Split("--")[0];

                // 盘符后跟反斜杠即判违规，不要求引号紧邻。V001 的实际写法是
                // '"E:\\BackupRepository"'::jsonb，引号与盘符之间还隔着 JSON 的双引号，
                // 要求紧邻的话恰好会漏掉真正要拦的那一行。
                if (Regex.IsMatch(code, @"[A-Za-z]:\\"))
                    offenders.Add($"{name}:{index + 1}: {line.Trim()}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "迁移脚本里出现了写死的盘符路径。迁移不能假定目标机器上存在某个盘符，"
            + "应当留空并由安装程序或配置给出取值：\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// 上面那条扫描必须真的能拦住 V001 里的写法，否则它只是个空过的测试。
    /// 白名单放行 V001 是有意为之，但正则本身不能对该写法失明。
    /// </summary>
    [Fact]
    public void 扫描规则能识别V001里的实际写法()
    {
        var v001 = Path.Combine(_fixture.MigrationDirectory, FrozenOffender);
        var seedLines = File.ReadLines(v001)
            .Where(line => line.Contains("repository_path") || line.Contains("staging_path"))
            .Where(line => Regex.IsMatch(line.Split("--")[0], @"[A-Za-z]:\\"))
            .ToList();

        Assert.True(
            seedLines.Count >= 2,
            "没能在 V001 里识别出写死盘符的种子行，说明扫描正则已经对该写法失明。");
    }
}
