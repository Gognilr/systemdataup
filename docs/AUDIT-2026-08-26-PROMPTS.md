# 代码审计整改提示词集（2026-08-26 · 8 批次）

配套方案文档：[AUDIT-2026-08-26.md](AUDIT-2026-08-26.md)。
每个提示词对应一个可独立上线的批次，粘贴给编码助手（Claude Code / Cursor 等）即可执行。

## 使用说明

1. **每次只跑一个批次。** 不要把多个提示词拼起来——范围一大，助手就会自作主张扩大改动。
2. **每个提示词前面都要先贴一遍「共通前置」**，助手是冷启动的，不知道项目背景与约束。
3. **顺序有讲究**：
   - `A` 最先跑（它解开「客户端被永久锁死」这条最急的故障链，且 D、H 复用它建的工作器）。
   - A 之后 `C` / `E` / `G` 三条线互不相交，可并行。
   - `B` 必须在 A 之后（两者都动 `ExecuteUploadAsync`）。
   - `F` 必须在 B 之后（两者都动 `AgentWorker`）。
   - `D` 依赖 A（往 A 建的 worker 里加一段）。`H` 放最后（碰 A 和 E 改过的文件）。

   ```
   A ──┬── B ── F
       ├── D
       ├── C
       ├── E ──┐
       └── G   ├── H
   ```
4. 每批跑完先 review diff、跑构建、再合并，不要连续跑两个批次。

---

## 【共通前置】

> 每个提示词之前先贴这一段。

```
## 项目背景

BackupMonitor：轻量级集中备份采集与监控系统。

服务端（.NET 8 + PostgreSQL + EF Core，四层）：
  src/src/BackupMonitor.{Api,Infrastructure,Core,Shared}
客户端 Agent（.NET 8 Windows 服务 + 托盘）：
  src/agent/BackupMonitor.Agent{,.Setup,.Tray}
安装器：src/server/BackupMonitor.Server.Setup
数据库迁移（权威源）：src/database/V001~V021__*.sql
测试：src/tests/BackupMonitor.Infrastructure.Tests（用 Testcontainers 起 PostgreSQL）
管理控制台：src/src/BackupMonitor.Api/wwwroot，原生 ES module，零构建

- 管理端 JWT 认证 + 16 个权限策略；Agent 端客户端证书（mTLS）认证
- 两种部署形态：Secure（nginx 终结 TLS/mTLS 反代到 Kestrel 回环 5080）
  与 LAN Turnkey（Kestrel 自己终结 HTTPS，证书指纹固定）
- 后台工作器都是同一个形状：BackgroundService + IServiceScopeFactory
  + IScheduledLockService 单实例锁 + system_settings 里的可配间隔。
  参考 SystemWatchdogWorker / RetentionCleanupWorker / MissedBackupWorker

## 硬约束

1. 代码注释与错误文案一律中文。注释解释「为什么这么写」和「原先错在哪」，
   不要写「这里做了什么」这种复述代码的注释。匹配现有风格——
   现有代码里大量注释是在讲踩过的坑，照着那个密度和口吻写。
2. 复用现有封装：BusinessException / NotFoundException / ApiResponse<T> /
   EnumMapping.ToSnakeCase / SystemSettingsProvider / IAlertingService /
   IAuditRecorder / PathSafety。不要另起炉灶。
3. 机密（数据库口令、签名密钥）绝不写回任何 appsettings*.json，它们已全部外置。
4. 不新增第三方 NuGet 依赖，除非提示词里明确允许。
5. 新增 system_settings 配置项必须同时写进 src/database/ 下的新迁移脚本，
   版本号接着 V021 往后排。枚举新增成员必须同步扩 PostgreSQL DOMAIN，
   否则 EnumDomainParityTests 会红。
6. 不要扩大范围。只做当前批次明确要求的事。发现别的问题记下来，
   在报告末尾列出来，不要顺手改。
7. 不要碰 git、分支、提交、.gitignore、CI 配置——版本控制由我自己管。
8. 完成后必须跑 `dotnet build src/BackupMonitor.sln`，当前基线是 0 警告 0 错误，
   不许引入新警告。测试用 `dotnet test`；本机没有 Docker 时会有约 80 条
   集成测试报 DockerUnavailableException，那是环境问题不是你的改动导致的，
   照常报告即可，但你新写的测试必须能在有 Docker 的环境跑通。

## 交付格式

改完给我一份报告，包含：
- 改了哪些文件，每个文件一句话说明改了什么
- 新增的配置项 / 迁移脚本 / 测试用例清单
- 构建与测试结果原文
- 你发现但没有改的问题（如果有）
```

