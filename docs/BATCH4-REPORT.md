# 批次 4 报告 · 性能与纵深（提示词 4）

范围：DEV-PROMPTS 提示词 4 —— OPEN-ISSUES #4（登录笛卡尔积 Include）、#6（/health/db 写探针行）、#8（ZIP 条目名 zip-slip 纵深防御）。三项均不阻塞上线，一批完成。

编译状态：`dotnet build -c Release` 0 警告 0 错误；`dotnet test` **59/59 全绿**（Testcontainers 真实 PostgreSQL 15 容器，复用既有 V001~V005 夹具）。

---

## OPEN-ISSUES #4 · 登录笛卡尔积 Include 改为投影查询

### 问题

`AuthService` 三处（`LoginAsync` / `RefreshAsync` / `GetCurrentUserAsync`）用两级集合 Include 链 `UserRoles→Role→RolePermissions→Permission` 加载角色与权限。EF 默认 `SingleQuery` 下两层集合导航生成笛卡尔积：挂 3 角色、每角色 16 权限的用户单次登录要拉约 48 行重复数据。实测基线登录 P95 ≈ 842ms，且 EF 日志出现 `MultipleCollectionIncludeWarning`。

这些实体被 Include 进来其实只为取两串字符串（角色编码、权限编码），根本不需要实体图。

### 修复

- 主查询去掉全部 Include，只查 `User` 本体。`LoginAsync` 里 `user` 实体**仍保持被跟踪**（后续要写 `FailedLoginCount` / `LockedUntil` / `LastLoginAt`），因此投影查询独立于主查询，不回写实体。
- 新增私有辅助 `LoadRoleAndPermissionCodesAsync(userId, ct)`：两条独立的 `AsNoTracking` 窄投影查询——一条从 `UserRoles→Role.Code` 取角色编码，一条从 `UserRoles→RolePermissions→Permission.Code` 取权限编码，各自 `Distinct()` 后返回元组。三处调用点统一改用它。
- `RefreshAsync` 里 `stored`（刷新令牌）的查询保留单个 `Include(t => t.User)`：那是**单值引用**（非集合），不产生笛卡尔积，且需读 `stored.User.Status`，不在本缺陷范围内，刻意不动。

### 验收（实测，临时 API 端口 4900/4901，独立 scratch 库）

| 指标 | 修复前（基线，批 3 提交） | 修复后 |
| --- | --- | --- |
| 登录 P50 | 366.8ms | 372.3ms |
| 登录 P95 | **842.4ms** | **402.7ms** |
| 登录 max | 1502.4ms | 490.3ms |
| `MultipleCollectionIncludeWarning` 日志命中 | 1 | **0** |

P95 从 842ms 降到 402ms（-52%），尾部延迟（max 1502→490）收窄尤为明显——笛卡尔积的代价在大结果集/慢查询时被放大，消除后长尾消失。P50 基本持平属预期：单次登录的主要开销在 bcrypt（workFactor 12），投影改造砍掉的是查询侧的行放大与实体物化，收益集中在高负载尾部。

EF 警告通过对照实验确认：同一 scratch 库分别跑基线构建与新构建各 50 次登录，基线日志出现 `MultipleCollectionInclude`（控制组成立），新日志 0 命中。

---

## OPEN-ISSUES #6 · /health/db 只读化，探针自检迁入测试工程

### 问题

原 `/health/db` 为验证乐观锁，向生产表 `system_settings` 写入探针行（`diag_probe`），更新三次、再用第二个上下文模拟并发冲突后删除。风险：污染生产配置表、异常路径可能残留探针行、并发探测互相干扰，把"健康检查"变成了"会写库的操作"。

### 修复（按"本来就是两个东西"拆分）

**① `/health/db` 只保留只读检查。** 改用框架内置 `AddHealthChecks()` 注册自定义 `DatabaseHealthCheck`（新文件 `BackupMonitor.Api/Health/DatabaseHealthCheck.cs`），只做 `SELECT version()` + 只读计数，失败按 `FailureStatus=Unhealthy` 上报，经 `MapHealthChecks("/health/db")` 暴露，仍保留 `perm:system.manage` 授权（会回显数据库版本，不匿名开放）。

**② 乐观锁与枚举映射自检迁入测试工程。** 新增 `DatabaseSelfCheckTests`（3 用例，真实库）：
- `乐观锁_版本号递增且并发冲突可检测`：完整复刻原探针逻辑——插入后每次更新 `row_version` 递增、第二上下文持旧版本号更新必触发 `DbUpdateConcurrencyException`，`finally` 清理探针行并断言无残留（每用例独立探针键，避免并行干扰）。
- `枚举映射_种子模板识别类型与原始文本一致`：EF 读出的 `RecognizerType` 枚举经 `EnumMapping.ToSnakeCase` 转换后，与原生 SQL 读到的 `backup_task_templates.recognizer_type` 文本逐模板一致，并抽查已知种子值防整体改名假阳性。
- `种子数据_admin账号与角色齐备`：admin 存在且 `Active`，角色表非空。

### ⚠ 偏离说明（需审阅）

