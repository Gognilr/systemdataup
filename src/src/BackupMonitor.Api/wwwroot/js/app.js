/* js/app.js —— 应用外壳：主题 / 布局 / 路由 / 命令面板 / 键盘导航 */
import { store, api, getAccessToken, clearAuth } from './api.js';
import { App, ACTIONS, LOADERS } from './state.js';
import {
  $, esc, errToast, skeleton, batchBarHtml, batchSelectedRows, toast,
  openModal, closeModal, closeDrawer, drawerOpen
} from './ui.js';

import { vLogin, vChangePassword } from './views/auth.js';

/* ── 主题（P1 延续）：手动选择优先于系统偏好，持久化 ── */
export function effTheme() {
  const t = store.get('theme');
  if (t === 'light' || t === 'dark') return t;
  return matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}
export function applyTheme() {
  const t = effTheme();
  document.documentElement.dataset.theme = t;
  const b = document.getElementById('themeBtn');
  if (b) b.textContent = t === 'dark' ? '☀ 浅色' : '☾ 深色';
}
matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
  if (!store.get('theme')) applyTheme();
});

/* ── 信息架构（UI-REDESIGN §5）：按运维工作分组，而不是按 REST 端点排列 ── */
/* 侧栏图标（实施方案 U2）。
   原先是 Unicode 几何符号（⌂ ⇅ ○ ▲ …）。它们在 Windows 上多半落到 Segoe UI Symbol，
   字重、基线、视觉大小与正文字体对不齐，个别（▲ ✉ ↺）在某些环境还会走 emoji 呈现变成彩色。
   换成 16×16 / 1.5px 描边 / currentColor 的内联 SVG：跟随主题、跟随选中态，
   十七个 path 加起来不到 3KB，比引一个图标库或多一次 HTTP 请求都划算。 */
const ICON = {
  overview: '<svg viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M2 7.2 8 2.5l6 4.7V13a1 1 0 0 1-1 1H3a1 1 0 0 1-1-1z"/></svg>',
  transfers: '<svg viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M4.5 2.5v11m0 0L2 11m2.5 2.5L7 11M11.5 13.5v-11m0 0L9 5m2.5-2.5L14 5"/></svg>',
  todo: '<svg viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M5.5 4.5h8.5M5.5 8h8.5M5.5 11.5h8.5M2 4.5h.01M2 8h.01M2 11.5h.01"/></svg>',
  alerts: '<svg viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M8 2.2 14.5 13.5h-13zM8 6.6v3M8 11.6h.01"/></svg>',
  clients: '<svg viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M2.5 3.5h11v7h-11zM5.5 13.5h5M8 10.5v3"/></svg>',
  tasks: '<svg viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M2.5 2.5h11v11h-11zM5.2 8l2 2 3.6-4"/></svg>',
  plans: '<svg viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M2.5 3.5h11v10h-11zM2.5 6.5h11M5.5 2v2.5M10.5 2v2.5"/></svg>',
  backups: '<svg viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M8 2.5c3 0 5.5.9 5.5 2s-2.5 2-5.5 2-5.5-.9-5.5-2 2.5-2 5.5-2M2.5 4.5v7c0 1.1 2.5 2 5.5 2s5.5-.9 5.5-2v-7"/></svg>',
  restores: '<svg viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M2.6 8a5.4 5.4 0 1 0 1.7-3.9M2.2 2.6v3.2h3.2"/></svg>',
  settings: '<svg viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M8 5.6a2.4 2.4 0 1 0 0 4.8 2.4 2.4 0 0 0 0-4.8M13 8c0-.4 0-.7-.1-1l1.4-1-1.4-2.4-1.6.6a5 5 0 0 0-1.7-1L9.3 1.4H6.7l-.3 1.8a5 5 0 0 0-1.7 1l-1.6-.6L1.7 6l1.4 1a5 5 0 0 0 0 2l-1.4 1 1.4 2.4 1.6-.6a5 5 0 0 0 1.7 1l.3 1.8h2.6l.3-1.8a5 5 0 0 0 1.7-1l1.6.6 1.4-2.4-1.4-1c.1-.3.1-.6.1-1"/></svg>',
  retention: '<svg viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M3.5 4.5h9v8a1 1 0 0 1-1 1h-7a1 1 0 0 1-1-1zM2 4.5h12M6.2 2.5h3.6M6.5 7v4M9.5 7v4"/></svg>',
  notifications: '<svg viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M2 4.2h12v7.6H2zM2.3 4.6 8 8.8l5.7-4.2"/></svg>',
  upgrades: '<svg viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M8 13V3m0 0L4.4 6.6M8 3l3.6 3.6"/></svg>',
  runs: '<svg viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M8 2.6a5.4 5.4 0 1 1 0 10.8A5.4 5.4 0 0 1 8 2.6M8 5.2V8l2 1.4"/></svg>',
  audit: '<svg viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M2.5 3.5h11M2.5 6.5h11M2.5 9.5h7M2.5 12.5h7"/></svg>',
  'registration-tokens': '<svg viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M9.4 6.6a3 3 0 1 1 0 2.8L6 9.4v1.7H4.4v1.7H2V10.9L6 6.6z"/></svg>',
};

