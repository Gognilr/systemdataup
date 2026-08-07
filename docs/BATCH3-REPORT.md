# 批次 3 报告 · 测试骨架与 CI（提示词 3）

范围：DEV-PROMPTS 提示词 3 —— 补三处核心用例（上传断点续传 / 保留清理 / 路径安全）+ `.github/workflows/ci.yml`。
编译状态：`dotnet build -c Release` 0 警告 0 错误；`dotnet test` **53/53 全绿**（Testcontainers 真实 PostgreSQL 15 容器，复用批 2 的 V001~V005 夹具）。

## 新增测试（src/tests/BackupMonitor.Infrastructure.Tests/）

### a) 上传断点续传 —— UploadSessionResumeTests（6 用例）

真实库 + 真实 `UploadStorage` 磁盘暂存（暂存根经 `system_settings.staging_path` 注入每测试独立临时目录），不用内存库。测试载荷 40 字节 / 分块 16 字节 / 3 块：

- 缺块查询_初始全部缺失：`created` 会话 → `Missing=[0,1,2]`、`Received=[]`，含 `upload.session.create` 审计断言。
- 乱序上传_重复块_偏移校验：2→0→1 乱序收齐；重复传块 1 幂等（`upload_chunks` 仍 1 行）；错偏移/越界块 → `CHUNK_INVALID`；整文件哈希一致 → `verified`。
- 块哈希不匹配拒收：错块哈希 → `CHUNK_HASH_MISMATCH` 409，文件置 `failed`，该块不计入已接收。
- 整文件哈希比对：错整文件哈希 → `FILE_HASH_MISMATCH`（服务端真实哈希仍记录在 `server_sha256`）；纠正后 `verified`；重复完成幂等。
- 跨会话续传_幂等键恢复：**换一套 DI 容器（模拟进程重启）**，凭幂等键取回原会话（`resumed`、同会话 ID），缺块查询反映已持久化分块（只缺块 2），补传后完成。
- 大文件缺块查询返回范围：1100 块文件 → 不再逐块返回，`MissingRanges=[{0,1099}]`。

说明：`CreateUploadSessionRequest.ChunkSizeBytes` 的 4MB~32MB `Range` 注解只在控制器模型校验层生效，服务层不校验，故测试可用小分块（这本身是一处记录项，见"范围外发现"）。

### b) 保留清理 —— RetentionCleanupWorkerTests（3 用例）

通过真实 `BackgroundService`（`StartAsync` 首轮立即执行 → 轮询断言 → `StopAsync`），DI 组合与生产一致（`AppDbContext` / `DbAuditRecorder` / `AlertingService` / `ScheduledLockService` / `SystemSettingsProvider`）。全部时间用 `DateTime.UtcNow`（与 worker 内部一致；保留期边界即时区问题的根源在于混用本地时间，此处显式对齐 UTC）。

- Gfs保留_最后副本与保留期边界：策略 `KeepLastCount=1 / MinimumRetentionDays=30`。最新（-5d）为最后可用副本必须保留；-10d 在最短保留期内保留；-40d 超期且未被 GFS 保留 → 回收站（`retention_until≈now+7d`），审计 `retention.recycle` 恰 1 条且只对应被回收集合。
- 保留锁与字段锁阻止回收：策略 `MinimumRetentionDays=1`（全部超期、无 GFS 名额）。永久活动 `retention_locks` 与 `backup_sets.locked=true` 阻止回收；**已到期锁（expires_at 已过）与失效锁（active=false）不阻止**；无锁集合被回收作对照。
- 回收站到期物理清除_锁与未到期阻止：到期且无锁 → 状态 `deleted` 且仓库目录被物理删除（含审计 `retention.delete`）；同到期但被活动锁锁定 → 保持 `recycle_bin`、目录保留；未到期 → 保持。临时目录满足"至少两级"删除守卫。

### c) 路径安全 —— PathSafetyTests（约 40 断言，纯逻辑）

