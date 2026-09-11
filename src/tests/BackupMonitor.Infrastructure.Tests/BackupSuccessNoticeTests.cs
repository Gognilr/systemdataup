using BackupMonitor.Core.Enums;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BackupMonitor.Infrastructure.Tests;

/// <summary>
/// 非告警类通知的落单（备份成功回执、日报）。
///
/// 这一组盯的是「能不能关掉」。备份成功回执是全系统唯一一种**正常情况下也会持续产生**
/// 的通知：每台机器每个任务每天成功一次，几台机器就是每天几十条。
/// 关不掉的话，收件人会开始习惯性划过这些「都挺好」的消息——
/// 而报警的可信度是整体的，「备份复查未通过」会跟着一起被划过去。
/// </summary>
[Collection("postgres")]
public class BackupSuccessNoticeTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture;

    /// <summary>本用例落下的投递标题，收尾时按它清干净。</summary>
    private readonly List<string> _subjects = [];

    private ServiceProvider _services = null!;

    public BackupSuccessNoticeTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        _services = sc.BuildServiceProvider();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 清掉本用例落下的投递。
    ///
    /// 整个 postgres 测试集合共用一个库，而这里造的行长得和日报一模一样
    /// （AlertId 为空、Subject 非空）——不清的话，日报那组用例里
    /// 「取最新一条非告警投递」会取到这里的行，然后断言标题含「备份日报」失败。
    /// </summary>
    public async Task DisposeAsync()
    {
        if (_subjects.Count > 0)
        {
            using (var scope = _services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.NotificationDeliveries
                    .Where(d => _subjects.Contains(d.Subject!))
                    .ExecuteDeleteAsync();
            }
        }

        await _services.DisposeAsync();
    }

    /// <summary>三个渠道都启用且都不排除时，三条都落单。</summary>
    [Fact]
    public async Task 每个启用的渠道各落一条()
    {
        var subject = NewSubject();

        var created = await EnqueueAsync(Channels(), subject, AlertCategoryCatalog.BackupSucceeded);

        Assert.Equal(3, created);
        Assert.Equal(3, await CountAsync(subject));
    }

    /// <summary>
    /// 某个渠道把这一类排除掉之后，就不该再收到——这正是「加到可关闭选项里」要的效果。
    /// 而且是**按渠道**关：群里可以继续收全量，邮箱只留要紧的。
    /// </summary>
    [Fact]
    public async Task 渠道排除了这一类就不给它落单()
    {
        var channels = Channels();
        channels.Email.Filter.ExcludedCategories.Add(AlertCategoryCatalog.BackupSucceeded);
        var subject = NewSubject();

        var created = await EnqueueAsync(channels, subject, AlertCategoryCatalog.BackupSucceeded);

        Assert.Equal(2, created);
        Assert.Empty(await ChannelsOfAsync(subject, NotificationChannel.Email));
    }

    /// <summary>
    /// 不带类别的通知（日报）谁都关不掉。
    ///
    /// 日报要证明的恰恰是「这套系统今天还活着」——被关掉之后，
    /// 「一切正常」和「服务端三天前就死了」在收件人那里长得一模一样。
    /// </summary>
    [Fact]
    public async Task 不带类别的通知不受排除设置影响()
    {
        var channels = Channels();
        channels.Email.Filter.ExcludedCategories.Add(AlertCategoryCatalog.BackupSucceeded);
        channels.Dingtalk.Filter.ExcludedCategories.Add(AlertCategoryCatalog.BackupSucceeded);
        var subject = NewSubject();

        var created = await EnqueueAsync(channels, subject, category: null);

        Assert.Equal(3, created);
    }

    /// <summary>
    /// webhook 渠道的「收件人」里存的必须是标签，不是地址。
    ///
    /// webhook 地址含 access_token，本身就是凭据，而投递页会把这一列原样渲染出来。
    /// 真正发送时用的地址由 NotificationDispatchWorker 从解密后的渠道配置里现取。
    /// </summary>
    [Fact]
    public async Task webhook渠道不把地址写进收件人()
    {
        var subject = NewSubject();

        await EnqueueAsync(Channels(), subject, AlertCategoryCatalog.BackupSucceeded);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var recipients = await db.NotificationDeliveries.AsNoTracking()
            .Where(d => d.Subject == subject && d.Channel != NotificationChannel.Email)
            .Select(d => d.Recipient)
            .ToListAsync();

        Assert.Equal(2, recipients.Count);
        Assert.All(recipients, r => Assert.DoesNotContain("access_token", r));
        Assert.Contains(NotificationNotice.DingtalkRecipientLabel, recipients);
        Assert.Contains(NotificationNotice.WecomRecipientLabel, recipients);
    }

    private string NewSubject()
    {
        var subject = $"备份成功：测试任务 {Guid.NewGuid():N}";
        _subjects.Add(subject);
        return subject;
    }

    private static NotificationSettingsDto Channels() => new()
    {
        Email = { Enabled = true, Recipients = { "ops@example.com" } },
        Wecom = { Enabled = true, WebhookUrl = "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=SECRET" },
        Dingtalk = { Enabled = true, WebhookUrl = "https://oapi.dingtalk.com/robot/send?access_token=SECRET" }
    };

    private async Task<int> EnqueueAsync(NotificationSettingsDto channels, string subject, string? category)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var created = NotificationNotice.Enqueue(db, channels, subject, "正文", category);
        await db.SaveChangesAsync();
        return created;
    }

    private async Task<int> CountAsync(string subject)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.NotificationDeliveries.AsNoTracking().CountAsync(d => d.Subject == subject);
    }

    private async Task<List<Guid>> ChannelsOfAsync(string subject, NotificationChannel channel)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.NotificationDeliveries.AsNoTracking()
            .Where(d => d.Subject == subject && d.Channel == channel)
            .Select(d => d.Id)
            .ToListAsync();
    }
}
