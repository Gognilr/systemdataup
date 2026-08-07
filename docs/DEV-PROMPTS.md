# 交付提示词集

给开发者交接用。每个提示词对应一个可独立上线的批次，粘贴给 AI 编码助手（Claude Code / Cursor 等）即可执行。

## 使用说明

1. **每次只跑一个批次。** 不要把多个提示词拼起来——范围一大，助手就会开始自作主张扩大改动。
2. **每个提示词前面都要先贴一遍「共通前置」**，助手是冷启动的，不知道项目背景与约束。
3. **顺序有讲究**：`提示词 1` 必须最先跑（它同时承担了验证上一轮改动的职责）。之后 2→3→4 与 5→6→7→8 两条线可以并行。
4. 每批跑完先 review diff、跑构建、再合并，不要连续跑两个批次。

**必读背景文档**：[OPEN-ISSUES.md](OPEN-ISSUES.md)（问题清单与方案）、[UI-REDESIGN.md](UI-REDESIGN.md)（UI 重构方案）、[../deploy/README.md](../deploy/README.md)（部署形态）

---

## 【共通前置】

> 每个提示词之前先贴这一段。

```
## 项目背景

BackupMonitor：轻量级集中备份采集与监控系统的服务端。
- .NET 8 + PostgreSQL + EF Core，四层结构：
  src/src/BackupMonitor.{Api,Infrastructure,Core,Shared}
- 管理端 JWT 认证 + 16 个权限策略；Agent 端客户端证书（mTLS）认证
- 部署形态：nginx 终结 TLS/mTLS，反向代理到 Kestrel 127.0.0.1:5080
- 管理控制台是 wwwroot/index.html 单文件，原生 JS，零构建
- 代码注释与错误文案一律中文，注释里用「设计书 X.Y」引用需求文档章节

## 硬约束

1. 【重要】仓库在 2026-08-07 有一批安全改动（密钥外置、nginx/mTLS 透传、CA 口令
   持久化、中间件顺序），因为当时机器上没有 .NET SDK，**这批改动从未编译验证过**。
   你的第一件事永远是先跑通 `dotnet build`，发现编译错误先修掉再做本职任务，
   并在最终报告里单独说明修了什么。
2. 机密（数据库口令、签名密钥）绝不写回任何 appsettings*.json。它们已全部外置为
   环境变量，见 setup-env.ps1 与 deploy/README.md。
3. 这个目录当前不是 git 仓库。动手前先 `git init` 并提交一次初始快照，
   保证改动可回滚。.gitignore 已经写好，不要动它。
4. 不要扩大范围。只做当前批次明确要求的事。发现别的问题就记下来，
   在报告末尾列出来，不要顺手改。
5. 匹配现有代码风格：中文注释、现有的命名习惯、现有的异常与响应封装
   （BusinessException / ApiResponse<T>）。
6. 不新增第三方依赖，除非提示词里明确允许。
```

---

## 提示词 1 · P0 枚举缺陷（最优先）

```
读 docs/OPEN-ISSUES.md 的 #1，按方案修复。

背景：TaskMode 和 ImportanceLevel 的 0 号成员（Automatic / Low）恰好是 CLR 默认值，
EF 把「值等于 CLR 默认」当成「没赋值」，于是插入时改用 HasDefaultValue 指定的数据库
默认值。后果是建一个自动备份任务，落库后变成「需审批」——任务不会自动执行，静静地等
一个没人知道要去点的审批。这是静默的数据损坏。

任务：
1. 先 dotnet build 跑通（见共通前置第 1 条）
2. 按 OPEN-ISSUES #1 的方案修：去掉 BackupConfigurations.cs 里这两个属性的
   HasDefaultValue，把默认语义移到 BackupTask 实体的属性初始值
3. 全库扫一遍是否还有同类问题：任何 HasDefaultValue(枚举值) 的配置，只要该枚举值
   等于 0 号成员，就是同一个缺陷。找到就一并按同样方式修掉，并在报告里列出来
4. 写回归测试。项目现在没有测试工程，建一个
   tests/BackupMonitor.Infrastructure.Tests（xUnit）。这个用例必须真连 PostgreSQL
   才有意义——InMemory provider 不走数据库默认值，测了等于没测。
   用 Testcontainers.PostgreSql（允许新增这个依赖）
   用例：创建 TaskMode=Automatic + ImportanceLevel=Low 的任务，保存后重新查出来，
   断言两个值原样保留
5. 输出一份 SQL，用于排查存量数据里可能已被静默改写的任务

不要做：不要改枚举定义本身（会影响数据库域类型和已有数据）；不要改
snake_case 转换逻辑；不要顺手重构 BackupConfigurations 的其它部分。

验收：
- dotnet build 通过
- 新测试通过，且把修复回滚掉后测试会失败（确认测试真的测到了东西）
- 启动日志里两条 "no configured sentinel value" 警告消失

报告里请单独说明：修了哪些编译错误（如果有）、第 3 步还发现了哪些同类问题。
```