export const NAV_GROUPS = [
  { key: 'watch', label: '值守', items: [
    ['overview', '概览', ICON.overview], ['transfers', '传输中', ICON.transfers], ['todo', '待办', ICON.todo], ['alerts', '告警', ICON.alerts]
  ] },
  { key: 'fleet', label: '机群', items: [
    ['clients', '客户端', ICON.clients], ['tasks', '备份任务', ICON.tasks], ['plans', '备份计划', ICON.plans]
  ] },
  { key: 'data', label: '数据', items: [
    ['backups', '备份集', ICON.backups], ['restores', '恢复', ICON.restores]
  ] },
  { key: 'manage', label: '管理', items: [
    ['settings', '存储设置', ICON.settings], ['retention', '保留策略', ICON.retention], ['notifications', '通知', ICON.notifications],
    ['upgrades', '客户端升级', ICON.upgrades], ['runs', '运行记录', ICON.runs], ['audit', '审计日志', ICON.audit]
  ] }
];
const NAV_ITEMS = NAV_GROUPS.flatMap(g => g.items.map(([key, label, icon]) => ({ key, label, icon, group: g.label })));

// 界面评估：默认折叠是首次使用的硬伤——新管理员第一次登录只看到一竖排抽象几何符号，
// 且折叠态下分组标签是 visibility:hidden，留下几段无法解释的空隙。改为默认展开，
// 只有用户显式折叠过（存了 'collapsed'）才折叠。
function sidebarIsExpanded() { return store.get('sidebar') !== 'collapsed'; }
function sidebarClass() { return sidebarIsExpanded() ? '' : ' is-collapsed'; }

export function shell(active, title, body) {
  const density = store.get('density') === 'comfortable' ? 'comfortable' : 'compact';
  const expanded = sidebarIsExpanded();
  return `<div class="layout">
  <aside class="sidebar${sidebarClass()}" aria-label="主导航">
    <div class="logo"><span class="logo-mark" aria-hidden="true"><img src="/assets/brand/backupmonitor-mark.svg" alt=""></span><span class="logo-full">BackupMonitor<small>轻量级集中备份采集与监控</small></span></div>
    <button class="sidebar-toggle" id="sidebarToggle" data-ui-action="method" data-method="toggleSidebar" aria-label="${expanded ? '折叠侧栏' : '展开侧栏'}" aria-expanded="${expanded}">${expanded ? '‹' : '›'}</button>
    <nav>${NAV_GROUPS.map(group => `<section class="nav-group" aria-label="${esc(group.label)}">
      <div class="nav-group-label"><span class="nav-label">${esc(group.label)}</span></div>
      ${group.items.map(([key, label, icon]) => `<a href="#/${key}" class="${key === active ? 'active' : ''}" title="${esc(label)}" aria-label="${esc(label)}"><span class="nav-icon" aria-hidden="true">${icon}</span><span class="nav-label">${esc(label)}</span></a>`).join('')}
    </section>`).join('')}</nav>
    <div class="foot" title="${esc(App.serverVersion ? '服务端版本 ' + App.serverVersion + '；API v1' : 'API v1')}"><span class="nav-label">${App.serverVersion ? '服务端 v' + esc(App.serverVersion) : 'Server API v1'}</span><span class="logo-mark" aria-hidden="true">${App.serverVersion ? esc(App.serverVersion.split('.').slice(0, 2).join('.')) : 'v1'}</span></div>
  </aside>
  <div class="main">
    <div class="topbar">
      <div class="topbar-title"><button class="topbar-menu" data-ui-action="method" data-method="toggleSidebar" aria-label="切换侧栏">☰</button><span>${esc(title)}</span></div>
      <!-- 命令面板此前只能靠 Ctrl/⌘ K 打开，而这个快捷键只写在「键盘快捷键」弹窗里——
           一个没人知道入口的功能等于不存在。顶栏放一个看得见的入口，顺带把快捷键教出去。 -->
      <button class="topbar-command" data-ui-action="method" data-method="openCommands" aria-label="打开命令面板">
        <span aria-hidden="true">⌕</span><span class="topbar-command-text">搜索或跳转</span><kbd>${navigator.platform.startsWith('Mac') ? '⌘' : 'Ctrl'} K</kbd>
      </button>
      <div class="who">${App.user ? esc(App.user.displayName || App.user.username) : ''}
        &nbsp;<button class="small" id="densityBtn" data-ui-action="method" data-method="toggleDensity" aria-label="切换行密度" title="切换行密度">${density === 'comfortable' ? '▤ 紧凑' : '▦ 宽松'}</button>
        <button class="small" id="themeBtn" data-ui-action="method" data-method="toggleTheme" aria-label="切换明暗主题">${effTheme() === 'dark' ? '☀ 浅色' : '☾ 深色'}</button>
        <button class="small" data-ui-action="method" data-method="logout">退出登录</button></div>
    </div>
    <div class="content" id="view">${body}</div>
  </div></div>`;
}
export const loading = () => skeleton(5);