---

## 提示词 A · 上传会话生命周期闭环（P0，最优先）

```
读 docs/AUDIT-2026-08-26.md 的「批次 A」，实现 A-03、A-04、A-10 三条。

这三条是同一条故障链，必须一起做：
  源文件在预检后被删 → ExecuteUploadAsync 中途 return，会话不取消（A-10）
  → 会话永远停在 uploading，没有任何超时回收（A-03）
  → 攒够 max_concurrent_uploads=2，该客户端此后所有上传永久 409
  → 同时 .part 文件永不清理，暂存盘被填满，全体客户端 507（A-04）

核心事实（自己再验证一遍）：
- UploadSessionService.cs:131 把 upload_session_timeout_seconds 读进局部变量
  sessionTimeout 后再也没用过。编译器不报 CS0219，因为它来自方法调用。
- grep "UploadStatus.Expired" 全代码库零处写入。
- grep "CleanupSessionAsync" 只有一个调用点：UploadCommitWorker.cs:298，
  入库成功后的收尾。取消/失败/超时路径都不清理。
- UploadSession.StagingPath 这一列建表就有、EF 也映射了，但全代码库
  没有任何地方给它赋过值——这次给它一个真实职责，用作「是否已清理」的幂等标记。
- AgentApiClient 根本没实现取消会话的方法，服务端的 cancel 接口没有客户端。

要做的事：
1. 新增 src/src/BackupMonitor.Infrastructure/Services/LifecycleExpiryWorker.cs，
   形状照 SystemWatchdogWorker（单实例锁 + 可配间隔）。方案文档里有骨架代码。
   RunPassAsync 分三段：会话超时、暂存清理、指令回收（第三段这次先留空方法占位，
   批次 D 会填，写清楚 TODO 指向 D-05）。
   注意：会话超时那段刻意不用 ExecuteUpdateAsync——批量 UPDATE 拿不到
   「哪些会话刚过期」这个集合，第二段的暂存清理就没法接上。
   这个取舍和 SystemWatchdogWorker 里对客户端存活判定的理由是同一个，注释里写明。
2. CreateSessionAsync 补写 session.StagingPath（把现在在 SaveChangesAsync 之后的
   EnsureSessionDirectoryAsync 挪到之前，返回值写进实体一起保存）。
3. BuildCreateResponse 用真实的超时值算 ExpiresAt，删掉硬编码的 AddSeconds(3600)。
4. 在 DependencyInjection 里注册 LifecycleExpiryWorker。
5. 新增迁移 src/database/V022__lifecycle_expiry_settings.sql，
   种 lifecycle_expiry_interval_seconds(300)、staging_cleanup_retention_hours(24)、
   command_claim_timeout_seconds(900) 三个键，用 ON CONFLICT DO NOTHING。
   （第三个是批次 D 用的，一起种，省一次迁移。）
6. Agent 侧：AgentApiClient 补 CancelUploadSessionAsync；
   ExecuteUploadAsync 把建会话之后的整段包进 try/catch，任何非成功出口都
   尽力取消一次。LOCAL_FILE_MISSING 那条现在是 return 不是 throw，
   改成抛内部异常走统一收尾路径——别在每个 return 前面各加一句，会漏。
   取消失败只记 LogWarning，绝不能掩盖原始错误。
7. 把 CancelSessionAsync 里那句「由定时清理任务处理」的注释改成指向新 worker，
   它现在是真的了。

孤儿目录扫描也要做（进程崩在建目录与写库之间留下的）：
staging/sessions/ 下目录名不是 GUID 的一律不碰，是 GUID 但库里没有对应行、
且修改时间早于保留期的才删。

测试：新增 UploadSessionExpiryTests，覆盖
- 过期判定用 LastActivityAt，为空时回落 CreatedAt
- 终态会话不被重复处理（StagingPath 置 null 后不再进候选集）
- Committed 会话的暂存目录同样会被兜底清理
- 不认识的目录名不被删除
Agent 侧至少要有一个验证「上传失败会调 cancel」的测试或手工验证步骤说明。
```

---

## 提示词 B · Agent 主循环拆分（P0，风险最高，单独跑）