> 跑完这个提示词后，**存量数据核对要人来做**——无法从数据本身区分「本来就要审批」和「被静默改写」，需要拿着 SQL 结果找业务方逐条确认。这是整个交接里唯一无法自动化的事项，也是唯一可能已经在造成损失的事项。

---

## 提示词 2 · 认证安全批次

```
读 docs/OPEN-ISSUES.md 的 #2、#5、#7、#9、#10、#11，一并处理。

主体是 #2，其余五项都是十几分钟的小改动，顺带做掉。

#2 是两层问题，第二层更严重：默认口令 Admin@2026 硬编码在 V001 迁移脚本里，
而且【整个系统没有任何修改口令的接口】——AuthController 只有 login/refresh/
logout/me，也没有用户管理控制器。部署方即使想改口令也只能手工执行 SQL。

任务：
1. 先 dotnet build 跑通
2. #2 第一步：新增迁移 V005，给 users 表加 must_change_password 列；
   迁移脚本不再种入固定口令，改为部署时通过 psql -v 变量注入。
   同步更新 dbinit.bat（它已经改成从环境变量读 PGPASSWORD 了，照这个路子加 ADMIN_PW）
3. #2 第二步：新增 POST /api/v1/auth/change-password
   - 需 Bearer 认证
   - 校验旧口令，bcrypt workFactor 必须与 V001 的 gen_salt('bf',12) 一致
   - 成功后写 password_changed_at、清 must_change_password、
     并吊销该用户全部刷新令牌（AuthService.RevokeAllUserTokensAsync 已存在，
     reason 用 "password_changed"）
   - 口令强度校验放在 AuthService，不要只放前端：长度≥12、不等于用户名
   - 记审计（IAuditRecorder 已有）
4. 登录响应体加 mustChangePassword 字段；wwwroot/index.html 里加强制改密流程，
   未改密前不放行其它路由
5. #5 #7 #9 #10 按 OPEN-ISSUES 里给出的代码改
6. #11：doc1/doc2/doc3.md 与三个中文名文件字节级重复，删掉 docN.md 一组，
   把中文名一组移到 docs/。代码注释里大量引用「设计书 23.1」这类章节号，
   在 docs/README.md 放一张章节号→文件的对照表

不要做：不要引入完整的用户管理 CRUD（那是另一个迭代的事）；#10 只改注释不改实现
（令牌已签发在外，改编码会让所有存量刷新令牌与注册令牌失效）。

验收：
- dotnet build 通过
- 改密后旧的 access token 仍在有效期内可用（这是预期行为），但旧 refresh token 全部失效
- 用错误的旧口令改密返回 401 且不改变任何状态
- 全新部署走一遍：注入口令 → 登录 → 被强制改密 → 改完能正常用
```

---

## 提示词 3 · 测试骨架与 CI

