# 待处理问题清单

审查日期 2026-08-07。本文只收录**尚未修复**的问题；已在上一轮处理的五项（密钥轮换、nginx/mTLS、CA 口令、`/health/db` 与 CORS、中间件顺序与日志编码）不再列出。

严重度定义：

| 级别 | 含义 |
| --- | --- |
| P0 | 数据正确性受损，且是静默的 —— 出错时没人会知道 |
| P1 | 安全或质量基线缺口，需排期 |
| P2 | 影响可维护性 / 可观测性 / 性能 |
| P3 | 打磨项，可随手改 |

| ID | 级别 | 摘要 |
| --- | --- | --- |
| [#1](#1) | P0 | `TaskMode.Automatic` 与 `ImportanceLevel.Low` 写不进数据库，被静默替换 |
| [#2](#2) | P1 | 默认口令 `Admin@2026` 硬编码，且全系统没有改密接口 |
| [#3](#3) | P1 | 零测试、零 CI |
| [#4](#4) | P2 | 登录查询笛卡尔积膨胀 |
| [#5](#5) | P2 | `X-Request-Id` 原样回显，未做长度与字符校验 |
| [#6](#6) | P2 | `/health/db` 向生产表写探针行 |
| [#7](#7) | P3 | 登录接口存在用户名枚举计时侧信道 |
| [#8](#8) | P3 | 恢复下载 ZIP 未防 zip-slip |
| [#9](#9) | P3 | `logout` 强制要求请求体 |
| [#10](#10) | P3 | `TokenHasher.GenerateToken` 注释与实现不符 |
| [#11](#11) | P3 | 六份文档实为三份，字节级重复 |

---

<a id="1"></a>
## #1 · P0 · 枚举默认值被 EF 静默覆盖

**位置** [BackupConfigurations.cs:49](../src/src/BackupMonitor.Infrastructure/Data/Configurations/BackupConfigurations.cs:49)、[:53](../src/src/BackupMonitor.Infrastructure/Data/Configurations/BackupConfigurations.cs:53)

```csharp
builder.Property(e => e.TaskMode).HasDefaultValue(TaskMode.ApprovalRequired);
builder.Property(e => e.ImportanceLevel).HasDefaultValue(ImportanceLevel.Normal);
```

**根因**

EF Core 用「属性值是否等于 CLR 默认值」来判断该属性是否被显式赋值。而这两个枚举的 0 号成员恰好是业务上的合法值：

```csharp
public enum TaskMode { Automatic, ApprovalRequired, Manual, MonitorOnly, Paused }  // Automatic = 0
public enum ImportanceLevel { Low, Normal, High, Critical }                        // Low = 0
```

于是 `TaskMode = Automatic` 在 EF 看来等同于「没赋值」，插入时改用数据库默认值 `approval_required`。

**后果**

- **建一个自动备份任务，落库后变成「需审批」。** 任务不会自动执行，静静地等待一个没人知道要去点的审批。对备份系统而言这是最坏的一类故障：以为在备份，其实没有。
- 重要性设为「低」的任务，落库后变成「普通」，影响告警阈值与保留策略选择。

启动日志里 EF 已经明确警告过这件事，被当成噪音略过了：

```
The 'TaskMode' property ... is configured with a database-generated default, but has no
configured sentinel value. The database-generated default will always be used for inserts
when the property has the value 'Automatic', since this is the CLR default for the type.
```

**方案**

去掉 EF 侧的 `HasDefaultValue`，改由 C# 属性初始值承担默认语义。数据库列默认值保留（供 SQL 直接插入时兜底），但 EF 每次都发送显式值，不再有「猜是否赋值」这一步。

```csharp
// BackupConfigurations.cs —— 只保留列名映射
builder.Property(e => e.TaskMode).HasColumnName("task_mode");
builder.Property(e => e.ImportanceLevel).HasColumnName("importance_level");
```

```csharp
// BackupTask.cs —— 默认语义移到实体
public TaskMode TaskMode { get; set; } = TaskMode.ApprovalRequired;
public ImportanceLevel ImportanceLevel { get; set; } = ImportanceLevel.Normal;
```

备选方案是 `.HasSentinel(...)`，但需要为每个枚举找一个「永不使用」的哨兵值，两个枚举都没有空位，得先加 `Unspecified = -1` 成员，改动面反而更大。不推荐。

**验证**

1. 建任务时显式传 `taskMode = "automatic"`，查库确认落的是 `automatic` 而非 `approval_required`；
2. 同样验证 `importanceLevel = "low"`；
3. 启动日志中两条 sentinel 警告消失。

**排查存量数据**

修复只影响新写入。已经被静默改写的历史任务需要人工甄别：

```sql
-- 疑似受影响：创建时意图为 automatic、实际落为 approval_required 且从未被人工改过
SELECT id, name, client_id, task_mode, importance_level, created_at
FROM backup_tasks
WHERE task_mode = 'approval_required' AND previous_task_mode IS NULL
ORDER BY created_at;
```

无法从数据本身区分「本来就要审批」和「被改写」，需要和业务方逐条核对。**这一步比改代码重要**——存量里可能藏着一批以为在跑、其实从没跑过的备份任务。

**工作量** 代码 10 分钟；存量核对取决于任务数量。

---

<a id="2"></a>
## #2 · P1 · 默认口令硬编码且无改密入口

**位置** [V001__initial_schema.sql:1076](../src/database/V001__initial_schema.sql:1076)

```sql
-- 4.4 默认管理员账户（密码: Admin@2026，使用 pgcrypto 的 crypt + gen_salt）
INSERT INTO users (...) VALUES (..., crypt('Admin@2026', gen_salt('bf', 12)), ...);
```

**问题有两层**，第二层更严重：

1. 默认口令写死在版本库的迁移脚本里，人人可见；
2. **整个系统没有任何修改口令的接口。** `AuthController` 只有 login / refresh / logout / me，也不存在用户管理控制器。`users` 表有 `password_changed_at` 列，却没有 `must_change_password` 标志，也没有任何代码去写这个列。

也就是说部署方即使想改口令，只能手工执行 SQL。这不是「默认口令没改」的常见问题，而是**系统本身不具备改口令的能力**。

**方案**

分两步，第一步是止血，第二步是补能力。

*第一步 —— 立即可做：* 迁移脚本不再种入固定口令，改为部署时注入。

```sql
-- V005__admin_password_bootstrap.sql
-- 首次部署由 dbinit 传入：psql -v admin_pw="$ADMIN_PW"
INSERT INTO users (id, username, display_name, password_hash, status, password_changed_at,
                   must_change_password)
VALUES ('c0000001-0000-0000-0000-000000000001', 'admin', '系统管理员',
        crypt(:'admin_pw', gen_salt('bf', 12)), 'active', now(), true)
ON CONFLICT (id) DO NOTHING;
```

配套加列：

```sql
ALTER TABLE users ADD COLUMN must_change_password boolean NOT NULL DEFAULT false;
```

*第二步 —— 补改密能力：*

- `POST /api/v1/auth/change-password` —— 需 Bearer，校验旧口令，bcrypt 成本 12，成功后写 `password_changed_at`、清 `must_change_password`、**吊销该用户全部刷新令牌**（`AuthService.RevokeAllUserTokensAsync` 已有，reason 用 `password_changed`）；
- 登录响应体带上 `mustChangePassword`，控制台据此强制跳转改密页，未改密前不放行其它路由；
- 口令强度校验（长度 ≥ 12，不等于用户名，不在常见弱口令表内）放在 `AuthService`，不要只放前端。

`AuthService` 现有结构对此很友好——审计、锁定、令牌吊销都已具备，改密只是复用。

**工作量** 半天（含控制台改密页）。

---

<a id="3"></a>
## #3 · P1 · 零测试、零 CI

`BackupMonitor.sln` 中没有任何测试工程，仓库里没有 CI 配置。

对一般业务系统这是「欠债」，对备份系统是**风险等级更高的欠债**：这类系统的失效大多是静默的。上面 #1 就是活生生的例子——一个改变任务执行模式的缺陷，在运行日志里只表现为一条 EF 警告。没有测试，下一个同类问题同样会溜过去。

**优先补的三处**（按「错了会静默丢数据」排序）：

| 目标 | 为什么优先 | 用例要点 |
| --- | --- | --- |
| `UploadSessionService` 断点续传 | 分块上传是数据入口，错了直接丢数据 | 缺块查询准确性、乱序上传、重复上传同一块、块哈希不匹配拒收、整文件哈希比对、会话跨越重启后续传 |
| `RetentionCleanupWorker` | 删数据的代码，错了不可逆 | 保留期边界（含时区）、`retention_locks` 必须阻止删除、最后一个可用副本不得删除、干跑模式 |
| `PathSafety` | 已写得不错，正因如此更该锁住 | `..`、UNC、`\\?\`、盘符、尾随空格与点、大小写穿越、符号链接、超长路径 |

其次是 `#1` 的回归用例、`AuthService` 的锁定与令牌轮换、`ScheduledLockService` 的并发抢锁。

**技术选型**：xUnit + `Testcontainers.PostgreSql`。这个项目大量依赖 PostgreSQL 特性（`jsonb`、`ON CONFLICT ... WHERE`、`RETURNING`、`interval`、域类型枚举），用 InMemory provider 测等于没测——`ScheduledLockService` 的 `FromSqlRaw` 在 InMemory 下根本跑不起来。

CI 最小可用形态：

```yaml
# .github/workflows/ci.yml
name: ci
on: [push, pull_request]
jobs:
  build-test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '8.0.x' }
      - run: dotnet build src/BackupMonitor.sln -c Release --nologo
      - run: dotnet test  src/BackupMonitor.sln -c Release --nologo --no-build
```

顺带一提：本轮改动因为本机没有 .NET SDK 而**未经编译验证**，CI 的第一个价值就是堵上这个口子。

**工作量** 骨架 + 三处核心用例约两天。

---

<a id="4"></a>
## #4 · P2 · 登录查询笛卡尔积膨胀

**位置** [AuthService.cs:47](../src/src/BackupMonitor.Infrastructure/Services/AuthService.cs:47)、[:162](../src/src/BackupMonitor.Infrastructure/Services/AuthService.cs:162)、[:206](../src/src/BackupMonitor.Infrastructure/Services/AuthService.cs:206)

```csharp
.Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
    .ThenInclude(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
```

`UserRoles` 与 `RolePermissions` 是两层集合导航，EF 默认 `SingleQuery` 会生成笛卡尔积：一个挂 3 个角色、每角色 16 权限的用户，单次登录要拉 48 行重复的用户与角色数据。启动日志里的 `MultipleCollectionIncludeWarning` 指的就是这条。

登录接口实测 1228ms，其中相当一部分是这个查询。

**方案**

这些实体被 `Include` 进来，实际只为了取两串字符串（角色码、权限码）。直接投影，连实体跟踪都省掉：

```csharp
var roles = await _db.UserRoles.AsNoTracking()
    .Where(ur => ur.UserId == user.Id)
    .Select(ur => ur.Role.Code)
    .Distinct().ToListAsync(ct);

var permissions = await _db.UserRoles.AsNoTracking()
    .Where(ur => ur.UserId == user.Id)
    .SelectMany(ur => ur.Role.RolePermissions.Select(rp => rp.Permission.Code))
    .Distinct().ToListAsync(ct);
```

两条窄查询，无重复行，无需跟踪。注意 `LoginAsync` 里的 `user` 实体本身仍需跟踪（要写 `FailedLoginCount` / `LastLoginAt`），所以这两条投影查询要独立于主查询，主查询去掉全部 `Include`。

若想改动最小，`.AsSplitQuery()` 也能消除笛卡尔积，但仍会做无谓的实体物化，且 `GetCurrentUserAsync` 依然多余地拉了整个对象图。推荐投影。

**工作量** 1 小时（三处调用点）。

---

<a id="5"></a>
## #5 · P2 · `X-Request-Id` 原样回显

**位置** [RequestIdMiddleware.cs:20](../src/src/BackupMonitor.Api/Middleware/RequestIdMiddleware.cs:20)

```csharp
var requestId = context.Request.Headers[HeaderName].FirstOrDefault();
if (string.IsNullOrWhiteSpace(requestId))
    requestId = Guid.NewGuid().ToString("N");
```

客户端传入的值未经任何校验，就被推入 Serilog 的 `LogContext` 并写回响应头。

Kestrel 会拦截响应头里的 CR/LF，所以**不构成响应拆分**；实际风险是日志侧：超长值撑爆日志行、控制字符污染日志文件、伪造他人的 requestId 干扰问题追踪。属于「防御纵深」层面，不是可直接利用的漏洞。

**方案**

```csharp
private const int MaxLength = 64;

private static bool IsWellFormed(string value) =>
    value.Length <= MaxLength && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

// ...
var incoming = context.Request.Headers[HeaderName].FirstOrDefault();
var requestId = !string.IsNullOrWhiteSpace(incoming) && IsWellFormed(incoming)
    ? incoming
    : Guid.NewGuid().ToString("N");
```

**工作量** 10 分钟。

---

<a id="6"></a>
## #6 · P2 · `/health/db` 向生产表写探针行

**位置** [Program.cs](../src/src/BackupMonitor.Api/Program.cs)（`app.MapGet("/health/db", ...)`）

上一轮已加上 `perm:system.manage`，不再匿名开放。但设计本身仍值得重做：这个端点为了验证乐观锁，会向 `system_settings` 插入 `diag_probe` 行、更新三次、再删除，并额外开一个 `DbContext` 模拟并发冲突。

在生产库上写业务表来做自检，代价是：污染 `system_settings` 的变更审计、若中途异常会残留探针行（代码有清理逻辑，但异常路径下不保证）、并发调用互相干扰。

**方案**

拆成两个东西——它们本来就是两个东西：

- **健康检查**（保留在 `/health/db`）：只读。`SELECT 1`、连接池状态、迁移版本号。用 `AddHealthChecks().AddNpgSql(...)` 即可，无需自研。
- **乐观锁 / 枚举映射自检**：这是**集成测试**，不是运行时端点。随 #3 一起迁进测试工程，用 Testcontainers 起一次性数据库，想怎么写探针行都行。

**工作量** 1 小时（含迁走自检逻辑）。

---

<a id="7"></a>
## #7 · P3 · 用户名枚举计时侧信道

**位置** [AuthService.cs:52](../src/src/BackupMonitor.Infrastructure/Services/AuthService.cs:52)

用户不存在时直接抛出，不执行 bcrypt。而用户存在时要跑一次 cost=12 的 bcrypt（约 200–300ms）。响应时间差异足以稳定区分用户名是否存在。

其余防护都做到位了——统一错误文案、失败计数、临时锁定、审计留痕——只差这一步。

**方案**

```csharp
/// <summary>用户不存在时也执行一次等价开销的哈希校验，抹平响应时间差异</summary>
private static readonly string DummyHash =
    BCrypt.Net.BCrypt.HashPassword("dummy", workFactor: 12);

if (user is null)
{
    BCrypt.Net.BCrypt.Verify(request.Password, DummyHash);   // 仅为消耗等量时间
    await _audit.RecordAsync(...);
    throw new BusinessException("UNAUTHORIZED", "用户名或密码错误", 401);
}
```

注意 `DummyHash` 的 workFactor 必须与真实口令一致（当前是 12，见 V001 的 `gen_salt('bf', 12)`），否则时间差仍在。

**工作量** 15 分钟。

---

<a id="8"></a>
## #8 · P3 · 恢复下载 ZIP 未防 zip-slip

**位置** [DownloadsController.cs:68](../src/src/BackupMonitor.Api/Controllers/DownloadsController.cs:68)

```csharp
var entry = zip.CreateEntry(file.RelativePath, CompressionLevel.Fastest);
```

`RelativePath` 直接来自 `backup_files` 表。入库路径经过 `PathSafety` 校验，风险不高；但 ZIP 条目名是**交给下游解压方**的数据，服务端多校验一道成本极低。真正的防线在解压侧，服务端这层是纵深。

**方案**

```csharp
foreach (var file in context.Files)
{
    // ZIP 条目名统一正斜杠，且必须是规范化后的相对路径
    var entryName = file.RelativePath.Replace('\\', '/');
    if (!PathSafety.IsValidRelativePath(entryName))
        throw new BusinessException("INVALID_REQUEST", $"非法的归档条目名：{file.RelativePath}", 400);

    var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
    // ...
}
```

**工作量** 15 分钟。

---

<a id="9"></a>
## #9 · P3 · `logout` 强制要求请求体

**位置** [AuthController.cs:47](../src/src/BackupMonitor.Api/Controllers/AuthController.cs:47)

`[FromBody] RefreshTokenRequest request` 是必填的，不带 body 调用返回 415/400。内置控制台始终会带 body（[index.html:346](../src/src/BackupMonitor.Api/wwwroot/index.html:346)），所以**实际不影响使用**；日志里那两条 415/400 是手工测试留下的。

纯健壮性项：退出登录不该因为少个 body 就失败。`AuthService.LogoutAsync` 本来就对 null / 空串做了兜底。

```csharp
public async Task<ActionResult<ApiResponse>> Logout(
    [FromBody] RefreshTokenRequest? request, CancellationToken ct)
{
    await _authService.LogoutAsync(request?.RefreshToken, ct);
    return OkMessage("已退出登录");
}
```

**工作量** 5 分钟。

---

<a id="10"></a>
## #10 · P3 · `GenerateToken` 注释与实现不符

**位置** [TokenHasher.cs:16](../src/src/BackupMonitor.Infrastructure/Common/TokenHasher.cs:16)

注释写「生成加密安全的随机令牌（base64url）」，实现返回的是小写十六进制。熵值没问题（48 字节），只是注释误导——按注释预期长度做的调用方会算错。

改注释即可，**不要改实现**：令牌已经签发在外，改编码会让所有存量刷新令牌与注册令牌失效。

```csharp
/// <summary>生成加密安全的随机令牌（小写十六进制，长度为 byteLength × 2）</summary>
```

**工作量** 2 分钟。

---

<a id="11"></a>
## #11 · P3 · 文档字节级重复

`doc1.md` / `doc2.md` / `doc3.md` 与三份中文名文档 MD5 两两相同，共约 235KB 冗余：

| 内容 | 副本 |
| --- | --- |
| 系统功能说明书 | `doc1.md` = `轻量级集中备份采集与监控系统_系统功能说明书_V1.0.md` |
| 系统架构书 | `doc2.md` = `轻量级集中备份采集与监控系统_系统架构书_V1.0.md` |
| 详细数据库及接口设计书 | `doc3.md` = `轻量级集中备份采集与监控系统_详细数据库及接口设计书_V1.0.md` |

保留中文名一组（有语义），删掉 `docN.md`，统一移到 `docs/`。代码注释里大量引用「设计书 23.1」这类章节号，移动后建议在 `docs/README.md` 里放一张章节号 → 文件的对照表。

**工作量** 10 分钟。

---

## 建议排期

| 批次 | 内容 | 理由 |
| --- | --- | --- |
| 立刻 | #1 代码修复 + 存量任务核对 | 静默数据损坏，且可能已经发生 |
| 本周 | #2、#5、#7、#9、#10、#11 | 都是小改动，一起做掉 |
| 本迭代 | #3（测试骨架 + 三处核心用例 + CI） | 是防止 #1 重演的唯一手段 |
| 下迭代 | #4、#6、#8 | 性能与纵深，不阻塞 |

#1 的**存量数据核对**是整份清单里唯一无法靠改代码解决的事项，也是唯一可能已经在造成实际损失的事项，建议优先于其它一切。