/* ── 轮询编排（实施方案 W4）────────────────────────────────────────────────

   页面不可见时不排下一轮，回到前台**立刻刷一次**再恢复节奏。

   「立刻刷」这一步不能省：少了它，人切回来看到的是切走那一刻的旧数字，
   而这正是最容易被当成「系统卡住了」的场景——传输页尤其如此，
   一屏不动的进度条和一个真的卡死的传输长得一模一样。

   浏览器本来就会把后台标签页的 setTimeout 节流到约一分钟一次，所以这不是失控问题；
   省下的是「几个管理员各挂一个页面，服务端为没人看的界面持续跑聚合查询」。 */
export function schedulePoll(routeKey, fn, delayMs) {
  clearTimeout(App.timer);
  App.pendingPoll = null;
  if (document.visibilityState === 'hidden') {
    App.pendingPoll = { routeKey, fn };
    return;
  }
  App.timer = setTimeout(() => {
    if (location.hash.replace(/^#\/?/, '').split('/')[0] === routeKey) fn();
  }, delayMs);
}

document.addEventListener('visibilitychange', () => {
  if (document.visibilityState !== 'visible') return;
  const pending = App.pendingPoll;
  App.pendingPoll = null;
  if (!pending) return;
  if (location.hash.replace(/^#\/?/, '').split('/')[0] === pending.routeKey) pending.fn();
});

/* ── 动作分发 / 分页 / 排序 / 批量 ── */
App.act = async function (view, action, id) {
  // 动作在视图模块求值时才注册，而视图是按路由懒加载的（见下方 VIEW_LOADERS）：
  // 待办页只加载了 todo.js，点上面的「签发」时 restores.js 根本没进来，
  // 于是所有跨页动作都必然落到「暂不可用」——只有先逛过那一页才碰巧能用。
  // 查不到就按 view 名把对应模块拉进来再试一次（动态 import 有缓存，重试不额外下载），
  // 以后再加跨页按钮也不用记得手工 import。
  let fn = ACTIONS[view + ':' + action];
  if (!fn && VIEW_LOADERS[view]) {
    try { await VIEW_LOADERS[view](); } catch (e) { errToast(e); return; }
    fn = ACTIONS[view + ':' + action];
  }
  if (!fn) { toast('该动作暂不可用', 'err'); return; }
  try { await fn(id); }
  catch (e) { errToast(e); }
};
App.page = function (key, delta) {
  const st = App.state[key];
  if (!st) return;
  st.page = Math.max(1, st.page + delta);
  if (LOADERS[key]) LOADERS[key]();
};
App.sort = function (key, colKey) {
  const st = App.state[key];
  if (!st || !LOADERS[key]) return;
  if (st.sortKey === colKey) st.sortDesc = st.sortDesc === false;
  else { st.sortKey = colKey; st.sortDesc = true; }
  st.page = 1;
  LOADERS[key]();
};
App.batch = async function (key, actIdx) {
  const acts = (App.batchActs && App.batchActs[key]) || [];
  const act = acts[actIdx];
  const st = App.state[key];
  if (!act || !st || !st.selected || !st.selected.length) return;

  // 选中的行里有做不了这个动作的（已注销的机器不能刷新指标、不能再注销一次），
  // 就只对做得了的那些执行，并把跳过了几个说出来——闷声跳过等于让人以为都做了。
  const rows = batchSelectedRows(key);
  let ids = st.selected.slice();
  let skipped = 0;
  if (rows && act.allow) {
    const usable = rows.filter(act.allow).map(r => r.id);
    skipped = ids.length - usable.length;
    ids = usable;
    if (!ids.length) { toast(act.why || '所选项目都不支持这个操作', 'err'); return; }
  }

  if (act.confirm && !await act.confirm(ids)) return;
  let okc = 0, fail = 0;
  try {
    if (act.bulk) {
      const result = await act.fn(ids);
      if (result === false) return;
      // 批量端点自己知道成功了几条：返回 { ok, fail } 就用它的数，
      // 否则整批算一次（老写法，那些 fn 自己会把细节 toast 出来）。
      if (result && typeof result === 'object') { okc = result.ok || 0; fail = result.fail || 0; }
      else okc = 1;
    }
    else {
      for (const id of ids) { try { await act.fn(id); okc++; } catch (e) { fail++; } }
    }
  } catch (e) { fail = 1; }
  toast(`批量完成：${okc}${fail ? `，失败 ${fail}` : ''}${skipped ? `，跳过 ${skipped}（做不了这个动作）` : ''}`, fail ? 'err' : 'ok');
  st.selected = [];
  if (LOADERS[key]) LOADERS[key]();
};
App.clearSel = function (key) {
  const st = App.state[key];
  if (st) st.selected = [];
  if (LOADERS[key]) LOADERS[key]();
};
App.clearFilter = function (key) {
  const st = App.state[key];
  if (!st) return;
  for (const k of ['status', 'keyword', 'mode', 'level', 'channel', 'action', 'result']) {
    if (k in st) st[k] = '';
  }
  st.page = 1;
  if (LOADERS[key]) LOADERS[key]();
};
App.applyFilter = function (key, field, value) {
  const st = App.state[key];
  if (!st) return;
  st[field] = st[field] === value ? '' : value;
  st.page = 1;
  const control = document.querySelector(`#view #f_${field}`);
  if (control) control.value = st[field];
  if (LOADERS[key]) LOADERS[key]();
};
App.toggleTheme = function () {
  store.set('theme', effTheme() === 'dark' ? 'light' : 'dark');
  applyTheme();
};
App.toggleDensity = function () {
  const next = store.get('density') === 'comfortable' ? 'compact' : 'comfortable';
  store.set('density', next);
  if (next === 'comfortable') document.documentElement.dataset.density = 'comfortable';
  else delete document.documentElement.dataset.density;
  const b = document.getElementById('densityBtn');
  if (b) b.textContent = next === 'comfortable' ? '▤ 紧凑' : '▦ 宽松';
};
App.toggleSidebar = function () {
  const next = sidebarIsExpanded() ? 'collapsed' : 'expanded';
  store.set('sidebar', next);
  const sidebar = document.querySelector('.sidebar');
  if (sidebar) {
    sidebar.classList.toggle('is-collapsed', next !== 'expanded');
    const b = document.getElementById('sidebarToggle');
    if (b) { b.textContent = next === 'expanded' ? '‹' : '›'; b.setAttribute('aria-expanded', String(next === 'expanded')); b.setAttribute('aria-label', next === 'expanded' ? '折叠侧栏' : '展开侧栏'); }
  }
};
App.logout = async function () {
  try {
    await fetch('/api/v1/auth/logout', {
      method: 'POST', headers: { 'Content-Type': 'application/json', 'Authorization': 'Bearer ' + getAccessToken() },
      credentials: 'same-origin'
    });
  } catch (e) {}
  clearAuth();
  location.hash = '#/login'; location.reload();
};

/* ── 全局事件委托：多选复选框 / 清除筛选 ── */
document.addEventListener('change', e => {
  const t = e.target;
  if (t.matches && t.matches('[data-sel]')) {
    const st = App.state[t.dataset.selkey];
    if (!st) return;
    st.selected = st.selected || [];
    const id = t.dataset.sel;
    if (t.checked) { if (!st.selected.includes(id)) st.selected.push(id); }
    else st.selected = st.selected.filter(x => x !== id);
    const bb = document.getElementById('bb-' + t.dataset.selkey);
    if (bb) bb.outerHTML = `<div id="bb-${t.dataset.selkey}">${window.batchBarHtml(t.dataset.selkey)}</div>`;
  } else if (t.matches && t.matches('[data-selall]')) {
    const st = App.state[t.dataset.selall];
    if (!st) return;
    const ids = (t.dataset.ids || '').split(',').filter(Boolean);
    st.selected = t.checked
      ? [...new Set((st.selected || []).concat(ids))]
      : (st.selected || []).filter(x => !ids.includes(x));
    if (LOADERS[t.dataset.selall]) LOADERS[t.dataset.selall]();
  }
});
/* ── act 分发的在途去重 ──
   「立即备份」这类动作要跑几分钟，期间按钮仍然可用：点空白处把等待窗口误关掉，
   列表里的按钮立刻又能点，同一个任务就被下发了两条、三条指令。
   这里按 view+action+id 记一个在途集合，在途期间的点击直接丢弃，
   并把按钮置成 disabled/aria-busy——修的是分发层，所有走 act 的按钮一起受益。

   复位放在 finally 里：失败也要解锁，否则一次网络错误就把按钮永久卡死。 */
const IN_FLIGHT_ACTS = new Set();

function runActOnce(el) {
  const key = `${el.dataset.view}:${el.dataset.action}:${el.dataset.id || ''}`;
  if (IN_FLIGHT_ACTS.has(key)) return;
  IN_FLIGHT_ACTS.add(key);
  el.disabled = true;
  el.setAttribute('aria-busy', 'true');
  const afterLoader = el.dataset.afterLoader;
  Promise.resolve(App.act(el.dataset.view, el.dataset.action, el.dataset.id))
    .catch(errToast)
    .finally(() => {
      IN_FLIGHT_ACTS.delete(key);
      // 动作跑完时列表往往已经重渲染过，原来那个 DOM 节点不在文档里了——
      // 对已经脱离文档的节点解锁是无害的空操作，新渲染出来的按钮本来就是可用的。
      el.disabled = false;
      el.removeAttribute('aria-busy');
      if (afterLoader) LOADERS[afterLoader]?.();
    });
}

/* 列表重渲染之后，把还在跑的那些动作重新按回 disabled。
   在途集合本身仍然挡得住重复分发（runActOnce 先查它），但按钮看着是可点的——
   人点下去没有任何反应，只会以为界面卡了然后接着点。列表刷新在「立即备份」
   这条路径上是必然发生的（动作跑完会调 LOADERS.tasks），所以这一步不是锦上添花。 */
export function syncInFlightButtons(root = document) {
  if (!IN_FLIGHT_ACTS.size) return;
  root.querySelectorAll('[data-ui-action="act"]').forEach(el => {
    const key = `${el.dataset.view}:${el.dataset.action}:${el.dataset.id || ''}`;
    if (!IN_FLIGHT_ACTS.has(key)) return;
    el.disabled = true;
    el.setAttribute('aria-busy', 'true');
  });
}

document.addEventListener('click', e => {
  const action = e.target.closest ? e.target.closest('[data-ui-action]') : null;
  if (action) {
    try {
      switch (action.dataset.uiAction) {
        case 'method':
          // data-arg 是可选的：不带的按钮照旧调无参方法（多传一个 undefined 无害），
          // 带的（如客户端列表的「在用 / 已注销 / 全部」）用它区分是哪一个按钮。
          if (typeof App[action.dataset.method] === 'function') App[action.dataset.method](action.dataset.arg);
          break;
        case 'act':
          runActOnce(action);
          break;
        case 'loader':
          LOADERS[action.dataset.loader]?.();
          break;
        case 'filter':
          App.applyFilter(action.dataset.filterKey, action.dataset.filterField, action.dataset.filterValue);
          break;
        case 'page':
          App.page(action.dataset.pageKey, Number(action.dataset.pageDelta));
          break;
        case 'sort':
          App.sort(action.dataset.sortKey, action.dataset.sortColumn);
          break;
        case 'batch':
          App.batch(action.dataset.batchKey, Number(action.dataset.batchIndex));
          break;
        case 'clear-selection':
          App.clearSel(action.dataset.batchKey);
          break;
        case 'navigate':
          location.hash = action.dataset.hash;
          break;
        case 'select-self':
          action.select();
          break;
        case 'copy':
          navigator.clipboard.writeText(action.dataset.copyValue || '')
            .then(() => toast('已复制', 'ok'))
            .catch(() => toast('浏览器未授予剪贴板权限，请手动复制', 'err'));
          break;
      }
    } catch (error) {
      errToast(error);
    }
  }
  const t = e.target.closest ? e.target.closest('[data-clearfilter]') : null;
  if (t) App.clearFilter(t.dataset.clearfilter);
  const row = e.target.closest ? e.target.closest('#view table tbody tr') : null;
  if (row) App.keyboardRowIndex = [...row.parentElement.children].indexOf(row);
});

/* ── 命令面板（UI-REDESIGN §6.4）：跳转 / 搜索 / 动作三类结果 ── */
let commandEl = null;
let commandItems = [];
let commandIndex = 0;
let commandSearchSerial = 0;
let commandSearchTimer = null;

function ensureCommandPalette() {
  if (commandEl) return commandEl;
  commandEl = document.createElement('div');
  commandEl.id = 'commandPalette';
  commandEl.hidden = true;
  commandEl.innerHTML = `<div class="command-backdrop" data-command-close></div><div class="command-panel" role="dialog" aria-modal="true" aria-labelledby="commandTitle">
    <div class="command-head"><h2 id="commandTitle">命令面板</h2><kbd>Esc</kbd></div>
    <input id="commandInput" class="command-input" autocomplete="off" placeholder="跳转、搜索客户端/任务/备份集，或输入动作…" aria-label="命令面板输入">
    <div id="commandResults" class="command-results" role="listbox"></div>
  </div>`;
  document.body.appendChild(commandEl);
  commandEl.addEventListener('click', e => {
    if (e.target.matches('[data-command-close]')) closeCommandPalette();
    const button = e.target.closest('[data-command-index]');
    if (button) executeCommand(Number(button.dataset.commandIndex));
  });
  // P1-5：每次按键都并发拉三个接口，机群规模上去后是明显的性能问题。
  // 200ms 防抖：连续输入只在停顿后发一次请求；已有的 commandSearchSerial 负责丢弃过期响应。
  commandEl.querySelector('#commandInput').addEventListener('input', e => {
    const value = e.target.value.trim();
    clearTimeout(commandSearchTimer);
    commandSearchTimer = setTimeout(() => renderCommands(value), 200);
  });
  return commandEl;
}
function closeCommandPalette() {
  if (!commandEl) return;
  commandEl.hidden = true;
  commandSearchSerial++;
}
async function searchCommandRecords(q) {
  const read = path => api(path).then(x => x || { items: [] }).catch(() => ({ items: [] }));
  const [clients, tasks, backups] = await Promise.all([
    // P1-5：任务与备份集此前误用 applicationName——它只匹配「应用名」字段，
    // 于是按任务名搜不到任务、按备份集编号搜不到备份集。keyword 才是全字段检索参数：
    // BackupTaskService 的 keyword 同时匹配 Name 与 ApplicationName，
    // BackupSetService 的 keyword 匹配 BackupSetCode。
    // pageSize 由 200 降到 20——服务端已按 keyword 过滤，不需要再拉大量数据到前端二次筛。
    read(`/api/v1/admin/clients?page=1&pageSize=20&keyword=${encodeURIComponent(q)}`),
    read(`/api/v1/admin/backup-tasks?page=1&pageSize=20&keyword=${encodeURIComponent(q)}`),
    read(`/api/v1/admin/backups?page=1&pageSize=20&keyword=${encodeURIComponent(q)}`)
  ]);
  const items = [];
  for (const c of clients.items || []) {
    items.push({ kind: '搜索', title: c.hostname, hint: `客户端 · ${c.displayName || c.id}`, run: () => { location.hash = '#/clients/' + c.id; } });
  }
  for (const t of tasks.items || []) {
    items.push({ kind: '搜索', title: t.name, hint: `备份任务 · ${t.clientHostname || ''}`, run: () => { location.hash = '#/tasks/' + t.id; } });
  }
  for (const b of backups.items || []) {
    items.push({ kind: '搜索', title: b.backupSetCode, hint: `备份集 · ${b.taskName || b.clientHostname || ''}`, run: () => { location.hash = '#/backups/' + b.id; } });
  }
  return items.slice(0, 12);
}
function renderCommandItems(items) {
  commandItems = items;
  commandIndex = 0;
  const wrap = commandEl.querySelector('#commandResults');
  if (!items.length) { wrap.innerHTML = '<div class="command-empty">没有匹配结果</div>'; return; }
  const groups = [];
  for (const item of items) {
    let group = groups.find(x => x.name === item.kind);
    if (!group) { group = { name: item.kind, items: [] }; groups.push(group); }
    group.items.push(item);
  }
  wrap.innerHTML = groups.map(group => `<div class="command-group"><div class="command-group-label">${esc(group.name)}</div>${group.items.map((item, idx) => {
    const globalIndex = items.indexOf(item);
    return `<button class="command-item${globalIndex === 0 ? ' is-active' : ''}" data-command-index="${globalIndex}" role="option" aria-selected="${globalIndex === 0}"><span>${esc(item.title)}</span><small>${esc(item.hint || '')}</small></button>`;
  }).join('')}</div>`).join('');
}
async function renderCommands(query = '') {
  ensureCommandPalette();
  const serial = ++commandSearchSerial;
  const q = query.toLocaleLowerCase();
  const items = [];
  const nav = NAV_ITEMS.filter(x => !q || `${x.label} ${x.key}`.toLocaleLowerCase().includes(q));
  items.push(...nav.map(x => ({ kind: '跳转', title: x.label, hint: `${x.group} · #/${x.key}`, run: () => { location.hash = '#/' + x.key; } })));
  const actions = [
    { title: '打开待办', hint: '值守 · 汇总需要人处理的事项', run: () => { location.hash = '#/todo'; } },
    { title: '切换侧栏展开状态', hint: '界面动作', run: () => App.toggleSidebar() },
    { title: '切换明暗主题', hint: '界面动作', run: () => App.toggleTheme() },
    { title: '切换行密度', hint: '界面动作', run: () => App.toggleDensity() }
  ];
  if (!q) items.push(...actions.map(x => ({ ...x, kind: '动作' })));
  if (q.length >= 2) {
    const old = commandEl.querySelector('#commandResults');
    old.innerHTML = '<div class="command-empty">搜索中…</div>';
    const found = await searchCommandRecords(query);
    if (serial !== commandSearchSerial || commandEl.hidden) return;
    items.push(...found);
  }
  renderCommandItems(items.slice(0, 24));
}
App.openCommands = function () { openCommandPalette(); };

function openCommandPalette(initial = '') {
  ensureCommandPalette();
  commandEl.hidden = false;
  const input = commandEl.querySelector('#commandInput');
  input.value = initial;
  renderCommands(initial);
  requestAnimationFrame(() => { input.focus(); input.select(); });
}
function executeCommand(index) {
  const item = commandItems[index];
  if (!item) return;
  closeCommandPalette();
  Promise.resolve(item.run()).catch(errToast);
}
function moveCommandSelection(delta) {
  if (!commandItems.length) return;
  commandIndex = (commandIndex + delta + commandItems.length) % commandItems.length;
  commandEl.querySelectorAll('[data-command-index]').forEach((el, i) => {
    const active = i === commandIndex;
    el.classList.toggle('is-active', active);
    el.setAttribute('aria-selected', String(active));
    if (active) el.scrollIntoView({ block: 'nearest' });
  });
}

/* ── 键盘导航（UI-REDESIGN §6.4） ── */
function focusListRow(delta) {
  const rows = [...document.querySelectorAll('#view table tbody tr')];
  if (!rows.length) return;
  const next = Math.max(0, Math.min(rows.length - 1, (App.keyboardRowIndex ?? -1) + delta));
  App.keyboardRowIndex = next;
  rows.forEach((row, i) => row.classList.toggle('kbd-focus', i === next));
  rows[next].scrollIntoView({ block: 'nearest' });
}
function activateListRow() {
  const row = document.querySelectorAll('#view table tbody tr')[App.keyboardRowIndex];
  if (!row) return;
  const target = row.querySelector('a[href],button:not([disabled])');
  if (target) target.click();
}
function toggleListRow() {
  const row = document.querySelectorAll('#view table tbody tr')[App.keyboardRowIndex];
  const checkbox = row && row.querySelector('input[type="checkbox"]');
  if (checkbox) checkbox.click();
}
function focusCurrentSearch() {
  const input = document.querySelector('#view input:not([type="checkbox"]),#view select');
  if (input) { input.focus(); input.select?.(); }
}
function showShortcuts() {
  openModal('键盘快捷键', `<div class="shortcut-list">
    <div><kbd>Ctrl/⌘ K</kbd><span>打开命令面板</span></div><div><kbd>g d</kbd><span>跳转概览</span></div>
    <div><kbd>g c</kbd><span>跳转客户端</span></div><div><kbd>g a</kbd><span>跳转告警</span></div>
    <div><kbd>/</kbd><span>聚焦当前页搜索框</span></div><div><kbd>j / k</kbd><span>上下移动当前行</span></div>
    <div><kbd>Enter</kbd><span>打开当前行</span></div><div><kbd>x</kbd><span>勾选当前行</span></div>
    <div><kbd>Esc</kbd><span>关闭抽屉/面板或清除选择</span></div>
  </div>`, { wide: true });
}
let keyPrefix = '';
let keyPrefixTimer = null;
document.addEventListener('keydown', e => {
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); openCommandPalette(); return; }
  if (commandEl && !commandEl.hidden) {
    if (e.key === 'Escape') { e.preventDefault(); closeCommandPalette(); }
    else if (e.key === 'ArrowDown') { e.preventDefault(); moveCommandSelection(1); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); moveCommandSelection(-1); }
    else if (e.key === 'Enter') { e.preventDefault(); executeCommand(commandIndex); }
    return;
  }
  const tag = e.target?.tagName?.toLowerCase();
  const typing = tag === 'input' || tag === 'textarea' || tag === 'select' || e.target?.isContentEditable;
  if (typing) return;
  if (e.key === 'Escape') {
    if (drawerOpen()) { closeDrawer(); return; }
    if (document.getElementById('overlay')) { closeModal(); return; }
    const selected = Object.entries(App.state).filter(([, st]) => st?.selected?.length);
    if (selected.length) selected.forEach(([key, st]) => { st.selected = []; LOADERS[key]?.(); });
    return;
  }
  if (e.key === '?') { e.preventDefault(); showShortcuts(); return; }
  if (e.key === '/') { e.preventDefault(); focusCurrentSearch(); return; }
  if (e.key === 'g') {
    keyPrefix = 'g'; clearTimeout(keyPrefixTimer); keyPrefixTimer = setTimeout(() => { keyPrefix = ''; }, 800); return;
  }
  if (keyPrefix === 'g') {
    keyPrefix = '';
    const routes = { d: 'overview', c: 'clients', a: 'alerts', t: 'transfers' };
    if (routes[e.key]) { e.preventDefault(); location.hash = '#/' + routes[e.key]; return; }
  }
  if (e.key === 'j') { e.preventDefault(); focusListRow(1); }
  else if (e.key === 'k') { e.preventDefault(); focusListRow(-1); }
  else if (e.key === 'Enter') { e.preventDefault(); activateListRow(); }
  else if (e.key.toLowerCase() === 'x') { e.preventDefault(); toggleListRow(); }
});

/* ── 路由：新 IA + 旧 hash 可重定向 ── */
const LEGACY_ROUTES = {
  dashboard: 'overview',
  notif: 'notifications/settings',
  deliveries: 'notifications/deliveries',
  operations: 'runs',
  // 这一页原先只查批量上传批次，现在是全部执行记录；老书签照旧能进来。
  'job-history': 'runs'
};
/* 视图按路由加载：登录首屏只带认证与基础组件，避免把所有列表代码提前下载。 */
const VIEW_LOADERS = {
  overview: () => import('./views/dashboard.js'), todo: () => import('./views/todo.js'),
  transfers: () => import('./views/transfers.js'),
  clients: () => import('./views/clients.js'), 'registration-tokens': () => import('./views/registration-tokens.js'), tasks: () => import('./views/tasks.js'),
  plans: () => import('./views/plans.js'),
  backups: () => import('./views/backups.js'), restores: () => import('./views/restores.js'),
  alerts: () => import('./views/alerts.js'), notifications: () => import('./views/notifications.js'),
  audit: () => import('./views/audit.js'), retention: () => import('./views/retention.js'),
  runs: () => import('./views/runs.js'), upgrades: () => import('./views/upgrades.js'),
  settings: () => import('./views/settings.js')
};
const VIEW_EXPORTS = {
  overview: 'vDashboard', transfers: 'vTransfers', todo: 'vTodo', clients: 'vClients', tasks: 'vTasks', plans: 'vPlans', backups: 'vBackups',
  restores: 'vRestores', alerts: 'vAlerts', notifications: 'vNotifications', audit: 'vAudit',
  'registration-tokens': 'vRegistrationTokens',
  retention: 'vRetention', runs: 'vRuns', upgrades: 'vUpgrades',
  settings: 'vSettings'
};
const DRAWER_EXPORTS = { tasks: 'openTaskDrawer', restores: 'openRestoreDrawer', alerts: 'openAlertDrawer', audit: 'openAuditDrawer' };
/* 路由的外壳只做一件事：**不让任何一页无声地打不开**。

   此前 route() 里没有 try：视图模块只要在加载或首屏渲染时抛一次异常
   （某个 .js 没发布上去、导出名对不上、首屏 innerHTML 前先崩了），
   promise 静静地 reject，页面停在上一页——点击左边导航「什么反应都没有」。
   这是最难报修的一类故障：人只能说「点不开」，而控制台里那行错误谁都没看。

   现在一律兜住并当场把原因显示在内容区。技术细节（模块路径、异常消息）照原样给出：
   看这一屏的人是管理员，他要么自己认得，要么要把这句话转给支持。 */
async function route() {
  try {
    await routeInner();
  } catch (e) {
    console.error('[route]', location.hash, e);
    const message = e && e.message ? e.message : String(e);
    const body = `<div class="card"><h3>这一页没能打开</h3>
      <p class="text-muted">页面：<span class="mono">${esc(location.hash || '#/')}</span></p>
      <p class="mono">${esc(message)}</p>
      <p class="hint">多半是这一页的脚本没有随服务端一起更新。先按 Ctrl+F5 强制刷新；
        仍然如此就是服务端上的管理页面文件不全，需要重新执行一次「修复 / 升级安装」。</p>
      <button class="small" onclick="location.reload()">重新加载</button></div>`;
    // 视图还没来得及铺开外壳时，连同外壳一起画——否则错误卡片会把左边导航一起吃掉，
    // 人就被困在这一页上，连换一页都做不到。
    const view = $('#view');
    if (view) view.innerHTML = body;
    else $('#app').innerHTML = shell('', '这一页没能打开', body);
  }
}

async function routeInner() {
  clearTimeout(App.timer);
  // 切页时把「等回到前台再刷」的那一笔也丢掉，否则旧页的轮询会在切回来时复活。
  App.pendingPoll = null;
  const hash = location.hash || '#/login';
  const parts = hash.replace(/^#\/?/, '').split('/');
  const page = parts[0] || 'login';
  const param = parts[1];
  if (LEGACY_ROUTES[page]) { location.replace('#/' + LEGACY_ROUTES[page]); return; }
  if (!getAccessToken()) { vLogin(); return; }
  // 改密拦截以服务端为准：/auth/me 的 mustChangePassword 是权威值，localStorage 只是首屏之前的兜底。
  // 服务端 PasswordChangeRequiredMiddleware 会独立拦截，这里只负责把用户送到改密页而不是让他撞一墙 403。
  if (App.user?.mustChangePassword || store.get('mustChange') === '1') {
    if (page === 'change-password') { vChangePassword(); return; }
    location.hash = '#/change-password'; return;
  }
  if (page === 'login') { location.hash = '#/overview'; return; }
  if (page === 'change-password') { vChangePassword(); return; }
  const loader = VIEW_LOADERS[page];
  if (!loader) { location.hash = '#/overview'; return; }
  const view = await loader();
  if (page === 'clients' && param) { await view.vClientDetail(param); return; }
  if (page === 'backups' && param) { await view.vBackupDetail(param); return; }
  if (page === 'notifications') { await view.vNotifications(param === 'deliveries' ? 'deliveries' : 'settings'); return; }
  if (param && DRAWER_EXPORTS[page]) { await view[VIEW_EXPORTS[page]](); await view[DRAWER_EXPORTS[page]](param, true); return; }
  await view[VIEW_EXPORTS[page]]();
}
window.addEventListener('hashchange', route);

/* ── 全局接线：内联处理器需要 window 全局 ── */
window.App = App;
window.ACTIONS = ACTIONS;
window.LOADERS = LOADERS;
window.batchBarHtml = batchBarHtml;
window.toast = toast;

/* ── 启动 ── */
const density = store.get('density');
if (density === 'comfortable') document.documentElement.dataset.density = 'comfortable';
applyTheme();
try { App.user = await api('/api/v1/auth/me'); } catch (e) {}

/* 版本号取一次就够：它只在服务端重装时才变，而那会让这个页面整个重新加载。
   走登录前就匿名可见的 /api/v1/public/server-identity（登录页显示指纹用的是同一个），
   因此这一笔不会因为没登录而失败，也不需要任何权限。
   取不到就保持原来那行「Server API v1」——版本号缺失不该把侧栏底部变成一行错误。 */
try {
  const identity = await api('/api/v1/public/server-identity', { noAuth: true });
  // InformationalVersion 可能带 +构建元数据（如 1.2.0+9f3a1c），显示到 + 为止就够了。
  App.serverVersion = identity?.version ? String(identity.version).split('+')[0] : null;
} catch (e) {}

route();