```
读 docs/AUDIT-2026-08-26.md 的「批次 B」，实现 B-02、B-09、B-15 三条。

这是本轮风险最高的一批，改的是 Agent 的骨架。先把方案文档整节读完再动手。

三条问题同一个根因：AgentWorker 只有一条顺序循环，心跳、指令执行、
计划扫描共用它，而指令执行是 await 到底的。

后果（这是本轮最要命的一条）：传一份 50GB 的备份要几小时，这几小时里
一次心跳都发不出去。服务端 SystemWatchdogWorker 按 client_offline_threshold_seconds
（默认 300 秒）判离线并发 Critical 告警——每一次正经的备份上传都会在 5 分钟后
把这台机器判成离线，而它正在老老实实干活。这会把离线巡检训练成「狼来了」然后被关掉。

要做的事：
1. 把 AgentWorker 拆成三条独立节奏：心跳循环、指令循环、计划扫描循环。
   ExecuteAsync 里先完成注册与证书（仍然串行），然后 Task.WhenAll 三条长任务。
   方案文档里有骨架。
2. _activeCommands 从 HashSet 换成 ConcurrentDictionary<Guid, byte>——
   它现在跨线程了。心跳循环读它组装 ActiveCommands，指令循环写它。
   顺带修掉第 94 行 `_activeCommands.Count == 0` 恒成立这个死条件。
3. 配置读取要线程安全：现在 LoadVerifiedConfig() 每次都从 configStore 读并验签，
   三条循环都要用。做一个 volatile 引用的缓存字段，SyncConfigAsync 成功后
   整体替换引用，读侧只做快照读不加锁。不要让三条循环各自反复 RSA 验签。
4. SyncConfigAsync 必须用 SemaphoreSlim(1,1) 互斥——心跳循环和 sync_config
   指令都会调它，避免两边同时写 config.json。
5. 指令之间仍然串行（MaxItems=3 不变）。拆的是「心跳 vs 指令」，
   不是「指令之间」——并发跑多个上传会把源盘 IO 打满。
6. 三条循环各自 try/catch，一条挂掉不能拖垮另外两条。OperationCanceledException
   的处理照抄现有写法。401 时 RecycleConnections() 的自愈逻辑放进心跳循环即可。
7. B-09：心跳循环按服务端下发的 HeartbeatIntervalSeconds 走
   （Math.Clamp(delay, 5, 300)），指令循环按 CommandPollIntervalSeconds 走，
   指令轮询不携带任何快照。现在的 Math.Min(heartbeatDelay, CommandPollIntervalSeconds)
   让服务端配的 heartbeat_interval_seconds 只要大于 10 秒就完全失效，心跳量是设计值 6 倍。
8. B-09 进阶：磁盘/用户会话/服务状态三块算一个稳定摘要，没变就整块不发
   （HeartbeatRequest 新增 SnapshotUnchanged 字段）。服务端 AgentHeartbeatService
   现在已经是 `if (disks is not null)` 的语义，几乎不用改——
   但 EvaluateResourceAlertsAsync 的磁盘告警分支在 disks 为 null 时会整段跳过，
   必须改成回落到库里已有的 ClientDisks 记录做判定，否则磁盘告警会因为
   「快照没变」而不再评估。这一条容易漏，写测试盯住。
9. B-15：ExecuteUploadAsync 的分块循环里按节流上报进度
   （ReportProgressAsync 服务端和客户端都实现了，AgentWorker 一次都没调过）。
   必须节流：每块一次会把 commands 表写爆（50GB/8MB = 6400 次）。
   做一个 ProgressReporter，min 间隔 30 秒且 min 百分比增量 5%，两个条件都不满足直接返回。
   上报本身 try/catch 吞掉只记 LogDebug——它是给人看的，报不上去不该让上传失败。

不要做的事：不要用「在上传循环里插补心跳」代替拆循环。那是止血——
扫描阶段的长哈希覆盖不到，而扫描一个大目录同样能超过 5 分钟；
且每加一个耗时动作就要记得插一次，迟早会漏。

验收（请在报告里说明怎么验的）：
- 把 BandwidthLimitKbps 调到很低让上传持续 10 分钟以上，期间
  clients.last_heartbeat_at 持续刷新，客户端保持 online，没有 offline 告警。
- 上传期间 client_heartbeats.active_command_count = 1。
- 上传期间下发 refresh_metrics 能在下一个轮询周期内被领取。
- heartbeat_interval_seconds 改成 120 后，心跳确实变成约 120 秒一次，
  指令轮询仍是 10 秒一次。
```

---

## 提示词 C · 指令签名 v2（P0，安全）