提示词建议用 `AddHealthChecks().AddNpgSql()`。`AddNpgSql` 是扩展方法，**依赖第三方包 `AspNetCore.HealthChecks.NpgSql`**；而共通前置硬约束 6 明确"不新增第三方依赖，除非提示词里明确允许"，提示词 4 未放开依赖。因此我用**框架内置** `AddHealthChecks()`（零新增依赖）+ 自定义只读 `DatabaseHealthCheck` 实现等价只读检查（`SELECT version()` + 表可达性）。若你希望严格用 `AddNpgSql`，需要显式批准引入该包，我可一行切换。

### 验收

- 匿名 `GET /health/db` → 401；持 `system.manage` 令牌 → 200，`ok=true`，`entries[0]` 报告 `Healthy`（回显 PostgreSQL 版本与表计数）。
- 连续 5 次调用后 `system_settings` 行数 **17→17 不变**，`setting_key LIKE 'diag_probe%'` 计数为 **0**（无任何新增/残留行）。

---

## OPEN-ISSUES #8 · ZIP 条目名 zip-slip 纵深防御

`DownloadsController` 打包备份集时，ZIP 条目名直接使用库里的 `RelativePath`。加一道 `PathSafety.IsValidRelativePath` 校验作为纵深防御（真正的防线在解压侧，见 BATCH3 的 `PathSafety` 修复）：

- 流式输出前的预检循环中，对每个文件先校验 `IsValidRelativePath(file.RelativePath)`，非法则抛 `INVALID_REQUEST` 400——此时尚未写出响应头，错误仍走统一 JSON 形状，且随后走既有记账逻辑，不会卡在 `downloading`。
- 创建条目时对条目名做 `Replace('\\','/')` 归一化为正斜杠，避免 Windows 反斜杠进入 ZIP 条目名（ZIP 规范要求 `/` 分隔）。

---

## 新增测试（src/tests/BackupMonitor.Infrastructure.Tests/）

- `DatabaseSelfCheckTests`（3 用例）：见 #6 ②。
- `AuthServiceProjectionTests`（3 用例，真实库 + 与生产一致的 DI 图）：
  - `登录_种子管理员_角色与全量权限完整交付`：admin → `Roles=[system_admin]`、`Permissions` 恰 16 项且无重复，含 `system.manage`/`clients.read`；并解码访问令牌断言 claims 与响应体一致（16 权限 + 1 角色）。
  - `登录_多角色用户_权限并集去重`：构造两角色（`clients.read` 重叠）的用户，断言交付权限为并集去重（3 项），角色数 2。
  - `刷新令牌_新访问令牌权限与登录一致`：刷新后新访问令牌 claims 权限与登录一致（覆盖 `RefreshAsync` 的投影路径）。

### 变异验证（mutation red-check）

把 `LoadRoleAndPermissionCodesAsync` 的权限投影临时改成"恒返回空列表"，`AuthServiceProjectionTests` **3/3 全红**（断言 `Expected 16, Actual 0`），证明测试真能捕获回归；随即还原，`git diff` 确认无残留，全套 59/59 复绿。

---

## 交付物清单

- `src/src/BackupMonitor.Infrastructure/Services/AuthService.cs`（投影重构，三处调用点 + 新辅助方法）
- `src/src/BackupMonitor.Api/Program.cs`（`AddHealthChecks` 注册 + `/health/db` 改 `MapHealthChecks` 只读，移除探针与无用 using）
- `src/src/BackupMonitor.Api/Health/DatabaseHealthCheck.cs`（新增，只读健康检查）
- `src/src/BackupMonitor.Api/Controllers/DownloadsController.cs`（zip-slip 校验 + 条目名归一化）
- `src/tests/BackupMonitor.Infrastructure.Tests/DatabaseSelfCheckTests.cs`（新增）
- `src/tests/BackupMonitor.Infrastructure.Tests/AuthServiceProjectionTests.cs`（新增）

## 验收对照

| 提示词 4 验收项 | 结果 |
| --- | --- |
| `dotnet build` 通过，已有测试仍全绿 | ✅ 0 警告 0 错误；59/59 全绿（含批 3 全部 53 项） |
| 登录 P95 明显下降，`MultipleCollectionIncludeWarning` 消失 | ✅ P95 842→402ms；对照实验确认警告 1→0 |
| `/health/db` 调用后 `system_settings` 无任何新增/残留行 | ✅ 5 次调用后行数不变，`diag_probe%` 计数 0 |

## 范围外发现（仅记录，未改动）

1. **`RefreshAsync` 的 `stored.Include(t => t.User)`** 是单值引用，不产生笛卡尔积，本批未动。若未来要进一步压登录延迟，可考虑只投影 `stored.User.Status`，但收益有限。
2. **登录延迟主体是 bcrypt**（workFactor 12，单次约 350ms+）。本批消除了查询侧行放大，但若要 P50 再降，方向是调整口令哈希成本或引入缓存，属安全权衡，不在本批范围。
3. **`/health`（应用级）与 `/health/db`（数据库级）** 现在形状不同（前者 `{status,timestamp}`，后者健康检查报告 JSON）。监控脚本若按旧 `/health/db` 形状解析需适配。这是有意为之：健康检查端点返回标准 `HealthReport` 投影，更利于接入通用探针。
4. `DatabaseHealthCheck` 目前只读 `version()` + 两张表计数。若希望覆盖"迁移版本"检查（提示词提到 SELECT 1、迁移版本），可后续加一张 `schema_migrations` 记录表——当前项目用 `dbinit.bat` 顺序执行 V*.sql，库内无迁移版本表，故以"结构可达（表可查）"作为等价信号。
