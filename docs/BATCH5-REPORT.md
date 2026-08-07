# 批次 5 报告 · UI 令牌层（提示词 5）

范围：DEV-PROMPTS 提示词 5 —— UI-REDESIGN §10 的 P1。只改 `src/src/BackupMonitor.Api/wwwroot/index.html`，落地 §4 设计令牌层 + 暗色主题，消除「Tailwind 默认色板原样搬用」（§1 定位表症状 #1）与「字阶过平、无数字对齐」（症状 #7）两条纯 CSS 病灶。

零构建约束保持：无新依赖、无外部网络请求、无构建工具；全部令牌集中在单个 `<style>` 块内（提示词 6 再拆分为 `css/tokens.css` 等文件）。

---

## 与「只改 CSS、不动 HTML/JS」的偏差说明

提示词 5 的总约束是「只改 `<style>` 块，不动任何 HTML 结构和 JS」，但其任务 4 同时要求「顶栏加切换按钮、选择持久化到 localStorage」——这在物理上不可能纯 CSS 完成。按「任务条目优先于总括描述」处理，做了**最小**的 HTML/JS 触点：

- HTML：顶栏追加 1 个 `<button id="themeBtn">`（切换明暗）；`<head>` 内 8 行首帧主题还原脚本（读 `localStorage.bm_theme`，仅还原显式保存过的选择，避免暗色用户首帧闪白）。
- JS：`effTheme()` / `applyTheme()` / `App.toggleTheme` 三个小函数 + 系统偏好变化监听。合计约 30 行，不触碰任何既有逻辑、路由、渲染函数。

其余 6 项任务全部纯 CSS 完成。JS 中约 70 处内联样式仍按旧变量名（`--primary`/`--card`/`--muted`/`--ok`…）引用，通过在 `:root` 中保留**主题感知的兼容别名**（如 `--primary:var(--accent)`、`--card:var(--surface)`）使 JS 一行不改即随主题切换。

---

## 七项任务逐条落地

1. **颜色令牌（§4.1）**：`:root` 内 Tailwind 默认色板（`#2563eb`/`#f1f5f9`/`#1e293b`/`#16a34a`/`#d97706`/`#dc2626`）全部移除，替换为 ink 中性色阶（`--ink-0`~`--ink-950`，极轻微冷偏）、强调色 `--accent:#5b4fc7`（浅色）/`#8b7ee8`（暗色，刻意选在语义色相之外，「按钮」与「状态」不再混淆）、四组语义三色对（`--ok/-warn/-err/-info` 各含 `-fg/-bg/-bd`）与语义别名层（`--bg/--surface/--surface-sunk/--border/--border-strong/--text/--text-muted/--text-faint`）。
2. **排版令牌（§4.2）**：`--t-micro`~`--t-num` 七档字阶；全站字重收敛到 400/500/600（原指标数字的 700 降为 600，`font-weight:700` 已清零）；`td,.stat .n,.pager` 一律 `font-variant-numeric:tabular-nums`；`.mono,pre.json` 用 `--font-mono` 等宽栈。
3. **间距/圆角/层级/动效令牌（§4.3）**：`--s-1`~`--s-12`（4px 栅格）、`--r-sm/md/lg`、`--e-1/e-2`、`--dur-fast/base`、`--ease`、`--row-h:32px`。原 `6px 9px`、`14px 16px`、`18px 20px`、`9px 18px` 等随手值全部归一到令牌。
4. **暗色主题**：`:root[data-theme="dark"]`（手动优先）+ `@media (prefers-color-scheme: dark){:root:not([data-theme="light"])}`（未手动选择时跟随系统）双通道；暗色下不用阴影表达层级（表面提亮：`--surface` 比 `--bg` 亮一档）；两套均声明 `color-scheme`，原生控件（滚动条、下拉）随之适配；选择持久化到 `localStorage.bm_theme`。
5. **正文密度**：14px → 13px（`--t-body:13px/20px`），同屏行密度提升。
6. **登录页渐变移除**：`linear-gradient(135deg,#1e293b,#334155)` → `var(--bg)`（§7.5），登录卡片改为表面色 + 边框 + `--e-2`。
7. **`prefers-reduced-motion`**：`reduce` 时全站 `animation/transition` 清零。

