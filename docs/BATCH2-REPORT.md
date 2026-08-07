# 批次 2 报告 · 认证安全（提示词 2）

范围：OPEN-ISSUES #2、#5、#7、#9、#10、#11。
编译状态：本批改动一次编译通过，**无需修复编译错误**（`dotnet build -c Release`，0 警告 0 错误）。

## #2 · 默认口令硬编码 + 无改密入口（两层都做）

**数据库层（止血）**

- `V001__initial_schema.sql`：删除硬编码 `Admin@2026` 的 admin 种子（含 `user_roles` 分配，因外键需同移），留指向注释；4.5 节 `system_settings` 种子的 `updated_by` 改为 `NULL`（该列是 `REFERENCES users(id)` 的外键，admin 移出后若保留会违反 FK；列可空，语义上即"系统引导值"）。
- 新增 `V005__admin_password_bootstrap.sql`：
  - `ALTER TABLE users ADD COLUMN must_change_password boolean NOT NULL DEFAULT false`；
  - admin 账户改由 `psql -v admin_pw="$ADMIN_PW"` 注入（`crypt(:'admin_pw', gen_salt('bf',12))`），`must_change_password=true`，`ON CONFLICT DO NOTHING` 保证对已有库幂等；
  - 防御守卫：`SET app.admin_pw = :'admin_pw'` + DO 块校验。未注入变量时 SET 行成语法错误（ON_ERROR_STOP 中止）；空值或字面量 `admin_pw` 由 DO 块 RAISE 拦截。**注意 psql 变量在美元引号体内不插值**，守卫不能直接写在 DO 里。
- `dbinit.bat`：新增 `%ADMIN_PW%` 必填检查；步骤改为 1~7，V005 以 `-v admin_pw="%ADMIN_PW%"` 执行；验收输出新增 admin 标志查询。

**应用层（补能力）**

- `User.MustChangePassword` + EF 映射。**刻意不配 `HasDefaultValue(false)`**：false 即 CLR 默认值，配了会触发批 1 的哨兵值缺陷——改密成功后显式写 false 会被 EF 从 UPDATE 省略，标志永远清不掉（批 1 教训的直接应用）。
- `AuthService.ChangePasswordAsync`：校验旧口令（bcrypt，错误 → 401 + 审计失败、零状态变更）；强度校验在服务端（长度 ≥12、不等于用户名、小型常见弱口令表）；新哈希 workFactor 12 与 V001/V005 一致；成功后写 `password_changed_at`、清 `must_change_password`、`RevokeAllUserTokensAsync(reason: "password_changed")`、审计成功。
- 登录响应新增 `mustChangePassword`；新增 `POST /api/v1/auth/change-password`（Bearer）。
- 控制台（wwwroot/index.html）：登录成功按标志跳转 `#/change-password`；强制改密期间路由守卫拦截一切其它路由；改密成功清本地登录态回登录页（服务端已吊销全部刷新令牌）。

**验收（全部通过）**：改密后旧访问令牌仍可用（无状态 JWT）、全部刷新令牌 401；旧口令错 → 401 且哈希/标志/令牌零变更；全新部署 E2E：注入口令 → 登录 `mustChangePassword=true` → 改密 → 新口令登录标志为 false。

## #5 · X-Request-Id 注入面

`RequestIdMiddleware`：外部传入的 requestId 须 ≤64 且仅 `[A-Za-z0-9_-]`，否则丢弃并服务端生成 Guid-N（不再回显攻击者可控内容进日志/响应头）。E2E 验证非法值被替换、合法值原样回显。

## #7 · 用户名枚举（时延）

`AuthService` 增加静态 `DummyHash`（workFactor 12，与真实哈希同成本），用户不存在分支先做一次 dummy `Verify` 再返回统一错误。

## #9 · 登出强制请求体

`Logout([FromBody] RefreshTokenRequest? request)` 改可空，空体登出 200。

## #10 · TokenHasher 注释

`GenerateToken` 的 XML 注释由错误的 "base64url" 改为实际行为（小写十六进制，长度 byteLength×2）。按要求仅改注释。

## #11 · 文档整理

删除 `doc1.md/doc2.md/doc3.md`（与中文命名文档逐字节相同，已 cmp 验证）；三份正式文档 `git mv` 进 `docs/`；新增 `docs/README.md`（文档索引 + 章节速查表，含「设计书 X.Y」引用约定说明）。

## 测试

- 夹具升级：按序执行 V001~V005；V005 的 `:'admin_pw'` 在执行前替换为测试常量（Npgsql 不支持 psql 变量）。
- 新增 `AuthSecurityRegressionTests`（6 用例）：V005 引导可登录且强制标志为真、未知用户统一 401、错旧口令 401 零变更 + 审计、成功改密清标志并全量吊销刷新令牌 + 新旧口令登录对照、强度规则服务端拦截。
- 结果：**8/8 通过**（含批 1 的 EF 哨兵回归 2 例）。
- E2E（一次性库 + 4900 端口临时实例）：**19/19 通过**。

## 范围外发现（仅记录）

1. `users.mfa_enabled` 等既有列同样存在「`HasDefaultValue(false)` + CLR 默认值」的哨兵值模式（若将来有"关闭 MFA"写路径会踩坑）。本批不改，留给用户管理相关批次。
2. `V005` 守卫失败时 `must_change_password` 列已先行 ALTER 提交（psql 自动提交），重跑会报列已存在——属预期：修正 ADMIN_PW 后需人工确认库状态；生产路径由 dbinit.bat 前置检查兜底。
3. setup-env.ps1 为无 BOM UTF-8，Windows PowerShell 5.1 默认按 ANSI 读取会解析错位，脚本处理需显式 `-Encoding UTF8`（交接检查表执行时注意）。