```
读 docs/AUDIT-2026-08-26.md 的「批次 C」，实现 C-01。

问题：AgentSignatureCanonicalizer.CommandPayload 只签
id·nonce·type·clientId·expiresAt，而 CommandDto 里真正决定指令做什么的三个字段
——TaskId、CandidateBackupSetId、Payload——完全在签名之外。

对照 ConfigPayload：它逐字段覆盖了 SourcePath、RecognizerConfig、ScanSchedule
等等，做得很完整。唯独指令这条漏了。

后果：能改写响应体的人可以把 upgrade_agent 的 packageUrl 和 sha256 一起换掉，
Agent 会照着攻击者给的哈希校验通过、解压、写 pending-update.json 等待重启切换，
而 VerifyCommand 全程返回 true。Secure 形态由 nginx 终结 TLS 后明文回环到
Kestrel，这中间就是一个可插入点；能写 commands.payload 的数据库账号同样够用。
指令签名这套机制存在的全部意义就是防这个。

要做的事：
1. AgentSignatureCanonicalizer：CurrentVersion 从 "v1" 提到 "v2"，
   CommandPayload 增加 taskId / candidateBackupSetId / payload 三个参数。
   JoinFields 已经处理了 null（写 "~"）和 Base64Url 编码，不用改。
   把现有 v1 版本原样保留、改名 CommandPayloadV1，供灰度窗口验签。
   连同更早的 LegacyCommandPayload，形成三级兼容链。
2. CommandSigner.SignCommand 把新增的三个字段传进去。
3. AgentSignatureVerifier.VerifyCommand 三级回退：v2 → v1 → legacy。
4. 纵深防御：AgentWorker.StageUpgradeAsync 现在只校验 scheme 是 http/https，
   加一条「升级包地址的 host:port 必须等于 _options.ServerUrl 的 host:port」，
   否则返回 UPGRADE_PACKAGE_URL_FORBIDDEN。签名覆盖 payload 之后这一条是冗余的，
   但升级是唯一一条「服务端说什么就执行什么代码」的路径，值得两道锁。
5. AgentApiClient.DownloadBytesAsync 现在把整包读进 byte[] 没有任何大小限制。
   加一个上限（读 Content-Length，超过 512MB 直接拒绝），并在读取时用
   流式限长，别信 Content-Length 就完事。

关键细节 —— payload 必须逐字节一致：
commands.payload 是 jsonb，PostgreSQL 会重排键、规范化空白。签名和验签用的
都是 Command.Payload 这个从库里读回来的字符串（ClaimAsync 每次下发都用当前
算法重签），所以两端拿到的是同一份规范化后的文本。绝对不要在中间对它做任何
JsonSerializer 往返，那会产生一份和库里不一样的文本，签名就对不上了。

测试：新增 CommandSignatureTests，覆盖
- 改动 payload 任意一个字节 → VerifyCommand 返回 false
- 改动 taskId / candidateBackupSetId → 同样 false
- 用 v1 算法签的指令仍然验签通过（灰度兼容）
- payload 为 null 与为空串产生不同的签名原文（"~" vs 空 Base64）
- packageUrl 指向外部主机时回报 UPGRADE_PACKAGE_URL_FORBIDDEN

最后在报告里用醒目的一段写明发布顺序的硬性要求：
Agent 必须先于服务端升级（Agent 能验 v1 和 v2；服务端升级后开始签 v2）。
反过来会让所有老 Agent 拒收全部指令。
另外把「灰度窗口结束后删掉 v1 和 legacy 分支」这件事登记到
docs/系统审查-整改追踪.md，别靠记忆。
```

---

## 提示词 D · 指令生命周期与结果收口（P1，依赖 A）