```
读 docs/OPEN-ISSUES.md 的 #3。

项目现在零测试、零 CI。对一般业务系统这是欠债，对备份系统风险等级更高——
这类系统的失效大多是静默的（#1 就是活例子，一个改变任务执行模式的缺陷，
在日志里只表现为一条 EF 警告）。

任务：
1. 先 dotnet build 跑通
2. 建测试工程（若提示词 1 已建则复用）：xUnit + Testcontainers.PostgreSql。
   不要用 EF InMemory provider——这个项目大量依赖 PostgreSQL 特性
   （jsonb、ON CONFLICT...WHERE、RETURNING、interval、域类型枚举），
   ScheduledLockService 的 FromSqlRaw 在 InMemory 下根本跑不起来
3. 测试容器要跑 src/database/V001~V005 全部迁移，保证测的是真实 schema
4. 按优先级补三处核心用例（理由见 OPEN-ISSUES #3 的表格）：
   a) UploadSessionService 断点续传：缺块查询、乱序上传、重复上传同一块、
      块哈希不匹配拒收、整文件哈希比对、跨会话续传
   b) RetentionCleanupWorker：保留期边界（注意时区）、retention_locks 必须阻止删除、
      最后一个可用副本不得删除
   c) PathSafety：`..`、UNC、\\?\、盘符、尾随空格与点、大小写穿越、超长路径
5. 加 .github/workflows/ci.yml：build + test，PR 和 push 都跑

不要做：不要追求覆盖率数字；不要给 Controller 写一堆浅层测试。
优先级标准是「错了会静默丢数据」，不是「好写」。

验收：
- dotnet test 全绿
- 每个用例都能通过「把被测逻辑改坏，测试要红」的检验
- CI 在 PR 上能跑起来
```

---

## 提示词 4 · 性能与纵深

```
读 docs/OPEN-ISSUES.md 的 #4、#6、#8。都不阻塞上线，一批做掉。

#4：AuthService 三处 Include 链（UserRoles→Role→RolePermissions→Permission）
是两层集合导航，EF 默认 SingleQuery 生成笛卡尔积。一个挂 3 角色、每角色 16 权限
的用户，单次登录拉 48 行重复数据。登录实测 1228ms。
按 OPEN-ISSUES #4 改成投影查询——这些实体被 Include 进来其实只为了取两串字符串。
注意 LoginAsync 里的 user 实体本身仍需跟踪（要写 FailedLoginCount / LastLoginAt），
两条投影查询要独立于主查询，主查询去掉全部 Include。

#6：/health/db 会向生产表 system_settings 写探针行做乐观锁自检。
拆成两个东西——它们本来就是两个东西：
  - /health/db 只保留只读检查（SELECT 1、迁移版本），用 AddHealthChecks().AddNpgSql()
  - 乐观锁与枚举映射自检迁进测试工程（依赖提示词 3 的成果）

#8：DownloadsController 的 ZIP 条目名直接用库里的 RelativePath，
加一道 PathSafety.IsValidRelativePath 校验（纵深防御，真正的防线在解压侧）。

验收：
- dotnet build 通过，已有测试仍全绿
- 登录接口 P95 明显下降，且 EF 的 MultipleCollectionIncludeWarning 消失
- /health/db 调用后 system_settings 表无任何新增/残留行
```

---

## 提示词 5 · UI 令牌层（纯 CSS，风险最低）