- `..`（头部/段级/纯..）、绝对路径、空段、UNC、`\\.\` 与 `\\?\`（含 `\\?\UNC\`）、盘符绝对/相对（`C:\`、`C:x`）、段尾随空格、段尾随点、超长（>2048 拒绝、≤2048 通过）、null/空白。
- 段含 OS 非法字符拒绝（**平台感知**：Windows 含 `<>:"|?*` 等，Linux 仅 `\0`，CI 与本机都绿）。
- `ResolveUnderBase`：穿越返回 null；合法拼接位于基目录内；**大小写不同的基目录不误拒**（OrdinalIgnoreCase 包含判断）。
- `SanitizePathComponent`：`..`→`_`、尾随点去除、首尾空格去除、空→`unnamed`、超长截断 100。

## CI —— .github/workflows/ci.yml

按 OPEN-ISSUES #3 骨架落地：`on: [push, pull_request]`，ubuntu-latest，checkout v4 + setup-dotnet 8.0.x，`dotnet build` + `dotnet test --no-build`（Release）。ubuntu-latest 运行器自带 Docker，Testcontainers 可直接启动一次性 PostgreSQL 容器。

## 唯一的生产代码改动（测试发现并修复）

`PathSafety.IsValidRelativePath` 尾随空格/点漏检（设计书 23.4）：

- 原实现先 `Trim()` 整条路径再做段级检查，**整条路径末尾的尾随空格会被吃掉而漏检**；且从未拒绝尾随点。NTFS 写盘时会静默剥离文件名尾随空格与点，这类路径若放行，清单名与实际落盘名将不一致（预检 `AgentPrecheckService` 校验 → 提交入库 `UploadCommitWorker.ResolveUnderBase` 链路都经过它）。
- 修复：段级检查改用未去首尾空白的规范化串；段尾随空格**或尾随点**一律拒绝。对应新增用例 `a/b `、`a/b.`、`a/b.../c`。
- 变异检验：注释掉该检查后 3 个用例立即变红（见下）。

## 变异（改坏被测逻辑）红检验

| # | 改坏点 | 变红用例 | 已还原 |
|---|--------|----------|--------|
| 1 | `PathSafety` 去掉尾随空格/点拒绝 | 非法路径应拒绝 ×3（`a/b `、`a/b.`、`a/b.../c`） | ✅（与 HEAD 逐字节一致） |
| 2 | `RetentionCleanupWorker.ApplyGfsRetentionAsync` 锁定不再跳过 | 保留锁与字段锁阻止回收 | ✅ |
| 3 | `UploadSessionService.UploadChunkAsync` 块哈希不一致不再拒收 | 块哈希不匹配拒收（`Assert.Throws` 无异常） | ✅ |

还原后全量 53 用例复跑全绿；`git diff` 确认生产代码仅 `PathSafety.cs` 一处净改动（+7/−4）。

## 范围外发现（仅记录，不改）

1. **保留策略全空时无"最后副本"兜底**：`KeepLastCount/Weekly/Monthly/Yearly` 全 NULL 时 GFS 不保留任何版本，超过 `MinimumRetentionDays` 的**全部** available 集合都会被回收——含最后一个可用副本。本批用例按提示词以 `KeepLastCount=1` 覆盖"最后副本不得删除"；策略全空的兜底（建议：全空时隐式保留最新 1 个，或建策略时强制至少一项）留待产品决策。
2. `IsValidRelativePath` 对**以两个点开头**的整条路径 `StartsWith("..")` 拒绝，会误伤 `..b/x` 这类合法文件名（段级检查本身只拒绝恰为 `.`/`..` 的段）。影响极小，未改。
3. `SanitizePathComponent("name...")` 得 `"name_"`（`..` 替换先于 `TrimEnd('.')` 的顺序产物），安全但略丑，未改；`"name."` 行为正确。
4. `UploadSessionService` 不校验 `ChunkSizeBytes` 范围（4MB~32MB 仅控制器层 DataAnnotation），服务被直接调用时可传任意值。属设计内分层，仅记录。
5. CI 首次运行的关注点：测试需要拉取 `postgres:15` 镜像并启动容器，PR 流水线耗时以分钟计属正常；若企业 runner 无 Docker，需另配（当前 GitHub hosted runner 无此问题）。