```
读 docs/AUDIT-2026-08-26.md 的「批次 D」，实现 D-05、D-06。
前置：批次 A 已完成，LifecycleExpiryWorker 已存在且留了第三段的空方法占位。

D-05：grep "CommandStatus.Expired" 全代码库零处写入。ClaimAsync 只挑 Pending，
一旦 ExecuteUpdateAsync 翻成 Claimed 就再没有回到 Pending 的路径，即使
ExpiresAt 早就过了。Agent 在领取和回报之间挂掉，这条指令永远停在
claimed 或 running：管理端显示一条永不结束的「正在执行」，
ClientAdminService.cs:65 那份「进行中」判断跟着失真，
带幂等键的重发也救不了（CreateCommandAsync 只对 Failed/Cancelled/Expired/Rejected 复位）。

  填 LifecycleExpiryWorker 的第三段，两小段：
  一、Pending/Claimed/Running 且 ExpiresAt < now 的置 Expired，
      result_code = COMMAND_EXPIRED。
  二、Claimed 且 ClaimedAt 早于 command_claim_timeout_seconds（V022 已种，默认 900）
      且尚未过期的，退回 Pending 并清空 ClaimedAt，让 Agent 重领——
      这才是断线重连该有的行为，机器重启后应该继续干活而不是等 TTL 熬完。
  Running 不退回 Pending：它已经上报过开始，可能正在传几十 GB，重发会重复执行。
  这两段可以用 ExecuteUpdateAsync（与 A-03 不同，不需要拿到受影响的集合）。

D-06：result_message 是 varchar(2000)，ReportCompletedAsync 原样赋值，
中间没有任何长度检查——EF Core 的 HasMaxLength 只影响建表 DDL，运行时不截断。
Agent 的失败回报走 ex.Message，一条带完整路径的 IOException 轻易超过 2000 字节
→ PostgreSQL 抛 22001 → 接口 500 → Agent 的 catch 分支再报一次、再 500
→ 这条指令永远留在 running（正好接上 D-05）。
result_payload 更糟：不校验是不是合法 JSON（非法 JSON 进 jsonb 直接 500），
也不限大小；browse_path 最多 20000 个条目，序列化几 MB 一份塞进 commands 表。
Agent 自己在 ExecuteRecognitionTestAsync 里写了「会把 commands 表撑坏」的注释
并限制到 50 个文件，浏览这条路径却没设限。

  在 ReportCompletedAsync 里收口，服务端截断不要信客户端：
  - ResultCode 截 64，ResultMessage 截 1900（列宽 2000 留余量）。
    UploadCommitWorker 对 ErrorMessage 已经是这个写法，照抄。
  - ResultPayload 走一个 NormalizeResultPayload：先按 UTF8 字节数判超限
    （上限 256KB，超了替换成一个 {truncated:true,...} 摘要），
    再 JsonDocument.Parse 验形（不合法替换成 {invalidJson:true,...}）。
    两种情况都只记 LogWarning，不让指令失败——它已经执行完了，坏的只是结果的呈现。
  - ReportProgressAsync 也写 ResultPayload，同样收口。
  - 配套：browse_path 的条目上限改成「服务端 payload 里下发的 maxEntries
    与客户端 BrowseMaxEntries 取小」，让服务端能约束自己会收到多大的东西。

测试：
- claimed 且 claimed_at 早于阈值的指令，一轮巡检后回到 pending 并能被重新领取
- running 且已过期的置 expired；running 且未过期的不被碰
- 回报 5000 字符的 resultMessage：接口 200，库里存前 1900 字符，状态正常流转
- 回报 result = "not json"：接口 200，result_payload 是摘要对象，不再 500
```

---

## 提示词 E · 入库收口（P1）

```
读 docs/AUDIT-2026-08-26.md 的「批次 E」，实现 E-07、E-16。

E-07（这条是数据一致性问题，优先）：
UploadCommitWorker.CommitSessionAsync 在 :291 设 committed = true（事务已提交、
正式目录已就位），:344 的内层 catch 见到 committed 为真时选择 throw——
这一步是对的，注释也解释得很清楚，绝不能回滚或删目录。
但这个 throw 会被 :365 的外层 catch 接住，而外层无条件执行
session.Status = Failed + ErrorCode = COMMIT_FAILED + 一条 Critical 告警。

后果：备份集实际是 Available、正式目录已就位、文件都在，会话却被记成 Failed
并伴随严重告警。更麻烦的是 CommandService.CreateCommandAsync 的
uploadAcceptedButCommitFailed 分支专门查「该候选存在 Failed 会话」，
于是会把已经成功的上传指令复位重发，客户端再传一遍。
停机时的 OperationCanceledException 走的也是这条路。

  修法：定义一个私有的 PostCommitException(Exception inner)，
  内层 catch 在 committed 时抛它而不是裸 throw。外层 catch 分三种情况：
  - PostCommitException：备份是成功的，只留 LogError，不改状态、不告警。
  - OperationCanceledException when ct.IsCancellationRequested：
    停机不是失败，记 LogInformation 后重抛，会话留在 verifying，
    RecoverPendingWorkAsync 重启后会重新入队。
  - 其余：保留现有的置 Failed + 告警，但 SaveChangesAsync 和 RaiseAsync
    改用 CancellationToken.None，否则 ct 已取消时这段收尾自己也会抛。

E-16：GenerateBackupSetCodeAsync 返回 "BS-{year}-{next:0000}"，
但 backup_set_code_seq 是全局序列，年份换了不重置。格式暗示「某年的第 N 个」，
实际是「有史以来的第 N 个，前面贴了个年份」；跨年后从 BS-2026-8931 直接变成
BS-2027-8932，四位补零也早溢出成五位。

  修法（推荐这条）：承认它是全局流水号，改成 "BS-{next:000000}"，
  把会误导人的年份去掉。按年重置需要复合唯一约束加重试，跨年那一刻还有并发窗口，
  为一个纯展示属性不值得。
  存量编码不迁移——它们已经写进 manifest.json、审计日志和告警文本里了，
  改了会破坏可追溯性。在 BackupSet.BackupSetCode 的 XML 注释里写明两种格式并存
  及分界时间。

测试：
- E-07 用一个可注入的失败点（比如让 CleanupSessionAsync 抛）验证：
  备份集仍是 Available、会话是 Committed、没有 commit_failed 告警。
- E-16 验证新编码格式且并发入库不重复；存量 BS-2026-xxxx 引用仍然有效。
```