```
读 docs/UI-REDESIGN.md，执行第 10 节的 P1。

只改 wwwroot/index.html 的 <style> 块，不动任何 HTML 结构和 JS。

现有 UI 不是常见的「渐变+毛玻璃」那种 AI 味（全文件只有 1 处渐变、0 处
backdrop-filter），它的问题在别处，见 UI-REDESIGN 第 1 节那张定位表。
P1 针对的是其中第 1、7 两条纯 CSS 症状。

任务：
1. 按 UI-REDESIGN §4.1 落地颜色令牌，替换掉现在 :root 里的 Tailwind 默认色板
   （#2563eb / #f1f5f9 / #1e293b / #16a34a 那一套）
2. 落地 §4.2 排版令牌：字阶、三档字重（400/500/600，去掉 700）、
   所有数字单元格加 font-variant-numeric: tabular-nums、
   ID/哈希/路径/主机名改等宽字体
3. 落地 §4.3 的间距/圆角/层级/动效令牌，把散落的 6px 9px / 14px 16px 这类
   随手值统一到 4px 栅格
4. 补暗色主题：prefers-color-scheme + data-theme 手动覆盖，顶栏加切换按钮，
   选择持久化到 localStorage
5. 正文默认字号 14px → 13px，行高按 §4.2
6. 去掉登录页那处装饰性渐变
7. 补 prefers-reduced-motion 处理

不要做：不要动 HTML 结构；不要动 JS；不要改信息架构（那是 P3）；
不要引入任何构建工具或 CSS 框架——这个项目零构建是刻意的，见 UI-REDESIGN §9。

验收：
- 明暗两套主题下，正文对比度≥4.5:1、大字与图形≥3:1（用 axe 或 Lighthouse 验）
- 全部功能与改动前完全一致（纯样式改动）
- 页面截图转灰度后仍可读
- 无外部网络请求
```

---

## 提示词 6 · UI 组件层

```
读 docs/UI-REDESIGN.md 第 6 节，执行第 10 节的 P2。前置：提示词 5 已完成。

同时按 §9 做代码拆分：现在 1200 行 JS 挤在一个 <script> 里，视图函数、动作分发、
工具函数交织。拆成原生 ES module（浏览器直接支持，仍然零构建）：

  wwwroot/
    index.html          外壳 + <script type="module">
    css/tokens.css      提示词 5 的成果搬过来
    css/base.css        重置/排版/表单
    css/components.css  表格/状态/抽屉/命令面板/空态/骨架
    js/api.js           请求 + 令牌刷新 + 错误规范化
    js/ui.js            组件工厂
    js/views/*.js       一视图一文件

组件按 §6 规范实现：
1. 状态标记（§6.1）替换现有 .badge：形状+文字+颜色三重编码。
   关键取舍：列表里正常状态不再用背景色块，只有真正异常（失败/离线/严重告警）
   才用带背景的胶囊。一屏 20 行每行 3 个彩色胶囊，颜色就失去报警能力了
2. 数据表格（§6.2）：32px 紧凑行高 + 密度切换、粘性表头、数字列右对齐、
   相对时间（title 挂绝对时间）、可排序表头带 aria-sort、多选后工具条原地变形为
   批量动作条
3. 详情抽屉（§6.3）替换详情类模态：右侧 520px 滑入、focus trap、Esc 关闭、
   焦点归还、hash 同步可分享。表单类模态保留——区别标准是「需要对照列表看的→抽屉，
   需要专注填写的→模态」
4. 空态三分（§6.5）：首次为空/筛选为空/正常的空，各说各的话。
   「正常的空」是好消息，要说得像好消息
5. 骨架屏替换「加载中…」，不做闪烁动画

不要做：不要引入 React/Vue/任何构建链（理由见 §9）；不要改信息架构（P3）；
不要动后端。

验收：
- 全部现有功能不回归
- 表格默认行高 32px，1080p 下客户端列表首屏可见 ≥20 行（现在约 12 行）
- 抽屉可纯键盘操作：打开、切 Tab、关闭、焦点正确归还
- 截图转灰度后全部状态仍可区分
- 首屏 JS < 60KB 未压缩，零外部请求
```

---

## 提示词 7 · UI 信息架构与命令面板

