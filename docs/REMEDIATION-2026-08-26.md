# 系统缺陷整改方案（2026-08-26）

本文档是一次全栈审查的产出。每条都经过代码验证，标注了证据位置、根因、落地方案、验收标准和工作量。
按批次组织，**批次 A 必须先做**——它决定这个系统算不算一个监控系统。

审查基线：`src/`（Api / Core / Infrastructure / Shared）、`agent/`、`database/V001__initial_schema.sql`、
`src/BackupMonitor.Api/wwwroot/`（管理端前端）。

## 目录

| 编号 | 级别 | 问题 | 工作量 |
|---|---|---|---|
| [A1](#a1) | P0 | 离线检测完全没有实现 | 1.5 人日 |
| [A2](#a2) | P0 | 备份没做不会产生任何告警 | 2 人日 |
| [A3](#a3) | P0 | 备份大小异常判定未实现 | 1 人日 |
| [A4](#a4) | P0 | 服务端仓库空间不足没有预警 | 0.5 人日 |
| [B1](#b1) | P1 | 限速字段 Agent 从不读取 | 1 人日 |
| [B2](#b2) | P1 | 「最长稳定等待」没有等待逻辑 | 1.5 人日 |
| [B3](#b3) | P1 | randomDelayMinutes 根本不下发 | 0.5 人日 |
| [B4](#b4) | P1 | 待办漏判大部分预检失败态 + 任务列表枚举组用错 | 0.5 人日 |
| [B5](#b5) | P1 | 待办页 200 条静默天花板 | 1 人日 |
| [C1](#c1) | P1 | SMTP/webhook 凭据明文存储、回显、进审计日志 | 2 人日 |
| [C2](#c2) | P1 | 邮件通知不支持 TLS，在真实邮箱服务上发不出去 | 1.5 人日 |
| [C3](#c3) | P1 | JWT 签名密钥无启动校验 | 0.25 人日 |
| [C4](#c4) | P2 | 权限烤死在令牌里，撤权最长 1 小时后才生效 | 1.5 人日 |
| [D1](#d1) | P2 | 幽灵状态：一批枚举值没有生产者 | 1.5 人日 |
| [D2](#d2) | P2 | 心跳每次空转写 3~6 条 UPDATE | 0.25 人日 |
| [D3](#d3) | P2 | Agent 每个分块裸分配 8MB，全部落 LOH | 0.5 人日 |
| [D4](#d4) | P2 | api.js 刷新失败返回 null 导致调用方 NPE | 0.25 人日 |
| [D5](#d5) | P2 | 告警中心缺「指派」与「临时静默」（规格 8.18） | 2 人日 |

合计约 19 人日。

---

# 批次 A：监控系统必须能发现「沉默」

这四条的共同根因是同一个架构缺口：**全系统 13 处 `RaiseAsync` 调用点，无一例外挂在「Agent 报上来了什么」之后**
（心跳、预检回报、注册、指令完成、保留清理、入库提交）。Agent 不报 = 什么都没发生。
一个备份监控系统最该报警的三件事——机器掉线、备份没做、备份变空——恰好都属于「什么都没发生」。

批次 A 补的就是这个缺口：引入**主动巡检**。

<a id="a1"></a>
## A1 · P0 · 离线检测完全没有实现

### 现状与证据

功能说明书 7.3 明确定义：*2 个心跳周期内在线／超 3 个周期疑似离线／超 5 分钟离线*。
数据库种子里阈值也种好了：

- `database/V001__initial_schema.sql:1084` — `client_offline_threshold_seconds = 300`
- `database/V001__initial_schema.sql:1085` — `client_suspected_offline_seconds = 180`

这两个键在整个 C# 代码里**零引用**（`grep -rn "client_offline_threshold" --include=*.cs` 无结果）。

全系统只有 3 处写 `ClientStatus`，全部是被动触发：

- `ClientAdminService.cs:499` 审批 → Online
- `ClientAdminService.cs:569` 证书续签 → Online
- `AgentHeartbeatService.cs:91-95` 心跳到达且当前是 Offline/SuspectedOffline/CertificateExpired → 翻回 Online

已注册的 5 个 `HostedService`（`DependencyInjection.cs:68-71` + `Program.cs:85`）里没有任何巡检器。

**后果**：机房断电、Agent 崩溃、网线松了——客户端列表永远显示「在线 · 最近心跳 3 天前」，
不告警、不通知。`suspected_offline` 在筛选下拉里存在，但永远筛不出任何一行。

### 方案

新增 `src/BackupMonitor.Infrastructure/Services/ClientLivenessWorker.cs`，
完全照 `RetentionCleanupWorker` 的形状（`IServiceScopeFactory` + `IScheduledLockService` 单实例锁）：

```csharp
public class ClientLivenessWorker : BackgroundService
{
    public const string IntervalKey = "client_liveness_interval_seconds";   // 新增，默认 30
    public const string LockKey = "worker:client_liveness";
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(5);

    private async Task RunPassAsync(IServiceScope scope, CancellationToken ct)
    {
        var suspectedAfter = await settings.GetIntAsync("client_suspected_offline_seconds", 180, ct);
        var offlineAfter   = await settings.GetIntAsync("client_offline_threshold_seconds", 300, ct);
        var now = DateTime.UtcNow;

        // 只看仍在服役的客户端。刚审批完还没来得及发第一次心跳的，
        // 用 ApprovedAt 兜底，不能一上来就判离线。
        var watched = await db.Clients
            .Where(c => c.Status == ClientStatus.Online || c.Status == ClientStatus.SuspectedOffline)
            .ToListAsync(ct);

        var offlineTransitions = new List<Client>();
        foreach (var client in watched)
        {
            var lastSeen = client.LastHeartbeatAt ?? client.ApprovedAt ?? client.CreatedAt;
            var silence = (now - lastSeen).TotalSeconds;

            if (silence >= offlineAfter)
            {
                client.Status = ClientStatus.Offline;
                offlineTransitions.Add(client);      // 事务提交后再发告警
            }
            else if (silence >= suspectedAfter && client.Status == ClientStatus.Online)
            {
                client.Status = ClientStatus.SuspectedOffline;
            }
        }

        await db.SaveChangesAsync(ct);

        // 告警是旁路副作用，放在状态落库之后（与 AgentHeartbeatService 的做法一致）
        foreach (var client in offlineTransitions)
            await alerting.RaiseAsync(
                $"client:{client.Id}:offline", AlertLevel.Critical, "client_offline",
                $"客户端 {client.DisplayName} 已离线",
                $"最近心跳 {client.LastHeartbeatAt:yyyy-MM-dd HH:mm:ss} UTC，已静默超过 {offlineAfter} 秒",
                clientId: client.Id, ct: ct);
    }
}
```

配套改动：

1. **恢复告警**：`AgentHeartbeatService.cs:91-95` 那个「翻回 Online」的分支里补一句
   `await _alerting.RecoverAsync($"client:{client.Id}:offline", ct);`（注意放在事务提交后的旁路段落里，
   和现有 service_state 告警一起，不要放进事务）。
2. **注册**：`DependencyInjection.cs:71` 之后加 `services.AddHostedService<ClientLivenessWorker>();`
3. **新配置项**：`client_liveness_interval_seconds` 写进新的迁移脚本（见「发布注意事项」）。

### 为什么不用 `ExecuteUpdateAsync` 批量改

批量 UPDATE 快，但拿不到「哪些客户端刚刚转成离线」这个集合，就发不出告警。
客户端数量级是几十到几百，一次全量加载没有性能问题，且
`idx_clients_status_heartbeat (status, last_heartbeat_at)`（`V001:339`）正好覆盖这个查询。

### 验收标准

1. 停掉一台客户端的 Agent 服务；180 秒后列表状态变「疑似离线」，300 秒后变「离线」。
2. 转「离线」的同时，告警中心出现一条 `client_offline` 的**严重**告警，通知渠道收到。
3. 重新启动 Agent；一次心跳后状态回「在线」，该告警状态变为「已恢复」。
4. 已禁用 / 已注销 / 待审批的客户端不参与判定，状态不被改写。
5. 刚审批通过但尚未发出第一次心跳的客户端，在 300 秒内不被判离线。

<a id="a2"></a>
## A2 · P0 · 备份没做不会产生任何告警

### 现状与证据

告警 category 的全集只有 10 个（`grep -rn "RaiseAsync(" -A 3` 统计）：
`client_resource`(3 处)、`verification_failed`、`upload_commit_failed`、`service_state`、
`retention_delete_failed`、`retention_breaker_tripped`、`restore_verify_failed`、`precheck_failed`、
`client_enrollment`、`certificate_expiry`。

「今天该备份的没备份」目前唯一的体现是概览页日历上的一个红格子
（`ReportService.ResolveDayStatus`，`ReportService.cs:395-418`）——需要有人主动打开那一页并看懂那个格子。
不进告警中心、不发邮件、不进待办。

### 方案

新增 `src/BackupMonitor.Infrastructure/Services/MissedBackupWorker.cs`：

```csharp
public const string IntervalKey = "missed_backup_scan_interval_minutes";  // 新增，默认 15
public const string GraceKey    = "missed_backup_grace_minutes";          // 新增，默认 120
public const string LockKey     = "worker:missed_backup";

// 每轮：
foreach (var task in enabledScheduledTasks)   // Enabled && ScanSchedule != null
{                                             // && TaskMode 不是 paused / monitor_only
    if (!CronExpression.TryParse(task.ScanSchedule, out var cron, out _)) continue;
    var tz = ResolveTimeZone(task.ScheduleTimezone);

    var prevDue = cron.GetPreviousOccurrence(now, tz);      // ← 需要新增，见下
    if (prevDue is null) continue;

    var deadline = prevDue.Value.AddMinutes(grace);
    if (now < deadline) continue;                           // 还在宽限期内

    var alertKey = $"task:{task.Id}:missed";
    if (task.LastSuccessAt is null || task.LastSuccessAt < prevDue)
        await alerting.RaiseAsync(alertKey,
            task.ImportanceLevel >= ImportanceLevel.High ? AlertLevel.Critical : AlertLevel.Warning,
            "backup_missed",
            $"任务 {task.Name} 未按计划产生备份",
            $"计划时间 {prevDue:yyyy-MM-dd HH:mm}（{tz.Id}），已超过 {grace} 分钟宽限期仍无成功入库",
            clientId: task.ClientId, taskId: task.Id, ct: ct);
    else
        await alerting.RecoverAsync(alertKey, ct);
}
```

### 前置改动：`CronExpression.GetPreviousOccurrence`

`src/BackupMonitor.Shared/Scheduling/CronExpression.cs` 目前只有 `GetNextOccurrence`（:96）。
需要补一个对称的反向查找，**镜像 GetNext 的日期跳跃逻辑**（不匹配日期时整天跳过，
而不是逐分钟回退——逐分钟在 4 年搜索窗口下是两百万次迭代）：

```csharp
public DateTime? GetPreviousOccurrence(DateTime beforeUtc, TimeZoneInfo timeZone)
{
    var candidate = TimeZoneInfo.ConvertTimeFromUtc(...).AddMinutes(-1);
    var limit = candidate.AddYears(-SearchYears);
    while (candidate > limit)
    {
        if (!MatchesDate(candidate))
        {
            candidate = candidate.Date.AddDays(-1).AddHours(23).AddMinutes(59);
            continue;
        }
        if (!_hours[candidate.Hour])
        {
            candidate = candidate.Date.AddHours(candidate.Hour).AddMinutes(-1);
            continue;
        }
        if (_minutes[candidate.Minute])
            return TimeZoneInfo.ConvertTimeToUtc(candidate, timeZone);
        candidate = candidate.AddMinutes(-1);
    }
    return null;
}
```

`tests/BackupMonitor.Infrastructure.Tests/CronExpressionTests.cs` 已存在，**必须补单测**：
每天/每周/每月/每小时四种表达式，各断言
`GetPreviousOccurrence(GetNextOccurrence(t)) == t`（往返一致），
并覆盖夏令时切换日和跨月边界。

### 需要注意的坑

- **不要给每分钟级的 cron 报漏备份**：`*/5 * * * *` 这类任务「漏一次」没有运维意义。
  建议：`prevDue` 与其前一次的间隔小于 `grace` 时直接跳过该任务。
- **首次运行不要炸一屏告警**：新部署或长期停机后重启，所有任务都会命中。
  加一条守卫：任务创建时间距今不足一个宽限期时跳过。
- 恢复告警也可以顺手挂在 `UploadCommitWorker` 提交成功处，但 worker 每轮的 `RecoverAsync` 已经够用，
  不必两处都写。

### 验收标准

1. 建一个 `0 2 * * *` 的任务，把客户端 Agent 停掉；次日 04:01（2 点 + 120 分钟宽限）
   出现 `backup_missed` 告警。
2. 恢复 Agent 并成功入库一次；下一轮巡检把该告警置为「已恢复」。
3. 新建一个任务后立刻巡检，不产生告警。
4. `*/10 * * * *` 的任务不产生漏备份告警。
5. `CronExpressionTests` 全绿，含新增的往返一致性用例。

<a id="a3"></a>
## A3 · P0 · 备份大小异常判定未实现

### 现状与证据

`minTotalBytes` / `maxTotalBytes` / `minFileCount` 三个字段：

- 数据库有列（`BackupConfigurations.cs:63-65`）
- 实体有属性、PUT 模型收、`ApplyEditableFields` 赋值（`BackupTaskService.cs:674-676`）
- 任务详情页显示「大小限制 1 GB ~ 50 GB，文件数 ≥ 3」（`tasks.js:368`）

搜遍 `src/` 与 `agent/`：**没有任何一处做比较**。

配套地，`PrecheckStatus.SizeAbnormal`（`Enums.cs:45`）只在 `ReportService.cs:435` 被当作「失败态」消费，
**没有任何地方产生它**。

「备份文件突然变成 0 字节」——备份故障里最经典、最致命、也最容易检测的一种——系统认不出来。

### 方案

判定放在**服务端**，不放 Agent：数据全都已经在预检回报里了（`request.TotalBytes` / `request.TotalFiles`），
放服务端可以避免改配置 DTO 和签名（见 B3 的兼容性代价），也让旧版 Agent 立刻受益。

`src/BackupMonitor.Infrastructure/Services/AgentPrecheckService.cs`：

1. 现有的服务端复检钩子在 `:60`：
   ```csharp
   var validationError = ValidatePassedResult(task.RecognizerConfig, request);
   ```
   把签名改成接收 `task`，并在其后追加阈值判定：
   ```csharp
   var sizeError = ValidateSizeThresholds(task, request);
   if (sizeError is not null)
   {
       status = PrecheckStatus.SizeAbnormal;
       failureCode = "SIZE_ABNORMAL";
       failureMessage = sizeError;
   }
   ```

2. 新增私有方法：
   ```csharp
   private static string? ValidateSizeThresholds(BackupTask task, SubmitPrecheckResultRequest request)
   {
       var bytes = request.TotalBytes ?? 0;
       var files = request.TotalFiles ?? 0;
       if (task.MinTotalBytes is > 0 && bytes < task.MinTotalBytes)
           return $"备份总大小 {bytes} 字节低于下限 {task.MinTotalBytes} 字节——备份可能未正常生成";
       if (task.MaxTotalBytes is > 0 && bytes > task.MaxTotalBytes)
           return $"备份总大小 {bytes} 字节超过上限 {task.MaxTotalBytes} 字节";
       if (task.MinFileCount is > 0 && files < task.MinFileCount)
           return $"备份文件数 {files} 少于下限 {task.MinFileCount}";
       return null;
   }
   ```

3. **告警分级**：现有的 `status is not (Passed or NoNewBackup)` 分支（`:220`）会把它归入
   `precheck_failed` 且等级只有 `Notice`。大小异常必须更响：在那个分支前单独处理
   `SizeAbnormal` → category `size_abnormal`、等级 `Warning`（重要级 high/critical 的任务升 `Critical`）。

4. **前端**：`ui.js` 的 `L.precheck` 补齐 `size_abnormal: '大小异常'`、`access_denied: '拒绝访问'`；
   `STATUS_MAP` 补 `size_abnormal: ['err', 'pill']`、`access_denied: ['err', 'pill']`。

5. **表单**：任务编辑表单目前根本没有这三个字段的输入项（只在详情页显示），
   `taskFormValues` 靠 `carried` 原样回填。补进高级选项，用 `type: 'number'` + 单位提示。
   建议再加一个「按最近 5 次备份的平均值 ±50% 自动填」的按钮——人填不出合理的字节数。

### 验收标准

1. 设 `minTotalBytes = 1048576`，把源目录的 .bak 截断成 0 字节，触发预检
   → 候选状态 `size_abnormal`，任务列表「最近预检」显示「大小异常」，告警中心出现 `size_abnormal` 告警。
2. 该候选不能被上传（`PrecheckStatus != Passed` 已有拦截，回归确认）。
3. 三个阈值都留空时行为与改动前完全一致。
4. 重要级 critical 的任务，大小异常告警等级为「严重」。

<a id="a4"></a>
## A4 · P0 · 服务端仓库空间不足没有预警

### 现状与证据

`UploadSessionService.cs:126-129` 在创建上传会话时检查暂存空间，不足则抛 507 `STORAGE_SPACE_LOW`。
这是**拒收**，不是**预警**——第一次知道磁盘要满，是备份已经传不上来的时候。

`ReportService.cs:320-321` 已经有读取磁盘容量的实现，只用于概览页展示。

### 方案

合并进 A1 的 worker（建议 worker 直接命名 `SystemWatchdogWorker`，
同时承担客户端存活与服务端自检），每轮追加一段：

```csharp
var warnPercent = await settings.GetIntAsync("server_storage_alert_percent", 15, ct);  // 新增
foreach (var (name, root) in new[] { ("仓库", repositoryRoot), ("暂存区", stagingRoot) })
{
    var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
    if (!drive.IsReady) continue;
    var freePercent = drive.AvailableFreeSpace * 100.0 / drive.TotalSize;
    var alertKey = $"system:storage:{name}";
    if (freePercent <= warnPercent)
        await alerting.RaiseAsync(alertKey,
            freePercent <= 5 ? AlertLevel.Critical : AlertLevel.Warning,
            "server_storage_low",
            $"服务端{name}磁盘空间不足",
            $"可用 {freePercent:0.#}%（阈值 {warnPercent}%），备份上传即将开始被拒绝",
            ct: ct);
    else
        await alerting.RecoverAsync(alertKey, ct);
}
```

### 验收标准

1. 把阈值临时调到 99，一轮巡检后出现 `server_storage_low` 告警；调回 15 后告警转「已恢复」。
2. 仓库盘与暂存盘分别在不同磁盘上时，两条告警独立。

---

# 批次 B：界面上写着，实际不生效

这一批和「上传窗口只填一头等于没填」是同一个病：
**界面承诺了一件系统不做的事，而且没有任何反馈。**
每一条都要么补上实现，要么把界面上的承诺撤掉——不能留在中间状态。

<a id="b1"></a>
## B1 · P1 · 限速字段 Agent 从不读取

### 现状与证据

服务端存它、校验它、在三个不同的指令 payload 里下发它：

- `BackupTaskService.cs:512`（单条上传）
- `AgentPrecheckService.cs:263`（预检后自动上传）
- `BatchOperationService.cs:226`（批量上传）

`AgentTaskConfigDto.BandwidthLimitKbps`（`ConfigDtos.cs:41`）也已下发，
并且**已经在配置签名的字段列表里**（`AgentSignatureCanonicalizer.cs:76`）——
所以实现它不需要改 DTO、不需要改签名、没有版本兼容问题。

`grep -rn "andwidth" agent/ --include=*.cs` → **0 处**。
`AgentWorker.UploadChunkWithRetryAsync`（:684）里没有任何节流。

### 方案

在 `agent/BackupMonitor.Agent/AgentWorker.cs` 里加一个按分块粒度的节流器。
分块默认 8MB，对 1024 KB/s 而言一块要 8 秒，块级粒度的精度完全够用，不需要字节级令牌桶：

```csharp
private sealed class ChunkThrottle
{
    private readonly int _bytesPerSecond;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _sentBytes;

    public ChunkThrottle(int? kbps) => _bytesPerSecond = kbps is > 0 ? kbps.Value * 1024 : 0;

    public async Task AfterChunkAsync(int bytes, CancellationToken ct)
    {
        if (_bytesPerSecond <= 0) return;
        _sentBytes += bytes;
        var expected = TimeSpan.FromSeconds((double)_sentBytes / _bytesPerSecond);
        var delay = expected - _clock.Elapsed;
        if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
    }
}
```

- 在 `ExecuteUploadAsync` 里构造一次（整个会话共用一个实例，这样限速是会话级平均值而不是每块独立），
  限速值优先取指令 payload 里的 `bandwidthLimitKbps`，回退到 `task.BandwidthLimitKbps`。
- 在 `UploadChunkWithRetryAsync` 成功返回后调用 `AfterChunkAsync(length, ct)`。
- **重要**：节流放在上传成功之后而不是之前，重试不重复计费。

### 决策点

如果决定**不实现**，就必须把界面上的「限速」字段和任务详情里的「限速 / 分块」那一行删掉，
并从 DTO 和数据库列里移除。留着一个不生效的限速框，比没有这个功能更糟。

### 验收标准

1. 设 1024 KB/s，上传一个 100MB 的备份集，实际耗时 100 秒 ±10%。
2. 不设限速时，上传耗时与改动前无差异（回归）。
3. 上传过程中 Agent 心跳不中断（节流用的是 `Task.Delay`，不阻塞线程）。

<a id="b2"></a>
## B2 · P1 · 「最长稳定等待」没有等待逻辑

### 现状与证据

`agent/BackupMonitor.Agent/BackupScanner.cs:62-68`：

```csharp
var stabilitySeconds = Math.Max(0, task.StabilityIntervalSeconds);
if (stabilitySeconds > 0 && DateTime.UtcNow - newest < TimeSpan.FromSeconds(stabilitySeconds))
{
    results.Add(Failure(task, selected.Root, "still_changing", "最新文件仍处于稳定观察窗口", businessUnit));
    continue;   // ← 直接放弃，等下一次 cron
}
```

`maxStabilityWaitSeconds` 在 Agent 里**零引用**（它已经下发到 `AgentTaskConfigDto` 并进了签名，
所以同样不需要改 DTO）。

**真实后果**：备份 02:00 开始写、02:30 写完，cron 设 02:00 → 每天扫到的都是 `still_changing`，
**这一天就没有候选备份集**。任务显示「已启用」，一切看起来正常，但什么都没采到。
字段名叫「最长稳定等待 600 秒」，会让人以为系统会等——它不会。

### 方案

**关键约束：不能在 AgentWorker 的主循环里同步 sleep 10 分钟**——主循环是串行的
（`AgentWorker.cs:67-100`），阻塞它会让心跳停摆，进而被 A1 判成离线。

因此分两条路径处理：

**路径一 · 计划扫描与指令预检（异步重排）**

在 `AgentStateStore` 里新增 `PendingRestabilizeScans: Dictionary<taskId, RestabilizeEntry>`，
`RestabilizeEntry { FirstAttemptAtUtc, NextRetryAtUtc, AttemptCount }`：

```
扫描返回 still_changing
  → 若该任务没有 pending 记录：建一条，FirstAttemptAt = now，NextRetryAt = now + stabilityInterval
  → 若已有记录且 now - FirstAttemptAt >= maxStabilityWaitSeconds：
        放弃，按 still_changing 上报（现有行为），清除记录
  → 否则：NextRetryAt = now + stabilityInterval，不上报，等下一轮
主循环每轮检查 PendingRestabilizeScans，到点的重新扫描
```

这样等待是「跨主循环轮次」的，心跳照常，且 `maxStabilityWaitSeconds` 有了真实语义：
在这个预算内反复重试，超预算才认输。

**路径二 · 识别测试（同步等待，人在等）**

`ExecuteRecognitionTestAsync`（:425）是人点了「试一下识别」在对话框前等结果的场景，
可以在 `min(maxStabilityWait, 60s)` 内同步重试，并在结果里明确写「等待了 N 秒后仍在变化」。

**上报也要带上重试信息**：`still_changing` 最终上报时，`failureMessage` 里写清
「已在 600 秒内重试 5 次，文件仍在变化」，否则运维看到的还是一句没有信息量的话。

### 验收标准

1. 源目录里持续写入文件，触发计划扫描 → Agent 日志出现「等待稳定，第 N 次重试」，心跳不中断。
2. 在 `maxStabilityWaitSeconds` 内停止写入 → 本次扫描最终成功产出候选，不需要等下一个 cron。
3. 持续写入超过 `maxStabilityWaitSeconds` → 上报 `still_changing`，消息里含重试次数。
4. `maxStabilityWaitSeconds = 0` 或小于 `stabilityIntervalSeconds` 时退化成当前行为（扫一次就放弃）。

<a id="b3"></a>
## B3 · P1 · randomDelayMinutes 根本不下发

### 现状与证据

`BackupTask.RandomDelayMinutes` 存在实体里、`ApplyEditableFields:670` 会 `Math.Clamp(0, 1440)`、
编辑任务时被 `taskFormValues` 的 `carried` 原样保住。
但 `AgentTaskConfigDto`（`ConfigDtos.cs:22-48`）里**压根没有这个字段**。纯死字段。

它本该解决的问题是真实存在的：50 台客户端的 cron 都写 `0 2 * * *`，
到点同时开扫、同时上传，服务端和网络一起被打满。

### 方案（二选一，建议 A）

**方案 A · 实现它**

1. `AgentTaskConfigDto` 加 `public int RandomDelayMinutes { get; set; }`
2. `AgentConfigService.cs:79-96` 的映射里加 `RandomDelayMinutes = t.RandomDelayMinutes`
3. `AgentSignatureCanonicalizer.cs` 的字段列表里加同一项
   （位置放在 `ChunkSizeBytes` 之后、`RecognizerConfig` 之前，顺序一旦定下不能再改）
4. Agent 侧 `SaveScheduleEntry`（`AgentWorker.cs:848`）算 `NextDueAtUtc` 时叠加
   `TimeSpan.FromMinutes(Random.Shared.Next(0, task.RandomDelayMinutes + 1))`
5. 表单里补一个输入项（高级选项，`type: 'number'`，提示「0~1440 分钟，用来错开多台机器同时扫描」）

> **兼容性警告**：canonicalizer 是 `Shared` 里的共享实现，服务端和 Agent 各自编译进自己的程序集。
> 改了字段列表后，**旧版 Agent 会验签失败**。按 `AgentSignatureVerifier` 的设计，
> 验签失败时 Agent 会拒绝新配置并继续使用本地缓存配置（功能说明书 7.4），不会崩溃，
> 但**配置从此不再更新**，直到 Agent 升级。
>
> 因此发布顺序必须是：**先通过 Agent 升级通道把新版 Agent 推下去，确认全部客户端升级完成，再升服务端**。
> 如果做不到全量升级，改用方案 B。

**方案 B · 删掉它**

删除实体属性、数据库列（新迁移）、`ApplyEditableFields` 的参数、`taskFormValues` 的 carried 项。
零风险，且比留一个死字段诚实。

### 验收标准

- 方案 A：3 台客户端配同一个 `0 2 * * *` + `randomDelayMinutes = 30`，
  观察三次实际触发时刻分散在 02:00~02:30 内且互不相同；Agent 日志里 `next=` 的时刻含随机偏移。
- 方案 B：全库搜索 `RandomDelay` 无残留，任务编辑保存后不再出现该字段。

<a id="b4"></a>
## B4 · P1 · 待办漏判预检失败态 + 任务列表枚举组用错

### 现状与证据

`lastPrecheckStatus` 是 `PrecheckStatus` 的 snake_case（`BackupTaskService.cs:336-337`），
取值包括 `path_not_found`、`required_file_missing`、`access_denied`、`still_changing`、`no_new_backup`。

三处都错：

1. **`todo.js:38`**
   ```js
   ['failed', 'failure', 'error'].includes(String(t.lastPrecheckStatus || '').toLowerCase())
   ```
   → **源路径写错（`path_not_found`）这个最常见的配置错误，永远不进待办**。
   `required_file_missing`（备份不完整）同样漏掉。

2. **`tasks.js:49`**
   ```js
   { l: '最近预检', render: r => r.lastPrecheckStatus ? status('result', r.lastPrecheckStatus) : '—' }
   ```
   用的是 `L.result`（只有 `{success, failure}`），查不到就回退显示原始英文。
   于是界面显示英文的 `path_not_found`，而 `L.precheck` 里就躺着正确的「路径不存在」。

3. **`todo.js:51`** 同样直接 `esc(task.lastPrecheckStatus)`，显示英文。

### 方案

```js
// todo.js:38 —— 改成白名单，未来新增状态自动纳入待办，不会再漏
const PRECHECK_OK = ['passed', 'no_new_backup', 'not_scanned', ''];
const precheckBad = !PRECHECK_OK.includes(String(t.lastPrecheckStatus || '').toLowerCase());

// todo.js:51
const label = L.precheck[task.lastPrecheckStatus] || task.lastPrecheckStatus;

// tasks.js:49
status('precheck', r.lastPrecheckStatus)
```

`ui.js` 的 `L.precheck` 补齐缺失项：`access_denied: '拒绝访问'`、`size_abnormal: '大小异常'`
（以及 D1 决定保留的其他值），`STATUS_MAP` 补对应的形状。

> `still_changing` 是否算待办有讨论空间：B2 实现后它意味着「等满了预算还在变」，是真问题，应该算。

### 验收标准

1. 把某任务源路径改成不存在的目录 → 预检 → 待办出现该任务，理由显示「路径不存在」。
2. 任务列表「最近预检」列全部显示中文，无英文枚举名泄漏。
3. 正常通过的任务不出现在待办里（回归）。

<a id="b5"></a>
## B5 · P1 · 待办页 200 条静默天花板

### 现状与证据

`todo.js:26-31` 拉 `backup-tasks?page=1&pageSize=200` 然后在浏览器里过滤，
而 `PagedQuery.MaxPageSize = 200`（`PagedQuery.cs:6`）——所以 200 就是硬上限。
超过 200 个任务时，**待办会静默漏掉后面的**，没有任何提示。
待审批客户端和严重告警各卡在 100 条，同样问题。

而且「哪些任务算有问题」这段业务逻辑写在浏览器里，服务端没有对应的权威判定。

### 方案（建议先做短期方案）

**短期 · 给列表加过滤参数（半天）**

`BackupTaskQuery` 加 `PrecheckStatus`（多值）与 `OnlyProblematic`（bool），
`BackupTaskService.GetListAsync` 里落成 `Where`。待办只拉真正需要的那些行，天花板问题自然消失。

**中期 · 服务端聚合端点（推荐最终形态）**

新增 `GET /api/v1/admin/reports/todo-summary`，返回：

```json
{
  "pendingApprovalClients": { "count": 3, "items": [] },
  "problematicTasks":       { "count": 41, "items": [] },
  "readyRestores":          { "count": 2,  "items": [] },
  "criticalAlerts":         { "count": 7,  "items": [] },
  "recentAutoEnrollments":  { "count": 1,  "items": [] }
}
```

计数由服务端 `CountAsync` 得出，不受分页影响；明细只回前 N 条，列表页负责「查看全部」。
`ReportService` 已有 `client-summary` / `task-summary` / `alert-summary` 的先例，照着写即可。

顺带把「哪些任务算有问题」的判定从浏览器搬到服务端——这是业务规则，不该有两份。

### 验收标准

1. 造 300 个任务，让第 250 个的预检失败 → 待办计数正确，且该任务能被找到。
2. 待办页的请求数不随任务总数增长。
3. 前端不再出现 `pageSize=200` 这类「拉一大把再自己过滤」的写法。
   （2026-08-26：待办页已改走聚合端点；建任务的客户端选择与 Agent 升级下发的客户端多选，
   这两处原本也是一次拉 200 台铺开的写法，已一并改成搜索式选择器——
   `ui.js` 的 `searchPickerHtml` / `initSearchPicker`，按关键字问服务端要匹配项，
   只展示前 20 条并明说「还有 N 条未显示」。）

---

# 批次 C：安全

<a id="c1"></a>
## C1 · P1 · SMTP / webhook 凭据明文存储、明文回显、明文进审计日志

### 现状与证据

`NotificationService.cs:139-158`：

```csharp
var json = JsonSerializer.Serialize(request, JsonOpts);   // 含 smtpPassword / webhookUrl
row = new SystemSetting { SettingValue = json, Encrypted = false, ... };
...
await _audit.RecordAsync("notification.update_settings", AuditResult.Success, "system_setting", null,
    afterData: json, ct: ct);     // ← 整包凭据进审计日志
```

三个放大：

1. `system_settings` 表专门有一个 `encrypted` 列（`OtherConfigurations.cs:189`），
   说明设计上就要求加密——实现没做，全库三处写入全是 `Encrypted = false`。
2. `GetSettingsAsync` 原样返回密码，前端塞进
   `value="${esc(s.email.smtpPassword || '')}"`（`notifications.js:31`）
   → 明文出现在 HTTP 响应体和 DOM 里。
3. **越权路径**：通知配置由 `perm:system.manage` 管，审计日志由 `perm:audit.read` 看。
   凭据落进 `audit_logs.after_data` 后，**只有审计权限的人能读到管理员才该有的 SMTP 密码
   和钉钉/企微 webhook**（webhook URL 里的 access_token 本身就是凭据）。

其余 12 处 `afterData` 都只序列化窄字段集，只有这一处整包倒。

### 方案

**1. 引入密钥保护抽象**

项目目前没有注册 `DataProtection`（`grep AddDataProtection` 无结果）。新增：

```csharp
// src/BackupMonitor.Infrastructure/Security/SecretProtector.cs
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string payload);     // 非保护格式原样返回，供历史明文就地迁移
    bool IsProtected(string value);       // 约定前缀，如 "enc:v1:"
}
```

用 `Microsoft.AspNetCore.DataProtection`，密钥环持久化到数据目录
（Turnkey 模式复用 `LocalServerBootstrap` 已经建好并加了 ACL 的 ProgramData 目录；
Secure 模式读 `Security:DataProtection:KeyPath`）：

```csharp
services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyPath))
    .SetApplicationName("BackupMonitor");
```

**2. 写入时加密**

`UpdateSettingsAsync` 里，序列化前把 `Email.SmtpPassword`、`Wecom.WebhookUrl`、`Dingtalk.WebhookUrl`
换成 `_protector.Protect(...)`，`Encrypted = true`。

**3. 读取分两条路**

```csharp
Task<NotificationSettingsDto> GetSettingsAsync(...)           // 内部用（worker 发信），解密
Task<NotificationSettingsDto> GetSettingsForDisplayAsync(...) // 控制器用，凭据字段替换为掩码
```

掩码约定：有值时返回常量 `"__UNCHANGED__"`，无值时返回 `null`。
`UpdateSettingsAsync` 收到 `"__UNCHANGED__"` 时保留库里原值。
前端 `notifications.js` 相应地把密码框的 `value` 去掉、`placeholder` 改成「已配置，留空则不修改」。

**4. 审计只记非机密字段**

```csharp
afterData: JsonSerializer.Serialize(new {
    email    = new { request.Email.Enabled, request.Email.SmtpHost, request.Email.SmtpPort,
                     recipientCount = request.Email.Recipients.Count },
    wecom    = new { request.Wecom.Enabled },
    dingtalk = new { request.Dingtalk.Enabled }
})
```

**5. 历史数据处理（不能只靠加密补救）**

- 首次读取时若发现是明文，就地重新加密（lazy migration），日志记一条 INFO。
- **必须提示轮换**：已经进过审计日志的凭据要视为已泄露。在整改说明里明确要求运维
  重置 SMTP 密码、重新生成钉钉/企微机器人 webhook。
- 清理历史审计（作为迁移脚本或运维手册的一条）：
  ```sql
  UPDATE audit_logs SET after_data = '{"redacted":true}'
  WHERE action = 'notification.update_settings';
  ```

### 验收标准

1. 保存 SMTP 密码后，`GET /api/v1/admin/notification-settings` 的响应体里搜不到密码明文。
2. `SELECT setting_value FROM system_settings WHERE setting_key='notification_channels'` 不含明文，
   `encrypted = true`。
3. 新产生的 `notification.update_settings` 审计记录里不含任何凭据。
4. 只留空密码框保存其他字段 → 邮件仍能正常发送（原密码未被清空）。
5. 升级前已存在的明文配置，服务重启后能正常发信，并在首次读取后转为密文。

<a id="c2"></a>
## C2 · P1 · 邮件通知在真实邮箱服务上发不出去

### 现状与证据

`NotificationDispatchWorker.cs:216-228` 用 `System.Net.Mail.SmtpClient`
（微软官方文档标注为不建议用于新开发），并且**从不设置 `EnableSsl`**；
`EmailChannelSettingsDto`（`NotificationDtos.cs:46-63`）里也没有这个开关，端口默认 25。

后果：

- 腾讯企业邮、阿里云邮、Exchange Online、Gmail 全部强制 TLS → **发送直接失败**；
- 465 端口的隐式 TLS，`SmtpClient` 根本不支持；
- 若碰上允许明文 AUTH 的服务器，SMTP 密码明文过网。

而且配错了没有任何即时反馈——只能等下一次告警产生时在「通知记录」里看到失败。

### 方案

1. **换库**：引入 `MailKit` / `MimeKit`（微软文档指定的替代品），支持 STARTTLS 与隐式 TLS：
   ```csharp
   using var client = new MailKit.Net.Smtp.SmtpClient();
   await client.ConnectAsync(email.SmtpHost, email.SmtpPort,
       email.SecurityMode switch {
           "none"     => SecureSocketOptions.None,
           "starttls" => SecureSocketOptions.StartTls,
           "ssl"      => SecureSocketOptions.SslOnConnect,
           _          => SecureSocketOptions.Auto        // 默认按端口自动协商
       }, ct);
   if (!string.IsNullOrWhiteSpace(email.SmtpUsername))
       await client.AuthenticateAsync(email.SmtpUsername, email.SmtpPassword, ct);
   ```

2. **DTO 加字段**：`SecurityMode`（`auto` / `none` / `starttls` / `ssl`，默认 `auto`），
   `SmtpPort` 默认值从 25 改成 587。前端加一个下拉。

3. **加「发送测试邮件」按钮**（强烈建议，成本极低）：
   `POST /api/v1/admin/notification-settings/test`，用当前表单里的配置试发一封，
   把 SMTP 服务器返回的错误原样带回界面。没有这个按钮，配错了要等到出事才知道。

4. **时区**：`NotificationDispatchWorker.cs:211` 邮件正文里写的是
   `发生时间: ... UTC`，而全站其他地方用的是本地时区。改成按系统配置的报表时区渲染并标注时区名。

### 验收标准

1. 对 587/STARTTLS 与 465/隐式 TLS 各成功发送一封测试邮件。
2. 故意填错密码 → 测试按钮返回 SMTP 服务器的原始错误信息（如 `535 Authentication failed`）。
3. 邮件正文里的时间是本地时区并带时区标注。
4. 现有的企微/钉钉 webhook 投递不受影响（回归）。

<a id="c3"></a>
## C3 · P1 · JWT 签名密钥无启动校验

### 现状与证据

`appsettings.json:31` 里 `"SigningKey": ""`。
Turnkey 模式会自动生成 48 字节随机密钥并写入 ACL 保护的 `server-secrets.json`
（`LocalServerBootstrap.cs:205`）——这块做得很好。

但 Secure / 开发模式如果漏配 `Security__Jwt__SigningKey` 环境变量，
`Program.cs:224-235` 照常启动，直到第一次登录才在
`JwtTokenService.cs:28` 的 `Encoding.UTF8.GetBytes("")` 上炸出一个没有上下文的 500。
全代码库没有任何长度或非空校验。

### 方案

`Program.cs` 在 `AddJwtBearer` 之前加一段快速失败：

```csharp
static void RequireSecret(string? value, int minBytes, string configPath, string envName, string hint)
{
    if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) < minBytes)
        throw new InvalidOperationException(
            $"{configPath} 未配置或强度不足（至少 {minBytes} 字节）。{hint}"
            + $" 生产环境请设置环境变量 {envName}；LAN Turnkey 模式会自动生成，无需手工配置。");
}

RequireSecret(jwtSettings.SigningKey, 32, "Security:Jwt:SigningKey", "Security__Jwt__SigningKey",
    "该密钥用于签发管理端登录令牌，弱密钥等同于任何人都能伪造管理员身份。");
```

同样处理 `Security:CommandSigningPrivateKey`（指令签名，`CommandSigner.cs`）和
`ConnectionStrings:Default`。

### 验收标准

1. 清空 `Security__Jwt__SigningKey` 以 Secure 模式启动 → 启动即失败，
   日志里一句话说清缺什么、怎么补。
2. Turnkey 模式（`BACKUPMONITOR_TURNKEY=1`）启动不受影响。
3. 配一个 16 字节的短密钥 → 同样被拒绝。

<a id="c4"></a>
## C4 · P2 · 权限烤死在令牌里，撤权最长 1 小时后才生效

### 现状与证据

权限以 claim 形式写进 access token（`Program.cs:253-259` 的 16 个 `perm:` 策略全部是
`RequireClaim`），TTL 默认 3600 秒（`JwtSettings.AccessTokenTtlSeconds`）。
撤销权限、禁用账号、降级角色之后，**已签发的令牌最长还能用一小时**，
且没有任何令牌版本号可以立即作废。

### 方案

1. `users` 表加 `token_version int NOT NULL DEFAULT 0`（新迁移）。
2. 签发 JWT 时带 `tv` claim。
3. `AuthService` 在改密、禁用、改角色/权限时 `user.TokenVersion++`。
4. 加一个轻量校验（`IClaimsTransformation` 或最小 middleware）：
   比对 token 里的 `tv` 与库中当前值，不一致直接 401。
   为避免每请求一次 DB 读，用 60 秒内存缓存按 `userId` 缓存 `tokenVersion`
   （与 `SystemSettingsProvider` 同样的缓存形状）——最坏 60 秒生效，比一小时好两个数量级。
5. 主动登出时也 `++`，让刷新令牌与访问令牌一起失效。

### 验收标准（2026-08-26 已按产品决策调整）

> **决策：不做用户管理。** 产品形态是服务端单管理员，多账号 / 角色 / 逐项授权都不在范围内，
> 加上反而更繁琐。因此原验收标准 1、2 描述的「撤权」「禁用账号」两个动作在产品里不存在，
> 不再作为验收项。`token_version` 机制仍然保留并已实现——它让**改密和登出**在 60 秒内
> 作废已签发的访问令牌，这部分价值与是否有用户管理无关。
>
> 若日后真要加多用户，`IAuthService` 的注释里写明了新接口必须同样 `TokenVersion++`。

1. 管理员改密后，旧访问令牌在 60 秒内返回 401。
2. 主动登出后，该会话的访问令牌与刷新令牌在 60 秒内一并失效。
3. 正常使用时每请求不产生额外的数据库查询（缓存命中）。

---

# 批次 D：一致性与性能

<a id="d1"></a>
## D1 · P2 · 幽灵状态：一批枚举值没有生产者

### 现状与证据

每一个都会在界面上留下一个永远筛不出东西的选项：

| 枚举值 | 界面上有 | 生产者 |
|---|---|---|
| `ClientStatus.SuspectedOffline` / `Offline` | 状态筛选、状态分布图 | 无 → **A1 修复后自然有** |
| `PrecheckStatus.SizeAbnormal` | `STATUS_MAP` | 无 → **A3 修复后自然有** |
| `PrecheckStatus.Scanning` | — | 零引用 |
| `PrecheckStatus.CandidateFound` | — | 零引用 |
| `PrecheckStatus.WaitingStable` | — | 零引用 |
| `BackupSetStatus.Quarantined`（已隔离） | 筛选下拉 + 红色 pill | 无 |
| `BackupSetStatus.RetentionPending`（保留待定） | 筛选下拉 | 无 |

### 方案（逐个决策）

- **`Scanning` / `CandidateFound` / `WaitingStable`** → **删除**。
  这三个是预检的中间态，但预检是一次性上报，不存在中间态。

  > **注意**：`tests/BackupMonitor.Infrastructure.Tests/EnumDomainParityTests.cs` 专门校验
  > C# 枚举与数据库 domain 取值一致。删枚举必须同步写迁移删 domain 取值，
  > 两边一起改，否则测试立刻红。

- **`BackupSetStatus.Quarantined`** → **实现**。
  「隔离」应该是人工动作：校验通过但人工怀疑这份备份有问题时，标记隔离——
  不参与保留计算、不能用于恢复、但也不删除。
  新增 `POST /api/v1/admin/backups/{id}/quarantine` 与 `/unquarantine`，
  前端在备份集详情加按钮，`RetentionCleanupWorker` 跳过隔离状态。

- **`BackupSetStatus.RetentionPending`** → **删除**。
  它本意是「下一轮将被回收」的预告状态，但 `recycle_bin` 本身就已经是反悔窗口，
  再加一层没有增量价值。

- 删掉的枚举值同步从 `ui.js` 的 `L.*` 与 `STATUS_MAP` 以及各页筛选下拉里移除。

### 验收标准

1. 所有筛选下拉里的每一个选项，都至少存在一条可能产生它的代码路径。
2. `EnumDomainParityTests` 全绿。
3. 隔离/解除隔离有完整闭环：按钮 → 状态变化 → 保留策略跳过 → 审计记录。

<a id="d2"></a>
## D2 · P2 · 心跳每次空转写 3~6 条 UPDATE

### 现状与证据

`AgentHeartbeatService.EvaluateResourceAlertsAsync`（:225-295）对 CPU、内存、每一块源盘，
在**正常**时都调用 `_alerting.RecoverAsync(alertKey)`，
而 `AlertingService.RecoverAsync`（:156）无条件执行一条 `ExecuteUpdateAsync`。

一切正常时，每次心跳（默认 60 秒 × 每台机器）也要跑 3~6 条 UPDATE。
100 台机器 ≈ 每分钟 500 条无谓的写语句往复。
`EvaluateCertificateAlertAsync` 和 service_state 的恢复分支同理。

### 方案

`AlertingService.RecoverAsync` 开头加一次存在性判断：

```csharp
var hasActive = await _db.Alerts
    .AnyAsync(a => a.AlertKey == alertKey && ActiveStatuses.Contains(a.Status), ct);
if (!hasActive) return;
```

`alerts` 表在 `alert_key` 上有索引，`AnyAsync` 是一次索引探测（且绝大多数情况下命中缓存页），
比一条 UPDATE 便宜得多，也不产生 WAL。

### 验收标准

1. 单客户端稳定心跳 5 分钟，`pg_stat_statements` 里该 UPDATE 的调用次数从 ~15 降到 0。
2. 告警恢复功能本身不变：制造一次 CPU 超限告警，恢复后状态仍能正确转「已恢复」。

<a id="d3"></a>
## D3 · P2 · Agent 每个分块裸分配 8MB，全部落 LOH

### 现状与证据

`agent/BackupMonitor.Agent/AgentWorker.cs:693`：

```csharp
var bytes = new byte[length];      // length 默认 8MB
```

每个分块一次，全部落进大对象堆。传一个 100 GB 的备份 = 12800 次 8MB LOH 分配。
客户端是生产服务器，这个 GC 压力完全没必要。

### 方案

```csharp
var buffer = ArrayPool<byte>.Shared.Rent(length);
try
{
    // ... 读取 length 字节到 buffer
    var hash = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, length))).ToLowerInvariant();
    await _api.UploadChunkAsync(sessionId, fileId, chunkIndex, offset,
        buffer.AsMemory(0, length), hash, ct);
}
finally { ArrayPool<byte>.Shared.Return(buffer); }
```

> **风险点**：`Rent` 返回的数组**可能比请求的大**，所有下游都必须按 `length` 切片，
> 不能再传整个 `byte[]`。需要同步把 `AgentApiClient.UploadChunkAsync` 的参数
> 从 `byte[]` 改成 `ReadOnlyMemory<byte>`，否则会把池里的垃圾字节一起传上去——
> 这是这条改动唯一的正确性风险，务必检查所有调用点。

### 验收标准

1. 上传一个 10 GB 备份集，用 `dotnet-counters` 观察 Agent 进程，
   LOH 分配量与 Gen2 回收次数显著下降。
2. 上传内容逐字节一致（对比服务端入库后的 SHA256），**必须做，这是本条唯一的正确性风险**。

<a id="d4"></a>
## D4 · P2 · api.js 刷新失败返回 null 导致调用方 NPE

### 现状与证据

`src/BackupMonitor.Api/wwwroot/js/api.js:45-51`：

```js
if (result.res.status === 401 && !opts.noAuth && !opts.retried) {
  if (await tryRefresh()) return api(path, ...);
  forceLogin();
  return null;          // ← 所有调用方都直接 data.totalCount
}
```

会话过期时用户看到的是「加载失败：Cannot read properties of null (reading 'totalCount')」，
而不是干净地回到登录页；`Promise.all` 的场景（概览页）还会产生 unhandled rejection。

### 方案

```js
forceLogin();
const error = new Error('会话已过期，请重新登录');
error.code = 'SESSION_EXPIRED';
throw error;
```

改动前需要全局搜一遍是否有调用方依赖「返回 null」的行为，确认没有之后再改。
各视图的 `catch` 已经会 toast 或写空态，不会再 NPE。

### 验收标准

1. 手工让刷新令牌失效后触发一次请求 → 干净跳转登录页，控制台无未捕获异常。
2. 概览页在会话过期时不产生 unhandled rejection。

<a id="d5"></a>
## D5 · P2 · 告警中心缺「指派」与「临时静默」

### 现状与证据

功能说明书 8.18 要求的告警操作是：确认、**指派**、添加处理说明、关闭、忽略本次、**创建临时静默**。

`AdminAlertController` 实际只有：list、detail、acknowledge、handle、close。
数据库里既没有 `assigned_to` 列，也没有静默表。

「临时静默」的缺失有实际代价：一台机器在维护窗口内会持续产生告警，
现在只能一条条关闭，或者忍受通知轰炸。

### 方案

1. 迁移：`alerts` 加 `assigned_to uuid REFERENCES users(id)` + `assigned_at timestamptz`；
   新增表 `alert_silences(id, alert_key_pattern, reason, until, created_by, created_at)`。
2. `AlertingService.RaiseAsync` 在建新告警前查静默：命中则只累加 `occurrence_count`，
   不建新告警、不发通知、不推送 Agent 托盘。
3. API：`POST /alerts/{id}/assign`、`POST /alerts/{id}/silence`、`DELETE /alerts/silences/{id}`。
4. 前端：告警抽屉加「指派给」下拉与「静默 N 小时」按钮；列表加「静默中」标记。

### 验收标准

1. 对某客户端创建 2 小时静默 → 期间该客户端的新告警不产生通知，但计数仍在累加。
2. 静默到期后恢复正常告警。
3. 指派后告警列表可按「指派给我」筛选。

---

# 发布注意事项

## 顺序依赖

1. **B3（方案 A）必须先升 Agent 再升服务端**。配置签名字段列表变更会让旧版 Agent 验签失败、
   停止接收新配置（不崩溃，但配置冻结）。如果无法保证全量升级，改用方案 B（删字段）。
2. **A1 上线前先确认存量**。如果生产环境本来就有一批长期不心跳的僵尸客户端，
   上线瞬间会集中产生一批 `client_offline` 告警。上线前先查：
   ```sql
   SELECT count(*) FROM clients
   WHERE status = 'online' AND last_heartbeat_at < now() - interval '5 minutes';
   ```
   有存量就先清理或先禁用这些客户端。
3. **A2 同理**，首次上线时把 `missed_backup_grace_minutes` 临时调大，观察一轮再收紧。
4. **C1 上线后必须轮换凭据**：已经进过审计日志的 SMTP 密码和 webhook 要视为已泄露，
   加密只能防止将来泄露，不能撤回过去的。

## 数据库迁移

需要新增迁移脚本（按现有 `V00x__*.sql` 命名规则），内容包括：

- 新配置项：`client_liveness_interval_seconds`、`missed_backup_scan_interval_minutes`、
  `missed_backup_grace_minutes`、`server_storage_alert_percent`
- D1 决定删除的枚举值 → 同步修改对应 domain
- C4 的 `users.token_version`
- D5 的 `alerts.assigned_to` / `alert_silences`
- C1 的历史审计脱敏 UPDATE

`MigrationRunnerTests` 与 `EnumDomainParityTests` 会守住这些，改完必须跑。

## 回归清单

跑完整改后至少验证这些既有能力没被碰坏：

- [ ] 客户端注册 → 审批 → 心跳 → 上线 全链路
- [ ] 预检 → 候选 → 上传 → 校验 → 入库 全链路（含断点续传）
- [ ] 恢复请求 → 签发令牌 → ZIP 下载 / 单文件 Range 下载
- [ ] 保留策略清理与回收站物理删除（含熔断）
- [ ] 配置签名验证：Agent 能正常拉取并应用新配置
- [ ] `dotnet test`：Docker 可用时 270 个用例应全绿
      （无 Docker 的环境下 68 个 Testcontainers 用例会失败，这是环境问题不是代码问题）

## 建议节奏

| 阶段 | 内容 | 产出 |
|---|---|---|
| 第一周 | 批次 A（A1~A4） | 系统开始能发现「沉默」，这是最大的价值增量 |
| 第二周 | 批次 B（B1~B5） | 界面上的每一个承诺都真实生效 |
| 第三周 | 批次 C（C1~C3） | 凭据不再明文，邮件通知真正可用 |
| 后续 | C4 + 批次 D | 一致性与性能收尾 |