---

## 提示词 F · Agent 状态文件瘦身（P1，依赖 B）

```
读 docs/AUDIT-2026-08-26.md 的「批次 F」，实现 F-08。
前置：批次 B 已完成（AgentWorker 已拆成三条循环）。

问题：AgentStateStore.Snapshot() 用「序列化再反序列化」做深拷贝——语义没错，
代价是每调一次就把整个状态对象走一遍 JSON。Update() 每次都 SaveUnsafe()：
建临时文件、打 ACL、全量写、File.Move、再打一次 ACL。
而 state.Candidates 里装的是每个候选备份集的完整文件清单
（相对路径 + 绝对路径 + 大小 + SHA-256），并且只写不删——
ScheduledScans 和 PendingRestabilizeScans 都有清理僵尸条目的逻辑，唯独 Candidates 没有。

后果：一个 5 万文件的备份任务，单个候选就是 10MB 量级的 JSON。
主循环每轮要 Snapshot() 好几次；ProcessNotifications 每条通知一次 Update()
（一次心跳最多 20 条）；每条指令 nonce 一次；每个扫描结果两次。
也就是每分钟几十次、每次几十 MB 的 JSON 序列化加同步落盘，全在同一把锁里。
任务删掉之后那些候选还留在文件里，state.json 只增不减。

要做的事（按收益排序）：
1. 候选文件清单挪出 state.json（收益最大）。LocalCandidateState 去掉 Files 属性，
   只留摘要。新增 AgentCandidateFileStore（形状参考已有的 AgentConfigStore），
   文件落在 %ProgramData%\BackupMonitor\Agent\candidates\ 下，
   文件名用 candidateKey 的 SHA-256 十六进制——candidateKey 里有路径分隔符，
   不能直接当文件名。ExecuteUploadAsync 里读 candidate.Files 的地方改成从这个 store 加载。
2. Candidates 跟着任务生命周期清理。RetryPendingRestabilizeScansAsync 里
   已经算好了 liveTaskIds，直接复用；清理时同步删对应的清单文件。
   注意：收集要删的 key 在锁里做，删文件在锁外做，别在锁里做 IO。
   再加一个总量上限（比如 200 条，按 UpdatedAt 淘汰最旧的），
   防止 CandidateKey 因为 status 段变化而无限累积。
3. 合并批量修改。ProcessNotifications 改成循环外一次 Update()；
   SubmitScansWithRestabilizeAsync 里的多次 Snapshot() 合并成开头取一次。

不要做的事：不要给 SaveUnsafe 加异步去抖后台写。状态文件的价值就在于
「进程被杀之后重启能接上」，延迟写会让这一点失效。降低写入次数可以，延迟写不行。

升级兼容是这一批的硬要求：老版本写的 state.json（Candidates 里带 Files）
必须能被新版本读出来并自动迁移到新布局。否则升级后所有候选丢失、
已通过预检的备份要重新扫一遍。这一条必须有测试。

测试：
- 5 万文件的候选，state.json 在 100KB 量级而不是 10MB 量级
- 删掉任务后跑一轮，state.json 里的 Candidates 条目和 candidates/ 下的清单文件都消失
- 一次心跳带 20 条通知，state.json 只被重写一次（用文件修改时间或 IO 计数验证）
- 老格式 state.json 能被读出并迁移，候选不丢
```

---

## 提示词 G · 告警语义（P2）

