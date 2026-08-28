/* js/app.js —— 应用外壳：主题 / 布局 / 路由 / 命令面板 / 键盘导航 */
import { store, api, getAccessToken, clearAuth } from './api.js';
import { App, ACTIONS, LOADERS } from './state.js';
import {
  $, esc, errToast, skeleton, batchBarHtml, toast,
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
export const NAV_GROUPS = [
  { key: 'watch', label: '值守', items: [
    ['overview', '概览', '⌂'], ['transfers', '传输中', '⇅'], ['todo', '待办', '○'], ['alerts', '告警', '▲']
  ] },
  { key: 'fleet', label: '机群', items: [
    ['clients', '客户端', '◉'], ['tasks', '备份任务', '▣'], ['plans', '备份计划', '≣']
  ] },
  { key: 'data', label: '数据', items: [
    ['backups', '备份集', '▤'], ['restores', '恢复', '↺']
  ] },
  { key: 'manage', label: '管理', items: [
    ['settings', '存储设置', '⚙'], ['retention', '保留策略', '◫'], ['notifications', '通知', '✉'],
    ['upgrades', '客户端升级', '↑'], ['job-history', '运行记录', '◷'], ['audit', '审计日志', '≡']
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
    <div class="foot"><span class="nav-label">Server API v1</span><span class="logo-mark" aria-hidden="true">v1</span></div>
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
  if (act.confirm && !await act.confirm(st.selected)) return;
  const ids = st.selected.slice();
  let okc = 0, fail = 0;
  try {
    if (act.bulk) {
      const result = await act.fn(ids);
      if (result === false) return;
      okc = 1;
    }
    else {
      for (const id of ids) { try { await act.fn(id); okc++; } catch (e) { fail++; } }
    }
  } catch (e) { fail = 1; }
  toast(`批量完成：${okc}${fail ? `，失败 ${fail}` : ''}`, fail ? 'err' : 'ok');
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
document.addEventListener('click', e => {
  const action = e.target.closest ? e.target.closest('[data-ui-action]') : null;
  if (action) {
    try {
      switch (action.dataset.uiAction) {
        case 'method':
          if (typeof App[action.dataset.method] === 'function') App[action.dataset.method]();
          break;
        case 'act':
          Promise.resolve(App.act(action.dataset.view, action.dataset.action, action.dataset.id))
            .then(() => action.dataset.afterLoader && LOADERS[action.dataset.afterLoader]?.());
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
  operations: 'job-history'
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
  'job-history': () => import('./views/job-history.js'), upgrades: () => import('./views/upgrades.js'),
  settings: () => import('./views/settings.js')
};
const VIEW_EXPORTS = {
  overview: 'vDashboard', transfers: 'vTransfers', todo: 'vTodo', clients: 'vClients', tasks: 'vTasks', plans: 'vPlans', backups: 'vBackups',
  restores: 'vRestores', alerts: 'vAlerts', notifications: 'vNotifications', audit: 'vAudit',
  'registration-tokens': 'vRegistrationTokens',
  retention: 'vRetention', 'job-history': 'vJobHistory', upgrades: 'vUpgrades',
  settings: 'vSettings'
};
const DRAWER_EXPORTS = { tasks: 'openTaskDrawer', restores: 'openRestoreDrawer', alerts: 'openAlertDrawer', audit: 'openAuditDrawer' };
async function route() {
  clearTimeout(App.timer);
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
route();