附加的小项：`:focus-visible` 统一 `2px solid var(--accent)` 焦点环（不准 `outline:none`）；侧栏底部署名与 `.frow .hint` 从不可达标的浅色改到 `--text-muted`/`ink-400`（见下对比度节）。

---

## 验收（实测）

验证环境：临时 API（Release 构建）跑在 127.0.0.1:4900，独立 scratch 库 `backup_monitor_e2e_b5`（V001~V005 迁移 + 种子），验证后已停进程、删库。生产 5080 服务全程未受影响。

### ① 对比度（程序化计算，相对亮度公式 WCAG 2.x）

对 31 组「前景/背景」配对计算对比度，初测 2 组不合格，均已修复：

| 配对 | 初测 | 处理 | 复测 |
| --- | --- | --- | --- |
| 浅色 `.frow .hint`（ink-400 文字 / 白面） | 3.34:1 | `.frow .hint` 改用 `--text-muted`(ink-500) | 5.34:1 ✓ |
| 暗色 `.frow .hint` | 3.16:1 | 同上（令牌主题感知） | 5.06:1 ✓ |
| 侧栏 `.foot`（ink-500 / ink-900 侧栏） | 3.16:1 | 提亮到 ink-400 | 5.06:1 ✓ |

修复后全部配对达标：正文类 ≥4.5:1（如浅 5.34 / 暗 5.06 起步，主文本浅 ~15 / 暗 ~13），大字与图形类 ≥3:1（徽标、按钮、焦点环所在配对逐一验过）。环境无浏览器扩展运行 axe/Lighthouse，采用与 Lighthouse 相同的 WCAG 相对亮度算法逐对程序化核算，等价且可复现。

### ② 功能一致性

- JS 语法：两个 `<script>` 块均通过 `new Function()` 解析校验，无语法错误。
- 逻辑零改动：diff 中 JS 部分仅有主题相关新增（约 30 行）与 1 处下载按钮内联样式的令牌化，视图渲染、路由、API 封装、动作分发一行未动。
- 渲染冒烟：headless Edge（1600×900）实截 6 页——登录页明/暗、仪表盘明/暗、客户端列表明/暗。确认：侧栏/卡片/表格/徽标/分页/筛选条在两套主题下均正确着色；顶栏主题按钮文案随主题互换（「☾ 深色」/「☀ 浅色」）并持久化；首帧脚本生效（暗色截图无白底残留）。

### ③ 灰度可读性（§11 验收 #2 的 P1 口径）

截图逐像素转灰度（BT.709 亮度系数）后复检：全部状态徽标凭**文字**仍可一一区分，背景/文字灰度层次未糊成一团。按 §6.1 的完整「形状+文字+颜色」三重编码属提示词 6（P2 组件层）范围，P1 达成「灰度下状态可区分、界面可读」。

### ④ 无外部网络请求

样式只引用系统字体栈（`system-ui`/`Segoe UI`/`Microsoft YaHei UI`/`PingFang SC`…）与内联 `data:` favicon，无任何 CDN/@font-face/外链。

---

## 变更清单

| 文件 | 变更 |
| --- | --- |
| `src/src/BackupMonitor.Api/wwwroot/index.html` | `<style>` 块整体重写为令牌体系（+暗色/动效/焦点/密度）；首帧主题脚本；`effTheme/applyTheme/App.toggleTheme`；顶栏 themeBtn；1 处下载按钮内联样式令牌化。+203/−71 行 |
| `docs/BATCH5-REPORT.md` | 本报告 |

验证用临时文件（引导页/徽标测试页/样式提取）已全部移除，不入库。

---

## 遗留与交接（给提示词 6）

- `<style>` 内令牌仍是单文件形态，提示词 6 按 §9 拆到 `css/tokens.css` 等模块时原样搬迁即可。
- 兼容别名层（`--primary`/`--card`/`--muted`/…）是为了让既有 JS 内联样式免改；提示词 6/7 重写视图时应逐步改为直接引用语义令牌，最终删除别名。
- 徽标仍是纯文字胶囊（P1 保留原形态仅换色），§6.1 的形状编码（● ◐ ○ ▲ ⊘）与「正常态去背景」在提示词 6 落地。