```
读 docs/AUDIT-2026-08-26.md 的「批次 G」，实现 G-11、G-12、G-13。

三条共同指向一件事：告警模型只有「新建」和「恢复」两个通知触发点，
而真实运维关心的是三件事——出现、恶化、恢复。

G-11：AlertingService.RaiseAsync 的 existing 分支只累加次数、必要时提升等级
（level < existing.Level，Critical=0 数值小即等级高），
而 CreateDeliveriesForAsync 和 Agent 托盘推送都在 else 分支里。
后果：磁盘可用率从 14%（Warning）掉到 3%（Critical），等级在库里升上去了，
但没有任何人收到通知——第一封警告邮件可能是几天前发的、早被忽略了。
「事情变严重了」这个最该被推送的信号恰好是唯一不推送的。

  修法：existing 分支里检测真实的等级提升，提升且未命中静默时补一次
  CreateDeliveriesForAsync（标题带「【已升级】」前缀）和托盘推送。
  托盘去重键必须带等级（alert:{id}:escalated:{level}），
  这样 Warning→Critical 推一次，之后同等级的重复发生不再推。
  CreateDeliveriesForAsync 补一个可选的标题前缀参数即可复用。

G-12：RecoverAsync 只把告警置 Recovered，对应的 notification_deliveries
仍是 Pending；派发器只看投递记录自身的状态，不回查告警是否已恢复。
后果：短暂抖动产生的告警邮件，在告警早已恢复之后仍会照发，
重试间隔 5 分钟最多 3 次，一次抖动能在恢复后继续发三轮。

  修法两处都改，双保险：
  一、RecoverAsync 里连带把该告警下 Pending 的投递置为 Cancelled。
      ExecuteUpdate 不支持跨表条件，先查出告警 ID 再按 alert_id 更新。
  二、派发器查询里加 ActiveAlertStatuses.Contains(d.Alert.Status) 过滤，
      覆盖并发窗口。
  NotificationStatus 若没有 Cancelled 成员则需要新增，
  并在迁移里扩 notification_status DOMAIN——EnumDomainParityTests
  会校验枚举与 DOMAIN 一致，两边必须同时改。

G-13：MissedBackupWorker 的任务查询排除了 disabled/paused/monitor_only，
循环内还有四处 continue（新建不足一个宽限期、cron 解析失败、
相邻计划间隔小于宽限期、仍在宽限期内），所有这些路径都不调 RecoverAsync。
后果：一个任务先报了 task:{id}:missed，随后被停用/暂停/改成只监控/
cron 改成分钟级，那条严重告警就永远留在告警中心，只能人工关闭。
而「先停掉这个任务再说」是排障时最自然的动作，正好触发它。

  修法：每轮维护一个 judgedTaskIds 集合——只有真正走到「比对 LastSuccessAt
  与 prevDue」那一步的才算判定过，四处 continue 的一律不算。
  轮末对 allTaskIds.Except(judgedTaskIds) 统一 RecoverAsync。
  RecoverAsync 内部已有「没有活动告警就直接返回」的前置 AnyAsync，
  所以对绝大多数任务这是一次索引探测，不产生 UPDATE。
  配套：BackupTaskService 的删除路径上也要恢复该任务的全部告警键
  （task:{id}:missed、task:{id}:upload_failed，以及按 TaskId 批量处理
  command:*:failed 这类带任务关联的）。

测试：
- 制造 Warning 级告警再升级为 Critical：收到第二封标题带「【已升级】」的通知，
  同等级重复发生不再发第三封
- 制造告警后立刻恢复：pending 投递变为 cancelled，派发器不再发送
- 制造 backup_missed 后停用任务：下一轮巡检后该告警为 recovered
- EnumDomainParityTests 全绿
```

---

## 提示词 H · 零散收口（P2，最后跑）