```
读 docs/UI-REDESIGN.md 第 5、6.4 节，执行第 10 节的 P3。
前置：提示词 6 已完成。

这一批解决的是比视觉严重得多的问题：现在的信息架构复刻了 REST API 而不是运维员
的工作——导航项叫 deliveries / notif / operations，是端点名不是任务名。

任务：
1. 按 §5 的表把 12 项扁平导航重组为 4 组（值守/机群/数据/管理），
   侧栏默认折叠成 56px 图标栏，可展开
2. 新增「待办」页——这是整个重组的核心。汇总待审批客户端、连续失败任务、
   待签发恢复、未确认严重告警。现在这些事项散落在各列表的行内按钮里，
   你得先想到去那儿看
3. 合并「通知设置」+「通知投递」为一页两 Tab（设置是配置，投递是该配置的运行结果）
4. 「批量操作」不再是导航项：批量预检/批量上传改为机群列表里多选行后的动作，
   这个页面保留下来的价值是查看已提交批次的结果，改名「作业历史」放进管理组
5. 命令面板（§6.4）：⌘K/Ctrl+K，三类结果——跳转、搜索（复用现有列表接口的
   keyword 参数）、动作
6. 键盘导航按 §6.4 的键位表；? 键出快捷键一览
7. hash 路由要跟着新 IA 更新，且保持旧路径可重定向（别让人存的书签全失效）

不要做：不要改后端接口；不要重做单个列表页的内容（那是 P5）。

验收：
- 核心流程可纯键盘完成：登录→查待办→审批客户端→查看备份集详情
- 旧 hash 路径能正确重定向到新路径
- 命令面板可搜到客户端主机名、任务名、备份集编码
```

---

## 提示词 8 · UI 值守台（收益最大，依赖最多）

```
读 docs/UI-REDESIGN.md 第 7.1 节，执行第 10 节的 P4。
前置：提示词 6、7 已完成。这一批需要改后端。

要解决的核心问题：现有 UI 完全无法回答「昨晚该跑的备份都跑了吗」。
仪表盘那张 14 天柱状图只表达「全系统每天入库了几个备份集」——一根柱子高不代表
该跑的都跑了：可能是同一个任务跑了 14 次，另外 30 个任务一次没跑。
要查单个任务得跳四次页面。

任务：
1. 后端：扩展 GET /api/v1/admin/reports/task-summary，
   增加按「任务 × 日期」的入库状态矩阵（最近 14 天），
   每格状态：成功/失败/进行中/当日无计划。
   注意权限沿用 perm:tasks.read；注意别把这个查询写成 N+1
2. 前端按 §7.1 重做概览页四层结构：
   ① 状态条：单行，不是四张计数卡。多数时候答案是「没事」，不该占掉首屏
   ② 待办队列：每行一件事 + 一个主动作。空时说「没有待处理事项」+ 最后检查时间
   ③ 备份时序热力图：X=最近14天，Y=备份任务，行按异常优先排序。
      这张图是本次重构的根本目的
   ④ 容量与趋势：仓库用量 + 预计写满时间 + 14天入库量迷你折线，
      带 Y 轴刻度和基线（现在那张图无轴无刻度无基线）
3. 原来的三个分布列表（客户端状态/备份集状态/告警分布）移到各自列表页顶部，
   做成可点击的筛选 chip——分布信息在能直接筛选的地方才有用
4. 热力图要有无障碍替代：表格形式的等价内容，供屏幕阅读器使用

不要做：不要引入图表库（热力图和迷你折线用 CSS grid + inline SVG 就够，
引库违背零构建约束）。

验收：
- 无异常时概览首屏不超过一屏高，且明确显示「一切正常」
- 「昨晚该跑的备份都跑了吗」能在首屏一眼回答，无需任何跳转
- 热力图在灰度下仍可区分四种状态
- task-summary 接口在 100 任务 × 14 天规模下响应 < 300ms
```

---

## 交接检查表

给接手的人：

- [ ] 已 `git init` 并提交初始快照
- [ ] 已在有 .NET SDK 的机器上跑通 `dotnet build`（**上一轮改动从未编译验证过**）
- [ ] 已运行 `setup-env.ps1` 注入环境变量，服务能正常启动
- [ ] 已在 PostgreSQL 侧执行 `ALTER USER` 轮换口令，并同步更新 setup-env.ps1
      （旧口令 `zll4239833` 曾以明文躺在版本库里，必须视为已泄露）
- [ ] 已按提示词 1 的 SQL 排查存量任务，并与业务方核对
      （**这是唯一无法自动化、且可能已在造成损失的事项**）
