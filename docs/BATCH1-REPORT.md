# 批次 1 报告 · 提示词 1（P0 枚举默认值缺陷）

执行日期：2026-08-07　提交：`699d251`（基于 `6da1b6c` 初始快照）

## 1. 编译验证

2026-08-07 安全改动（机密外置、nginx 反代形态）此前未经编译验证。本批次先行全量重建：

**结论：0 警告 0 错误，无需修复任何编译错误。**（`dotnet build BackupMonitor.sln --no-incremental`）

## 2. OPEN-ISSUES #1 修复

缺陷：`BackupConfigurations.cs` 对 `task_mode` / `importance_level` 配置了
`HasDefaultValue(TaskMode.ApprovalRequired)` / `HasDefaultValue(ImportanceLevel.Normal)`。
EF 以"值等于 CLR 默认"判断"未设置"，于是显式写入 0 号成员
`TaskMode.Automatic` / `ImportanceLevel.Low` 时列被省略，数据库默认值
`approval_required` / `normal` 静默覆盖业务意图。

修复（与 OPEN-ISSUES 方案一致）：

- `BackupConfigurations.cs`：两处 `HasDefaultValue(...)` 移除，只保留列名映射，并加注释说明。
- `BackupTask.cs`：实体初始化器 `= TaskMode.ApprovalRequired` / `= ImportanceLevel.Normal`
  **已经存在**，无需改动——默认语义本来就由实体承担，本次只是拆掉 EF 侧的错误配置。
- 数据库列默认值（SQL 脚本）未动，非 EF 写入路径行为不变。

## 3. 全库同类扫描结果

按判据"任何 `HasDefaultValue(枚举值)`，且该枚举值等于 0 号成员"扫描全部 6 个配置文件，
另发现并一并修复 **18 处**（全部同样移除 `HasDefaultValue`，保留列名映射；
对应实体属性均已带与数据库默认值一致的初始化器，逐一核对过）：

| # | 位置（配置文件:实体.属性） | 原配置值（=0号成员） |
|---|---|---|
| 1 | Backup: CandidateBackupSet.PrecheckStatus | NotScanned |
| 2 | Backup: Command.Status | Pending |
| 3 | Backup: BackupSet.Status | Verifying |
| 4 | Backup: BackupFile.VerificationStatus | Verified |
| 5 | Client: RegistrationToken.Status | Active |
| 6 | Client: Client.Status | PendingApproval |
| 7 | Client: ClientCertificate.Status | Active |
| 8 | Client: MonitoredServiceDefinition.ExpectedState | Running |
| 9 | Client: ClientServiceState.StartType | Auto |
| 10 | Other: RestoreRequest.Status | Requested |
| 11 | Other: Alert.Status | Open |
| 12 | Other: NotificationDelivery.Status | Pending |
| 13 | Rbac: User.Status | Active |
| 14 | Rbac: UserRole.ScopeType | Global |
| 15 | Upload: UploadBatch.Status | Pending |
| 16 | Upload: UploadSession.Status | Created |
| 17 | Upload: UploadFileEntity.Status | Pending |
| 18 | Upload: UploadChunk.Status | Pending |

说明：这 18 处配置值与 CLR 默认相同，EF"总是用数据库默认值"恰好写入同一个值，
**不会造成数据损坏**（这也是它们不产生 sentinel 警告的原因）；但与 #1 同属一个缺陷形态，
按提示词要求一并消除。全库唯一真正损坏数据的组合就是 #2 节的两个非 0 号成员默认值。
`RetentionConfigurations.cs` 仅有 int/bool 默认值，无此问题。

## 4. 回归测试（新增）

`src/tests/BackupMonitor.Infrastructure.Tests`（已加入 `BackupMonitor.sln`）：
xUnit + **Testcontainers.PostgreSql**（提示词允许的依赖），真实容器内按序执行
`database/V001–V004` 迁移，不使用 EF InMemory。

- 用例 1「显式写入零号枚举成员」：TaskMode=Automatic + ImportanceLevel=Low 保存后，
  新上下文 `AsNoTracking` 重查 + 绕过 EF 的原生 SQL 双重断言，库中原始值必须为
  `automatic` / `low`。
- 用例 2「默认语义不回退」：不显式赋值时仍落 `approval_required` / `normal`。

验证记录：

| 步骤 | 结果 |
|---|---|
| 修复在位运行 | 2/2 通过 |
| 临时恢复 HasDefaultValue（重现缺陷） | 用例 1 失败、用例 2 通过（符合预期） |
| 恢复修复再运行 | 2/2 通过 |
| 启动 sentinel 警告 | 修复前 2 条（8/6 日志实证）→ 修复后 0 条（以 Development 模式短暂启动，登录并命中 /health/db 触发 EF 模型构建后检查控制台与 Serilog 文件日志，均无 sentinel/WRN） |

运行提示（本机环境）：

- 测试需要 Docker Desktop。本机 CLI 上下文与 Testcontainers 默认端点不一致，
  已在 `C:\Users\Administrator\.testcontainers.properties` 写入
  `docker.host=npipe://./pipe/dockerDesktopLinuxEngine`。
- 测试项目 `Npgsql` 显式钉在 **8.0.6**（与 Infrastructure 实际解析版本一致；
  官方源当前取 8.0.11 会因解析到 9.x 触发 `HackyEnumTypeMapping` TypeLoadException）。

## 5. 存量数据排查 SQL

曾被缺陷影响的行无法从数据本身确证（被覆盖后与"本来就是默认值"不可区分），
以下查询列出**疑似**被改写的任务（当前为 approval_required 且从未发生过模式切换），
交由业务负责人逐条确认创建时的真实意图：

```sql
SELECT id, name, client_id, task_mode, importance_level, created_at
FROM backup_tasks
WHERE task_mode = 'approval_required'
  AND previous_task_mode IS NULL
ORDER BY created_at;
```

importance_level 同理可查 `= 'normal'` 的行，但区分度更低，建议结合上线时间点人工核对。

本机联调库实测：任务共 1 条，疑似被改写 0 条（该环境任务由 SQL 脚本直接播种，未经 EF 插入路径）。

## 6. 计划外发现（不在本提示词范围，仅记录不动手）

1. **`.gitignore` 的 `data/` 规则误伤代码目录**：Windows 下忽略匹配大小写不敏感，
   `src/src/BackupMonitor.Infrastructure/Data/` 整个 EF 数据层被排除在初始快照之外。
   已锚定为 `/data/` 并在本提交补齐缺失文件（此为提交本批次的前提，故随批修复）。
2. **`setup-env.ps1` 尚未执行**：Machine 作用域无任何机密环境变量，
   交接检查表该条目仍未完成。当前运行中的 Windows 服务仍是 08-07 安全改动前的旧构建
   （C:\BackupMonitor，机密在其本地 appsettings 内）。
3. **数据库口令轮换未完成**：setup-env.ps1 中连接串仍用旧口令（TODO ALTER USER），
   与检查表一致。
4. 本机 Hyper-V 保留了大量 TCP 端口段（如 5940–6039），临时起服务选端口需先
   `netsh interface ipv4 show excludedportrange protocol=tcp` 核对。

## 7. 验收对照

- [x] 编译通过（且确认 08-07 改动本就无编译错误）
- [x] 回归测试通过，且回滚修复后必然失败（已实测）
- [x] 两条 sentinel 启动警告消失（2 → 0，实测）
- [x] 存量数据排查 SQL 已给出并本机演示
- [x] 未改枚举定义、未改 snake_case 转换、未重构其它配置、未扩大范围