```
读 docs/AUDIT-2026-08-26.md 的「批次 H」，实现 H-14、H-17、H-18。
这三条互不相关，放一起是因为都小且都碰前面批次改过的文件，放最后减少冲突。

H-14（安全）：AuthService.RefreshAsync 检测到已吊销刷新令牌重用时
（典型的令牌泄露信号），调 RevokeAllUserTokensAsync 吊销全部刷新令牌，
但没有 user.TokenVersion++。改密和登出都做了这一步，唯独安全性最敏感的
重放检测漏了。另外 TokenVersionCache.Invalidate() 写好了却一处都没被调用，
改密和登出实际都要等满 60 秒 TTL。
后果：确认令牌泄露的那一刻，攻击者手上的访问令牌仍然全权有效，最长一小时——
而这一小时正是最需要立刻切断的时候。

  修法：重放检测分支里 TokenVersion++ 并保存；AuthService 构造函数注入
  TokenVersionCache（它是单例，注入到 scoped 服务里没问题），在改密、登出、
  重放检测三处调 Invalidate(userId)，让撤权即时生效。
  顺带补一条 auth.token_reuse 的审计记录——令牌重放是最该留痕的安全事件之一，
  现在一条都没有。

H-17：PathSafety.SanitizePathComponent 的清洗顺序是
「替非法字符 → 去 .. → Trim() → TrimEnd('.') → 最后截断到 100 字符」。
截断在净化之后，所以截断点恰好落在点或空格上时，这层保护就被绕过了。
后果：NTFS 写盘时会静默剥掉结尾的点和空格，于是 backup_sets.repository_path
记的路径和磁盘上实际的目录名不一致。日常读写因为 Win32 会做同样的规范化
而看不出问题，但任何逐字符比对路径的逻辑都会踩到——
保留清理前的 PathSafety.IsUnderBase 围栏、跨平台迁移、目录审计。
IsValidRelativePath 里对客户端提交路径的同一条规则是完整的，
只有服务端自己生成路径这条漏了。

  修法：把截断挪到 Trim/TrimEnd 之前，截断后再跑一遍收尾净化与保留名检查。
  截断按码位（StringInfo/Rune）而不是 UTF-16 码元，避免切断代理对——
  中文是 BMP 内的所以现在看不出问题，主机名里出现 emoji 或扩展 B 区汉字就会。
  注意 "_" + cleaned 可能让长度回到 101，返回前再截一次或把上限设为 99，
  测试要覆盖这个边界。

H-18（性能，两个独立问题）：
  其一，UploadSessionService.GetSessionStatusAsync 对每个 Pending/Uploading
  的文件单独查一次分块表——文件多时就是几百次往返。
  改成一次性把整个会话的分块按 UploadFileId 分组查回来，内存里分配给各文件。

  其二，入库队列注册为 SingleReader，UploadCommitWorker 顺序处理
  CommitSession / ReverifyBackupSet / VerifyRestoreRequest 三类工作项。
  一次几十 GB 的入库（复制到仓库并逐文件算哈希）会把后面的备份集重校验和
  恢复请求校验全部堵住——「点了恢复之后等了半小时还在校验」，
  而排队原因在界面上完全不可见。
  改法：把恢复校验拆到独立通道（keyed singleton "commit" / "verify"），
  新增一个轻量的 VerificationWorker 消费 verify 队列，复用现有的
  VerifyRestoreRequestAsync / ReverifyBackupSetAsync。
  RecoverPendingWorkAsync 里的两段扫描分别写进两条队列。
  恢复是有人在界面前等的交互操作，入库是后台批处理，两者不该共享一条队列。
  同时把队列位置（Channel.Reader.Count 是现成的）暴露到恢复请求详情 DTO 里，
  让界面能显示「前面还有 N 个任务」而不是干等。

测试：
- PathSafetyTests 新增：长度 105 且第 100 个字符是 '.' / 是空格 / 含代理对
  三种边界，输出都不以点或空格结尾、长度 ≤ 100、不含半个代理对
- AuthSecurityRegressionTests 新增：令牌重放后 TokenVersion 递增、
  缓存被 Invalidate、审计里有 auth.token_reuse
- 500 文件的会话，GET /upload-sessions/{id} 的数据库往返从 500+ 降到个位数
- 大备份入库进行中同时发起恢复校验，校验秒级完成不等入库
```

---

## 交接检查表

每批跑完对照一遍：

- [ ] `dotnet build src/BackupMonitor.sln` —— 0 警告 0 错误（基线就是 0 警告，不许引入新的）
- [ ] `dotnet test` —— 新增测试全绿；本机无 Docker 时那 80 条集成测试的失败属环境问题
- [ ] 有 Docker 的环境复跑过一次完整测试
- [ ] 新增的 `system_settings` 键都进了迁移脚本
- [ ] 新增的枚举成员都同步扩了 PostgreSQL DOMAIN（`EnumDomainParityTests` 绿）
- [ ] 注释是中文，且在讲「为什么」而不是复述代码
- [ ] 条目状态更新到 [系统审查-整改追踪.md](系统审查-整改追踪.md)
- [ ] 批次 C：报告里写明了「Agent 必须先于服务端升级」
- [ ] 批次 F：验证过老 `state.json` 的升级兼容
